namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Coordinates local portfolio persistence with optional quote enrichment.
/// </summary>
internal interface IPortfolioService
{
    /// <summary>Lists local watchlist groups.</summary>
    Task<IReadOnlyList<WatchlistGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a local watchlist group.</summary>
    Task<WatchlistGroup> CreateGroupAsync(string name, string? description, CancellationToken cancellationToken = default);

    /// <summary>Deletes a local watchlist group and returns the cascade count.</summary>
    Task<DeleteGroupResult> DeleteGroupAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Adds a stock to a local watchlist group.</summary>
    Task<WatchlistItemAdded> AddWatchlistAsync(string symbol, string group, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Removes a stock from a local watchlist group.</summary>
    Task<RemoveResult> RemoveWatchlistAsync(string symbol, string group, CancellationToken cancellationToken = default);

    /// <summary>Lists watchlist groups and items with optional quote enrichment.</summary>
    Task<WatchlistListResult> ListWatchlistAsync(string? group, CancellationToken cancellationToken = default);

    /// <summary>Adds or updates a watched sector/theme.</summary>
    Task<WatchedSector> WatchSectorAsync(string sectorCode, string? sectorName, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Removes a watched sector/theme.</summary>
    Task<RemoveResult> UnwatchSectorAsync(string sectorCode, CancellationToken cancellationToken = default);

    /// <summary>Lists watched sectors/themes with optional quote enrichment.</summary>
    Task<SectorListResult> ListSectorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the local default portfolio account.</summary>
    Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates the local default portfolio account.</summary>
    Task<AccountInfo> SetAccountAsync(string accountNo, string? nickname, CancellationToken cancellationToken = default);

    /// <summary>Adds or replaces a manually-entered holding.</summary>
    Task<HoldingAddedResult> AddHoldingAsync(string symbol, int quantity, double avgPrice, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Partially updates a manually-entered holding.</summary>
    Task<HoldingUpdatedResult> UpdateHoldingAsync(string symbol, int? quantity, double? avgPrice, string? notes, CancellationToken cancellationToken = default);

    /// <summary>Removes a manually-entered holding.</summary>
    Task<RemoveResult> RemoveHoldingAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>Lists manually-entered holdings with optional quote enrichment.</summary>
    Task<HoldingListResult> ListHoldingsAsync(CancellationToken cancellationToken = default);
}

