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
/// LS theme saved by the user for tracking. v0.5 mis-named these "sectors";
/// v0.6 split sector (KRX industry) and theme (LS curated tmcode) — this row
/// stores tmcode entries (e.g. 0012 반도체 장비, 0064 2차전지).
/// </summary>
internal sealed class WatchedTheme
{
    public long Id { get; init; }
    public string ThemeCode { get; init; } = "";
    public string ThemeName { get; init; } = "";
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

    /// <summary>
    /// Average price as fractional won (×<see cref="AvgPriceScale"/>). v0.7 schema v4 stores
    /// this as <c>INTEGER</c> so split↔reverse_split round-trips are exact and cost basis math
    /// stays drift-free. Materialised directly from the <c>avg_price</c> column.
    /// </summary>
    public long AvgPriceFr { get; init; }

    /// <summary>Multiplier converting won to fractional-won storage. 1 won = 10000 fr.</summary>
    public const long AvgPriceScale = 10_000;

    /// <summary>
    /// Average price in won (display form). Computed from <see cref="AvgPriceFr"/>; do not
    /// use for arithmetic that needs to be reversible. Internal math should stay in fr.
    /// </summary>
    public double AvgPrice => AvgPriceFr / (double)AvgPriceScale;

    public string? Notes { get; init; }
    public string UpdatedAt { get; init; } = "";

    /// <summary>Converts a won price (e.g. user input) to fractional-won storage.</summary>
    public static long ToFractionalWon(double won) =>
        (long)Math.Round(won * AvgPriceScale, MidpointRounding.AwayFromZero);
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
/// Compact LS theme quote. t1531 exposes average percent change but not an index value.
/// </summary>
internal sealed record ThemeQuote(double? IndexValue, double? Change, double ChangePct, string Timestamp);

/// <summary>
/// One row of the t1531 theme catalog cache — code and human-readable name only,
/// without quote data. Used for keyword resolution in ls_get_theme_stocks.
/// </summary>
internal sealed record ThemeCatalogRow(string Code, string Name);

/// <summary>Result wrapper returned by <c>IQuoteService.GetThemeCatalogAsync</c>.</summary>
internal sealed record ThemeCatalogResult(IReadOnlyList<ThemeCatalogRow> Rows, string? Error);

/// <summary>
/// stock_themes row — one LS theme membership a given stock belongs to.
/// Populated by fire-and-forget enrichment on portfolio writes.
/// </summary>
internal sealed class StockTheme
{
    public string Symbol { get; init; } = "";
    public string ThemeCode { get; init; } = "";
    public string ThemeName { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
}

/// <summary>
/// Result wrapper for the per-stock theme fetch (t1532). Distinct from
/// <see cref="ThemeCatalogResult"/> which is the market-wide t1531 catalog.
/// </summary>
internal sealed record StockThemesFetchResult(IReadOnlyList<ThemeCatalogRow> Themes, string? Error);

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
/// Watched LS theme plus optional t1531 quote enrichment.
/// </summary>
internal sealed record WatchedThemeWithQuote(string ThemeCode, string ThemeName, string? Note, string AddedAt, ThemeQuote? Quote);

/// <summary>
/// Watched LS theme list response.
/// </summary>
internal sealed record ThemeListResult(IReadOnlyList<WatchedThemeWithQuote> Items, string? QuoteError);

/// <summary>
/// Holding plus optional current quote and derived valuation fields.
/// </summary>
/// <remarks>
/// <c>Themes</c> + <c>ThemesStatus</c> ride the v0.6 fire-and-forget enrichment
/// path: when the cache has rows the response carries the array and omits the
/// status field; when it's empty the response sets <c>themes_status="pending"</c>
/// so the model can explain the in-flight metadata.
/// </remarks>
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
    string? Warning)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ThemeCatalogRow>? Themes { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ThemesStatus { get; init; }
}

/// <summary>
/// Top-level enrichment freshness block emitted by <c>ls_holdings_list</c>.
/// </summary>
internal sealed record MetadataFreshness(
    bool FullyEnriched,
    IReadOnlyDictionary<string, int> Pending)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Hint { get; init; }
}

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
    string? QuoteError)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MetadataFreshness? MetadataFreshness { get; init; }

    /// <summary>Echo of the active filter inputs (only present when at least one filter is applied).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public HoldingsFilterEcho? Filter { get; init; }

    /// <summary>Unique theme names matched by the active filter, for false-positive visibility on LIKE matches.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? MatchedThemes { get; init; }
}

/// <summary>Echo of the active <c>ls_holdings_list</c> filter parameters.</summary>
internal sealed record HoldingsFilterEcho
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ThemeCode { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ThemeKeyword { get; init; }
}

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
