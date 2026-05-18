using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Server;
using RedoxNet.Mcp.LsOpenApi.Portfolio;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// Exposes local portfolio, watchlist, and watched-theme MCP tools.
/// </summary>
[McpServerToolType]
internal static class PortfolioTools
{
    const string DefaultGroup = "default";

    // ---------------------- Watchlist groups ----------------------

    [McpServerTool(Name = "ls_watchlist_group_create")]
    [Description("""
        Creates a watchlist group, updates its description, or renames an existing group — all through the same tool. Does not require LS credentials.

        USE WHEN: the user asks to "그룹 만들어 / create a list", "그룹 설명 바꿔 / update the description", or "이름 바꿔 / rename group X to Y".

        Three modes:
        - rename_from omitted, group `name` does not exist → INSERT the group.
        - rename_from omitted, group `name` exists → UPDATE its description (upsert).
        - rename_from set → RENAME the group currently called `rename_from` to `name` and (optionally) override its description. Fails with ValidationError when the target `name` already belongs to a different group.

        v0.7 Tier 2 BREAKING: the previous `ls_watchlist_group_rename` tool was folded here as the `rename_from` parameter.
        """)]
    public static async Task<string> WatchlistGroupCreate(
        IPortfolioService portfolio,
        [Description("Target group name (the post-rename name when rename_from is set).")]
        string name,
        [Description("Optional group description. Applied to the row whose name ends up being `name`.")]
        string? description = null,
        [Description("Optional. Existing group name to rename to `name`. Set this for the rename path; leave null/empty for insert / description upsert.")]
        string? rename_from = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.CreateGroupAsync(name, description, rename_from, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_watchlist_group_delete")]
    [Description("Deletes a watchlist group and cascades its saved stock items. Does not require LS credentials.")]
    public static async Task<string> WatchlistGroupDelete(
        IPortfolioService portfolio,
        [Description("Group name to delete.")]
        string name,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.DeleteGroupAsync(name, cancellationToken)).ConfigureAwait(false);

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
        Lists local saved watchlist data. Two shapes selected by `scope`:
        - scope="items" (default) → grouped watchlist items, each enriched with current price / change_pct via batch t8407 when LS credentials are available. Partial quote failures are allowed: quote may be null but saved items still return.
        - scope="groups" → group metadata only ({ name, description, sort_order, item_count } per group). No LS calls.

        v0.7 Tier 2 BREAKING: the previous `ls_watchlist_groups_list` tool was folded here as `scope="groups"`.

        USE WHEN: the user asks for 관심종목 / watchlist / tracked stocks (scope="items"), or for the saved group list / "내 watchlist 그룹 뭐가 있어 / show my watchlist groups" (scope="groups"). AVOID WHEN: the user asks general market info unrelated to saved watchlists.
        """)]
    public static async Task<string> WatchlistList(
        IPortfolioService portfolio,
        [Description("Optional group name (only meaningful when scope='items'). When omitted, all groups are returned, including empty groups.")]
        string? group_name = null,
        [Description("Optional. 'items' (default) returns grouped items + quote enrichment; 'groups' returns group metadata only.")]
        string? scope = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedScope = string.IsNullOrWhiteSpace(scope) ? "items" : scope.Trim().ToLowerInvariant();
        if (normalizedScope != "items" && normalizedScope != "groups")
            return McpJson.Error($"scope '{scope}' is not recognized. Use 'items' (default) or 'groups'.");
        if (normalizedScope == "groups" && !string.IsNullOrWhiteSpace(group_name))
            return McpJson.Error("scope='groups' does not accept group_name. Use scope='items' to filter items by group.");
        return normalizedScope switch
        {
            "groups" => await SerializeAsync(async () => new { scope = "groups", groups = await portfolio.ListGroupsAsync(cancellationToken).ConfigureAwait(false) }).ConfigureAwait(false),
            _ => await SerializeAsync(() => portfolio.ListWatchlistAsync(group_name, cancellationToken)).ConfigureAwait(false),
        };
    }

    // ---------------------- Themes ----------------------

    [McpServerTool(Name = "ls_watched_themes_add")]
    [Description("Adds an LS theme code (t1531 tmcode, 4-char) to the local watched themes list. Quotes enrich on list via t1531 avgdiff. Does not require LS credentials to save metadata.")]
    public static async Task<string> ThemeWatch(
        IPortfolioService portfolio,
        [Description("LS theme code (tmcode), e.g. '0012' (반도체 장비), '0064' (2차전지).")]
        string theme_code,
        [Description("Optional human-readable theme_name. Defaults to theme_code when omitted.")]
        string? theme_name = null,
        [Description("Optional user note for this theme.")]
        string? note = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.WatchThemeAsync(theme_code, theme_name, note, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_watched_themes_remove")]
    [Description("Removes an LS theme code from the saved theme watch list. Does not require LS credentials.")]
    public static async Task<string> ThemeUnwatch(
        IPortfolioService portfolio,
        [Description("LS theme code (tmcode) to remove.")]
        string theme_code,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.UnwatchThemeAsync(theme_code, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_watched_themes_list")]
    [Description("Lists local watched LS themes and enriches them with t1531 avgdiff as change_pct when LS credentials are available. Saved metadata still returns without credentials.")]
    public static async Task<string> ThemeList(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListThemesAsync(cancellationToken)).ConfigureAwait(false);

    // ---------------------- Accounts ----------------------

    [McpServerTool(Name = "ls_accounts_list")]
    [Description("Lists all registered local portfolio accounts with their holdings counts and the default flag. Returns an empty array when no accounts are registered.")]
    public static async Task<string> AccountsList(
        IPortfolioService portfolio,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListAccountsAsync(cancellationToken)).ConfigureAwait(false);

    // ls_account_get removed in v0.6 (Tier 1 compression). The default
    // account is exposed via ls_accounts_list's is_default flag — the model
    // filters the array to get the same information without a dedicated tool.

    [McpServerTool(Name = "ls_account_upsert")]
    [Description("""
        Two modes selected by the optional `rename_broker_from`:

        - Upsert mode (default — rename_broker_from omitted): creates or updates a local portfolio account by `account_number`. When `set_default` is true the account becomes the sole default; otherwise the previous default is preserved (and if none existed, the upserted account is auto-promoted to default).

        - Rename-broker mode (`rename_broker_from` set): renames the broker label across every account currently using `rename_broker_from`, replacing it with the value passed in `broker`. In this mode `account_number`, `nickname`, and `set_default` are ignored.

        v0.7 Tier 2 BREAKING: the previous `ls_broker_rename` tool was folded here as the `rename_broker_from` parameter.

        USE WHEN: the user wants to register their brokerage account locally — "내 한투 계좌 등록해줘. 번호 X 닉네임 한투" or adding a second account; OR wants to rename a broker label across accounts — "broker 'KB' 를 'KB증권' 으로 바꿔" (rename_broker_from="KB", broker="KB증권").
        """)]
    public static async Task<string> AccountUpsert(
        IPortfolioService portfolio,
        [Description("Brokerage account number (upsert mode only — ignored when rename_broker_from is set).")]
        string? account_number = null,
        [Description("Human-readable nickname (upsert mode only — ignored when rename_broker_from is set). Must be unique across accounts.")]
        string? nickname = null,
        [Description("Upsert mode: free-text broker label for this account (defaults to 'LS'). Rename-broker mode: REQUIRED — the replacement label that overwrites every account whose current broker is `rename_broker_from`.")]
        string? broker = null,
        [Description("Upsert mode only: if true, promote this account to default. Rename-broker mode ignores this.")]
        bool set_default = false,
        [Description("Optional. Current broker label to rename. When set, the tool enters rename-broker mode: every account whose broker equals this value is updated to use `broker` as the new label.")]
        string? rename_broker_from = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(rename_broker_from))
        {
            if (string.IsNullOrWhiteSpace(broker))
                return McpJson.Error("rename_broker_from requires `broker` (the replacement label).");
            return await SerializeAsync(() => portfolio.RenameBrokerAsync(rename_broker_from!.Trim(), broker!.Trim(), cancellationToken)).ConfigureAwait(false);
        }
        if (string.IsNullOrWhiteSpace(account_number))
            return McpJson.Error("account_number is required for upsert mode (omit only when rename_broker_from is set).");
        if (string.IsNullOrWhiteSpace(nickname))
            return McpJson.Error("nickname is required for upsert mode.");
        return await SerializeAsync(() => portfolio.UpsertAccountAsync(account_number, nickname, broker, set_default, cancellationToken)).ConfigureAwait(false);
    }

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

    // ls_account_set_default removed in v0.6 (Tier 1 compression). The same
    // effect is reachable via ls_account_upsert(set_default=true) without
    // adding a separate tool to the surface.

    // ls_broker_rename removed in v0.7 (Tier 2 compression). The same effect
    // is reachable via ls_account_upsert(rename_broker_from=OLD, broker=NEW).

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
        Lists the user's locally registered holdings grouped by account, enriches them with current price, market_value, PnL, and PnL percent using batch t8407 when LS credentials are available, and computes a per-account summary plus a total_summary across all accounts. v0.6 adds optional theme filters; when at least one filter is active the response also echoes the filter and a matched_themes array so LIKE false positives are visible.

        USE WHEN: the user refers to their own position or portfolio, including Korean phrases like "내가 가진", "내가 산", "보유 중", "들고 있는", "내 종목", "내 포트폴리오", or English phrases like "my holdings", "my position", "my portfolio". Before calling general stock tools such as ls_get_chart or ls_get_quote, call this tool first to check the user's registered holdings. If it returns empty, guide the user to register holdings or fall back to general analysis.
        AVOID WHEN: the user asks about a stock in general without personal ownership context, e.g. "삼성전자 어때?"; use market/quote/chart tools directly.

        Filter natural-language mapping:
        - "내 보유 중 2차전지 테마" → theme_keyword="2차전지"
        - "내 한투 계좌의 AI 테마" → account="한투", theme_keyword="AI"
        - "테마 코드 0064 보유" → theme_code="0064"
        - "내 보유 중 반도체 업종" / "내 금융주" → industry="반도체" / industry="금융"
        All filters AND-combine. The `industry` filter is a case-insensitive substring match against the FICS industry label sourced from t3320 (the "FICS " prefix is stripped before matching, so "반도체" matches "반도체 및 관련장비"). ETF / SPAC symbols have no industry record and are excluded from any industry-filtered list.
        """)]
    public static async Task<string> HoldingList(
        IPortfolioService portfolio,
        [Description("Optional account identifier (account_number or nickname). When omitted, holdings across all accounts are returned grouped.")]
        string? account = null,
        [Description("Optional exact 4-character LS theme code (tmcode), e.g. '0064'. Keeps only holdings whose cached themes contain this code.")]
        string? theme_code = null,
        [Description("Optional theme name keyword. Case-sensitive LIKE match against cached theme names. '2차전지' matches '2차전지 셀', '2차전지 소재', etc.")]
        string? theme_keyword = null,
        [Description("Optional FICS industry substring (case-insensitive). '반도체' matches FICS 반도체 및 관련장비. ETF/SPAC symbols are excluded automatically since they have no industry record.")]
        string? industry = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListHoldingsAsync(account, theme_code, theme_keyword, industry, cancellationToken)).ConfigureAwait(false);

    // ---------------------- Metadata refresh ----------------------

    [McpServerTool(Name = "ls_stocks_refresh_metadata")]
    [Description("""
        Synchronously refreshes the local metadata cache (themes / FICS industry) for the requested symbols. Blocks until every fetch completes — typical cold cost is ~1s per symbol per kind due to LS's 1 TPS limit on t1532 and t3320.

        USE WHEN: the user explicitly asks to "다시 가져와", "업데이트해 줘", "refresh", or wants a known-bad cache row repaired right now without waiting for a write-path fire-and-forget.
        AVOID WHEN: you're just listing holdings — the list path already dispatches enrichment in the background.

        shcodes omitted → refreshes every symbol across holdings ∪ watchlist (deduplicated). Pass an explicit list for targeted refresh.
        kinds omitted → refreshes BOTH themes and industry. Pass ["themes"] or ["industry"] to scope a single kind.

        Response shape:
          {
            kinds: ["themes", "industry"],
            refreshed: [{ shcode, themes_updated, industry_updated }, ...],
            errors:    [{ shcode, kind, error }, ...]
          }
        themes_updated / industry_updated are TRUE when the cache landed (including fetched-but-empty sentinels for ETF / SPAC); errors lists per-(shcode, kind) failures.
        """)]
    public static async Task<string> StocksRefreshMetadata(
        IPortfolioService portfolio,
        [Description("Optional list of 6-char short codes. Omit to refresh every holding + watchlist symbol.")]
        string[]? shcodes = null,
        [Description("Optional subset of kinds to refresh: 'themes', 'industry'. Omit for both.")]
        string[]? kinds = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.RefreshStockMetadataAsync(shcodes, kinds, cancellationToken)).ConfigureAwait(false);

    // ---------------------- Corporate actions ----------------------

    /// <summary>
    /// v0.6 Tier 1 compression. The three v0.5 tools (ls_holdings_split,
    /// ls_holdings_reverse_split, ls_holdings_bonus) collapsed into one
    /// open-enum dispatcher so the v0.7 additions
    /// (stock_dividend / spin_off / merger) can land without expanding
    /// the tool surface. See SPEC §4.5.
    /// </summary>
    [McpServerTool(Name = "ls_holdings_corporate_action")]
    [Description("""
        Applies a corporate action (액면분할 / 액면병합 / 무상증자) to a holding by adjusting quantity and average price so the cost basis is preserved. v0.6 replaces the three v0.5 tools (ls_holdings_split / _reverse_split / _bonus) with this single dispatcher.

        type values (v0.6, open enum):
        - "split"          — 액면분할. ratio must be an integer ≥ 2 (e.g. 10 for 1:10). qty *= ratio, avg /= ratio.
        - "reverse_split"  — 액면병합. ratio must be an integer ≥ 2; rejects when qty is not divisible. qty /= ratio, avg *= ratio.
        - "bonus"          — 무상증자. ratio is a positive fraction (e.g. 0.1 for 10%). qty *= (1+ratio), avg /= (1+ratio).
        Unknown types return a ValidationError envelope listing the v0.6 set; v0.7+ extends the enum (stock_dividend / spin_off / merger) without adding new tools.

        Account omitted → applied to every account holding the symbol (one corporate event affects all owners).
        USE FOR phrases like "삼성전자 10:1 분할했대" (type=split, ratio=10), "삼성전자 10:1 액면병합" (type=reverse_split, ratio=10), "무상증자 1주당 0.1주" (type=bonus, ratio=0.1).
        """)]
    public static async Task<string> HoldingCorporateAction(
        IPortfolioService portfolio,
        [Description("6-character Korean short code.")]
        string shcode,
        [Description("Corporate action type: 'split', 'reverse_split', or 'bonus'. Other values planned for v0.7+ (stock_dividend / spin_off / merger).")]
        string type,
        [Description("Ratio. For split / reverse_split: integer ≥ 2. For bonus: positive double (0.1 = 10%).")]
        double ratio,
        [Description("Optional account identifier. Omit to apply across every account holding the symbol.")]
        string? account = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync<object>(() => DispatchCorporateActionAsync(portfolio, shcode, type, ratio, account, cancellationToken)).ConfigureAwait(false);

    static async Task<object> DispatchCorporateActionAsync(
        IPortfolioService portfolio,
        string shcode,
        string? type,
        double ratio,
        string? account,
        CancellationToken cancellationToken)
    {
        string normalizedType = (type ?? "").Trim().ToLowerInvariant();
        return normalizedType switch
        {
            "split" => await portfolio.SplitHoldingAsync(shcode, ToInteger(ratio, normalizedType), account, cancellationToken).ConfigureAwait(false),
            "reverse_split" => await portfolio.ReverseSplitHoldingAsync(shcode, ToInteger(ratio, normalizedType), account, cancellationToken).ConfigureAwait(false),
            "bonus" => await portfolio.BonusHoldingAsync(shcode, ratio, account, cancellationToken).ConfigureAwait(false),
            _ => throw new PortfolioValidationException(
                $"Unsupported corporate action type '{type}'. Supported in v0.6: split / reverse_split / bonus. " +
                "Additional types (stock_dividend / spin_off / merger) are planned for future releases via enum extension."),
        };
    }

    /// <summary>
    /// Coerces the wire-side <c>ratio</c> (declared as double on the MCP tool
    /// so JSON numbers round-trip cleanly) into the integer that
    /// SplitHoldingAsync / ReverseSplitHoldingAsync expect. Fractional values
    /// like 1.5 or 3.7 are rejected to match the v0.5 service-layer contract.
    /// </summary>
    static int ToInteger(double ratio, string typeForMessage)
    {
        if (Math.Abs(ratio - Math.Round(ratio)) > 1e-9)
            throw new PortfolioValidationException($"{typeForMessage} ratio must be an integer (e.g. 10), got {ratio:G}.");
        return (int)Math.Round(ratio);
    }

    // ---------------------- Portfolio I/O ----------------------

    [McpServerTool(Name = "ls_portfolio_export")]
    [Description("""
        Exports the local portfolio (accounts, holdings, watchlists, watched themes) to a single versioned JSON file. Default path is a timestamped file under the exports/ directory next to portfolio.db; pass an absolute path to override.

        USE WHEN: the user asks to back up, export, save, "백업해줘", "내보내기", or wants to move data between machines. Stocks metadata cache (names, themes) is intentionally not exported — it rebuilds from quote enrichment after import.
        """)]
    public static async Task<string> PortfolioExport(
        IPortfolioService portfolio,
        [Description("Optional absolute path for the export file. When omitted, writes 'exports/portfolio-YYYY-MM-DDTHHmmss.json' next to portfolio.db.")]
        string? path = null,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ExportPortfolioAsync(path, cancellationToken)).ConfigureAwait(false);

    [McpServerTool(Name = "ls_portfolio_import")]
    [Description("""
        Imports a previously-exported portfolio JSON file. Two modes: 'merge' (default — skips rows that already exist by account_number / group name / theme_code / (group, shcode) / (account, shcode)), and 'replace' (wipes accounts/holdings/watchlists/themes first, requires confirm=true, and writes a before-import-* auto-backup to the same exports/ directory).

        USE WHEN: the user wants to restore from a backup, migrate to a new machine, or "가져오기 / 복원해줘". After import, themes/quotes are re-enriched on next list/write call.
        """)]
    public static async Task<string> PortfolioImport(
        IPortfolioService portfolio,
        [Description("Absolute path to the previously-exported JSON file.")]
        string path,
        [Description("Import mode. 'merge' (default) skips duplicates. 'replace' wipes export-covered domains first and requires confirm=true.")]
        string mode = "merge",
        [Description("Must be true to proceed with replace mode. Ignored for merge mode.")]
        bool confirm = false,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ImportPortfolioAsync(path, mode, confirm, cancellationToken)).ConfigureAwait(false);

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
        ImportSchemaMismatchException schemaMismatch => JsonSerializer.Serialize(
            new
            {
                error = schemaMismatch.Code,
                message = schemaMismatch.Message,
                file_schema_version = schemaMismatch.FileSchemaVersion,
                supported_schema_version = schemaMismatch.SupportedSchemaVersion,
            },
            McpJson.Tool),
        ImportReplaceRequiresConfirmationException needsImportConfirm => JsonSerializer.Serialize(
            new
            {
                error = needsImportConfirm.Code,
                message = needsImportConfirm.Message,
                source_path = needsImportConfirm.SourcePath,
                accounts_in_file = needsImportConfirm.AccountsInFile,
                holdings_in_file = needsImportConfirm.HoldingsInFile,
                mode = "replace",
            },
            McpJson.Tool),
        _ => JsonSerializer.Serialize(new { error = ex.Code, message = ex.Message }, McpJson.Tool),
    };
}
