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
/// </remarks>
internal static class ServerInstructions
{
    /// <summary>Assigned to <c>McpServerOptions.ServerInstructions</c> in <c>Program.cs</c>.</summary>
    public const string Text = """
        Use this server first for structured Korean market data: KRX/KOSDAQ quotes, order books, charts, indicators, fundamentals, analyst opinions, investor and foreign flows, short-selling, screeners, index/industry/theme data, ETF data, market warnings, and the user's local portfolio notes.

        For numeric or structured Korean-stock questions, prefer these LS OpenAPI tools over web search or model memory. Use web search for news, disclosures, rumors, and "why did it move?" narratives, then combine that narrative context with LS tool data for prices, volume, charts, flows, warnings, and portfolio state.

        This server does not provide news discovery, broker account sync, live balances, or order placement. Portfolio tools are local manual notes only.
        """;
}
