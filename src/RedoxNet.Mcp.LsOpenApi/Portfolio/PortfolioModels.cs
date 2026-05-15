namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Summary row for a user-defined watchlist group.
/// </summary>
internal sealed class WatchlistGroupSummary
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public int SortOrder { get; init; }
    public int ItemCount { get; init; }
}

/// <summary>
/// User-defined watchlist group metadata.
/// </summary>
internal sealed class WatchlistGroup
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public int SortOrder { get; init; }
    public string CreatedAt { get; init; } = "";
}

/// <summary>
/// Stock saved in a user watchlist group.
/// </summary>
internal sealed class WatchlistItem
{
    public long Id { get; init; }
    public long GroupId { get; init; }
    public string GroupName { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Name { get; init; } = "";
    public string Market { get; init; } = "";
    public string? KrxSector { get; init; }
    public string? Notes { get; init; }
    public string AddedAt { get; init; } = "";
}

/// <summary>
/// Theme or sector saved by the user for tracking.
/// </summary>
internal sealed class WatchedSector
{
    public long Id { get; init; }
    public string SectorCode { get; init; } = "";
    public string SectorName { get; init; } = "";
    public string? Notes { get; init; }
    public string AddedAt { get; init; } = "";
}

/// <summary>
/// Local portfolio account metadata.
/// </summary>
internal sealed class Account
{
    public long Id { get; init; }
    public string AccountNo { get; init; } = "";
    public string Nickname { get; init; } = "";
    public string Broker { get; init; } = "";
    public bool IsDefault { get; init; }
    public string CreatedAt { get; init; } = "";
}

/// <summary>
/// Manually-entered holding in a local portfolio account.
/// </summary>
internal sealed class Holding
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public string Symbol { get; init; } = "";
    public string Name { get; init; } = "";
    public string Market { get; init; } = "";
    public int Quantity { get; init; }
    public double AvgPrice { get; init; }
    public string? Notes { get; init; }
    public string UpdatedAt { get; init; } = "";
}

/// <summary>
/// Compact current stock quote used to enrich portfolio responses.
/// </summary>
internal sealed record StockQuote(
    long Price,
    long Change,
    double ChangePct,
    long Open,
    long High,
    long Low,
    long Volume,
    string Timestamp)
{
    /// <summary>
    /// Optional display name returned by the quote TR and used to refresh the local stock cache.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Name { get; init; }
}

/// <summary>
/// Compact sector/theme quote. t1531 exposes average percent change but not an index value.
/// </summary>
internal sealed record SectorQuote(double? IndexValue, double? Change, double ChangePct, string Timestamp);

/// <summary>
/// Result envelope for batch quote calls with per-key quote values and an optional top-level failure message.
/// </summary>
internal sealed record QuoteBatchResult<T>(IReadOnlyDictionary<string, T?> Quotes, string? TopLevelError);

/// <summary>
/// Result of deleting a watchlist group.
/// </summary>
internal sealed record DeleteGroupResult(bool Deleted, int CascadedItems);

/// <summary>
/// Generic remove result used by portfolio mutation tools.
/// </summary>
internal sealed record RemoveResult(bool Removed);

/// <summary>
/// Account metadata returned to MCP hosts. Used inside <c>applied_to</c> echoes and error envelope candidates.
/// </summary>
internal sealed record AccountInfo(string AccountNumber, string Nickname, string Broker, bool IsDefault);

/// <summary>
/// Per-account summary row for the accounts_list response. Class form so Dapper can
/// materialize from a SELECT with a COUNT(*) subquery (positional records fail when
/// Dapper cannot find a constructor that matches the inferred column types).
/// </summary>
internal sealed class AccountSummary
{
    public string AccountNumber { get; init; } = "";
    public string Nickname { get; init; } = "";
    public string Broker { get; init; } = "";
    public bool IsDefault { get; init; }
    public int HoldingsCount { get; init; }
}

/// <summary>
/// Lightweight watchlist add/update result without a quote field.
/// </summary>
internal sealed record WatchlistItemAdded(string Shcode, string Name, string GroupName, string? Note, string AddedAt);

/// <summary>
/// Watchlist item plus optional current quote for list responses.
/// </summary>
internal sealed record WatchlistItemWithQuote(string Shcode, string Name, string GroupName, string? Note, string AddedAt, StockQuote? Quote);

/// <summary>
/// Watchlist group and its current items.
/// </summary>
internal sealed record WatchlistGroupItems(string Name, string? Description, int SortOrder, IReadOnlyList<WatchlistItemWithQuote> Items);

/// <summary>
/// Watchlist list response. Grouped shape is used for both all-groups and single-group queries.
/// </summary>
internal sealed record WatchlistListResult(string? GroupName, IReadOnlyList<WatchlistGroupItems> Groups, string? QuoteError);

/// <summary>
/// Result of renaming a watchlist group.
/// </summary>
internal sealed record RenameGroupResult(string OldName, string NewName);

/// <summary>
/// Watched sector/theme plus optional t1531 quote enrichment.
/// </summary>
internal sealed record WatchedSectorWithQuote(string SectorCode, string SectorName, string? Note, string AddedAt, SectorQuote? Quote);

/// <summary>
/// Watched sector/theme list response.
/// </summary>
internal sealed record SectorListResult(IReadOnlyList<WatchedSectorWithQuote> Items, string? QuoteError);

/// <summary>
/// Holding plus optional current quote and derived valuation fields.
/// </summary>
internal sealed record HoldingWithQuote(
    string Shcode,
    string? Name,
    int Quantity,
    double AvgPrice,
    string? Note,
    StockQuote? Quote,
    double? MarketValue,
    double? CostBasis,
    double? Pnl,
    double? PnlPct,
    string? Warning);

/// <summary>
/// Portfolio valuation summary used both per-account and at the total roll-up.
/// </summary>
internal sealed record PortfolioSummary(double CostBasis, double? MarketValue, double? Pnl, double? PnlPct);

/// <summary>
/// Result of a single-account write echo (set/buy/sell/remove).
/// </summary>
internal sealed record HoldingWriteResult(
    string Shcode,
    string? Name,
    int Quantity,
    double AvgPrice,
    AccountInfo AppliedTo);

/// <summary>
/// Before/after snapshot for one account inside a corporate action result.
/// </summary>
internal sealed record CorporateActionAccountResult(
    AccountInfo Account,
    HoldingSnapshot Before,
    HoldingSnapshot After);

/// <summary>
/// Holding state at a point in time.
/// </summary>
internal sealed record HoldingSnapshot(int Quantity, double AvgPrice);

/// <summary>
/// Result of a corporate action (split/reverse_split/bonus). Lists every account that was touched.
/// </summary>
internal sealed record CorporateActionResult(
    string Shcode,
    string Action,
    double Ratio,
    IReadOnlyList<CorporateActionAccountResult> AppliedTo);

/// <summary>
/// Per-account holdings group inside the holdings_list response.
/// </summary>
internal sealed record AccountHoldings(
    string AccountNumber,
    string Nickname,
    string Broker,
    bool IsDefault,
    IReadOnlyList<HoldingWithQuote> Holdings,
    PortfolioSummary Summary);

/// <summary>
/// Holdings list response. Always grouped — single-account responses have <c>accounts</c> of length 1.
/// </summary>
internal sealed record HoldingListResult(
    IReadOnlyList<AccountHoldings> Accounts,
    PortfolioSummary TotalSummary,
    string? QuoteError);

/// <summary>
/// Result of removing an account. Reports whether holdings were cascaded and who inherited default, if anyone.
/// </summary>
internal sealed record RemoveAccountResult(
    bool Removed,
    int CascadedHoldings,
    AccountInfo? NewDefault);

/// <summary>
/// Result of renaming a broker across all matching accounts.
/// </summary>
internal sealed record RenameBrokerResult(string From, string To, int AccountsAffected);
