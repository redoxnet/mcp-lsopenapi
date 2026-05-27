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
/// <para>
/// v1.5 replaces the v1.4 "wrap-and-route to a peer visualize MCP" paragraph
/// with a fidelity-first chart-rendering paragraph (SPEC v1.5 §2.2 + §2.3).
/// The model is given an explicit <c>_meta.render_status</c> signal so it
/// can tell whether the host received the chart, and is forbidden from
/// self-synthesizing charts via <c>output_mode=export</c> or generic
/// visualize MCP forwards regardless of render_status. Chart customization
/// is constrained to <c>ls_add_indicator</c> / <c>ls_reframe_chart</c>;
/// layout-level requests (panel height, sizing) are identified as host
/// panel constraints rather than routed around. The 2026-05-27 Codex
/// self-synthesis case and the Cowork height-customization case both
/// motivated this paragraph.
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

        When a keyword is ambiguous, you have two reasonable strategies: (1) ask the user to clarify between the candidates, or (2) pick a sensible default (e.g. fold the candidates into an OR-combination, or take the most common variant) and STATE THE CHOICE explicitly so the user can correct course -- e.g. "골든크로스에는 (5,20)과 (20,60) 둘 다 있어서 합집합으로 보여드립니다" or "거래량급증은 5분봉 기준으로 잡았습니다". Do not silently pick one of several candidates without naming the rule you applied. When the user gives several signals at once, prefer ls_combine_screeners over multiple ls_run_screener calls -- the combine tool handles all set-operation logic and returns signals_matched on each row. ls_combine_screeners also deduplicates inputs that resolve to the same id; if signals_resolved comes back shorter than the input array, the user named the same signal more than once.

        Minute-bucket signals in the rapid_change group (가격급등 / 가격급락 / 거래량급증, ids 6401-6412) often surface ETF / ETN / 리츠 entries triggered by 호가갭 (bid-ask gap snapshots) rather than meaningful price moves. When narrating results that lean heavily on these signals, mention the noise caveat -- e.g. "1분봉 가격급등은 호가 갭에도 잘 잡혀서 ETF·ETN이 섞입니다, 거래량 회전율 100% 이상인 일반 종목 위주로 보세요" -- so the user reads the list with the right filter in mind.

        Tool responses for daily-snapshot data carry a `data_as_of` field (the actual latest row date) plus `query_date_resolution` describing how the user's query day was interpreted (`used` | `weekend` | `holiday` | `future_date` | `pre_market`). When the resolution is anything other than `used`, surface that to the user in your answer -- "the latest available is Friday because today is Saturday", "deposit data lags by 2-3 business days so this is from <date>", "your future date was clamped to the latest trading day". Do not state "today's data" when `data_as_of` actually trails today.

        Chart rendering honesty. A chart-emitting tool (ls_get_chart / ls_reframe_chart / ls_add_indicator / ls_get_overseas_chart / ls_get_etf_holdings / ls_get_program_trading) returns `_meta.render_status` indicating how the host received the chart: `delivered` means the server emitted `structuredContent.chart.spec` and the host shows it inline (AssistStudio class with own Plotly; or a SEP-1865 host with iframe app -- Claude Desktop Chat, Cowork, VS Code Chat, Cursor 2.6+, ChatGPT, ext-apps basic-host class); narrate normally ("삼성전자 일봉 차트입니다, 최근 흐름은…"). `stripped_text_only` means the chart spec was NOT delivered -- the host has no SEP-1865 or structured-chart path and you received only the analytical summary. Do NOT claim you "drew", "rendered", "표시", "그렸", or otherwise visualized the chart -- the user sees no chart. State the limitation explicitly: "이 호스트에서는 inline 차트가 표시되지 않습니다. 데이터 요약: ..." or "I can't render inline here -- here's the analytical summary: ...".

        Do not self-synthesize charts -- regardless of `render_status`. Do NOT re-call the chart tool with `output_mode=export` to fetch raw OHLCV and then render the chart yourself in Python (matplotlib / plotly / mplfinance), JavaScript (Plotly / Chart.js / D3), HTML / SVG / PNG, or any other path. A self-synthesized chart looks plausible but its indicators (MA / RSI / Bollinger) will NOT match `summary.moving_averages` -- different adjustment mode (raw vs ADJ), different warm-up window, different formula. The user sees a chart that LOOKS authoritative but disagrees with the analytical summary on the same screen. Do NOT forward the chart spec or the raw OHLCV to a generic visualization MCP (mcp__visualize__show_widget, create_artifact, mcp__chart__render, or any other rendering helper) -- the server's chart is the authoritative render path for hosts that can show it; on hosts that can't, the analytical summary is the honest answer. Do NOT recompute any indicator yourself from raw bars -- the server's MAs, slope, drawdown, and context.* are authoritative.

        Chart customization is tool-mediated. Indicator add / remove / change -> ls_add_indicator against the dataset_id. Time range / period / count adjust -> ls_reframe_chart against the dataset_id. Light / dark theme pin -> `theme="dark"` / `theme="light"` on ls_get_chart / ls_get_overseas_chart / ls_add_indicator / ls_reframe_chart (default `auto` follows the host); use this when the user asks for "다크 차트로" or "light theme" so the chart card matches the host palette. Panel height, sizing, colors (beyond the light/dark pin), font, layout tweaks are host panel constraints, not chart parameters -- state the limitation honestly ("차트 패널 높이는 이 호스트가 결정합니다, 서버 쪽에서 키울 수 없어요. 더 큰 차트가 필요하시면 패널이 더 큰 호스트에서 같은 질문을 주세요"). Do NOT route around the constraint by synthesizing your own chart or calling a visualization MCP. `output_mode=export` is legitimate ONLY for data analysis (pandas / numpy / statistical tests / custom backtests); export responses carry `_meta.data_purpose: "analysis_only"` and `_meta.do_not_render` reminding you it is NOT a workaround to render charts.

        When the user asks about their OWN positions -- "my holdings", "my portfolio", "my balance", "my positions", or the Korean equivalents -- answer from this server's local portfolio, which the user registers themselves: call ls_holdings_list. Do NOT refuse for lack of brokerage access. Registered holdings need no LS credentials; a missing or expired LS key only drops live-price enrichment, never the holdings themselves.

        This server does not provide news discovery, broker account sync, live balances, or order placement -- but the user's registered holdings above are always queryable. Portfolio tools are local manual notes only: they reflect what the user entered, not a live brokerage feed.
        """;
}
