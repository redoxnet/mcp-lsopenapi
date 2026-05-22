namespace RedoxNet.Mcp.LsOpenApi.Server;

/// <summary>
/// The server-level <c>instructions</c> string surfaced in the MCP
/// <c>initialize</c> response. MCP hosts typically inject this as a system
/// message, so the tool-routing guidance ships <em>with</em> the server — no
/// per-host AGENTS.md / project-instruction setup required.
/// </summary>
/// <remarks>
/// The text is a deliberate routing boundary, not a tool catalog: structured
/// Korean-market questions route to the LS tools; news / disclosures / rumors
/// / "why did it move" narratives route to host web search, whose output is
/// then combined with LS tool data. News discovery, broker account sync, and
/// order placement are out of scope — t3102 (뉴스본문) is catalog-only and
/// unusable as a news tool without NWS WebSocket number discovery.
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
        Use this server first for structured Korean market data: KRX/KOSDAQ quotes, order books, charts, indicators, fundamentals, analyst opinions, investor and foreign flows, short-selling, screeners, index/industry/theme data, ETF data, market warnings, and the user's local portfolio.

        For numeric or structured Korean-stock questions, prefer these LS OpenAPI tools over web search or model memory. Use web search for news, disclosures, rumors, and "why did it move?" narratives, then combine that narrative context with LS tool data for prices, volume, charts, flows, warnings, and portfolio state.

        When the user asks about their OWN positions -- "my holdings", "my portfolio", "my balance", "my positions", or the Korean equivalents -- answer from this server's local portfolio, which the user registers themselves: call ls_holdings_list. Do NOT refuse for lack of brokerage access. Registered holdings need no LS credentials; a missing or expired LS key only drops live-price enrichment, never the holdings themselves.

        This server does not provide news discovery, broker account sync, live balances, or order placement -- but the user's registered holdings above are always queryable. Portfolio tools are local manual notes only: they reflect what the user entered, not a live brokerage feed.
        """;
}
