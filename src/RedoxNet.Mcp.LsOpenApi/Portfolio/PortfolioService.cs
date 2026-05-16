namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Default portfolio service that combines local persistence with optional live quote enrichment,
/// ambiguity resolution, and applied_to echoes for the MCP tool layer.
/// </summary>
internal sealed class PortfolioService : IPortfolioService
{
    /// <summary>
    /// Threshold for the "분할/무상증자 가능성" warning attached to holding rows when the live price
    /// diverges absurdly from the recorded average. Five-fold or more in either direction.
    /// </summary>
    const double WarningRatioThreshold = 5.0;

    readonly IPortfolioRepository _repository;
    readonly IQuoteService _quoteService;

    public PortfolioService(IPortfolioRepository repository, IQuoteService quoteService)
    {
        _repository = repository;
        _quoteService = quoteService;
    }

    // -------- Watchlist groups --------

    public Task<IReadOnlyList<WatchlistGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default) =>
        _repository.ListGroupsAsync(cancellationToken);

    public Task<WatchlistGroup> CreateGroupAsync(string name, string? description, CancellationToken cancellationToken = default) =>
        _repository.CreateGroupAsync(name, description, cancellationToken);

    public Task<DeleteGroupResult> DeleteGroupAsync(string name, CancellationToken cancellationToken = default) =>
        _repository.DeleteGroupAsync(name, cancellationToken);

    public Task<RenameGroupResult> RenameGroupAsync(string oldName, string newName, CancellationToken cancellationToken = default) =>
        _repository.RenameGroupAsync(oldName, newName, cancellationToken);

    // -------- Watchlist items --------

    public async Task<WatchlistItemAdded> AddWatchlistAsync(string symbol, string group, string? notes, CancellationToken cancellationToken = default)
    {
        WatchlistItem item = await _repository.AddWatchlistItemAsync(symbol, group, notes, cancellationToken).ConfigureAwait(false);
        StockQuote? quote = await TryCacheStockMetadataAsync(item.Symbol, cancellationToken).ConfigureAwait(false);
        string name = quote?.Name ?? item.Name;
        return new WatchlistItemAdded(item.Symbol, name, item.GroupName, item.Notes, item.AddedAt);
    }

    public async Task<RemoveResult> RemoveWatchlistAsync(string symbol, string group, CancellationToken cancellationToken = default)
    {
        bool removed = await _repository.RemoveWatchlistItemAsync(symbol, group, cancellationToken).ConfigureAwait(false);
        return new RemoveResult(removed);
    }

    public async Task<WatchlistListResult> ListWatchlistAsync(string? group, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WatchlistItem> items = await _repository.ListWatchlistAsync(group, cancellationToken).ConfigureAwait(false);
        QuoteBatchResult<StockQuote> quoteResult = await EnrichStocksAsync(items.Select(i => i.Symbol), cancellationToken).ConfigureAwait(false);

        WatchlistItemWithQuote Project(WatchlistItem item)
        {
            quoteResult.Quotes.TryGetValue(item.Symbol, out StockQuote? quote);
            string name = quote?.Name ?? item.Name;
            return new WatchlistItemWithQuote(item.Symbol, name, item.GroupName, item.Notes, item.AddedAt, quote);
        }

        IReadOnlyList<WatchlistGroupSummary> allGroups = await _repository.ListGroupsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<WatchlistGroupSummary> groups = string.IsNullOrWhiteSpace(group)
            ? allGroups
            : allGroups.Where(g => string.Equals(g.Name, group.Trim(), StringComparison.Ordinal)).ToList();
        Dictionary<string, List<WatchlistItemWithQuote>> byGroup = items
            .GroupBy(i => i.GroupName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(Project).ToList(), StringComparer.Ordinal);

        var groupItems = groups.Select(g => new WatchlistGroupItems(
            g.Name,
            g.Description,
            g.SortOrder,
            byGroup.TryGetValue(g.Name, out List<WatchlistItemWithQuote>? list) ? list : Array.Empty<WatchlistItemWithQuote>())).ToList();

        return new WatchlistListResult(NullIfWhiteSpace(group), groupItems, quoteResult.TopLevelError);
    }

    // -------- Themes --------

    public Task<WatchedTheme> WatchThemeAsync(string themeCode, string? themeName, string? notes, CancellationToken cancellationToken = default) =>
        _repository.WatchThemeAsync(themeCode, themeName, notes, cancellationToken);

    public async Task<RemoveResult> UnwatchThemeAsync(string themeCode, CancellationToken cancellationToken = default)
    {
        bool removed = await _repository.UnwatchThemeAsync(themeCode, cancellationToken).ConfigureAwait(false);
        return new RemoveResult(removed);
    }

    public async Task<ThemeListResult> ListThemesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WatchedTheme> themes = await _repository.ListThemesAsync(cancellationToken).ConfigureAwait(false);
        QuoteBatchResult<ThemeQuote> quoteResult = await _quoteService.GetThemeQuotesAsync(
            themes.Select(s => s.ThemeCode).ToArray(), cancellationToken).ConfigureAwait(false);
        var items = themes.Select(s =>
        {
            quoteResult.Quotes.TryGetValue(s.ThemeCode, out ThemeQuote? quote);
            return new WatchedThemeWithQuote(s.ThemeCode, s.ThemeName, s.Notes, s.AddedAt, quote);
        }).ToList();
        return new ThemeListResult(items, quoteResult.TopLevelError);
    }

    // -------- Accounts --------

    public Task<IReadOnlyList<AccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        _repository.ListAccountSummariesAsync(cancellationToken);

    public async Task<AccountInfo?> GetDefaultAccountAsync(CancellationToken cancellationToken = default)
    {
        Account? account = await _repository.GetDefaultAccountAsync(cancellationToken).ConfigureAwait(false);
        return account is null ? null : SqlitePortfolioRepository.ToAccountInfo(account);
    }

    public async Task<AccountInfo> UpsertAccountAsync(string accountNumber, string nickname, string? broker, bool setDefault, CancellationToken cancellationToken = default)
    {
        string normalizedNumber = (accountNumber ?? throw new ArgumentNullException(nameof(accountNumber))).Trim();
        string normalizedNickname = (nickname ?? throw new ArgumentNullException(nameof(nickname))).Trim();
        if (normalizedNumber.Length == 0)
            throw new PortfolioValidationException("account_number must not be empty.");
        if (normalizedNickname.Length == 0)
            throw new PortfolioValidationException("nickname must not be empty.");

        // Pre-check nickname collision: another account_no owning this nickname.
        Account? nicknameOwner = await _repository.GetAccountByIdentifierAsync(normalizedNickname, cancellationToken).ConfigureAwait(false);
        if (nicknameOwner is not null && !string.Equals(nicknameOwner.AccountNo, normalizedNumber, StringComparison.Ordinal))
            throw new PortfolioValidationException(
                $"Nickname '{normalizedNickname}' is already used by account '{nicknameOwner.AccountNo}'.");

        Account saved = await _repository.UpsertAccountAsync(normalizedNumber, normalizedNickname, broker, setDefault, cancellationToken).ConfigureAwait(false);
        return SqlitePortfolioRepository.ToAccountInfo(saved);
    }

    public async Task<RemoveAccountResult> RemoveAccountAsync(string accountIdentifier, bool confirm, CancellationToken cancellationToken = default)
    {
        Account account = await ResolveByIdentifierAsync(accountIdentifier, cancellationToken).ConfigureAwait(false);
        RemoveAccountResult result = await _repository.RemoveAccountAsync(account, confirm, cancellationToken).ConfigureAwait(false);
        if (!result.Removed && result.CascadedHoldings > 0)
        {
            double? marketValue = await EstimateAccountMarketValueAsync(account.Id, cancellationToken).ConfigureAwait(false);
            throw new RequiresConfirmationException(SqlitePortfolioRepository.ToAccountInfo(account), result.CascadedHoldings, marketValue);
        }
        return result;
    }

    public async Task<AccountInfo> SetDefaultAccountAsync(string accountIdentifier, CancellationToken cancellationToken = default)
    {
        Account account = await ResolveByIdentifierAsync(accountIdentifier, cancellationToken).ConfigureAwait(false);
        Account updated = await _repository.SetDefaultAccountAsync(account, cancellationToken).ConfigureAwait(false);
        return SqlitePortfolioRepository.ToAccountInfo(updated);
    }

    public Task<RenameBrokerResult> RenameBrokerAsync(string fromBroker, string toBroker, CancellationToken cancellationToken = default) =>
        _repository.RenameBrokerAsync(fromBroker, toBroker, cancellationToken);

    // -------- Holdings (set/buy/sell/remove) --------

    public async Task<HoldingWriteResult> SetHoldingAsync(string symbol, int quantity, double avgPrice, string? notes, string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
            throw new PortfolioValidationException("quantity must be positive; use ls_holdings_remove to delete a holding.");
        if (avgPrice < 0)
            throw new PortfolioValidationException("avg_price must be non-negative.");

        Account account = await ResolveTargetForWriteAsync(accountIdentifier, cancellationToken).ConfigureAwait(false);
        Holding saved = await _repository.SetHoldingAsync(account.Id, symbol, quantity, avgPrice, notes, cancellationToken).ConfigureAwait(false);
        StockQuote? quote = await TryCacheStockMetadataAsync(saved.Symbol, cancellationToken).ConfigureAwait(false);
        return new HoldingWriteResult(saved.Symbol, ResolveDisplayName(quote, saved.Name, saved.Symbol), saved.Quantity, saved.AvgPrice, SqlitePortfolioRepository.ToAccountInfo(account));
    }

    public async Task<HoldingWriteResult> BuyHoldingAsync(string symbol, int quantity, double price, string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
            throw new PortfolioValidationException("quantity must be positive.");
        if (price < 0)
            throw new PortfolioValidationException("price must be non-negative.");

        Account account = await ResolveTargetForWriteAsync(accountIdentifier, cancellationToken).ConfigureAwait(false);
        Holding saved = await _repository.BuyHoldingAsync(account.Id, symbol, quantity, price, cancellationToken).ConfigureAwait(false);
        StockQuote? quote = await TryCacheStockMetadataAsync(saved.Symbol, cancellationToken).ConfigureAwait(false);
        return new HoldingWriteResult(saved.Symbol, ResolveDisplayName(quote, saved.Name, saved.Symbol), saved.Quantity, saved.AvgPrice, SqlitePortfolioRepository.ToAccountInfo(account));
    }

    public async Task<HoldingWriteResult> SellHoldingAsync(string symbol, int quantity, string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
            throw new PortfolioValidationException("quantity must be positive.");

        Account account = await ResolveSellRemoveTargetAsync(symbol, accountIdentifier, requireHolding: true, cancellationToken).ConfigureAwait(false);
        AccountInfo accountInfo = SqlitePortfolioRepository.ToAccountInfo(account);

        Holding? existing = await _repository.GetHoldingAsync(account.Id, symbol, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            throw new PortfolioValidationException($"Symbol '{symbol.Trim().ToUpperInvariant()}' is not held in account '{account.Nickname}'.");
        if (quantity > existing.Quantity)
            throw new InsufficientQuantityException(existing.Symbol, existing.Quantity, quantity, accountInfo);

        Holding? after = await _repository.SellHoldingAsync(account.Id, symbol, quantity, cancellationToken).ConfigureAwait(false);
        int remainingQty = after?.Quantity ?? 0;
        double avgPrice = after?.AvgPrice ?? existing.AvgPrice;
        return new HoldingWriteResult(existing.Symbol, existing.Name == existing.Symbol ? null : existing.Name, remainingQty, avgPrice, accountInfo);
    }

    public async Task<HoldingWriteResult?> RemoveHoldingAsync(string symbol, string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        Account? account = await ResolveSellRemoveTargetOptionalAsync(symbol, accountIdentifier, cancellationToken).ConfigureAwait(false);
        if (account is null)
            return null;
        Holding? existing = await _repository.GetHoldingAsync(account.Id, symbol, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return null;
        await _repository.RemoveHoldingAsync(account.Id, symbol, cancellationToken).ConfigureAwait(false);
        return new HoldingWriteResult(
            existing.Symbol,
            existing.Name == existing.Symbol ? null : existing.Name,
            0,
            existing.AvgPrice,
            SqlitePortfolioRepository.ToAccountInfo(account));
    }

    public async Task<HoldingListResult> ListHoldingsAsync(string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Account> accounts;
        if (!string.IsNullOrWhiteSpace(accountIdentifier))
        {
            Account account = await ResolveByIdentifierAsync(accountIdentifier, cancellationToken).ConfigureAwait(false);
            accounts = new[] { account };
        }
        else
        {
            accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (accounts.Count == 0)
            return new HoldingListResult(Array.Empty<AccountHoldings>(), EmptySummary(), null);

        IReadOnlyList<Holding> allHoldings;
        if (accounts.Count == 1 && accountIdentifier is not null)
        {
            allHoldings = await _repository.ListHoldingsAsync(accounts[0].Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            allHoldings = await _repository.ListAllHoldingsAsync(cancellationToken).ConfigureAwait(false);
        }

        var distinctSymbols = allHoldings.Select(h => h.Symbol).Distinct(StringComparer.Ordinal).ToArray();
        QuoteBatchResult<StockQuote> quoteResult = await EnrichStocksAsync(distinctSymbols, cancellationToken).ConfigureAwait(false);

        var perAccount = new List<AccountHoldings>(accounts.Count);
        double totalCost = 0;
        double totalValue = 0;
        bool totalAllQuoted = true;

        foreach (Account account in accounts)
        {
            List<Holding> accountRows = allHoldings.Where(h => h.AccountId == account.Id).ToList();
            (List<HoldingWithQuote> projected, double cost, double value, bool allQuoted) = ProjectHoldings(accountRows, quoteResult);
            var summary = BuildSummary(cost, value, allQuoted);
            totalCost += cost;
            if (allQuoted)
                totalValue += value;
            else
                totalAllQuoted = false;

            perAccount.Add(new AccountHoldings(
                account.AccountNo,
                account.Nickname,
                account.Broker,
                account.IsDefault,
                projected,
                summary));
        }

        return new HoldingListResult(
            perAccount,
            BuildSummary(totalCost, totalValue, totalAllQuoted),
            quoteResult.TopLevelError);
    }

    // -------- Corporate actions --------

    public Task<CorporateActionResult> SplitHoldingAsync(string symbol, int ratio, string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        if (ratio < 2)
            throw new PortfolioValidationException("split ratio must be 2 or greater.");
        return ApplyCorporateActionAsync(symbol, "split", ratio, qtyMultiplier: ratio, priceMultiplier: 1.0 / ratio, accountIdentifier, cancellationToken);
    }

    public Task<CorporateActionResult> ReverseSplitHoldingAsync(string symbol, int ratio, string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        if (ratio < 2)
            throw new PortfolioValidationException("reverse-split ratio must be 2 or greater.");
        return ApplyCorporateActionAsync(symbol, "reverse_split", ratio, qtyMultiplier: 1.0 / ratio, priceMultiplier: ratio, accountIdentifier, cancellationToken);
    }

    public Task<CorporateActionResult> BonusHoldingAsync(string symbol, double ratio, string? accountIdentifier, CancellationToken cancellationToken = default)
    {
        if (ratio <= 0)
            throw new PortfolioValidationException("bonus ratio must be positive.");
        double multiplier = 1.0 + ratio;
        return ApplyCorporateActionAsync(symbol, "bonus", ratio, qtyMultiplier: multiplier, priceMultiplier: 1.0 / multiplier, accountIdentifier, cancellationToken);
    }

    async Task<CorporateActionResult> ApplyCorporateActionAsync(
        string symbol,
        string actionName,
        double ratio,
        double qtyMultiplier,
        double priceMultiplier,
        string? accountIdentifier,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Account> targets;
        if (!string.IsNullOrWhiteSpace(accountIdentifier))
        {
            Account account = await ResolveByIdentifierAsync(accountIdentifier, cancellationToken).ConfigureAwait(false);
            targets = new[] { account };
        }
        else
        {
            targets = await _repository.FindAccountsHoldingAsync(symbol, cancellationToken).ConfigureAwait(false);
            if (targets.Count == 0)
                throw new PortfolioValidationException($"Symbol '{symbol.Trim().ToUpperInvariant()}' is not held in any account.");
        }

        var results = new List<CorporateActionAccountResult>(targets.Count);
        foreach (Account account in targets)
        {
            Holding? before = await _repository.GetHoldingAsync(account.Id, symbol, cancellationToken).ConfigureAwait(false);
            if (before is null)
            {
                // Explicit account targeted but not holding — skip silently? Reject for clarity.
                throw new PortfolioValidationException($"Symbol '{symbol.Trim().ToUpperInvariant()}' is not held in account '{account.Nickname}'.");
            }
            Holding? after;
            try
            {
                after = await _repository.ApplyCorporateActionAsync(account.Id, symbol, qtyMultiplier, priceMultiplier, cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw new PortfolioValidationException(ex.Message);
            }
            if (after is null)
                continue;
            results.Add(new CorporateActionAccountResult(
                SqlitePortfolioRepository.ToAccountInfo(account),
                new HoldingSnapshot(before.Quantity, before.AvgPrice),
                new HoldingSnapshot(after.Quantity, after.AvgPrice)));
        }
        return new CorporateActionResult(symbol.Trim().ToUpperInvariant(), actionName, ratio, results);
    }

    // -------- Resolution helpers --------

    async Task<Account> ResolveTargetForWriteAsync(string? identifier, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(identifier))
            return await ResolveByIdentifierAsync(identifier, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Account> accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        return accounts.Count switch
        {
            0 => throw new RequiresAccountException("No accounts registered. Use ls_account_upsert to create one first."),
            1 => accounts[0],
            _ => throw new AmbiguousAccountException(
                $"Multiple accounts exist ({accounts.Count}). Specify an account via account_number or nickname.",
                accounts.Select(SqlitePortfolioRepository.ToAccountInfo).ToList()),
        };
    }

    async Task<Account> ResolveSellRemoveTargetAsync(string symbol, string? identifier, bool requireHolding, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(identifier))
            return await ResolveByIdentifierAsync(identifier, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Account> accountsHolding = await _repository.FindAccountsHoldingAsync(symbol, cancellationToken).ConfigureAwait(false);
        if (accountsHolding.Count == 0)
        {
            if (requireHolding)
                throw new PortfolioValidationException($"Symbol '{symbol.Trim().ToUpperInvariant()}' is not held in any account.");
            // Allow caller (remove path) to signal a no-op via the optional variant below.
            // This shouldn't be reached when requireHolding=true.
            throw new PortfolioValidationException($"Symbol '{symbol.Trim().ToUpperInvariant()}' is not held in any account.");
        }
        if (accountsHolding.Count == 1)
            return accountsHolding[0];

        throw new AmbiguousAccountException(
            $"Holding '{symbol.Trim().ToUpperInvariant()}' exists in {accountsHolding.Count} accounts. Specify the account.",
            accountsHolding.Select(SqlitePortfolioRepository.ToAccountInfo).ToList());
    }

    async Task<Account?> ResolveSellRemoveTargetOptionalAsync(string symbol, string? identifier, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(identifier))
            return await ResolveByIdentifierAsync(identifier, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<Account> accountsHolding = await _repository.FindAccountsHoldingAsync(symbol, cancellationToken).ConfigureAwait(false);
        return accountsHolding.Count switch
        {
            0 => null,
            1 => accountsHolding[0],
            _ => throw new AmbiguousAccountException(
                $"Holding '{symbol.Trim().ToUpperInvariant()}' exists in {accountsHolding.Count} accounts. Specify the account.",
                accountsHolding.Select(SqlitePortfolioRepository.ToAccountInfo).ToList()),
        };
    }

    async Task<Account> ResolveByIdentifierAsync(string identifier, CancellationToken cancellationToken)
    {
        Account? found = await _repository.GetAccountByIdentifierAsync(identifier, cancellationToken).ConfigureAwait(false);
        if (found is not null)
            return found;
        IReadOnlyList<Account> all = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        throw new AccountNotFoundException(identifier, all.Select(SqlitePortfolioRepository.ToAccountInfo).ToList());
    }

    async Task<double?> EstimateAccountMarketValueAsync(long accountId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Holding> holdings = await _repository.ListHoldingsAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (holdings.Count == 0)
            return 0;
        QuoteBatchResult<StockQuote> quotes = await EnrichStocksAsync(holdings.Select(h => h.Symbol), cancellationToken).ConfigureAwait(false);
        if (quotes.TopLevelError is not null)
            return null;
        double total = 0;
        foreach (Holding h in holdings)
        {
            if (!quotes.Quotes.TryGetValue(h.Symbol, out StockQuote? q) || q is null)
                return null;
            total += h.Quantity * q.Price;
        }
        return total;
    }

    // -------- Quote enrichment + projection helpers --------

    async Task<StockQuote?> TryCacheStockMetadataAsync(string symbol, CancellationToken cancellationToken)
    {
        QuoteBatchResult<StockQuote> result = await _quoteService.GetStockQuotesAsync(new[] { symbol }, cancellationToken).ConfigureAwait(false);
        if (result.Quotes.TryGetValue(symbol, out StockQuote? quote) && !string.IsNullOrWhiteSpace(quote?.Name))
        {
            await _repository.UpsertStockAsync(symbol, quote.Name, "unknown", null, cancellationToken).ConfigureAwait(false);
            return quote;
        }
        return null;
    }

    async Task<QuoteBatchResult<StockQuote>> EnrichStocksAsync(IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        string[] arr = symbols.Distinct(StringComparer.Ordinal).ToArray();
        QuoteBatchResult<StockQuote> result = await _quoteService.GetStockQuotesAsync(arr, cancellationToken).ConfigureAwait(false);
        foreach ((string symbol, StockQuote? quote) in result.Quotes)
        {
            if (!string.IsNullOrWhiteSpace(quote?.Name))
                await _repository.UpsertStockAsync(symbol, quote.Name, "unknown", null, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    static (List<HoldingWithQuote> Projected, double CostBasis, double MarketValue, bool AllQuoted) ProjectHoldings(
        IReadOnlyList<Holding> accountRows,
        QuoteBatchResult<StockQuote> quoteResult)
    {
        var projected = new List<HoldingWithQuote>(accountRows.Count);
        double cost = 0;
        double value = 0;
        bool allQuoted = true;

        foreach (Holding holding in accountRows)
        {
            quoteResult.Quotes.TryGetValue(holding.Symbol, out StockQuote? quote);
            double rowCost = holding.Quantity * holding.AvgPrice;
            cost += rowCost;

            double? marketValue = null;
            double? pnl = null;
            double? pnlPct = null;
            string? warning = null;
            if (quote is not null)
            {
                marketValue = holding.Quantity * quote.Price;
                pnl = marketValue.Value - rowCost;
                pnlPct = rowCost == 0 ? null : pnl.Value / rowCost * 100;
                value += marketValue.Value;
                if (holding.AvgPrice > 0)
                {
                    double ratio = quote.Price / holding.AvgPrice;
                    if (ratio >= WarningRatioThreshold || ratio <= 1.0 / WarningRatioThreshold)
                        warning = $"분할/무상증자 가능성: 현재가/평단 비율 {ratio:F1}배. 분할 도구로 보정하세요.";
                }
            }
            else
            {
                allQuoted = false;
            }

            projected.Add(new HoldingWithQuote(
                holding.Symbol,
                ResolveDisplayName(quote, holding.Name, holding.Symbol),
                holding.Quantity,
                holding.AvgPrice,
                holding.Notes,
                quote,
                marketValue,
                rowCost,
                pnl,
                pnlPct,
                warning));
        }

        return (projected, cost, value, allQuoted);
    }

    static PortfolioSummary BuildSummary(double cost, double value, bool allQuoted) =>
        allQuoted
            ? new PortfolioSummary(cost, value, value - cost, cost == 0 ? null : (value - cost) / cost * 100)
            : new PortfolioSummary(cost, null, null, null);

    static PortfolioSummary EmptySummary() => new(0, 0, 0, 0);

    static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Returns the quote's display name when available, falls back to the cached name when distinct from the symbol placeholder, otherwise null.
    /// </summary>
    static string? ResolveDisplayName(StockQuote? quote, string cachedName, string symbol)
    {
        if (!string.IsNullOrWhiteSpace(quote?.Name))
            return quote.Name;
        return string.Equals(cachedName, symbol, StringComparison.Ordinal) ? null : cachedName;
    }
}
