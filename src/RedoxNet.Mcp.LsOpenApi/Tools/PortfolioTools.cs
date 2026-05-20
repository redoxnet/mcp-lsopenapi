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

    // ---------------------- Watchlist ----------------------

    /// <summary>
    /// v0.10 domain dispatcher (SPEC-v0.10 §2.6.2). Folds ls_watchlist_add /
    /// ls_watchlist_remove / ls_watchlist_list / ls_watchlist_group_create /
    /// ls_watchlist_group_delete into one action-routed tool. The v0.7
    /// sub-merges stay folded — group rename is the group_upsert
    /// `rename_from` parameter, group listing is action="list" scope="groups".
    /// </summary>
    [McpServerTool(Name = "ls_watchlist")]
    [Description("""
        Manages the local saved watchlist — stock items and their groups. Does not require LS credentials to save. Pick the operation with `action`:

        - action="list" — lists watchlist data. Default returns grouped items, each enriched with current price / change_pct (batch t8407 when LS credentials are available; partial quote failures allowed). `scope="groups"` instead returns group metadata only ({name, description, sort_order, item_count}). Optional `group_name` filters items.
        - action="add" — adds `shcode` to a watchlist group (`group_name`, defaults 'default'). Optional `note`. The item is saved even when LS credentials are unavailable.
        - action="remove" — removes `shcode` from `group_name` (defaults 'default').
        - action="group_upsert" — creates a group, updates its description, or renames one. `name` is the target group name; with `rename_from` set it renames that group to `name`, otherwise it inserts (or upserts the description). Optional `description`.
        - action="group_delete" — deletes the group `name` and cascades its saved items.

        USE WHEN: the user wants to remember / track / 관심종목 추가 a stock, see their watchlist, or manage watchlist groups. AVOID for general market questions unrelated to saved watchlists.

        v0.10 BREAKING: replaces ls_watchlist_add / ls_watchlist_remove / ls_watchlist_list / ls_watchlist_group_create / ls_watchlist_group_delete.
        """)]
    public static async Task<string> Watchlist(
        IPortfolioService portfolio,
        [Description("Operation to perform: 'list', 'add', 'remove', 'group_upsert', or 'group_delete'.")]
        string action,
        [Description("add / remove: 6-character Korean short code (e.g. '005930' or '0117V0').")]
        string? shcode = null,
        [Description("add / remove: watchlist group name (defaults to 'default'). list: optional group filter (scope='items' only).")]
        string? group_name = null,
        [Description("add: optional user note for this watchlist item.")]
        string? note = null,
        [Description("list: 'items' (default — grouped items + quote enrichment) or 'groups' (group metadata only).")]
        string? scope = null,
        [Description("group_upsert / group_delete: the target group name.")]
        string? name = null,
        [Description("group_upsert: optional group description.")]
        string? description = null,
        [Description("group_upsert: optional existing group name to rename to `name`.")]
        string? rename_from = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "ls_watchlist";
        switch (NormalizeAction(action))
        {
            case "list":
                return await WatchlistListAsync(portfolio, group_name, scope, cancellationToken).ConfigureAwait(false);
            case "add":
                if (string.IsNullOrWhiteSpace(shcode))
                    return MissingArgs(tool, "add", "shcode");
                return await SerializeAsync(() => portfolio.AddWatchlistAsync(shcode!, group_name ?? DefaultGroup, note, cancellationToken)).ConfigureAwait(false);
            case "remove":
                if (string.IsNullOrWhiteSpace(shcode))
                    return MissingArgs(tool, "remove", "shcode");
                return await SerializeAsync(() => portfolio.RemoveWatchlistAsync(shcode!, group_name ?? DefaultGroup, cancellationToken)).ConfigureAwait(false);
            case "group_upsert":
                if (string.IsNullOrWhiteSpace(name))
                    return MissingArgs(tool, "group_upsert", "name");
                return await SerializeAsync(() => portfolio.CreateGroupAsync(name!, description, rename_from, cancellationToken)).ConfigureAwait(false);
            case "group_delete":
                if (string.IsNullOrWhiteSpace(name))
                    return MissingArgs(tool, "group_delete", "name");
                return await SerializeAsync(() => portfolio.DeleteGroupAsync(name!, cancellationToken)).ConfigureAwait(false);
            default:
                return UnknownAction(tool, action, "list", "add", "remove", "group_upsert", "group_delete");
        }
    }

    /// <summary>action="list" body — dispatches the <c>scope</c> shape (items vs groups).</summary>
    static async Task<string> WatchlistListAsync(
        IPortfolioService portfolio,
        string? groupName,
        string? scope,
        CancellationToken cancellationToken)
    {
        string normalizedScope = string.IsNullOrWhiteSpace(scope) ? "items" : scope.Trim().ToLowerInvariant();
        if (normalizedScope != "items" && normalizedScope != "groups")
            return McpJson.Error($"scope '{scope}' is not recognized. Use 'items' (default) or 'groups'.");
        if (normalizedScope == "groups" && !string.IsNullOrWhiteSpace(groupName))
            return McpJson.Error("scope='groups' does not accept group_name. Use scope='items' to filter items by group.");
        return normalizedScope switch
        {
            "groups" => await SerializeAsync(async () => new { scope = "groups", groups = await portfolio.ListGroupsAsync(cancellationToken).ConfigureAwait(false) }).ConfigureAwait(false),
            _ => await SerializeAsync(() => portfolio.ListWatchlistAsync(groupName, cancellationToken)).ConfigureAwait(false),
        };
    }

    // ---------------------- Watched themes ----------------------

    /// <summary>
    /// v0.10 domain dispatcher (SPEC-v0.10 §2.6.3). Folds ls_watched_themes_add
    /// / ls_watched_themes_remove / ls_watched_themes_list into one
    /// action-routed tool. Kept separate from ls_watchlist — a theme code
    /// (t1531 tmcode) is not a stock short code.
    /// </summary>
    [McpServerTool(Name = "ls_watched_themes")]
    [Description("""
        Manages the local watched-themes list (LS t1531 theme codes); no LS credentials needed to save. `action`: "list" (watched themes, enriched with t1531 avgdiff as change_pct when credentials exist), "add" (requires `theme_code` — a 4-char tmcode, e.g. '0064' 2차전지; optional `theme_name`, `note`), "remove" (drops `theme_code`). USE WHEN the user wants to track / 관심 테마 추가 an LS theme; theme codes are not stock short codes (use ls_watchlist for stocks). v0.10 BREAKING: replaces ls_watched_themes_add / _remove / _list.
        """)]
    public static async Task<string> WatchedThemes(
        IPortfolioService portfolio,
        [Description("Operation to perform: 'list', 'add', or 'remove'.")]
        string action,
        [Description("add / remove: LS theme code (tmcode, 4-char), e.g. '0064'.")]
        string? theme_code = null,
        [Description("add: optional human-readable theme name (defaults to theme_code).")]
        string? theme_name = null,
        [Description("add: optional user note for this theme.")]
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "ls_watched_themes";
        switch (NormalizeAction(action))
        {
            case "list":
                return await SerializeAsync(() => portfolio.ListThemesAsync(cancellationToken)).ConfigureAwait(false);
            case "add":
                if (string.IsNullOrWhiteSpace(theme_code))
                    return MissingArgs(tool, "add", "theme_code");
                return await SerializeAsync(() => portfolio.WatchThemeAsync(theme_code!, theme_name, note, cancellationToken)).ConfigureAwait(false);
            case "remove":
                if (string.IsNullOrWhiteSpace(theme_code))
                    return MissingArgs(tool, "remove", "theme_code");
                return await SerializeAsync(() => portfolio.UnwatchThemeAsync(theme_code!, cancellationToken)).ConfigureAwait(false);
            default:
                return UnknownAction(tool, action, "list", "add", "remove");
        }
    }

    // ---------------------- Accounts ----------------------

    /// <summary>
    /// v0.10 domain dispatcher (SPEC-v0.10 §2.6.1). Folds ls_accounts_list /
    /// ls_account_upsert / ls_account_remove into one action-routed tool. The
    /// earlier v0.6/v0.7 sub-merges stay folded — ls_account_get is the
    /// is_default flag on action="list", ls_account_set_default is the
    /// set_default flag, ls_broker_rename is the rename_broker_from parameter.
    /// </summary>
    [McpServerTool(Name = "ls_account")]
    [Description("""
        Manages local portfolio accounts — list, create/update, or remove. Does not require LS credentials. Pick the operation with `action`:

        - action="list" — lists every registered account with its holdings count and the is_default flag. Returns an empty array when none exist; no other parameters.
        - action="upsert" — creates or updates an account by `account_number` (also requires `nickname`). `set_default=true` promotes it to the sole default; the first account is auto-promoted. Rename-broker sub-mode: set `rename_broker_from` to relabel the broker across every account currently using that label, with the replacement passed in `broker` (account_number / nickname / set_default are ignored).
        - action="remove" — removes the account identified by `account` (account_number or nickname). When it owns holdings, returns RequiresConfirmation with a preview unless `confirm=true`. Removing the default auto-promotes the oldest remaining account.

        USE WHEN: the user wants to register a brokerage account ("내 한투 계좌 등록해줘. 번호 X 닉네임 한투"), list their accounts, rename a broker label, or delete an account.

        v0.10 BREAKING: replaces ls_accounts_list / ls_account_upsert / ls_account_remove.
        """)]
    public static async Task<string> Account(
        IPortfolioService portfolio,
        [Description("Operation to perform: 'list', 'upsert', or 'remove'.")]
        string action,
        [Description("upsert: brokerage account number (required unless rename_broker_from is set).")]
        string? account_number = null,
        [Description("upsert: human-readable nickname, unique across accounts (required for a normal upsert).")]
        string? nickname = null,
        [Description("upsert: free-text broker label (defaults to 'LS'). In rename-broker sub-mode this is REQUIRED — the replacement label.")]
        string? broker = null,
        [Description("upsert: if true, promote this account to the sole default.")]
        bool set_default = false,
        [Description("upsert: set this to enter rename-broker sub-mode — the current broker label to replace across every account.")]
        string? rename_broker_from = null,
        [Description("remove: account identifier (account_number or nickname).")]
        string? account = null,
        [Description("remove: must be true to cascade-delete holdings owned by the account.")]
        bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        const string tool = "ls_account";
        switch (NormalizeAction(action))
        {
            case "list":
                return await SerializeAsync(() => portfolio.ListAccountsAsync(cancellationToken)).ConfigureAwait(false);
            case "upsert":
                return await AccountUpsertAsync(portfolio, account_number, nickname, broker, set_default, rename_broker_from, cancellationToken).ConfigureAwait(false);
            case "remove":
                if (string.IsNullOrWhiteSpace(account))
                    return MissingArgs(tool, "remove", "account");
                return await SerializeAsync(() => portfolio.RemoveAccountAsync(account!, confirm, cancellationToken)).ConfigureAwait(false);
            default:
                return UnknownAction(tool, action, "list", "upsert", "remove");
        }
    }

    /// <summary>
    /// action="upsert" body. Two modes: rename-broker (when
    /// <paramref name="renameBrokerFrom"/> is set) vs account upsert.
    /// </summary>
    static async Task<string> AccountUpsertAsync(
        IPortfolioService portfolio,
        string? accountNumber,
        string? nickname,
        string? broker,
        bool setDefault,
        string? renameBrokerFrom,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(renameBrokerFrom))
        {
            if (string.IsNullOrWhiteSpace(broker))
                return MissingArgs("ls_account", "upsert", "broker");
            return await SerializeAsync(() => portfolio.RenameBrokerAsync(renameBrokerFrom!.Trim(), broker!.Trim(), cancellationToken)).ConfigureAwait(false);
        }
        List<string> missing = new();
        if (string.IsNullOrWhiteSpace(accountNumber)) missing.Add("account_number");
        if (string.IsNullOrWhiteSpace(nickname)) missing.Add("nickname");
        if (missing.Count > 0)
            return MissingArgs("ls_account", "upsert", missing.ToArray());
        return await SerializeAsync(() => portfolio.UpsertAccountAsync(accountNumber!, nickname!, broker, setDefault, cancellationToken)).ConfigureAwait(false);
    }

    // ---------------------- Holdings ----------------------

    /// <summary>
    /// v0.10 domain dispatcher (SPEC-v0.10 §2.6.5 — F1 conservative split).
    /// Folds the five holding-write tools (ls_holdings_set / _buy / _sell /
    /// _remove / _corporate_action) into one action-routed tool. The read
    /// path stays a dedicated tool — <c>ls_holdings_list</c> — since "show my
    /// holdings" is the single most common portfolio intent.
    /// </summary>
    [McpServerTool(Name = "ls_holding")]
    [Description("""
        Records a change to one of the user's locally registered holdings — the write path. To VIEW holdings use ls_holdings_list instead. Does not require LS credentials. `shcode` is required for every action. Pick the operation with `action`:

        - action="set" — replaces the holding with the supplied state. Requires `quantity` (≥1) and `avg_price`. USE FOR "현재 보유 수량은 N주, 평단 M원이야" or initial registration.
        - action="buy" — records an incremental buy, merging with any existing lot at weighted-average cost. Requires `quantity` and `price`. USE FOR "삼성전자 N주 더 샀어. 단가 P원".
        - action="sell" — records a sell. Requires `quantity`. Subtracts shares; the row is auto-removed at zero; returns InsufficientQuantity when quantity exceeds the position.
        - action="remove" — removes the holding row outright regardless of quantity (returns removed=false when the symbol is not held).
        - action="corporate_action" — applies a corporate action that preserves cost basis. Requires `type` ('split' 액면분할, 'reverse_split' 액면병합, 'bonus' 무상증자) and `ratio` (split/reverse_split: integer ≥2; bonus: positive fraction, 0.1 = 10%).

        `account` is optional — auto-resolved when the symbol is held in exactly one account, AmbiguousAccount otherwise. For corporate_action, omitting `account` applies the event to every account holding the symbol.

        USE WHEN: the user reports buying / selling / setting / removing one of their own positions, or a corporate action on a stock they hold.

        v0.10 BREAKING: replaces ls_holdings_set / ls_holdings_buy / ls_holdings_sell / ls_holdings_remove / ls_holdings_corporate_action. The read tool ls_holdings_list is unchanged.
        """)]
    public static async Task<string> Holding(
        IPortfolioService portfolio,
        [Description("Operation to perform: 'set', 'buy', 'sell', 'remove', or 'corporate_action'.")]
        string action,
        [Description("6-character Korean short code (e.g. '005930' or '0117V0'). Required for every action.")]
        string shcode,
        [Description("Share quantity. Required for 'set' (≥1), 'buy', and 'sell'. Unused by 'remove' / 'corporate_action'.")]
        int? quantity = null,
        [Description("Average purchase price (≥0). Required for action='set'.")]
        double? avg_price = null,
        [Description("Price per share for this transaction. Required for action='buy'.")]
        double? price = null,
        [Description("set: optional user note for the holding.")]
        string? note = null,
        [Description("Optional account identifier (account_number or nickname). Auto-resolved when unambiguous; for corporate_action, omit to apply across every account holding the symbol.")]
        string? account = null,
        [Description("corporate_action: type — 'split', 'reverse_split', or 'bonus'.")]
        string? type = null,
        [Description("corporate_action: ratio. split/reverse_split require an integer ≥2; bonus is a positive fraction (0.1 = 10%).")]
        double? ratio = null,
        CancellationToken cancellationToken = default)
    {
        const string tool = "ls_holding";
        switch (NormalizeAction(action))
        {
            case "set":
            {
                List<string> missing = new();
                if (quantity is null) missing.Add("quantity");
                if (avg_price is null) missing.Add("avg_price");
                if (missing.Count > 0)
                    return MissingArgs(tool, "set", missing.ToArray());
                return await SerializeAsync(() => portfolio.SetHoldingAsync(shcode, quantity!.Value, avg_price!.Value, note, account, cancellationToken)).ConfigureAwait(false);
            }
            case "buy":
            {
                List<string> missing = new();
                if (quantity is null) missing.Add("quantity");
                if (price is null) missing.Add("price");
                if (missing.Count > 0)
                    return MissingArgs(tool, "buy", missing.ToArray());
                return await SerializeAsync(() => portfolio.BuyHoldingAsync(shcode, quantity!.Value, price!.Value, account, cancellationToken)).ConfigureAwait(false);
            }
            case "sell":
                if (quantity is null)
                    return MissingArgs(tool, "sell", "quantity");
                return await SerializeAsync(() => portfolio.SellHoldingAsync(shcode, quantity.Value, account, cancellationToken)).ConfigureAwait(false);
            case "remove":
                return await SerializeAsync<object>(async () =>
                {
                    HoldingWriteResult? removed = await portfolio.RemoveHoldingAsync(shcode, account, cancellationToken).ConfigureAwait(false);
                    return removed is null
                        ? (object)new { removed = false }
                        : new { removed = true, shcode = removed.Shcode, applied_to = removed.AppliedTo };
                }).ConfigureAwait(false);
            case "corporate_action":
            {
                List<string> missing = new();
                if (string.IsNullOrWhiteSpace(type)) missing.Add("type");
                if (ratio is null) missing.Add("ratio");
                if (missing.Count > 0)
                    return MissingArgs(tool, "corporate_action", missing.ToArray());
                return await SerializeAsync<object>(() => DispatchCorporateActionAsync(portfolio, shcode, type, ratio!.Value, account, cancellationToken)).ConfigureAwait(false);
            }
            default:
                return UnknownAction(tool, action, "set", "buy", "sell", "remove", "corporate_action");
        }
    }

    [McpServerTool(Name = "ls_holdings_list")]
    [Description("""
        Lists the user's locally registered holdings grouped by account, enriches them with current price, market_value, PnL, and PnL percent using batch t8407 when LS credentials are available, and computes a per-account summary plus a total_summary across all accounts. v0.6 adds optional theme filters; when at least one filter is active the response also echoes the filter and a matched_themes array so LIKE false positives are visible.

        USE WHEN: the user refers to their own position or portfolio, including Korean phrases like "내 보유 주식", "내 보유 주식 현황", "보유 현황", "내가 가진", "내가 산", "보유 중", "들고 있는", "내 종목", "내 잔고", "내 포트폴리오", or English phrases like "my holdings", "my position", "my portfolio". This is local data the user registered themselves — do NOT refuse such questions for lack of brokerage access. Before calling general stock tools such as ls_get_chart or ls_get_quote, call this tool first to check the user's registered holdings. If it returns empty, guide the user to register holdings or fall back to general analysis.
        AVOID WHEN: the user asks about a stock in general without personal ownership context, e.g. "삼성전자 어때?"; use market/quote/chart tools directly.

        Filter natural-language mapping:
        - "내 보유 중 2차전지 테마" → theme_keyword="2차전지"
        - "내 한투 계좌의 AI 테마" → account="한투", theme_keyword="AI"
        - "테마 코드 0064 보유" → theme_code="0064"
        - "내 보유 중 반도체 업종" / "내 금융주" → industry="반도체" / industry="금융"
        All filters AND-combine. The `industry` filter is a case-insensitive substring match against the FICS industry label sourced from t3320 (the "FICS " prefix is stripped before matching, so "반도체" matches "반도체 및 관련장비"). ETF / SPAC symbols have no industry record and are excluded from any industry-filtered list.

        Payload size: themes_limit caps themes per holding (default 5; 0 = count only; -1 = all), include_industry / include_quote drop those blocks when false. Lightest call — themes_limit=0, include_industry=false, include_quote=false — returns just the holding rows + account/total summaries.
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
        [Description("Max themes shown per holding. Default 5; 0 omits the theme items (themes count only); -1 returns every theme. Each holding's themes is a {count, shown, items} object.")]
        int themes_limit = 5,
        [Description("Include the FICS industry label per holding. Default true; pass false to omit it.")]
        bool include_industry = true,
        [Description("Include the live quote + market_value / PnL per holding. Default true; pass false to drop the per-holding valuation block (account and total summaries are still computed).")]
        bool include_quote = true,
        CancellationToken cancellationToken = default) =>
        await SerializeAsync(() => portfolio.ListHoldingsAsync(
            account, theme_code, theme_keyword, industry,
            themes_limit, include_industry, include_quote, cancellationToken)).ConfigureAwait(false);

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

    // ---------------------- Corporate-action helpers ----------------------

    /// <summary>
    /// Routes <c>ls_holding action="corporate_action"</c> by <paramref name="type"/>
    /// (액면분할 / 액면병합 / 무상증자) to the cost-basis-preserving service math.
    /// Unknown types raise a <see cref="PortfolioValidationException"/> — an open
    /// enum so v0.7+ types (stock_dividend / spin_off / merger) can land without
    /// touching the tool surface.
    /// </summary>
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

    /// <summary>
    /// v0.10 domain dispatcher (SPEC-v0.10 §2.6.4). Folds ls_portfolio_export
    /// / ls_portfolio_import into one action-routed tool.
    /// </summary>
    [McpServerTool(Name = "ls_portfolio_io")]
    [Description("""
        Backs up or restores the local portfolio — accounts, holdings, watchlists, watched themes — as a single versioned JSON file. Does not require LS credentials. Pick the operation with `action`:

        - action="export" — writes the portfolio to a JSON file. `path` is optional; when omitted a timestamped file is written under exports/ next to portfolio.db. The stocks metadata cache is intentionally not exported — it rebuilds from quote enrichment after import.
        - action="import" — restores from a previously-exported JSON file at `path` (required). `mode="merge"` (default) skips rows that already exist; `mode="replace"` wipes accounts/holdings/watchlists/themes first, requires `confirm=true`, and writes a before-import auto-backup.

        USE WHEN: the user asks to back up / export / "백업해줘", or restore / import / "복원해줘" / migrate to a new machine.

        v0.10 BREAKING: replaces ls_portfolio_export / ls_portfolio_import.
        """)]
    public static async Task<string> PortfolioIo(
        IPortfolioService portfolio,
        [Description("Operation to perform: 'export' or 'import'.")]
        string action,
        [Description("export: optional absolute output path (default: timestamped file under exports/). import: REQUIRED absolute path to the JSON file.")]
        string? path = null,
        [Description("import: 'merge' (default — skips duplicates) or 'replace' (wipes export-covered domains first, requires confirm).")]
        string mode = "merge",
        [Description("import: must be true to proceed with replace mode. Ignored for merge.")]
        bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        const string tool = "ls_portfolio_io";
        switch (NormalizeAction(action))
        {
            case "export":
                return await SerializeAsync(() => portfolio.ExportPortfolioAsync(path, cancellationToken)).ConfigureAwait(false);
            case "import":
                if (string.IsNullOrWhiteSpace(path))
                    return MissingArgs(tool, "import", "path");
                return await SerializeAsync(() => portfolio.ImportPortfolioAsync(path!, mode, confirm, cancellationToken)).ConfigureAwait(false);
            default:
                return UnknownAction(tool, action, "export", "import");
        }
    }

    // ---------------------- Dispatcher plumbing ----------------------

    /// <summary>Normalizes a dispatcher <c>action</c> argument (trim + lowercase).</summary>
    static string NormalizeAction(string? action) => (action ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Structured "unknown action" error for a v0.10 domain dispatcher. The
    /// echoed <c>valid_actions</c> list lets the model self-correct without a
    /// docs round-trip (SPEC-v0.10 §2.3).
    /// </summary>
    static string UnknownAction(string tool, string? action, params string[] validActions) =>
        McpJson.Error(
            $"{tool}: unknown action '{action}'. Valid actions: {string.Join(", ", validActions)}.",
            new { action, valid_actions = validActions });

    /// <summary>
    /// Structured "missing required argument(s) for this action" error. The
    /// shape is model-recoverable — it echoes the action and the exact missing
    /// parameter names (SPEC-v0.10 §2.3).
    /// </summary>
    static string MissingArgs(string tool, string action, params string[] missing) =>
        McpJson.Error(
            $"{tool} action='{action}' is missing required argument(s): {string.Join(", ", missing)}.",
            new { action, missing });

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
