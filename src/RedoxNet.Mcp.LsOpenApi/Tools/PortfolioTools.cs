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

    // ---------------------- Watchlist groups ----------------------

    [McpServerTool(Name = "ls_watchlist_groups_list")]
    [Description("Lists user-defined watchlist groups with item counts. Does not require LS credentials.")]
    public static async Task<string> WatchlistGroupsList(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListGroupsAsync(cancellationToken)).ConfigureAwait(false);

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

    [McpServerTool(Name = "ls_watchlist_group_delete")]
    [Description("Deletes a watchlist group and cascades its saved stock items. Does not require LS credentials.")]
    public static async Task<string> WatchlistGroupDelete(
        IPortfolioService portfolio,
        [Description("Group name to delete.")]
        string name,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.DeleteGroupAsync(name, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_watchlist_group_rename")]
    [Description("Renames a watchlist group. Fails if the new name already exists.")]
    public static async Task<string> WatchlistGroupRename(
        IPortfolioService portfolio,
        [Description("Existing group name.")]
        string old_name,
        [Description("New group name. Must be unique.")]
        string new_name,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.RenameGroupAsync(old_name, new_name, cancellationToken)).ConfigureAwait(false);

    // ---------------------- Watchlist items ----------------------

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

    // ---------------------- Sectors ----------------------

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

    [McpServerTool(Name = "ls_watched_sectors_remove")]
    [Description("Removes a sector/index code from the saved sector watch list. Does not require LS credentials.")]
    public static async Task<string> SectorUnwatch(
        IPortfolioService portfolio,
        [Description("Theme/sector code to remove.")]
        string sector_code,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.UnwatchSectorAsync(sector_code, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_watched_sectors_list")]
    [Description("Lists local watched sectors/themes and enriches t1531 theme codes with avgdiff as change_pct when LS credentials are available. Saved metadata still returns without credentials.")]
    public static async Task<string> SectorList(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListSectorsAsync(cancellationToken)).ConfigureAwait(false);

    // ---------------------- Accounts ----------------------

    [McpServerTool(Name = "ls_accounts_list")]
    [Description("Lists all registered local portfolio accounts with their holdings counts and the default flag. Returns an empty array when no accounts are registered.")]
    public static async Task<string> AccountsList(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListAccountsAsync(cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_account_get")]
    [Description("Returns the default local portfolio account, or null when no accounts are registered. Does not require LS credentials.")]
    public static async Task<string> AccountGet(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.GetDefaultAccountAsync(cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_account_upsert")]
    [Description("""
        Creates or updates a local portfolio account by account_number. When set_default is true the account becomes the sole default; otherwise the previous default is preserved (and if none existed, the upserted account is auto-promoted to default).
        USE WHEN: the user wants to register their brokerage account locally — "내 한투 계좌 등록해줘. 번호 X 닉네임 한투" or adding a second account.
        """)]
    public static async Task<string> AccountUpsert(
        IPortfolioService portfolio,
        [Description("Brokerage account number to store locally. Primary identity; unique.")]
        string account_number,
        [Description("Human-readable nickname (e.g. '한투', '주식1', 'ISA'). Must be unique across accounts.")]
        string nickname,
        [Description("Free-text broker label (e.g. '한국투자', 'KB증권'). Defaults to 'LS'.")]
        string? broker = null,
        [Description("If true, promote this account to default. If false and no default exists, the account is promoted anyway to maintain the >=1 default invariant.")]
        bool set_default = false,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.UpsertAccountAsync(account_number, nickname, broker, set_default, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_account_remove")]
    [Description("""
        Removes a local portfolio account. If the account owns holdings, returns RequiresConfirmation with the count/value preview unless confirm=true is passed. When removing the default account and others remain, the oldest (id ASC) is auto-promoted.
        """)]
    public static async Task<string> AccountRemove(
        IPortfolioService portfolio,
        [Description("Account identifier: account_number or nickname.")]
        string account,
        [Description("Must be true to cascade-delete holdings owned by the account.")]
        bool confirm = false,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.RemoveAccountAsync(account, confirm, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_account_set_default")]
    [Description("Promotes the target account to default. Exactly one account is the default whenever >= 1 account exists.")]
    public static async Task<string> AccountSetDefault(
        IPortfolioService portfolio,
        [Description("Account identifier: account_number or nickname.")]
        string account,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.SetDefaultAccountAsync(account, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_broker_rename")]
    [Description("Renames a broker label across every account currently using it. Free-text label; no validation against an external broker list.")]
    public static async Task<string> BrokerRename(
        IPortfolioService portfolio,
        [Description("Existing broker label to replace (e.g. '한투').")]
        string from,
        [Description("Replacement broker label (e.g. '한국투자증권').")]
        string to,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.RenameBrokerAsync(from, to, cancellationToken)).ConfigureAwait(false);

    // ---------------------- Holdings ----------------------

    [McpServerTool(Name = "ls_holdings_set")]
    [Description("""
        Replaces a holding with the supplied state. USE FOR "현재 보유 수량은 N주, 평단 M원이야" or initial registration.
        AVOID WHEN: the user said "추가로 N주 더 샀어" — that's ls_holdings_buy and merges weighted average.
        quantity must be positive; to delete a holding call ls_holdings_remove.
        """)]
    public static async Task<string> HoldingSet(
        IPortfolioService portfolio,
        [Description("6-character Korean short code (usually 6 digits, some ETFs include an uppercase letter, e.g. '005930' or '0117V0').")]
        string shcode,
        [Description("Held quantity. Must be positive (>= 1).")]
        int quantity,
        [Description("Average purchase price. Must be zero or positive.")]
        double avg_price,
        [Description("Optional user notes for this holding.")]
        string? note = null,
        [Description("Optional account identifier (account_number or nickname). When omitted: auto if exactly 1 account exists, error otherwise.")]
        string? account = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.SetHoldingAsync(shcode, quantity, avg_price, note, account, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_holdings_buy")]
    [Description("""
        Records an incremental buy. The tool merges the new lot with any existing position using weighted average cost basis.
        USE FOR "삼성전자 N주 더 샀어. 단가 P원" or recording additional purchases.
        """)]
    public static async Task<string> HoldingBuy(
        IPortfolioService portfolio,
        [Description("6-character Korean short code (usually 6 digits, some ETFs include an uppercase letter, e.g. '005930' or '0117V0').")]
        string shcode,
        [Description("Number of shares bought in this transaction.")]
        int quantity,
        [Description("Price per share for this transaction.")]
        double price,
        [Description("Optional account identifier (account_number or nickname).")]
        string? account = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.BuyHoldingAsync(shcode, quantity, price, account, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_holdings_sell")]
    [Description("""
        Records a sell. Quantity is subtracted; the holding row is auto-removed when the remaining quantity reaches zero. Returns InsufficientQuantity when quantity exceeds the current position.
        """)]
    public static async Task<string> HoldingSell(
        IPortfolioService portfolio,
        [Description("6-character Korean short code (usually 6 digits, some ETFs include an uppercase letter, e.g. '005930' or '0117V0').")]
        string shcode,
        [Description("Number of shares sold. Must be positive and no greater than the current holding quantity.")]
        int quantity,
        [Description("Optional account identifier. Auto when the symbol is held in exactly one account; AmbiguousAccount otherwise.")]
        string? account = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.SellHoldingAsync(shcode, quantity, account, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_holdings_remove")]
    [Description("Removes a holding row outright (regardless of quantity). When the symbol is not held in any account, returns removed=false without raising.")]
    public static async Task<string> HoldingRemove(
        IPortfolioService portfolio,
        [Description("6-character Korean short code (usually 6 digits, some ETFs include an uppercase letter, e.g. '005930' or '0117V0').")]
        string shcode,
        [Description("Optional account identifier. Auto when the symbol is held in exactly one account; AmbiguousAccount otherwise.")]
        string? account = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(async () =>
        {
            HoldingWriteResult? removed = await portfolio.RemoveHoldingAsync(shcode, account, cancellationToken).ConfigureAwait(false);
            return removed is null
                ? (object)new { removed = false }
                : new { removed = true, shcode = removed.Shcode, applied_to = removed.AppliedTo };
        }).ConfigureAwait(false);

    [McpServerTool(Name = "ls_holdings_list")]
    [Description("""
        Lists the user's locally registered holdings grouped by account, enriches them with current price, market_value, PnL, and PnL percent using batch t8407 when LS credentials are available, and computes a per-account summary plus a total_summary across all accounts.

        USE WHEN: the user refers to their own position or portfolio, including Korean phrases like "내가 가진", "내가 산", "보유 중", "들고 있는", "내 종목", "내 포트폴리오", or English phrases like "my holdings", "my position", "my portfolio". Before calling general stock tools such as ls_get_chart or ls_get_quote, call this tool first to check the user's registered holdings. If it returns empty, guide the user to register holdings or fall back to general analysis.
        AVOID WHEN: the user asks about a stock in general without personal ownership context, e.g. "삼성전자 어때?"; use market/quote/chart tools directly.
        """)]
    public static async Task<string> HoldingList(
        IPortfolioService portfolio,
        [Description("Optional account identifier (account_number or nickname). When omitted, holdings across all accounts are returned grouped.")]
        string? account = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListHoldingsAsync(account, cancellationToken)).ConfigureAwait(false);

    // ---------------------- Corporate actions ----------------------

    [McpServerTool(Name = "ls_holdings_split")]
    [Description("""
        Applies a stock split (액면분할). Quantity is multiplied by ratio and average price divided by ratio, preserving cost basis.
        Account omitted → applied to every account holding the symbol (single corporate event affects all owners).
        USE FOR "삼성전자 10:1 분할했대" with ratio=10.
        """)]
    public static async Task<string> HoldingSplit(
        IPortfolioService portfolio,
        [Description("6-character Korean short code.")]
        string shcode,
        [Description("Split ratio. e.g. 50 for 1:50 split.")]
        int ratio,
        [Description("Optional account identifier. Omit to apply across every account holding the symbol.")]
        string? account = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.SplitHoldingAsync(shcode, ratio, account, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_holdings_reverse_split")]
    [Description("""
        Applies a reverse stock split (액면병합). Quantity is divided by ratio and average price multiplied by ratio. ValidationError when the existing quantity is not divisible by ratio (fractional shares are not supported).
        Account omitted → applied to every account holding the symbol.
        USE FOR "삼성전자 10:1 액면병합" with ratio=10.
        """)]
    public static async Task<string> HoldingReverseSplit(
        IPortfolioService portfolio,
        [Description("6-character Korean short code.")]
        string shcode,
        [Description("Reverse-split ratio. e.g. 10 for 10:1 reverse split.")]
        int ratio,
        [Description("Optional account identifier. Omit to apply across every account holding the symbol.")]
        string? account = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ReverseSplitHoldingAsync(shcode, ratio, account, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_holdings_bonus")]
    [Description("""
        Applies a bonus issue (무상증자). Quantity is multiplied by (1 + ratio) and average price divided by the same factor. Account omitted → applied to every account holding the symbol.
        USE FOR "무상증자 1주당 0.1주" with ratio=0.1.
        """)]
    public static async Task<string> HoldingBonus(
        IPortfolioService portfolio,
        [Description("6-character Korean short code.")]
        string shcode,
        [Description("Bonus ratio. e.g. 0.1 for 10% bonus.")]
        double ratio,
        [Description("Optional account identifier. Omit to apply across every account holding the symbol.")]
        string? account = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.BonusHoldingAsync(shcode, ratio, account, cancellationToken)).ConfigureAwait(false);

    // ---------------------- Serialization + error envelopes ----------------------

    /// <summary>
    /// Serializes the result of a portfolio tool action, converting typed portfolio errors into
    /// structured error envelopes the LLM can recover from (with candidates etc.).
    /// </summary>
    static async Task<string> SerializeAsync<T>(Func<Task<T>> action)
    {
        try
        {
            T result = await action().ConfigureAwait(false);
            return JsonSerializer.Serialize(result, McpJson.Tool);
        }
        catch (PortfolioException ex)
        {
            return SerializePortfolioError(ex);
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = "ValidationError", message = ex.Message }, McpJson.Tool);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { error = "ValidationError", message = ex.Message }, McpJson.Tool);
        }
        catch (SqliteException ex)
        {
            return JsonSerializer.Serialize(new { error = "StorageError", message = $"Portfolio database error: {ex.Message}" }, McpJson.Tool);
        }
        catch (IOException ex)
        {
            return JsonSerializer.Serialize(new { error = "StorageError", message = $"Portfolio storage error: {ex.Message}" }, McpJson.Tool);
        }
    }

    static string SerializePortfolioError(PortfolioException ex) => ex switch
    {
        AmbiguousAccountException ambig => JsonSerializer.Serialize(
            new { error = ambig.Code, message = ambig.Message, candidates = ambig.Candidates },
            McpJson.Tool),
        AccountNotFoundException notFound => JsonSerializer.Serialize(
            new { error = notFound.Code, message = notFound.Message, identifier = notFound.Identifier, candidates = notFound.Candidates },
            McpJson.Tool),
        RequiresConfirmationException needsConfirm => JsonSerializer.Serialize(
            new
            {
                error = needsConfirm.Code,
                message = needsConfirm.Message,
                account = needsConfirm.Account,
                holding_count = needsConfirm.HoldingCount,
                market_value = needsConfirm.MarketValue,
            },
            McpJson.Tool),
        InsufficientQuantityException insufficient => JsonSerializer.Serialize(
            new
            {
                error = insufficient.Code,
                message = insufficient.Message,
                shcode = insufficient.Symbol,
                current_quantity = insufficient.CurrentQuantity,
                requested_quantity = insufficient.RequestedQuantity,
                applied_to = insufficient.AppliedTo,
            },
            McpJson.Tool),
        _ => JsonSerializer.Serialize(new { error = ex.Code, message = ex.Message }, McpJson.Tool),
    };
}
