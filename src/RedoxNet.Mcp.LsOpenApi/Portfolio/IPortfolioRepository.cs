namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Repository abstraction for local portfolio persistence.
/// </summary>
internal interface IPortfolioRepository
{
    /// <summary>Applies pending migrations and seed data if needed.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    // -------- Watchlist groups --------

    /// <summary>Lists watchlist groups with item counts.</summary>
    Task<IReadOnlyList<WatchlistGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a watchlist group.</summary>
    Task<WatchlistGroup> CreateGroupAsync(string name, string? description, CancellationToken cancellationToken = default);

    /// <summary>Deletes a watchlist group and returns whether it existed plus the number of cascaded items.</summary>
    Task<DeleteGroupResult> DeleteGroupAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Renames a watchlist group. Throws if the new name collides with an existing group.</summary>
    Task<RenameGroupResult> RenameGroupAsync(string oldName, string newName, CancellationToken cancellationToken = default);

    // -------- Watchlist items --------

    /// <summary>Adds or updates a watchlist item.</summary>
    Task<WatchlistItem> AddWatchlistItemAsync(string symbol, string group, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Removes a watchlist item.</summary>
    Task<bool> RemoveWatchlistItemAsync(string symbol, string group, CancellationToken cancellationToken = default);

    /// <summary>Lists watchlist items, optionally filtered by group.</summary>
    Task<IReadOnlyList<WatchlistItem>> ListWatchlistAsync(string? group, CancellationToken cancellationToken = default);

    // -------- Themes --------

    /// <summary>Adds or updates a watched LS theme (tmcode).</summary>
    Task<WatchedTheme> WatchThemeAsync(string code, string? name, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Removes a watched LS theme.</summary>
    Task<bool> UnwatchThemeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Lists watched LS themes.</summary>
    Task<IReadOnlyList<WatchedTheme>> ListThemesAsync(CancellationToken cancellationToken = default);

    // -------- Accounts --------

    /// <summary>Lists all accounts with their holdings counts. Empty when no accounts exist.</summary>
    Task<IReadOnlyList<AccountSummary>> ListAccountSummariesAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists all accounts as raw rows. Used by the service for ambiguity resolution.</summary>
    Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the default account if one exists; null when no accounts are registered.</summary>
    Task<Account?> GetDefaultAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>Resolves an account by account_number first, falling back to nickname. Returns null when neither matches.</summary>
    Task<Account?> GetAccountByIdentifierAsync(string identifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates an account keyed by <paramref name="accountNumber"/>. When
    /// <paramref name="setDefault"/> is true the row becomes the sole default; otherwise the
    /// previous default is preserved, and if no default existed the upserted account becomes one.
    /// </summary>
    Task<Account> UpsertAccountAsync(string accountNumber, string nickname, string? broker, bool setDefault, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an account, cascading holdings only when <paramref name="confirm"/> is true if any
    /// holdings exist. Returns the count of cascaded holdings and the newly promoted default account
    /// (id ASC succession) when applicable.
    /// </summary>
    Task<RemoveAccountResult> RemoveAccountAsync(Account account, bool confirm, CancellationToken cancellationToken = default);

    /// <summary>Promotes the given account to default within a transaction.</summary>
    Task<Account> SetDefaultAccountAsync(Account account, CancellationToken cancellationToken = default);

    /// <summary>Renames a broker label across all matching accounts.</summary>
    Task<RenameBrokerResult> RenameBrokerAsync(string fromBroker, string toBroker, CancellationToken cancellationToken = default);

    /// <summary>Returns every account currently holding the given symbol.</summary>
    Task<IReadOnlyList<Account>> FindAccountsHoldingAsync(string symbol, CancellationToken cancellationToken = default);

    // -------- Holdings --------

    /// <summary>Replaces a holding with the supplied state (upsert with no cost-basis merge).</summary>
    Task<Holding> SetHoldingAsync(long accountId, string symbol, int quantity, double avgPrice, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Records an incremental buy with weighted-average cost basis merge.</summary>
    Task<Holding> BuyHoldingAsync(long accountId, string symbol, int quantity, double price, CancellationToken cancellationToken = default);

    /// <summary>
    /// Subtracts <paramref name="quantity"/> from the existing holding. When the new quantity is
    /// zero the row is removed. Throws when no row exists or when quantity exceeds the current
    /// position; the service catches and translates these into typed errors.
    /// </summary>
    Task<Holding?> SellHoldingAsync(long accountId, string symbol, int quantity, CancellationToken cancellationToken = default);

    /// <summary>Gets a holding by account and symbol.</summary>
    Task<Holding?> GetHoldingAsync(long accountId, string symbol, CancellationToken cancellationToken = default);

    /// <summary>Removes a holding by account and symbol.</summary>
    Task<bool> RemoveHoldingAsync(long accountId, string symbol, CancellationToken cancellationToken = default);

    /// <summary>Lists holdings for one account.</summary>
    Task<IReadOnlyList<Holding>> ListHoldingsAsync(long accountId, CancellationToken cancellationToken = default);

    /// <summary>Lists holdings across every account.</summary>
    Task<IReadOnlyList<Holding>> ListAllHoldingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a corporate action multiplier to a single holding. Quantity is multiplied by
    /// <paramref name="qtyMultiplier"/> and average price by <paramref name="priceMultiplier"/>.
    /// Caller is responsible for divisibility checks on integer share counts.
    /// </summary>
    Task<Holding?> ApplyCorporateActionAsync(long accountId, string symbol, double qtyMultiplier, double priceMultiplier, CancellationToken cancellationToken = default);

    // -------- Stock metadata cache --------

    /// <summary>Creates or updates locally cached stock metadata.</summary>
    Task UpsertStockAsync(string symbol, string name, string market, string? krxSector, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically replaces the stock_themes rows for one symbol with the supplied list,
    /// keeping the cache in sync with the latest t1532 fetch (memberships LS removed
    /// disappear from cache rather than lingering as stale rows).
    /// </summary>
    Task ReplaceStockThemesAsync(string symbol, IReadOnlyList<ThemeCatalogRow> themes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the cached stock_themes rows for many symbols at once. Symbols with
    /// no cache rows are simply absent from the returned dictionary (callers treat
    /// missing as "pending" enrichment).
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<StockTheme>>> GetStockThemesBatchAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default);
}
