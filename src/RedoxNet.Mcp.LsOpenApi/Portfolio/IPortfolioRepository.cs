namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Repository abstraction for local portfolio persistence.
/// </summary>
internal interface IPortfolioRepository
{
    /// <summary>Applies pending migrations and seed data if needed.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists watchlist groups with item counts.</summary>
    Task<IReadOnlyList<WatchlistGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a watchlist group.</summary>
    Task<WatchlistGroup> CreateGroupAsync(string name, string? description, CancellationToken cancellationToken = default);

    /// <summary>Deletes a watchlist group and returns whether it existed plus the number of cascaded items.</summary>
    Task<DeleteGroupResult> DeleteGroupAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Adds or updates a watchlist item.</summary>
    Task<WatchlistItem> AddWatchlistItemAsync(string symbol, string group, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Removes a watchlist item.</summary>
    Task<bool> RemoveWatchlistItemAsync(string symbol, string group, CancellationToken cancellationToken = default);

    /// <summary>Lists watchlist items, optionally filtered by group.</summary>
    Task<IReadOnlyList<WatchlistItem>> ListWatchlistAsync(string? group, CancellationToken cancellationToken = default);

    /// <summary>Adds or updates a watched sector/theme.</summary>
    Task<WatchedSector> WatchSectorAsync(string code, string? name, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Removes a watched sector/theme.</summary>
    Task<bool> UnwatchSectorAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Lists watched sectors/themes.</summary>
    Task<IReadOnlyList<WatchedSector>> ListSectorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the local default account.</summary>
    Task<Account> GetDefaultAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates the local default account.</summary>
    Task<Account> SetDefaultAccountAsync(string accountNo, string? nickname, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces a holding.</summary>
    Task<Holding> UpsertHoldingAsync(long accountId, string symbol, int quantity, double avgPrice, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Gets a holding by account and symbol.</summary>
    Task<Holding?> GetHoldingAsync(long accountId, string symbol, CancellationToken cancellationToken = default);

    /// <summary>Removes a holding by account and symbol.</summary>
    Task<bool> RemoveHoldingAsync(long accountId, string symbol, CancellationToken cancellationToken = default);

    /// <summary>Lists holdings for an account.</summary>
    Task<IReadOnlyList<Holding>> ListHoldingsAsync(long accountId, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates locally cached stock metadata.</summary>
    Task UpsertStockAsync(string symbol, string name, string market, string? krxSector, CancellationToken cancellationToken = default);
}

