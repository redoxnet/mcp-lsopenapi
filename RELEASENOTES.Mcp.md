# Release Notes — RedoxNet.Mcp.LsOpenApi

## v0.1.0 (2026-05-13)

Initial public release of the MCP server for LS증권 OpenAPI.

### Tools (10)

- **Meta** — `ls_search_tr`, `ls_describe_tr`, `ls_call_tr`. `ls_call_tr` accepts both real JSON objects and JSON-stringified `in_block` payloads as a robustness fallback for clients whose MCP schema inference omits `type: object` on the `JsonElement` parameter.

- **Quotes & info**
  - `ls_get_quote` (t1101) — current price + 10-level order book + session OHLC.
  - `ls_get_multi_quote` (t8407) — up to 50 stocks per call.
  - `ls_get_stock_info` (t1102) — PER/PBR/EPS, quarterly financials, 52-week + YTD ranges, top-5 buy/sell brokerages, foreign-investor activity, SPAC / 관리종목 flags.

- **Charts** — `ls_get_chart` (t8410 / t8412 / t1301)
  - Period types: `day` / `week` / `month` / `year` / `min` / `tick`. Comma-separated period strings (`"day,week,month"`) return a `frames[]` array — one frame per timeframe, each with its own candles / indicators / context.
  - Optional technical indicators: `ma:N`, `ema:N`, `rsi:N`, `macd:F,S,Sig`, `bb:N,SD`.
  - Pre-computed `context` block: `divergence_from_ma`, `volume.{avg20,ratio20,avg60,ratio60}`, `drawdown.{period_high,date,pct}`, `ma_trend`, tristate `bullish_alignment` (`null` when MA warm-up makes the stack undecidable).
  - `include_chart=true` attaches a Plotly v5 JSON spec under `chart.spec`. Korean broker color convention applied (rising = red, falling = blue). `ma`/`ema`/`bb` overlay the price subplot; `rsi`/`macd` are emitted in `indicators` but not plotted (would need separate subplots).
  - `summary_only=true` keeps only the last 5 candles + final indicator scalars while preserving the full context — useful for multi-timeframe screening passes that would otherwise blow past inline token budgets.

- **Discovery & ETF**
  - `ls_search_stock` (t8436) — name → code search with an `instrument` filter (`all` / `stock` / `etf`).
  - `ls_get_etf_info` (t1901) — NAV + divergence (괴리율, from `kasis`) + tracking error (추적오차율, from `cocrate`) + reference index (참고지수 — e.g. KOSPI 200 for KODEX 200) + AUM + LP list + 52-week / year ranges + related futures. Foreign-ownership ratio (소진율) surfaced as `foreign_ownership_percent`. `listing_shares` converted from LS's 천-주 unit to raw share count.
  - `ls_get_etf_holdings` (t1904) — PDF (구성종목) array sorted by weight, with a top-N cap (`top_n=10` etc.) for ETFs with 200+ constituents. Summary block always reflects the full ETF (NAV, AUM, constituent count, cash); only the holdings array is truncated. The InBlock correctly sends LS's required `date` + `sgb` fields — omitting either produces a misleading `rsp_msg="해당자료가 없습니다"` response.

### Credentials

- **Environment variable only.** `LS_APPKEY` and `LS_APPSECRETKEY` are accepted via the process environment only — never through chat, tool arguments, or MCP elicitation. This is the strictest reading of the MCP spec's *"Servers MUST NOT use elicitation to request sensitive information"* guidance. Rationale is documented in [`docs/ADR-001-credential-management.md`](docs/ADR-001-credential-management.md).
- Logs, errors, and tool responses never echo credentials. Diagnostics show `****` + the last four characters of the app key; the secret key is never logged in any form.

### Packaging

- dotnet tool — `dnx RedoxNet.Mcp.LsOpenApi`. Dual-targeted `net8.0` + `net9.0`.
- MCP Server package type with `.mcp/server.json` for the registry. MSBuild `VerifyServerJsonVersion` target catches drift between csproj `<Version>` and `.mcp/server.json` at pack time.

### Out of scope (v0.1.0)

- Real-time (WebSocket) subscriptions — planned for v0.2.x (separate package `RedoxNet.Mcp.LsOpenApi.Realtime`).
- Account balance, order history, unfilled orders (read-only) — planned for v0.2.x.
- Order placement (매수 / 매도) — explicitly deferred to a future major release with elicitation-gated confirmation; this v0.1.x line stays read-only.

### Verified

Live-verified against the LS 모의투자 server on 2026-05-13: current quote (Samsung), multi-timeframe chart (SK하이닉스 일·주·월 with `ma:5/20/60`), search-then-quote (카카오), bio-only ETF discovery (`instrument="etf"`, 16 results), KODEX 200 PDF (201 holdings, AUM ≈ 25조원), and KODEX 200 ETF info (NAV / 괴리율 0.00% / 추적오차율 0.01% / KOSPI 200 reference / 외인 보유율 23.39%). 234 unit and fixture tests pass on .NET 8 and .NET 10.
