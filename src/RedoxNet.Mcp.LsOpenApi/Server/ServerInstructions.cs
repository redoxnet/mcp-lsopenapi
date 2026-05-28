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

        Tool responses for daily-snapshot data carry a `data_as_of` field reporting the actual latest row date. When `data_as_of` trails today (KSD T+2~T+3 lag on funds-trend, T+1 lag on short-selling, weekend / holiday clamping on any daily snapshot), surface that to the user -- "the latest available is Friday because today is Saturday", "deposit data lags 2-3 business days so this is from <date>". Do not claim "today's data" when `data_as_of` actually trails today. Market session state, holiday calendars, and weekend clamping are not pushed by the server -- LLM's clock + calendar knowledge plus the empty-data / trailing-date signal is more accurate than any classifier the server could ship.

        Chart rendering honesty. A chart-emitting tool (ls_get_chart / ls_reframe_chart / ls_add_indicator / ls_get_overseas_chart / ls_get_etf_holdings / ls_get_program_trading) returns `_meta.render_status` indicating how the host received the chart: `delivered` means the server emitted `structuredContent.chart.spec` and the host shows it inline (AssistStudio class with own Plotly; or a SEP-1865 host with iframe app -- Claude Desktop Chat, Cowork, VS Code Chat, Cursor 2.6+, ChatGPT, ext-apps basic-host class); narrate normally ("삼성전자 일봉 차트입니다, 최근 흐름은…"). `stripped_text_only` means the chart spec was NOT delivered -- the host has no SEP-1865 or structured-chart path and you received only the analytical summary. Do NOT claim you "drew", "rendered", "표시", "그렸", or otherwise visualized the chart -- the user sees no chart. State the limitation explicitly: "이 호스트에서는 inline 차트가 표시되지 않습니다. 데이터 요약: ..." or "I can't render inline here -- here's the analytical summary: ...".

        Do not self-synthesize charts -- regardless of `render_status`. Do NOT re-call the chart tool with `output_mode=export` to fetch raw OHLCV and then render the chart yourself in Python (matplotlib / plotly / mplfinance), JavaScript (Plotly / Chart.js / D3), HTML / SVG / PNG, or any other path. A self-synthesized chart looks plausible but its indicators (MA / RSI / Bollinger) will NOT match `summary.moving_averages` -- different adjustment mode (raw vs ADJ), different warm-up window, different formula. The user sees a chart that LOOKS authoritative but disagrees with the analytical summary on the same screen. Do NOT forward the chart spec or the raw OHLCV to a generic visualization MCP (mcp__visualize__show_widget, create_artifact, mcp__chart__render, or any other rendering helper) -- the server's chart is the authoritative render path for hosts that can show it; on hosts that can't, the analytical summary is the honest answer. Do NOT recompute any indicator yourself from raw bars -- the server's MAs, slope, drawdown, and context.* are authoritative.

        Chart customization is tool-mediated. Indicator add / remove / change -> ls_add_indicator against the dataset_id. Time range / period / count adjust -> ls_reframe_chart against the dataset_id. Light / dark theme pin -> `theme="dark"` / `theme="light"` on ls_get_chart / ls_get_overseas_chart / ls_add_indicator / ls_reframe_chart (default `auto` follows the host); use this when the user asks for "다크 차트로" or "light theme" so the chart card matches the host palette. Panel height, sizing, colors (beyond the light/dark pin), font, layout tweaks are host panel constraints, not chart parameters -- state the limitation honestly ("차트 패널 높이는 이 호스트가 결정합니다, 서버 쪽에서 키울 수 없어요. 더 큰 차트가 필요하시면 패널이 더 큰 호스트에서 같은 질문을 주세요"). Do NOT route around the constraint by synthesizing your own chart or calling a visualization MCP. `output_mode=export` is legitimate ONLY for data analysis (pandas / numpy / statistical tests / custom backtests); export responses carry `_meta.data_purpose: "analysis_only"` and `_meta.do_not_render` reminding you it is NOT a workaround to render charts.

        When the user asks about their OWN positions or account state, this server exposes TWO distinct sources -- pick deliberately:

        1. Local portfolio (ls_holdings_list, ls_watchlist, ls_watched_themes, ls_holding, ls_account, ls_portfolio_io) -- the user's manually-tracked book in portfolio.db. Useful for paper portfolios, multi-broker tracking (LS + Kiwoom + Mirae Asset + ...), watchlists, and theme/sector tracking. Does NOT require LS credentials and does NOT auto-sync with the user's real brokerage -- it reflects only what the user entered. A missing or expired LS key only drops live-price enrichment on these tools.

        2. LS broker live -- the ls_account_* family: ls_account_holdings (current positions), ls_account_orders (today's fills + pending), ls_account_balance (cash / orderable / total valuation), ls_account_bep (break-even prices), ls_account_order_history (per-day order/fill log), ls_account_transactions (deposits/withdrawals/trades over a date range), ls_account_performance (period P&L), ls_account_daily_pnl (single-day trade log + fees), ls_account_credit_limit (margin loan limits), ls_account_max_order_qty (inquiry-only orderable capacity). Each call is a fresh REST snapshot of the user's real LS Securities account -- no caching, no daemon, no background sync. Requires LS credentials AND a registered account in portfolio.db (call ls_account action="upsert" once; the ls_account_* family then auto-resolves via the default account).

        The two sources are intentionally allowed to differ -- a paper portfolio in source 1 doesn't change source 2, and vice versa. Route by intent: "내 보유 종목" / "my holdings" without LS context -> ls_holdings_list (local). "LS 계좌 잔고" / "실계좌" / "오늘 주문 체결" / "예수금" / "수익률" -> the ls_account_* family. The active LS_MARKET (real or virtual) decides which broker accounts the ls_account_* family sees -- real-mode accounts and virtual-mode (모의투자) accounts are stored under separate is_default rows and never leak across modes.

        Order placement is NOT supported in v1.6 -- the ls_account_* family is read-only inquiry only. ls_place_order / ls_cancel_order / ls_amend_order ship in v1.7 with paper-trading default, preview-required gating, idempotency tokens, and an explicit live-account confirmation pattern. For now, direct users to LS WTS / MTS / xingTrader for actual order entry. ls_account_max_order_qty is an inquiry helper -- it reports the orderable quantity but never places an order.

        This server does not provide news discovery -- web search remains the route for news, disclosures, rumors, and "why did it move?" narratives. Combine that narrative with the LS tool data for prices / volume / charts / flows / account state.
        """;
}
