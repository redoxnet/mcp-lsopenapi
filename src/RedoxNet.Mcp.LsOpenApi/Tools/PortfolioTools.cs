using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using RedoxNet.Mcp.LsOpenApi.Portfolio;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// Exposes local portfolio, watchlist, and watched-sector MCP tools.
/// </summary>
[McpServerToolType]
internal static class PortfolioTools
{
    const string DefaultGroup = "default";

    /// <summary>
    /// Lists all saved watchlist groups.
    /// </summary>
    [McpServerTool(Name = "ls_watchlist_groups_list")]
    [Description("Lists user-defined watchlist groups, including item counts. Does not require LS credentials.")]
    public static async Task<string> WatchlistGroupsList(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListGroupsAsync(cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Creates or updates a saved watchlist group.
    /// </summary>
    [McpServerTool(Name = "ls_watchlist_group_create")]
    [Description("Creates or updates a watchlist group for saved personal stock lists. Does not require LS credentials.")]
    public static async Task<string> WatchlistGroupCreate(
        IPortfolioService portfolio,
        [Description("Group name, e.g. 'semiconductors'. Must be unique.")]
        string name,
        [Description("Optional group description.")]
        string? description = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.CreateGroupAsync(name, description, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Deletes a saved watchlist group and its items.
    /// </summary>
    [McpServerTool(Name = "ls_watchlist_group_delete")]
    [Description("Deletes a watchlist group and cascades its saved stock items. Does not require LS credentials.")]
    public static async Task<string> WatchlistGroupDelete(
        IPortfolioService portfolio,
        [Description("Group name to delete.")]
        string name,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.DeleteGroupAsync(name, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Adds a stock to a saved watchlist group.
    /// </summary>
    [McpServerTool(Name = "ls_watchlist_add")]
    [Description("""
        Adds a stock to the local saved watchlist. If group_name is omitted, uses default. If shcode is not in the local stocks cache, the service attempts a lazy LS t8407 metadata fetch; the item is still saved when credentials are unavailable.
        USE WHEN: the user wants to remember, track, 관심종목 추가, or save a stock for later monitoring.
        """)]
    public static async Task<string> WatchlistAdd(
        IPortfolioService portfolio,
        [Description("6-character Korean short code (usually 6 digits, some ETFs include an uppercase letter, e.g. '005930' or '0117V0').")]
        string shcode,
        [Description("Optional watchlist group_name. Defaults to 'default'.")]
        string group_name = DefaultGroup,
        [Description("Optional user note for this watchlist item.")]
        string? note = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.AddWatchlistAsync(shcode, group_name, note, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Removes a stock from a saved watchlist group.
    /// </summary>
    [McpServerTool(Name = "ls_watchlist_remove")]
    [Description("Removes shcode from a local saved watchlist group_name. Does not require LS credentials.")]
    public static async Task<string> WatchlistRemove(
        IPortfolioService portfolio,
        [Description("6-character Korean short code (usually 6 digits, some ETFs include an uppercase letter, e.g. '005930' or '0117V0').")]
        string shcode,
        [Description("Optional watchlist group_name. Defaults to 'default'.")]
        string group_name = DefaultGroup,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.RemoveWatchlistAsync(shcode, group_name, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Lists saved watchlist groups and enriches items with current quotes when possible.
    /// </summary>
    [McpServerTool(Name = "ls_watchlist_list")]
    [Description("""
        Lists local saved watchlist items, optionally filtered by group_name, and enriches each item with current price/change_pct using batch t8407 when LS credentials are available. Partial quote failures are allowed: quote may be null but saved items still return.
        USE WHEN: the user asks for 관심종목, watchlist, tracked stocks, saved stocks, or a saved group. AVOID WHEN: the user asks general market info unrelated to saved watchlists.
        """)]
    public static async Task<string> WatchlistList(
        IPortfolioService portfolio,
        [Description("Optional group name. When omitted, all groups are returned, including empty groups.")]
        string? group_name = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListWatchlistAsync(group_name, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Adds a sector or theme code to the watched-sector list.
    /// </summary>
    [McpServerTool(Name = "ls_watched_sectors_add")]
    [Description("Adds a theme/sector code to the local watched sectors list. t1531 theme codes are supported for quote enrich. Does not require LS credentials to save metadata.")]
    public static async Task<string> SectorWatch(
        IPortfolioService portfolio,
        [Description("Theme/sector code, e.g. a t1531 tmcode such as '0012'.")]
        string sector_code,
        [Description("Optional human-readable sector_name. Defaults to sector_code when omitted.")]
        string? sector_name = null,
        [Description("Optional user note for this sector/theme.")]
        string? note = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.WatchSectorAsync(sector_code, sector_name, note, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Removes a sector or theme code from the watched-sector list.
    /// </summary>
    [McpServerTool(Name = "ls_watched_sectors_remove")]
    [Description("Removes a sector/index code from the saved sector watch list. Does not require LS credentials.")]
    public static async Task<string> SectorUnwatch(
        IPortfolioService portfolio,
        [Description("Theme/sector code to remove.")]
        string sector_code,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.UnwatchSectorAsync(sector_code, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Lists watched sectors and enriches t1531 themes when possible.
    /// </summary>
    [McpServerTool(Name = "ls_watched_sectors_list")]
    [Description("Lists local watched sectors/themes and enriches t1531 theme codes with avgdiff as change_pct when LS credentials are available. Saved metadata still returns without credentials.")]
    public static async Task<string> SectorList(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListSectorsAsync(cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Gets the default local portfolio account metadata.
    /// </summary>
    [McpServerTool(Name = "ls_account_get")]
    [Description("Returns the default local portfolio account placeholder or configured account metadata. Does not require LS credentials.")]
    public static async Task<string> AccountGet(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.GetAccountAsync(cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Sets the default local portfolio account metadata.
    /// </summary>
    [McpServerTool(Name = "ls_account_set")]
    [Description("Sets the default local portfolio account number and optional nickname. The account number is stored locally only.")]
    public static async Task<string> AccountSet(
        IPortfolioService portfolio,
        [Description("Brokerage account number to store locally.")]
        string account_no,
        [Description("Optional nickname for the account.")]
        string? nickname = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.SetAccountAsync(account_no, nickname, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Adds or replaces a manually entered holding.
    /// </summary>
    [McpServerTool(Name = "ls_holdings_add")]
    [Description("""
        Adds or replaces a manually-entered holding in the local default account. Manual input only; broker account sync is Phase 2. If shcode is not in the local stocks cache, the service attempts a lazy LS t8407 metadata fetch; the holding is still saved when credentials are unavailable.
        USE WHEN: the user says they bought/own/hold a stock and wants it recorded in their portfolio.
        """)]
    public static async Task<string> HoldingAdd(
        IPortfolioService portfolio,
        [Description("6-character Korean short code (usually 6 digits, some ETFs include an uppercase letter, e.g. '005930' or '0117V0').")]
        string shcode,
        [Description("Held quantity. Must be zero or positive.")]
        int quantity,
        [Description("Average purchase price. Must be zero or positive.")]
        double avg_price,
        [Description("Optional user notes for this holding.")]
        string? note = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.AddHoldingAsync(shcode, quantity, avg_price, note, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Updates a manually entered holding.
    /// </summary>
    [McpServerTool(Name = "ls_holdings_update")]
    [Description("Partially updates a manually-entered holding in the default local account. Does not require LS credentials.")]
    public static async Task<string> HoldingUpdate(
        IPortfolioService portfolio,
        [Description("6-character Korean short code (usually 6 digits, some ETFs include an uppercase letter, e.g. '005930' or '0117V0').")]
        string shcode,
        [Description("Optional replacement quantity. Must be zero or positive when provided.")]
        int? quantity = null,
        [Description("Optional replacement average purchase price. Must be zero or positive when provided.")]
        double? avg_price = null,
        [Description("Optional replacement notes. Omitted notes leave existing notes unchanged.")]
        string? note = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.UpdateHoldingAsync(shcode, quantity, avg_price, note, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Removes a manually entered holding.
    /// </summary>
    [McpServerTool(Name = "ls_holdings_remove")]
    [Description("Removes a manually-entered holding from the default local account. Does not require LS credentials.")]
    public static async Task<string> HoldingRemove(
        IPortfolioService portfolio,
        [Description("6-character Korean short code (usually 6 digits, some ETFs include an uppercase letter, e.g. '005930' or '0117V0').")]
        string shcode,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.RemoveHoldingAsync(shcode, cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Lists manually entered holdings and enriches them with current valuation when possible.
    /// </summary>
    [McpServerTool(Name = "ls_holdings_list")]
    [Description("""
        Lists the user's locally registered holdings and enriches them with current price, current value, PnL, and PnL percent using batch t8407 when LS credentials are available. Metadata still returns without credentials.

        USE WHEN: the user refers to their own position or portfolio, including Korean phrases like "내가 가진", "내가 산", "보유 중", "들고 있는", "내 종목", "내 포트폴리오", or English phrases like "my holdings", "my position", "my portfolio". Before calling general stock tools such as ls_get_chart or ls_get_quote, call this tool first to check the user's registered holdings. If it returns empty, guide the user to register holdings or fall back to general analysis.
        AVOID WHEN: the user asks about a stock in general without personal ownership context, e.g. "삼성전자 어때?"; use market/quote/chart tools directly.
        """)]
    public static async Task<string> HoldingList(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListHoldingsAsync(cancellationToken)).ConfigureAwait(false);

    /// <summary>
    /// Serializes a portfolio tool result and converts expected persistence errors into MCP error envelopes.
    /// </summary>
    static async Task<string> SerializeAsync<T>(Func<Task<T>> action)
    {
        try
        {
            T result = await action().ConfigureAwait(false);
            return JsonSerializer.Serialize(result, McpJson.Tool);
        }
        catch (ArgumentException ex)
        {
            return McpJson.Error(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(ex.Message);
        }
        catch (SqliteException ex)
        {
            return McpJson.Error($"Portfolio database error: {ex.Message}");
        }
        catch (IOException ex)
        {
            return McpJson.Error($"Portfolio storage error: {ex.Message}");
        }
    }
}

