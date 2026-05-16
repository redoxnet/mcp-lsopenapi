using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    readonly ILogger<PortfolioService> _logger;

    // Per-process enrichment scheduler state. v0.6: fire-and-forget runs on
    // every holdings/watchlist write *and* on import + on holdings_list cache
    // misses. Without dedup that storms LS (t1532 rate_limit_per_sec=1) when
    // a 100-symbol import or repeated list calls land in quick succession.
    //
    // - _enrichInFlight: symbol currently being fetched. Skip duplicate fires.
    // - _enrichRecentlyDone: symbol fetched within EnrichCooldown. Skip until TTL.
    // Both keyed by upper-cased symbol so casing differences fold together.
    readonly ConcurrentDictionary<string, byte> _enrichInFlight = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, DateTimeOffset> _enrichRecentlyDone = new(StringComparer.Ordinal);
    static readonly TimeSpan EnrichCooldown = TimeSpan.FromSeconds(60);

    // Test-only: tracks tasks for deterministic await in unit tests. We append
    // on dispatch and prune on completion in the same finally block that
    // updates _enrichRecentlyDone, so the snapshot grows bounded by in-flight
    // count, not lifetime call count.
    readonly object _enrichTasksLock = new();
    readonly List<Task> _enrichTasks = new();

    public PortfolioService(
        IPortfolioRepository repository,
        IQuoteService quoteService,
        ILogger<PortfolioService>? logger = null)
    {
        _repository = repository;
        _quoteService = quoteService;
        _logger = logger ?? NullLogger<PortfolioService>.Instance;
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
        FireAndForgetEnrich(item.Symbol);
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
        FireAndForgetEnrich(saved.Symbol);
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
        FireAndForgetEnrich(saved.Symbol);
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

    public async Task<HoldingListResult> ListHoldingsAsync(
        string? accountIdentifier,
        string? themeCode = null,
        string? themeKeyword = null,
        CancellationToken cancellationToken = default)
    {
        string? normalizedThemeCode = string.IsNullOrWhiteSpace(themeCode) ? null : themeCode.Trim().ToUpperInvariant();
        string? normalizedThemeKeyword = string.IsNullOrWhiteSpace(themeKeyword) ? null : themeKeyword.Trim();
        bool hasFilter = normalizedThemeCode is not null || normalizedThemeKeyword is not null;

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

        HoldingsFilterEcho? filterEcho = hasFilter
            ? new HoldingsFilterEcho { ThemeCode = normalizedThemeCode, ThemeKeyword = normalizedThemeKeyword }
            : null;

        if (accounts.Count == 0)
            return new HoldingListResult(Array.Empty<AccountHoldings>(), EmptySummary(), null)
            {
                MetadataFreshness = BuildFreshness(themesPending: 0),
                Filter = filterEcho,
                MatchedThemes = hasFilter ? Array.Empty<string>() : null,
            };

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
        IReadOnlyDictionary<string, IReadOnlyList<StockTheme>> themesMap =
            await _repository.GetStockThemesBatchAsync(distinctSymbols, cancellationToken).ConfigureAwait(false);

        // Fix A: lazy enrichment dispatch on cache miss. Holdings registered
        // in prior sessions or imported via ls_portfolio_import never had a
        // write-path FireAndForgetEnrich fire — without this, the cache stays
        // empty forever for read-only users. Dedup + cooldown in
        // FireAndForgetEnrich prevent storming when the same list call repeats.
        foreach (string symbol in distinctSymbols)
        {
            if (!themesMap.ContainsKey(symbol))
                FireAndForgetEnrich(symbol);
        }

        // Apply theme filters at the holdings level. Per spec §4.6 multiple
        // filters AND-combine; matched_themes echoes the unique theme names
        // (across the filtered set) so LIKE false positives are visible.
        // Fix B: track filterCacheMissCount separately so metadata_freshness
        // doesn't lie about "fully_enriched=true" just because every holding
        // was filtered out for missing the cache.
        HashSet<string>? matchedThemes = hasFilter
            ? new HashSet<string>(StringComparer.Ordinal)
            : null;
        IReadOnlyList<Holding> filteredHoldings;
        int filterCacheMissCount = 0;
        if (hasFilter)
        {
            var keep = new List<Holding>(allHoldings.Count);
            foreach (Holding h in allHoldings)
            {
                if (!themesMap.TryGetValue(h.Symbol, out IReadOnlyList<StockTheme>? holdingThemes))
                {
                    filterCacheMissCount++;
                    continue; // un-enriched stocks can't satisfy a theme filter
                }

                bool codeOk = normalizedThemeCode is null
                    || holdingThemes.Any(t => string.Equals(t.ThemeCode, normalizedThemeCode, StringComparison.Ordinal));
                bool keywordOk = normalizedThemeKeyword is null
                    || holdingThemes.Any(t => t.ThemeName.Contains(normalizedThemeKeyword, StringComparison.Ordinal));
                if (!codeOk || !keywordOk)
                    continue;

                keep.Add(h);
                foreach (StockTheme t in holdingThemes)
                {
                    if (normalizedThemeCode is not null && string.Equals(t.ThemeCode, normalizedThemeCode, StringComparison.Ordinal))
                        matchedThemes!.Add(t.ThemeName);
                    if (normalizedThemeKeyword is not null && t.ThemeName.Contains(normalizedThemeKeyword, StringComparison.Ordinal))
                        matchedThemes!.Add(t.ThemeName);
                }
            }
            filteredHoldings = keep;
        }
        else
        {
            filteredHoldings = allHoldings;
        }

        var perAccount = new List<AccountHoldings>(accounts.Count);
        double totalCost = 0;
        double totalValue = 0;
        bool totalAllQuoted = true;
        int themesPending = 0;

        foreach (Account account in accounts)
        {
            List<Holding> accountRows = filteredHoldings.Where(h => h.AccountId == account.Id).ToList();
            (List<HoldingWithQuote> projected, double cost, double value, bool allQuoted, int accountThemesPending) =
                ProjectHoldings(accountRows, quoteResult, themesMap);
            var summary = BuildSummary(cost, value, allQuoted);
            totalCost += cost;
            if (allQuoted)
                totalValue += value;
            else
                totalAllQuoted = false;
            themesPending += accountThemesPending;

            perAccount.Add(new AccountHoldings(
                account.AccountNo,
                account.Nickname,
                account.Broker,
                account.IsDefault,
                projected,
                summary));
        }

        // Fix B: freshness reflects the *full* picture of pending enrichment,
        // not just what survived the filter. Without filterCacheMissCount,
        // a list call that drops every holding due to empty cache would
        // report fully_enriched=true (themesPending=0 over the empty filtered
        // set) — which contradicts the empty matched_themes that the model
        // sees right next to it.
        return new HoldingListResult(
            perAccount,
            BuildSummary(totalCost, totalValue, totalAllQuoted),
            quoteResult.TopLevelError)
        {
            MetadataFreshness = BuildFreshness(themesPending + filterCacheMissCount),
            Filter = filterEcho,
            MatchedThemes = matchedThemes is null ? null : matchedThemes.OrderBy(s => s, StringComparer.Ordinal).ToList(),
        };
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

    // -------- Portfolio I/O --------

    /// <summary>JSON schema version this build supports for portfolio export/import.</summary>
    internal const int SupportedExportSchemaVersion = 1;

    /// <inheritdoc />
    public async Task<PortfolioExportResult> ExportPortfolioAsync(string? path, CancellationToken cancellationToken = default)
    {
        string resolvedPath = string.IsNullOrWhiteSpace(path)
            ? ResolveDefaultExportPath("portfolio")
            : path.Trim();

        string? dir = System.IO.Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        PortfolioExportDto dto = await _repository.ExportSnapshotAsync(GetExporterVersion(), cancellationToken).ConfigureAwait(false);

        string json = System.Text.Json.JsonSerializer.Serialize(dto, McpJson.Tool);
        await File.WriteAllTextAsync(resolvedPath, json, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        long size = new FileInfo(resolvedPath).Length;

        var counts = new PortfolioIoCounts(
            Accounts: dto.Accounts.Count,
            Holdings: dto.Accounts.Sum(a => a.Holdings.Count),
            WatchlistGroups: dto.WatchlistGroups.Count,
            WatchlistItems: dto.WatchlistGroups.Sum(g => g.Items.Count),
            WatchedThemes: dto.WatchedThemes.Count);
        return new PortfolioExportResult(resolvedPath, dto.SchemaVersion, counts, size);
    }

    /// <inheritdoc />
    public async Task<PortfolioImportResult> ImportPortfolioAsync(string path, string mode, bool confirm, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new PortfolioValidationException("path must not be empty.");
        string sourcePath = path.Trim();
        if (!File.Exists(sourcePath))
            throw new PortfolioValidationException($"Import file not found: {sourcePath}");

        string normalizedMode = (mode ?? "merge").Trim().ToLowerInvariant();
        if (normalizedMode is "")
            normalizedMode = "merge";
        if (normalizedMode is not ("merge" or "replace"))
            throw new PortfolioValidationException($"mode must be 'merge' or 'replace', got '{mode}'.");

        string json = await File.ReadAllTextAsync(sourcePath, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        PortfolioExportDto dto;
        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<PortfolioExportDto>(json, McpJson.Tool)
                  ?? throw new PortfolioValidationException("Import file is empty or invalid JSON.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new PortfolioValidationException($"Failed to parse import file: {ex.Message}");
        }

        if (dto.SchemaVersion != SupportedExportSchemaVersion)
            throw new ImportSchemaMismatchException(dto.SchemaVersion, SupportedExportSchemaVersion);

        string? autoBackupPath = null;
        if (normalizedMode == "replace")
        {
            int accountsInFile = dto.Accounts.Count;
            int holdingsInFile = dto.Accounts.Sum(a => a.Holdings.Count);
            if (!confirm)
                throw new ImportReplaceRequiresConfirmationException(sourcePath, accountsInFile, holdingsInFile);

            // Snapshot the current state before we wipe — gives the user a
            // recovery path if they imported the wrong file. Lives in the
            // same exports/ dir with a 'before-import-' prefix.
            autoBackupPath = ResolveDefaultExportPath("before-import");
            string? backupDir = System.IO.Path.GetDirectoryName(autoBackupPath);
            if (!string.IsNullOrEmpty(backupDir))
                Directory.CreateDirectory(backupDir);
            PortfolioExportDto backupDto = await _repository.ExportSnapshotAsync(GetExporterVersion(), cancellationToken).ConfigureAwait(false);
            string backupJson = System.Text.Json.JsonSerializer.Serialize(backupDto, McpJson.Tool);
            await File.WriteAllTextAsync(autoBackupPath, backupJson, System.Text.Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

        ApplyImportResult applied = await _repository.ApplyImportAsync(dto, normalizedMode, cancellationToken).ConfigureAwait(false);

        // Fix A: kick off theme enrichment for every symbol the import touched.
        // Without this, a fresh DB populated only via import would have an
        // empty stock_themes cache forever — holdings_list theme filters
        // would return matched_themes=[] even though the live t1532 data
        // exists. Dedup + cooldown in FireAndForgetEnrich prevent storming
        // even when the file carries hundreds of symbols.
        foreach (string symbol in CollectImportSymbols(dto))
            FireAndForgetEnrich(symbol);

        return new PortfolioImportResult(
            Mode: normalizedMode,
            SourcePath: sourcePath,
            SchemaVersion: dto.SchemaVersion,
            Imported: applied.Imported,
            Skipped: applied.Skipped,
            AutoBackupPath: autoBackupPath);
    }

    /// <summary>
    /// Collects the distinct stock symbols touched by an import — both
    /// holdings rows and watchlist items. Used to dispatch theme enrichment
    /// in <see cref="ImportPortfolioAsync"/>.
    /// </summary>
    static IEnumerable<string> CollectImportSymbols(PortfolioExportDto dto)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AccountExportDto acc in dto.Accounts)
        {
            foreach (HoldingExportDto h in acc.Holdings)
            {
                if (string.IsNullOrWhiteSpace(h.Shcode)) continue;
                string key = h.Shcode.Trim().ToUpperInvariant();
                if (seen.Add(key)) yield return key;
            }
        }
        foreach (WatchlistGroupExportDto g in dto.WatchlistGroups)
        {
            foreach (WatchlistItemExportDto item in g.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Shcode)) continue;
                string key = item.Shcode.Trim().ToUpperInvariant();
                if (seen.Add(key)) yield return key;
            }
        }
    }

    /// <summary>
    /// Resolves a timestamped path under the platform's default exports
    /// directory (next to portfolio.db). Prefix lets callers distinguish
    /// user exports from before-import auto-backups.
    /// </summary>
    static string ResolveDefaultExportPath(string prefix)
    {
        string dbPath = SqlitePortfolioRepository.ResolveDatabasePath();
        string parentDir = System.IO.Path.GetDirectoryName(dbPath) ?? Environment.CurrentDirectory;
        string exportsDir = System.IO.Path.Combine(parentDir, "exports");
        string timestamp = DateTime.Now.ToString("yyyy-MM-ddTHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        return System.IO.Path.Combine(exportsDir, $"{prefix}-{timestamp}.json");
    }

    static string GetExporterVersion()
    {
        string? info = typeof(PortfolioService).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return "0.0.0";
        int plus = info.IndexOf('+');
        return plus > 0 ? info[..plus] : info;
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

    /// <inheritdoc />
    public async Task EnrichStockMetadataAsync(string symbol, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;
        string normalized = symbol.Trim().ToUpperInvariant();

        StockThemesFetchResult fetched;
        try
        {
            fetched = await _quoteService.GetStockThemesAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort. Leave cache as-is; the next write retries.
            // Logged so "왜 enrichment가 안 되지?" surfaces in stderr.
            _logger.LogWarning(ex,
                "Theme enrichment fetch threw for {Symbol}; leaving stock_themes cache as-is (next write retries).",
                normalized);
            return;
        }

        if (fetched.Error is not null)
        {
            // LS-side business error (e.g. missing credentials, unknown shcode).
            // Expected during offline runs — Debug is the right level.
            _logger.LogDebug(
                "Theme enrichment for {Symbol} skipped: {Error}",
                normalized, fetched.Error);
            return;
        }

        try
        {
            await _repository.ReplaceStockThemesAsync(normalized, fetched.Themes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Theme enrichment cache write failed for {Symbol} ({Count} themes).",
                normalized, fetched.Themes.Count);
        }
    }

    /// <summary>
    /// Posts <see cref="EnrichStockMetadataAsync"/> to the thread pool so the
    /// HTTP write response returns immediately. Per spec §7 the task is
    /// intentionally not awaited.
    /// </summary>
    /// <remarks>
    /// Two backstops prevent storming LS when the dispatch sites multiply
    /// (every write + import + every cache-missing list row):
    /// <list type="bullet">
    /// <item>In-flight dedup: a symbol already being fetched is not re-fired.</item>
    /// <item>60s cooldown: a symbol fetched recently is not re-fired until the
    /// TTL expires. Protects against repeated list calls when the cache
    /// stays empty (e.g. credentials missing or LS error).</item>
    /// </list>
    /// On stdio shutdown a half-finished enrichment retries on next session.
    /// </remarks>
    internal void FireAndForgetEnrich(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return;
        string key = symbol.Trim().ToUpperInvariant();

        if (_enrichInFlight.ContainsKey(key))
            return;
        if (_enrichRecentlyDone.TryGetValue(key, out DateTimeOffset last)
            && DateTimeOffset.UtcNow - last < EnrichCooldown)
            return;

        // TryAdd races with another caller — first writer wins, second skips.
        if (!_enrichInFlight.TryAdd(key, 0))
            return;

        Task task = Task.Run(async () =>
        {
            try
            {
                await EnrichStockMetadataAsync(key, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // EnrichStockMetadataAsync catches its own LS / repo errors,
                // so an exception escaping here is genuinely unexpected
                // (thread-pool failure, AppDomain shutdown mid-flight, etc.).
                // Without this explicit catch the exception lands on the
                // Task as UnobservedTaskException — silently swallowed on
                // finalization, which is exactly the "왜 enrichment가
                // 안 되지?" debug black hole we want to avoid.
                _logger.LogWarning(ex,
                    "Unexpected error in fire-and-forget theme enrichment for {Symbol}.",
                    key);
            }
            finally
            {
                // Record completion timestamp regardless of success — a failed
                // fetch should still cool down so we don't hammer LS retries.
                _enrichRecentlyDone[key] = DateTimeOffset.UtcNow;
                _enrichInFlight.TryRemove(key, out _);
            }
        });
        lock (_enrichTasksLock)
        {
            _enrichTasks.RemoveAll(t => t.IsCompleted);
            _enrichTasks.Add(task);
        }
    }

    /// <summary>
    /// Test-only deterministic await for fire-and-forget enrichment tasks
    /// dispatched since the last call. Production code never awaits — the
    /// stdio response returns before this completes. <c>InternalsVisibleTo</c>
    /// on the test project keeps this out of the public surface.
    /// </summary>
    internal Task WaitForPendingEnrichmentsAsync()
    {
        Task[] snapshot;
        lock (_enrichTasksLock)
            snapshot = _enrichTasks.ToArray();
        return Task.WhenAll(snapshot);
    }

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

    static (List<HoldingWithQuote> Projected, double CostBasis, double MarketValue, bool AllQuoted, int ThemesPending) ProjectHoldings(
        IReadOnlyList<Holding> accountRows,
        QuoteBatchResult<StockQuote> quoteResult,
        IReadOnlyDictionary<string, IReadOnlyList<StockTheme>> themesMap)
    {
        var projected = new List<HoldingWithQuote>(accountRows.Count);
        double cost = 0;
        double value = 0;
        bool allQuoted = true;
        int themesPending = 0;

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

            // Theme cache projection: rows present → "ok" (status omitted in
            // envelope to save tokens); no rows → "pending" so the model can
            // surface the freshness hint. A truly themeless stock will look
            // pending until next-write retry — harmless false positive.
            IReadOnlyList<ThemeCatalogRow>? themes = null;
            string? themesStatus = null;
            if (themesMap.TryGetValue(holding.Symbol, out IReadOnlyList<StockTheme>? cached) && cached.Count > 0)
            {
                themes = cached.Select(t => new ThemeCatalogRow(t.ThemeCode, t.ThemeName)).ToList();
            }
            else
            {
                themesStatus = "pending";
                themesPending++;
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
                warning)
            {
                Themes = themes,
                ThemesStatus = themesStatus,
            });
        }

        return (projected, cost, value, allQuoted, themesPending);
    }

    static MetadataFreshness? BuildFreshness(int themesPending)
    {
        if (themesPending == 0)
            return new MetadataFreshness(FullyEnriched: true, Pending: new Dictionary<string, int>(0));
        return new MetadataFreshness(
            FullyEnriched: false,
            Pending: new Dictionary<string, int> { ["themes"] = themesPending })
        {
            Hint = "방금 등록한 종목의 테마 정보는 다음 호출에서 채워집니다.",
        };
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
