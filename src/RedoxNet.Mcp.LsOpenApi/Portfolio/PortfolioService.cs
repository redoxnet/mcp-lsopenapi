namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Default portfolio service that combines local persistence with optional live quote enrichment.
/// </summary>
internal sealed class PortfolioService : IPortfolioService
{
    readonly IPortfolioRepository _repository;
    readonly IQuoteService _quoteService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PortfolioService"/> class.
    /// </summary>
    public PortfolioService(IPortfolioRepository repository, IQuoteService quoteService)
    {
        _repository = repository;
        _quoteService = quoteService;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WatchlistGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default) =>
        _repository.ListGroupsAsync(cancellationToken);

    /// <inheritdoc />
    public Task<WatchlistGroup> CreateGroupAsync(string name, string? description, CancellationToken cancellationToken = default) =>
        _repository.CreateGroupAsync(name, description, cancellationToken);

    /// <inheritdoc />
    public Task<DeleteGroupResult> DeleteGroupAsync(string name, CancellationToken cancellationToken = default) =>
        _repository.DeleteGroupAsync(name, cancellationToken);

    /// <inheritdoc />
    public async Task<WatchlistItemAdded> AddWatchlistAsync(string symbol, string group, string? notes, CancellationToken cancellationToken = default)
    {
        WatchlistItem item = await _repository.AddWatchlistItemAsync(symbol, group, notes, cancellationToken).ConfigureAwait(false);
        StockQuote? quote = await TryCacheStockMetadataAsync(item.Symbol, cancellationToken).ConfigureAwait(false);
        string name = quote?.Name ?? item.Name;
        return new WatchlistItemAdded(item.Symbol, name, item.GroupName, item.Notes, item.AddedAt);
    }

    /// <inheritdoc />
    public async Task<RemoveResult> RemoveWatchlistAsync(string symbol, string group, CancellationToken cancellationToken = default)
    {
        bool removed = await _repository.RemoveWatchlistItemAsync(symbol, group, cancellationToken).ConfigureAwait(false);
        return new RemoveResult(removed);
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<WatchedSector> WatchSectorAsync(string sectorCode, string? sectorName, string? notes, CancellationToken cancellationToken = default) =>
        _repository.WatchSectorAsync(sectorCode, sectorName, notes, cancellationToken);

    /// <inheritdoc />
    public async Task<RemoveResult> UnwatchSectorAsync(string sectorCode, CancellationToken cancellationToken = default)
    {
        bool removed = await _repository.UnwatchSectorAsync(sectorCode, cancellationToken).ConfigureAwait(false);
        return new RemoveResult(removed);
    }

    /// <inheritdoc />
    public async Task<SectorListResult> ListSectorsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WatchedSector> sectors = await _repository.ListSectorsAsync(cancellationToken).ConfigureAwait(false);
        QuoteBatchResult<SectorQuote> quoteResult = await _quoteService.GetSectorQuotesAsync(
            sectors.Select(s => s.SectorCode).ToArray(), cancellationToken).ConfigureAwait(false);
        var items = sectors.Select(s =>
        {
            quoteResult.Quotes.TryGetValue(s.SectorCode, out SectorQuote? quote);
            return new WatchedSectorWithQuote(s.SectorCode, s.SectorName, s.Notes, s.AddedAt, quote);
        }).ToList();
        return new SectorListResult(items, quoteResult.TopLevelError);
    }

    /// <inheritdoc />
    public async Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        Account account = await _repository.GetDefaultAccountAsync(cancellationToken).ConfigureAwait(false);
        return AccountPayload(account);
    }

    /// <inheritdoc />
    public async Task<AccountInfo> SetAccountAsync(string accountNo, string? nickname, CancellationToken cancellationToken = default)
    {
        Account account = await _repository.SetDefaultAccountAsync(accountNo, nickname, cancellationToken).ConfigureAwait(false);
        return AccountPayload(account);
    }

    /// <inheritdoc />
    public async Task<HoldingAddedResult> AddHoldingAsync(string symbol, int quantity, double avgPrice, string? notes, CancellationToken cancellationToken = default)
    {
        Account account = await _repository.GetDefaultAccountAsync(cancellationToken).ConfigureAwait(false);
        Holding holding = await _repository.UpsertHoldingAsync(account.Id, symbol, quantity, avgPrice, notes, cancellationToken).ConfigureAwait(false);
        StockQuote? quote = await TryCacheStockMetadataAsync(holding.Symbol, cancellationToken).ConfigureAwait(false);
        return new HoldingAddedResult(holding.Symbol, quote?.Name ?? holding.Name, holding.Quantity, holding.AvgPrice);
    }

    /// <inheritdoc />
    public async Task<HoldingUpdatedResult> UpdateHoldingAsync(string symbol, int? quantity, double? avgPrice, string? notes, CancellationToken cancellationToken = default)
    {
        Account account = await _repository.GetDefaultAccountAsync(cancellationToken).ConfigureAwait(false);
        Holding existing = await _repository.GetHoldingAsync(account.Id, symbol, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Holding '{symbol}' does not exist.");
        Holding updated = await _repository.UpsertHoldingAsync(
            account.Id,
            existing.Symbol,
            quantity ?? existing.Quantity,
            avgPrice ?? existing.AvgPrice,
            notes ?? existing.Notes,
            cancellationToken).ConfigureAwait(false);
        return new HoldingUpdatedResult(updated.Symbol, updated.Quantity, updated.AvgPrice);
    }

    /// <inheritdoc />
    public async Task<RemoveResult> RemoveHoldingAsync(string symbol, CancellationToken cancellationToken = default)
    {
        Account account = await _repository.GetDefaultAccountAsync(cancellationToken).ConfigureAwait(false);
        bool removed = await _repository.RemoveHoldingAsync(account.Id, symbol, cancellationToken).ConfigureAwait(false);
        return new RemoveResult(removed);
    }

    /// <inheritdoc />
    public async Task<HoldingListResult> ListHoldingsAsync(CancellationToken cancellationToken = default)
    {
        Account account = await _repository.GetDefaultAccountAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<Holding> holdings = await _repository.ListHoldingsAsync(account.Id, cancellationToken).ConfigureAwait(false);
        QuoteBatchResult<StockQuote> quoteResult = await EnrichStocksAsync(holdings.Select(h => h.Symbol), cancellationToken).ConfigureAwait(false);

        var items = new List<HoldingWithQuote>(holdings.Count);
        double totalCost = 0;
        double totalValue = 0;
        bool allQuoted = true;

        foreach (Holding holding in holdings)
        {
            quoteResult.Quotes.TryGetValue(holding.Symbol, out StockQuote? quote);
            double cost = holding.Quantity * holding.AvgPrice;
            totalCost += cost;

            double? currentValue = null;
            double? pnl = null;
            double? pnlPct = null;
            if (quote is not null)
            {
                currentValue = holding.Quantity * quote.Price;
                pnl = currentValue.Value - cost;
                pnlPct = cost == 0 ? null : pnl.Value / cost * 100;
                totalValue += currentValue.Value;
            }
            else
            {
                allQuoted = false;
            }

            items.Add(new HoldingWithQuote(
                holding.Symbol,
                ResolveDisplayName(quote, holding.Name, holding.Symbol),
                holding.Quantity,
                holding.AvgPrice,
                holding.Notes,
                quote,
                currentValue,
                pnl,
                pnlPct));
        }

        PortfolioSummary summary = allQuoted
            ? new PortfolioSummary(
                TotalCost: totalCost,
                TotalValue: totalValue,
                TotalPnl: totalValue - totalCost,
                TotalPnlPct: totalCost == 0 ? null : (totalValue - totalCost) / totalCost * 100)
            : new PortfolioSummary(totalCost, TotalValue: null, TotalPnl: null, TotalPnlPct: null);

        return new HoldingListResult(
            Account: new AccountInfo(account.AccountNo, account.Nickname, account.Broker),
            Items: items,
            Summary: summary,
            QuoteError: quoteResult.TopLevelError);
    }

    /// <summary>
    /// Attempts to fetch a stock quote and use its name to refresh the local stock cache.
    /// </summary>
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

    /// <summary>
    /// Fetches quotes and refreshes locally cached stock names for successful rows.
    /// </summary>
    async Task<QuoteBatchResult<StockQuote>> EnrichStocksAsync(IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        QuoteBatchResult<StockQuote> result = await _quoteService.GetStockQuotesAsync(symbols.ToArray(), cancellationToken).ConfigureAwait(false);
        foreach ((string symbol, StockQuote? quote) in result.Quotes)
        {
            if (!string.IsNullOrWhiteSpace(quote?.Name))
                await _repository.UpsertStockAsync(symbol, quote.Name, "unknown", null, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// Converts whitespace-only strings to null and trims non-empty strings.
    /// </summary>
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

    /// <summary>
    /// Converts a repository account to the MCP-facing account shape.
    /// </summary>
    static AccountInfo AccountPayload(Account account) => new(account.AccountNo, account.Nickname, account.Broker);
}

