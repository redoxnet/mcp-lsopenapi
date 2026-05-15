# Release Notes — RedoxNet.Mcp.LsOpenApi

## v0.4.0 (2026-05-15)

Token-efficient chart payloads, two follow-up tools that operate on a dataset handle, a ZigZag-based swing detector for `key_turns`, and an `IndicatorCoverage` block that lets the model explain why an indicator is null.

### Added

- **`ls_add_indicator`** — follow-up tool that adds an indicator (e.g. `"ma:200"`) to an existing `dataset_id` and refetches the frame with that indicator's warm-up window. Updates the same `dataset_id`, returns the new summary + optional Plotly chart spec. Lets the model honor *"MA200도 추가해줘"* without sending raw OHLCV back through context.
- **`ls_reframe_chart`** — follow-up tool that reframes a dataset to a different period/count using the cached symbol + indicator specs. Replaces the current view in the same `dataset_id` so a subsequent `ls_add_indicator` doesn't need a `period_type` argument.
- **`output_mode` on `ls_get_chart`** — `display` | `analyze` | `export` | `reference`. `display`/`analyze`/`reference` keep raw OHLCV and full indicator arrays out of the model's text; only `export` returns them. Defaults to `display` when `include_chart=true`, otherwise `analyze`. Existing `summary_only` stays as a legacy flag and maps to `analyze` when `output_mode` is omitted.
- **`dataset_id`** is returned on every successful chart call — opaque `ds_*` handle backed by a process-local LRU (16 datasets, 5 MB per dataset). Used by the two follow-up tools.
- **`with_warmup`** — explicit opt-in for the analytical-summary warm-up policy. `null` (default) auto-applies warm-up when `from` is unspecified and skips it when `from` is given; `true` forces warm-up even with explicit `from` (analyze long-period indicators inside a narrow window); `false` skips warm-up even when `from` is null (fastest, narrowest read). The tool description spells out the three cases so the model toggles it without being told.
- **`summary.coverage`** on every chart response — per-indicator availability (`MA5`..`MA200`, `ma60_slope`, `change_1y`, `change_5y`, `key_turns`) reported as `ok` / `insufficient_data` / `disabled`, plus `warmup_applied`, `analytical_bar_count`, `display_bar_count`, and a human-readable `note` when something is missing. The narrow-window `note` literally tells the model *"pass with_warmup=true to populate them"* so it can self-correct in the next turn.

### Changed

- **`ls_get_chart` text payload is summary-first.** The text content now carries `dataset_id` + `summary` + (for `analyze`) the existing `context` block; raw `candles` and full indicator arrays are present only when `output_mode='export'`. Plotly chart specs continue to ship via `structuredContent.chart` — zero token cost. Long-range chart requests no longer blow tens of thousands of tokens into the conversation.
- **`AnalyticalSummary` is computed over a warm-up-inclusive series**, not the trimmed display window. Default warm-up: 240 day bars / 120 week+month / 200 min / 10 year. With `count=60`, `summary.moving_averages` now includes a populated `MA200`, `ma60_slope` is non-null, and `change_pct1_y` is populated — without changing what the user sees on the chart.
- **`key_turns` use a threshold-reversal ZigZag** instead of a 5-bar fractal. Reversal triggers on the close (not on intrabar high/low), so a single wide-range bar can't self-trigger a spurious pivot. Period-aware percent thresholds: day 4%, week 8%, month 12%, year 20%, min 1.5%, tick 1%. Pivots strictly alternate peak/trough; the trailing pivot is `is_confirmed=false` at the latest bar and represents the in-progress swing. The fix also closes a stale-index bug in the old fractal detector that could emit duplicate pivots on the same bar.
- **`InflectionPoint` shape**: `(date, price, kind: peak|trough, change_pct_from_prev, is_confirmed)` — `kind` is now a typed enum (serialized as `"peak"` / `"trough"` via a snake-case enum converter), and each turn carries its leg size + confirmation status.
- **MA60 slope** (`rising`/`flat`/`falling`) is classified by a least-squares fit over the lookback window of MA values, not a two-point delta. A single noisy endpoint no longer flips the verdict.

### Tool count

- **11 → 13** (added `ls_add_indicator`, `ls_reframe_chart`).

### Verified

322 unit and fixture tests pass on .NET 8. Live-verified end-to-end against the LS real-market server: default summary populates `MA5..200` + slope + 1Y change over a 300-bar analytical window; narrow explicit-`from` window with `with_warmup=true` forces padding and re-populates long indicators; `with_warmup=false` skips it (`coverage.note` then guides the model to re-call with `true`); `output_mode='export'` brings back raw OHLCV; `ls_add_indicator(ma:200)` preserves `dataset_id` and returns the latest value; `ls_reframe_chart` swaps day→week in-place; ZigZag pivots on 035720 monthly alternate strictly with a trailing tentative pivot.

## v0.3.0 (2026-05-14)

A new market-screener tool, a search-parameter rename, and Naver-style chart polish.

### Added

- **`ls_get_top_stocks`** — market-wide ranking screener wrapping five TRs behind one `kind` parameter: `gainers` / `losers` / `unchanged` (t1441), `market_cap` (t1444), `volume` (t1452), `amount` (t1463), `volume_surge` (t1466). Supports `market` (all / kospi / kosdaq), `basis` (today / previous_day), `exchange` (unified / krx / nxt), and price/volume floor filters; paginates via the LS `idx` continuation key and merges KOSPI + KOSDAQ for `market_cap` when `market=all`. Brings the tool count to 11.

### Fixed

- **`ls_get_top_stocks` price filters were silently ignored.** LS's ranking TRs (t1441/t1452/t1463/t1466) treat `eprice=0` as "no price filter" and — the non-obvious part — suppress the `sprice` floor along with it. So `min_price` with no `max_price` returned the full unfiltered list. The tool now sends the 8-digit ceiling (`99999999`) as `eprice` whenever a `min_price` floor is set without a `max_price` cap. `min_volume` and the both-bounds case were already correct. Verified end-to-end against the live LS server. (Sending the fields as JSON strings instead of numbers, an early hypothesis, makes LS return HTTP 500 — the field types were never the problem.)

### Changed

- **`ls_search_stock`'s `query` parameter renamed to `keyword`.** `ls_search_tr` already took `keyword`, and models generalize the sibling tool's parameter name — so `ls_search_stock` calls came in with `keyword` and were rejected by the .NET MCP SDK's parameter binder with an opaque *"An error occurred invoking 'ls_search_stock'."* (the real "missing required parameter" detail only reaches the server's stderr log). Both search tools' keyword parameters are now also optional at the protocol level, so a missing or misnamed argument reaches the in-body validation and returns a clear *"keyword is required."* instead.
- **`ls_get_chart` gained an optional `name` parameter.** When supplied, the inline chart title reads *"삼성전자 (005930) — 일봉"* instead of just the code. The chart TRs do not carry the stock name, so the caller passes it through; omitted, the title falls back to the code as before.
- **Inline chart specs polished toward the Naver Finance look.** Candlestick x-axis labels are now an evenly-spaced ~8-tick subset (`MM/dd`) rather than one label per candle; MA / EMA overlays use the Korean retail palette (green / red / orange / purple); the period high and low get `최고` / `최저` annotations; the ETF-holdings treemap uses white labels on a deeper blue so they stay legible regardless of the host theme.

### Internal

- The Plotly chart-spec builders moved into `RedoxNet.LsOpenApi.Core` (see the Core 0.3.0 notes) — no effect on the tool surface.

## v0.2.0 (2026-05-14)

MCP host interoperability fixes. **No tool surface or behavior changes** — the 10 tools, their inputs, and their outputs are identical to v0.1.0. This release only reshapes the published JSON schema and UI-resource metadata so MCP hosts accept and render them correctly.

### Fixed

- **Optional array/string parameters were rejected by strict MCP host validators.** The .NET MCP SDK emits JSON Schema 2020-12's `"type": ["array","null"]` form for nullable parameters. Claude Desktop, Claude Code, and cowork reject that shape, so every `ls_get_chart` call that passed `indicators` failed before reaching the server (`from` / `to` were affected the same way). A `tools/list` request filter — `SchemaNormalizer` — now rewrites `["X","null"]` → `"X"` and drops the orphaned `default: null`, leaving the C# `T?` signatures untouched. Verified end-to-end against the live LS server.

- **MCP Apps (SEP-1865) UI-resource CSP metadata was the wrong shape.** `_meta.ui.csp` used raw CSP directive names (`script-src`, `style-src`, …); the spec expects domain lists (`resourceDomains`, `connectDomains`). Hosts read `csp.resourceDomains` to allowlist the Plotly CDN inside the sandbox iframe — with the old shape the CDN was never allowed. The `_meta` block is now also attached to the `resources/read` content (the host reads CSP from there, not only from the `resources/list` entry), `prefersBorder: true` is declared, and the HTML template declares `availableDisplayModes: ["inline"]` in its `ui/initialize` handshake.

### Added

- **`LS_LOG_LEVEL` environment variable** — sets the minimum log level (`Trace` / `Debug` / `Information` / `Warning` / `Error` / `Critical` / `None`, default `Information`). With `Trace`, every JSON-RPC message — including full `tools/call` payloads — is written to stderr, for diagnosing host-side interop issues.

### Host support note

Inline Plotly rendering still depends on the host. It works on Claude.ai (web). Claude Desktop / cowork currently cannot embed the MCP Apps sandbox iframe — the `claudemcpcontent.com` sandbox serves a CSP `frame-ancestors` that lists only the web origins, not the desktop app — so on those hosts the tool degrades gracefully to the structured `candles` / `indicators` / `context` payload. This is an Anthropic-side limitation, not a server issue.

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
