namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Coordinates local portfolio persistence with optional live quote enrichment, ambiguity policy,
/// and applied_to echoes for the MCP-facing tool layer.
/// </summary>
internal interface IPortfolioService
{
    // -------- Watchlist groups --------
    Task<IReadOnlyList<WatchlistGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default);
    Task<WatchlistGroup> CreateGroupAsync(string name, string? description, CancellationToken cancellationToken = default);
    Task<DeleteGroupResult> DeleteGroupAsync(string name, CancellationToken cancellationToken = default);
    Task<RenameGroupResult> RenameGroupAsync(string oldName, string newName, CancellationToken cancellationToken = default);

    // -------- Watchlist items --------
    Task<WatchlistItemAdded> AddWatchlistAsync(string symbol, string group, string? notes, CancellationToken cancellationToken = default);
    Task<RemoveResult> RemoveWatchlistAsync(string symbol, string group, CancellationToken cancellationToken = default);
    Task<WatchlistListResult> ListWatchlistAsync(string? group, CancellationToken cancellationToken = default);

    // -------- Themes --------
    Task<WatchedTheme> WatchThemeAsync(string themeCode, string? themeName, string? notes, CancellationToken cancellationToken = default);
    Task<RemoveResult> UnwatchThemeAsync(string themeCode, CancellationToken cancellationToken = default);
    Task<ThemeListResult> ListThemesAsync(CancellationToken cancellationToken = default);

    // -------- Accounts --------
    Task<IReadOnlyList<AccountSummary>> ListAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountInfo?> GetDefaultAccountAsync(CancellationToken cancellationToken = default);
    Task<AccountInfo> UpsertAccountAsync(string accountNumber, string nickname, string? broker, bool setDefault, CancellationToken cancellationToken = default);
    Task<RemoveAccountResult> RemoveAccountAsync(string accountIdentifier, bool confirm, CancellationToken cancellationToken = default);
    Task<AccountInfo> SetDefaultAccountAsync(string accountIdentifier, CancellationToken cancellationToken = default);
    Task<RenameBrokerResult> RenameBrokerAsync(string fromBroker, string toBroker, CancellationToken cancellationToken = default);

    // -------- Holdings --------
    Task<HoldingWriteResult> SetHoldingAsync(string symbol, int quantity, double avgPrice, string? notes, string? accountIdentifier, CancellationToken cancellationToken = default);
    Task<HoldingWriteResult> BuyHoldingAsync(string symbol, int quantity, double price, string? accountIdentifier, CancellationToken cancellationToken = default);
    Task<HoldingWriteResult> SellHoldingAsync(string symbol, int quantity, string? accountIdentifier, CancellationToken cancellationToken = default);
    Task<HoldingWriteResult?> RemoveHoldingAsync(string symbol, string? accountIdentifier, CancellationToken cancellationToken = default);
    Task<HoldingListResult> ListHoldingsAsync(string? accountIdentifier, CancellationToken cancellationToken = default);

    // -------- Corporate actions --------
    Task<CorporateActionResult> SplitHoldingAsync(string symbol, int ratio, string? accountIdentifier, CancellationToken cancellationToken = default);
    Task<CorporateActionResult> ReverseSplitHoldingAsync(string symbol, int ratio, string? accountIdentifier, CancellationToken cancellationToken = default);
    Task<CorporateActionResult> BonusHoldingAsync(string symbol, double ratio, string? accountIdentifier, CancellationToken cancellationToken = default);
}
