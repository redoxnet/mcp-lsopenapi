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
/// Account metadata returned to MCP hosts.
/// </summary>
internal sealed record AccountInfo(string AccountNo, string Nickname, string Broker);

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
internal sealed record HoldingWithQuote(string Shcode, string? Name, int Quantity, double AvgPrice, string? Note, StockQuote? Quote, double? CurrentValue, double? Pnl, double? PnlPct);

/// <summary>
/// Portfolio valuation summary derived from saved holdings and current quotes.
/// </summary>
internal sealed record PortfolioSummary(double TotalCost, double? TotalValue, double? TotalPnl, double? TotalPnlPct);

/// <summary>
/// Holding add result.
/// </summary>
internal sealed record HoldingAddedResult(string Shcode, string Name, int Quantity, double AvgPrice);

/// <summary>
/// Holding update result.
/// </summary>
internal sealed record HoldingUpdatedResult(string Shcode, int Quantity, double AvgPrice);

/// <summary>
/// Holdings list response for the local default account.
/// </summary>
internal sealed record HoldingListResult(AccountInfo Account, IReadOnlyList<HoldingWithQuote> Items, PortfolioSummary Summary, string? QuoteError);

