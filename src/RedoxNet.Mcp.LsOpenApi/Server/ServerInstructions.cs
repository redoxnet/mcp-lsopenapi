namespace RedoxNet.Mcp.LsOpenApi.Server;

/// <summary>
/// The server-level <c>instructions</c> string surfaced in the MCP
/// <c>initialize</c> response. MCP hosts typically inject this as a system
/// message, so the tool-routing guidance ships <em>with</em> the server — no
/// per-host AGENTS.md / project-instruction setup required.
/// </summary>
/// <remarks>
/// The text is a deliberate routing boundary, not a tool catalog: structured
/// Korean-market AND US/overseas-stock questions route to the LS tools;
/// news / disclosures / rumors / "why did it move" narratives route to host
/// web search, whose output is then combined with LS tool data. News
/// discovery, broker account sync, and order placement are out of scope —
/// t3102 (뉴스본문) is catalog-only and unusable as a news tool without NWS
/// WebSocket number discovery.
/// <para>
/// v1.3 added the overseas-stock paragraph specifically because the v1.2
/// wording ("structured Korean market data" / "structured Korean-stock
/// questions") was read faithfully by hosted models as "this server is
/// Korean-only" — a query like "미장 엔비디아 종목코드" got answered from
/// model memory instead of <c>ls_search_overseas_stock</c>. The overseas
/// branch is now an explicit positive cue, with a negative reinforcement
/// ("NOT a Korean-stock question and NOT a web-search question") to close
/// the discoverability gap.
/// </para>
/// <para>
/// It also explicitly defuses the model's reflex to refuse personal-holdings
/// questions ("내 보유 주식 현황"): the local portfolio is the user's own
/// registered data and is queryable without LS credentials, so a "no live
/// balances" boundary must not be read as "cannot show the user's holdings".
/// </para>
/// </remarks>
internal static class ServerInstructions
{
    /// <summary>Assigned to <c>McpServerOptions.ServerInstructions</c> in <c>Program.cs</c>.</summary>
    /// <remarks>
    /// ASCII-only by design: the MCP <c>initialize</c> response is protocol
    /// handshake metadata and must survive any host's stdout decoding. The
    /// model is multilingual, so "or the Korean equivalents" routes Korean
    /// holdings queries without embedding the literal Korean phrases.
    /// </remarks>
    public const string Text = """
        Use this server first for structured Korean (KRX/KOSDAQ) and US/overseas (Nasdaq, NYSE, AMEX) stock market data: quotes, order books, charts (daily/weekly/monthly/year/minute/tick), technical indicators, fundamentals, analyst opinions, investor and foreign flows, short-selling, screeners, index/industry/theme data, ETF data, market warnings, and the user's local portfolio.

        For numeric or structured stock questions on EITHER Korean OR US/overseas markets, prefer these LS OpenAPI tools over web search or model memory. Korean stocks route through ls_get_quote / ls_get_chart / ls_search_stock and friends; US/overseas stocks route through ls_get_overseas_quote / ls_get_overseas_chart / ls_search_overseas_stock, which accepts ticker, Korean name, or English name. A query that names a US listing in any language is an overseas-stock question, NOT a Korean-stock question and NOT a web-search question -- call ls_search_overseas_stock first to resolve the symbol, then ls_get_overseas_quote / ls_get_overseas_chart. Use web search for news, disclosures, rumors, and "why did it move?" narratives, then combine that narrative context with LS tool data for prices, volume, charts, flows, warnings, and portfolio state.

        Korean Q-Click signals are an LS-curated catalog of standard chart / indicator / market-pattern / investor-flow screeners (golden-cross style, MA breakouts, short-side trend, foreign-flow streaks, and more, organised into core/indicator/market-trend/investor-trend groups). Discover the catalog with ls_list_screeners. Run one signal with ls_run_screener; run two or more together with ls_combine_screeners (mode=and for intersection, mode=or for union) -- this expresses compound conditions that no single HTS screen offers. Both tools accept exact names, 4-character ids, or Korean keywords; ambiguous keywords return a candidate list plus the matching group's full mini-catalog, so a follow-up call can target an exact id without an extra discovery round trip.

        When the user asks about their OWN positions -- "my holdings", "my portfolio", "my balance", "my positions", or the Korean equivalents -- answer from this server's local portfolio, which the user registers themselves: call ls_holdings_list. Do NOT refuse for lack of brokerage access. Registered holdings need no LS credentials; a missing or expired LS key only drops live-price enrichment, never the holdings themselves.

        This server does not provide news discovery, broker account sync, live balances, or order placement -- but the user's registered holdings above are always queryable. Portfolio tools are local manual notes only: they reflect what the user entered, not a live brokerage feed.
        """;
}
