<p align="right">
  <strong>English</strong> · <a href="README.md">한글</a>
</p>

# mcp-lsopenapi

[![NuGet Mcp](https://img.shields.io/nuget/v/RedoxNet.Mcp.LsOpenApi?label=Mcp)](https://www.nuget.org/packages/RedoxNet.Mcp.LsOpenApi/)
[![NuGet Core](https://img.shields.io/nuget/v/RedoxNet.LsOpenApi.Core?label=Core)](https://www.nuget.org/packages/RedoxNet.LsOpenApi.Core/)
[![CI](https://github.com/redoxnet/mcp-lsopenapi/actions/workflows/ci.yml/badge.svg)](https://github.com/redoxnet/mcp-lsopenapi/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

MCP server over **LS Securities OpenAPI**: quotes, charts, screeners, ETF holdings, program-trading flow, index / industry / theme context, the user's live LS broker account (read-only), and a local paper portfolio — accessed in natural language.

> **v1.6 scope:** read-only market data + read-only LS broker inquiry (live holdings / balance / orders / BEP / order history / transactions / period P&L / max orderable quantity / credit limit, fresh REST snapshot per call, no caching) + local paper portfolio notes (manually-entered multi-broker holdings, watchlists, watched themes, JSON backup/restore). **No order placement** — that's v1.7 with paper-default + preview-required gating.

## Why it works well in chat

An LLM-friendly window into Korean and US stock market data — designed so agents don't waste tokens reconstructing what the server can compute once.

- **Market context in one place.** Quotes, order books, OHLCV charts, indicators, fundamentals, analyst opinions, investor / foreign flows, short-selling, market warnings, ETF data, and index / industry / theme screens.
- **Live LS broker inquiry (v1.6+).** Ten read-only `ls_account_*` tools wrap the LS account family — holdings, orders, balance, BEP, credit limit, max orderable quantity, order history, transactions, period performance, daily P&L. Fresh REST snapshot on every call, no caching, no daemon. Order placement is **not** in v1.6 — that's v1.7.
- **Q-Click signal screeners (v1.4+).** LS-curated saved conditions (~99 signals: MA alignment, breakouts, short-side trend, foreign-flow streaks, …) discovered by keyword, run by name, and *combined* with AND / OR — compound conditions no single HTS screen offers.
- **Token-efficient by default.** Heavy responses are shaped for the model's context window — not the API's raw shape. Charts return summary-first payloads + dataset handles, raw bars stay off-context unless `output_mode: "export"` is asked for, and list tools cap rows with `limit`. The common chart / history / holdings payloads run 66-97% smaller than the pre-v0.9 shapes, so long multi-turn analysis conversations don't burn the budget on bar arrays.
- **A smaller routing surface.** The default `standard` profile exposes 50 semantic tools, while the 3 catalog escape-hatch tools stay hidden unless `LS_TOOL_PROFILE=all`.
- **Schema-split portfolio memory.** v1.6 separates the LS broker live row (auto-discovered, in its own `ls_accounts` table) from the manually-tracked paper portfolio (multi-broker accounts, holdings, watchlists, watched themes; `broker` field purely a display label). The two coexist physically — a paper "LS증권" account never shadows the live broker echo. JSON backup / restore covers the paper side; the live side is rediscovered on each appkey load.

Token-efficiency case study: [Same question, less context](docs/case-studies/v0.4.0-token-efficiency.en.md)

## Disclaimer

This is an **unofficial third-party MCP server**. It is not affiliated with, endorsed by, or sponsored by LS Securities Co., Ltd. "LS Securities" and related marks belong to their respective owners.

This tool provides **market data access for informational purposes only**. It does not constitute investment advice or a solicitation to trade. Trading carries risk, including loss of principal. All trading decisions and any resulting gains or losses are solely the user's responsibility.

When using the API, please review the [LS OpenAPI usage guide](https://openapi.ls-sec.co.kr/howto-use) and comply with the Terms of Service link in the site footer.

## Packages

| Package | Type | Purpose |
| --- | --- | --- |
| `RedoxNet.LsOpenApi.Core` | Library | SDK: auth (OAuth2 client_credentials), HTTP client, TR catalog, indicators. |
| `RedoxNet.Mcp.LsOpenApi` | dotnet tool | MCP server over stdio. |
| `RedoxNet.LsOpenApi.Core.Catalog.Builder` | Dev tool | Scrapes the LS docs site to (re)generate the embedded TR catalog. Not shipped. |

## Quick start

**Prerequisite.** `dnx` is the dotnet tool launcher that ships with **.NET SDK 10 or later**. Install from [.NET downloads](https://dotnet.microsoft.com/download/dotnet/10.0) if you don't have it yet (Windows / macOS / Linux). Verify with `dnx --help` — that should print the launcher help, not "command not found". The first `dnx RedoxNet.Mcp.LsOpenApi` call fetches the package from NuGet and caches it; subsequent launches start immediately.

Wire it into your MCP host:

### MCP standard config — Claude Desktop / Claude Code / Cursor / Google Antigravity

These hosts share the same `mcpServers` JSON schema. Paste the block below into the matching config file and restart the host.

| Host | Config path |
|---|---|
| Claude Desktop (Windows)   | `%APPDATA%\Claude\claude_desktop_config.json` |
| Claude Desktop (macOS)     | `~/Library/Application Support/Claude/claude_desktop_config.json` |
| Claude Code                | `.mcp.json` (workspace) or `~/.claude.json` (user) |
| Cursor                     | `.cursor/mcp.json` (workspace) or `~/.cursor/mcp.json` (user) |
| Google Antigravity (Windows)        | `%USERPROFILE%\.gemini\antigravity\mcp.config.json` |
| Google Antigravity (macOS / Linux)  | `~/.gemini/antigravity/mcp.config.json` |

```jsonc
{
  "mcpServers": {
    "lsopenapi": {
      "command": "dnx",
      "args": ["RedoxNet.Mcp.LsOpenApi", "--yes"],
      "env": {
        "LS_APPKEY": "...",
        "LS_APPSECRETKEY": "...",
        "LS_MARKET": "real"   // default if omitted; use "virtual" only for LS mock accounts
      }
    }
  }
}
```

Among these hosts, the inline chart surface (Plotly v5 spec rendered in chat) lights up on every host that advertises the SEP-1865 `io.modelcontextprotocol/ui` capability — empirically verified on Claude Desktop and Claude Cowork; Cursor advertises the capability per its changelog (not yet directly verified by us). Google Antigravity currently advertises no UI capability, so chart-emitting tools fall through to the text-only path: `include_chart` drops out of the tool schema, the chart spec is stripped, and `_meta.render_status: "stripped_text_only"` steers the model into honest narration (per `ServerInstructions` it must not claim it drew the chart and must not synthesize one of its own). See [`docs/MCP-APPS-INTEROP.md`](docs/MCP-APPS-INTEROP.md) §2 for the empirical host matrix.

### Codex CLI

`%USERPROFILE%\.codex\config.toml` (Windows) or `~/.codex/config.toml` (macOS / Linux):

```toml
[mcp_servers.lsopenapi]
command = "dnx"
args = ["RedoxNet.Mcp.LsOpenApi", "--yes"]

[mcp_servers.lsopenapi.env]
LS_APPKEY = "..."
LS_APPSECRETKEY = "..."
LS_MARKET = "real"  # default if omitted; use "virtual" only for LS mock accounts
```

### VS Code (`mcp.json`)

Workspace `.vscode/mcp.json` — VS Code uses a `servers` top-level key (not `mcpServers`) and an explicit `"type": "stdio"`:

```jsonc
{
  "servers": {
    "lsopenapi": {
      "type": "stdio",
      "command": "dnx",
      "args": ["RedoxNet.Mcp.LsOpenApi", "--yes"],
      "env": {
        "LS_APPKEY": "...",
        "LS_APPSECRETKEY": "...",
        "LS_MARKET": "real"  // default if omitted; use "virtual" only for LS mock accounts
      }
    }
  }
}
```

## Getting an API Key

You need an **AppKey** and **AppSecretKey** pair issued by LS Securities to use this MCP server.

### Prerequisites

- **LS Securities account** — Open via mobile app, online, or branch office. Additional permissions (overseas equities, derivatives, etc.) require separate agreement signups — see [Additional setup for overseas (US) stocks](#additional-setup-for-overseas-us-stocks) below.
- LS Securities home-trading ID.

### Issuance Steps

1. Log in to the [LS OpenAPI Portal](https://openapi.ls-sec.co.kr/) with your LS Securities ID.
2. Navigate to **OpenAPI Application** → agree to the terms → submit application.
3. After approval, find your **AppKey** and **AppSecretKey** under **MY > API Key Management**.
   - The **AppSecretKey is shown only once** at issuance — store it in a password manager (1Password, Bitwarden, etc.) immediately.
4. Use `LS_MARKET=real` for normal read-only market data. The server still accepts `LS_MARKET=virtual` for LS mock-account workflows, but quote/chart/screener data is served from the same LS OpenAPI host and `real` is the default.

### Real vs virtual (모의투자): separate AppKey pairs

LS issues **two separate AppKey / AppSecretKey pairs** — one for the real (실투) account and one for the virtual (모의투자) account. They are issued from the same portal page (**LS Securities → 고객센터 → 매매시스템 → API** for the Korean menu): the `Open API` radio gives a real pair, the `모의투자 Open API` radio gives a virtual pair. The REST endpoint is identical for both — `https://openapi.ls-sec.co.kr:8080` — so the appkey pair itself determines which account answers a TR call. See [LS-API-QUIRKS §4.2d](docs/LS-API-QUIRKS.md) for the full explanation.

What this means for `LS_MARKET`:
- It is **NOT a switch that flips the same key between modes**. It is a **runtime declaration of which kind of key pair is currently injected** — real (실전용) or virtual (모의용).
- The REST endpoint is already decided by the appkey pair itself. `LS_MARKET` only labels the server's own state, and the scope of that label is narrow: it tags the auto-discovered row in the live `ls_accounts` registry (keyed by `account_no + mode`), namespaces the token cache (so real and virtual tokens don't overwrite each other), echoes the resolved mode on `_meta.account_used`, and (in v1.7) will gate placement-safety. **Paper portfolios are mode-agnostic**: every paper account, holding, and watchlist row in `accounts` / `holdings` / `watchlist_*` is visible regardless of which mode is loaded.
- Set it to match the loaded appkey pair: `real` when `LS_APPKEY` is the real-account pair, `virtual` when it's the mock pair. A mismatch (real key + `LS_MARKET=virtual`) won't break anything — the broker still answers from the key — but the auto-discovered nickname will read `LS-virtual-{acntno}` even though the data is real. Relabel the live row via `ls_account(action="set_live_nickname", account_number="...", nickname="...")` (the paper-account `upsert` action is a separate surface and does not touch the live registry).

To switch between modes in practice, swap **both** the appkey pair **and** `LS_MARKET` in the host config and restart the MCP server.

### Security Notes

- Never commit the AppSecretKey to git or share it in chat transcripts. Store it in GitHub Secrets for CI workflows, or in your machine's environment variables for local use.
- If you suspect a leak, **regenerate the key** on the LS OpenAPI portal immediately to invalidate the old one.
- Access tokens issued by the OAuth endpoint are valid for 24 hours; this package refreshes them automatically.

For more details from LS, see the [official usage guide](https://openapi.ls-sec.co.kr/howto-use) (Korean only).

## Additional setup for overseas (US) stocks

Starting v1.3, the server covers US-listed stocks (Nasdaq / NYSE / AMEX) — quotes, charts, master search. **Skip this section if you only need Korean stocks.** US data requires two extra agreement signups at LS Securities, **but no new API key is issued** — LS accumulates permissions on the same `AppKey` / `AppSecretKey` pair as the agreements are signed.

### Signup order

1. **[HTS 2004] Overseas Stock Trading Agreement** — adds US trading permission to the account. In LS xingTrader, jump to screen `2004` or navigate `해외시장 → 해외주식 → 거래신청`. Sign the four disclosure documents (외화증권 거래설명서 / 해외주식 투자위험고지 / 해외주식 직접투자 신고 등 유의사항 / 개인신용정보 필수동의서). A 종합매매 (comprehensive) account works as-is — no new account needed.

2. **[HTS 2007] Real-time Overseas Quote Subscription** — free for the US. Screen `2007` or `해외시장 → 해외주식 → 실시간 시세신청`. You must also complete **비전문가 (non-professional / individual customer) registration** in the same flow, otherwise the real-time permission is not granted. Without [2007], LS serves **15-minute-delayed quotes** by default. LS may auto-cancel this subscription if you go an extended period without logging in to LS HTS / MTS or placing US trades — keep the LS client opened periodically.

3. **(If a `대차거래서비스` / stock-lending agreement exists, cancel it first)** — via LS Securities → 마이페이지 → 약정관리, or by calling `1544-9911`. An active 대차거래 agreement blocks the OpenAPI overseas signup at step 4.

4. **[Web] Register the Overseas Stock OpenAPI** — LS Securities → 마이페이지 → 매매시스템 → 사용등록/해지 → select the "해외주식 API" radio → `사용등록`. Permission is added to your existing `AppKey` / `AppSecretKey` pair; environment variables stay the same.

The v1.3 tools become available immediately after step 4 — `ls_search_overseas_stock`, `ls_get_overseas_quote`, `ls_get_overseas_chart`, plus follow-up handling (`ls_add_indicator` / `ls_reframe_chart`) on the same overseas datasets via the shared handle cache.

### Notes

- **No additional key issued** — KR and US share the same key pair. `LS_APPKEY` / `LS_APPSECRETKEY` env vars are unchanged.
- **Non-Display restriction** — LS's NYSE / Nasdaq data license is Display-only. The "AI assistant shows the result to the user" pattern this server is built for falls within Display use; automated trading bots and third-party data redistribution may violate LS terms.
- **Commissions** — US stock trades via the OPEN API channel are charged 0.25%. (No separate data-feed fee.) v1.3 tools are read-only, so no trading commissions apply — that becomes relevant only when order tools land in a later release.
- **Time zones** — quote timestamps are Seoul wall-clock (`timestamp_tz: "Asia/Seoul"`); daily / minute bars are US Eastern (`bar_timezone: "America/New_York"`). An NVDA "5/22 day bar" is the NYSE 5/22 session, which spans 22:30 5/22 → 05:00 5/23 in Seoul time.

## Environment variables

| Name | Required | Description |
| --- | --- | --- |
| `LS_APPKEY` | yes | LS OpenAPI app key. |
| `LS_APPSECRETKEY` | yes | LS OpenAPI app secret key. |
| `LS_MARKET` | no | `real` or `virtual` (default `real`). |
| `LS_TOOL_PROFILE` | no | `standard` (default — hides the 3 catalog tools from `tools/list`) or `all` (exposes them). |
| `LS_TOOL_PROFILE_STRICT` | no | `true` rejects a `tools/call` for a profile-hidden tool instead of honoring it (default `false`). |
| `LS_BASEURL` | no | Override REST base URL (rarely needed). |
| `LS_LOG_LEVEL` | no | Minimum log level: `Trace`/`Debug`/`Information`/`Warning`/`Error`/`Critical`/`None`. Default `Information`. |
| `LSOPENAPI_DB_PATH` | no | Override the local portfolio SQLite path. Default: alongside `token.db`. |

Local storage lives at:
- Windows: `%LOCALAPPDATA%\RedoxNet\LsOpenApi\` (`token.db` + `portfolio.db`)
- Linux/macOS: `~/.local/share/redoxnet/lsopenapi/`

Both files are SQLite (WAL mode). Token cache keys are `SHA256(appkey):market`, so the raw app key never lives on disk; tokens auto-refresh 5 minutes before expiry. `portfolio.db` carries two logically distinct tables: (1) the paper-portfolio surface (`accounts` / `holdings` / `watchlist_*` / `watched_themes`) — mode-agnostic, holds user-entered holdings and watchlist entries only, never auto-synced with any brokerage; and (2) the live LS broker registry (`ls_accounts`) — auto-populated from the AcntNo / branch / account-name echoed by the first successful `ls_account_*` call, keyed by `(account_no, mode)`, used purely to label the model-facing echoes. **Live balances / orders / holdings are never cached** — every `ls_account_*` call hits LS REST fresh.

## Credential handling policy

This server accepts `LS_APPKEY` / `LS_APPSECRETKEY` **only through environment variables**. By design, **no credential is ever requested through chat, tool arguments, or MCP elicitation** — any path the model could observe. The expectation is that the host (Claude Desktop, AssistStudio, etc.) reads the secrets from the OS environment or its own credential store and injects them into the child process.

- **No plaintext on disk.** The token cache stores only `SHA256(appkey):market`; the raw app key and secret never leave process memory.
- **Not surfaced in logs, errors, or tool responses.** Diagnostic output shows `****` plus the last four characters of the app key only; the secret key is never logged in any form.
- **Auth errors from LS (e.g. `IGW00121`)** are converted to [`LsAuthException`](src/RedoxNet.LsOpenApi.Core/Auth/LsAuthException.cs) and surface in the tool response's `error` field only — the credentials you passed in are never echoed back.

This is the strictest reading of the MCP spec's guidance that ["Servers MUST NOT use elicitation to request sensitive information"](https://modelcontextprotocol.io/specification/2025-06-18/client/elicitation#security-considerations).

## Example Workflows

#### 1. Automated Daily Signal Report

Schedule an end-of-day analysis for your watchlist and deliver it to your messenger:

```
Scheduler (e.g. Mcp.Runner) → invokes the LLM
  → ls_get_chart(shcode, period_type="day,week,month", indicators=["ma:5","ma:20","ma:60"])
  → LLM evaluates context.bullish_alignment, divergence_from_ma, etc.
  → Messenger MCP (e.g. Mcp.Outbox) delivers a KakaoTalk/Slack report
```

#### 2. Natural-Language Quote Lookup

```
User: "Show me Samsung Electronics' current price and order book"
  → ls_get_quote(shcode="005930")
  → Returns latest price + 10-level bid/ask
```

#### 3. Indicator-Driven Analysis Dialog

```
User: "Has KODEX AI Power Infrastructure ETF hit a sell signal on its 12-period MA?"
  → ls_get_chart(shcode="490090", period_type="day,week,month",
                 indicators=["ma:12","ma:60"])
  → LLM reasons across day/week/month timeframes using the context block
  → "Monthly view: still in buy zone. Daily view: price approaching 12-MA
     with surging volume — short-term caution warranted."
```

#### 4. Interactive Charts

Chart-emitting tools can return a Plotly v5 JSON spec for inline rendering, gated (from v1.2) on what the connected host can render. **Inline rendering works on AssistStudio, Claude Desktop Chat, Claude Cowork, and VS Code Chat** — every host that advertises the SEP-1865 `io.modelcontextprotocol/ui` capability. From v1.5 the PlotlyTemplate handshake was tightened so hosts that *advertise* the capability *actually render*; earlier blank-card / white-card behavior was a server-side bug that has been fixed. **Codex CLI**, **Claude Code CLI**, and any other host that doesn't advertise the capability are not offered the `include_chart` parameter at all — they receive `_meta.render_status: "stripped_text_only"` and the model narrates honestly that no chart was shown. In every case the model receives a clean analytical summary, and the Plotly spec never enters its text context. See [docs/MCP-APPS-INTEROP.md](docs/MCP-APPS-INTEROP.md) §3 for the empirical host matrix.

## Tools

### Meta

These three catalog tools are hidden from `tools/list` in the default `standard` profile — set `LS_TOOL_PROFILE=all` to expose them.

| Tool | Purpose |
| --- | --- |
| `ls_search_tr` | Search the embedded TR catalog by Korean/English keyword. |
| `ls_describe_tr` | Full input/output schema for one TR code. |
| `ls_call_tr` | Invoke any TR with a caller-supplied `in_block` JSON object. |

### Semantic (market data)

| Tool | TR | Purpose |
| --- | --- | --- |
| `ls_get_quote` | `t1101` | Current price + 10-level order book for a single Korean stock. |
| `ls_search_overseas_stock` | `g3190` | Search the overseas stock master by ticker / Korean / English name and return `keysymbol`, `exchcd`, and `symbol` for follow-up calls. |
| `ls_get_overseas_quote` | `g3101` + optional `g3104` / `g3106` | US/overseas stock snapshot. Optional profile fields and 10-level order book. |
| `ls_get_multi_quote` | `t8407` | Compact price snapshot for **up to 50 stocks in one call** — price, OHLC, volume, best ask/bid, total ask/bid volume, trade strength. Use for side-by-side comparison or watchlists. |
| `ls_get_top_stocks` | `t1441` / `t1444` / `t1452` / `t1463` / `t1466` | Market-wide screeners — top gainers/losers/unchanged, market cap, volume, trading value, and volume surges. |
| `ls_get_stock_info` | `t1102` | Company profile + fundamentals: PER/PBR/EPS, quarterly financials and growth rates, 52-week + YTD ranges, top-5 buy/sell brokerages, foreign-investor activity, status flags (SPAC / administrative issue). |
| `ls_get_chart` | `t8410` / `t8412` / `t1301` | OHLCV charts (day/week/month/year/min/tick), optional indicators (SMA, EMA, RSI, MACD, Bollinger), token-efficient `summary` + `dataset_id`, **multi-timeframe in one call** (`period_type: "day,week,month"`). Raw bars are returned only with `output_mode: "export"`. |
| `ls_get_overseas_chart` | `g3204` / `g3203` / `g3202` | US/overseas stock OHLCV charts (day/week/month/year/min/tick), optional indicators, token-efficient summary/context, and optional Plotly chart rendering. |
| `ls_add_indicator` | process-local handle cache + chart TR | Add an indicator to a `dataset_id` and return the updated `summary` / chart spec. Example: "add MA200 too". |
| `ls_reframe_chart` | process-local handle cache + chart TR | Re-query the same `dataset_id` symbol with a different period/count and update the handle. Example: "switch this to daily for the last 6 months". |
| `ls_search_stock` | `t8436` | Find KOSPI/KOSDAQ codes by name fragment; surfaces SPAC and administrative-issue flags and an `instrument` filter (`all` / `stock` / `etf`). |
| `ls_get_etf_info` | `t1901` | ETF/ETN-specific snapshot — NAV, tracking-index value, premium/discount, AUM, up to 5 liquidity providers, 52-week + year ranges, related futures. |
| `ls_get_etf_holdings` | `t1904` | ETF portfolio deposit file (holdings) — per-holding weight, valuation, market cap, plus an ETF summary (NAV/AUM/cash). Heterogeneous holdings (bonds, cash) pass through verbatim. |

### `ls_get_chart` token-efficient payloads

By default, `ls_get_chart` does not place raw OHLCV arrays in the model context. It returns a follow-up `dataset_id`, a compact model-facing `summary`, and the existing `context` block. Raw `candles` and full `indicators` arrays are returned only when the caller explicitly uses `output_mode: "export"` for table/raw/CSV-style requests.

`output_mode`:

- `display` — chart rendering. Text contains `summary`; Plotly spec goes to `structuredContent.chart`.
- `analyze` — default model reasoning mode. Returns `summary` + `context`, no raw bars.
- `export` — returns raw OHLCV/indicator arrays. Token-expensive; use only for explicit raw data requests.
- `reference` — returns `dataset_id` and metadata only for follow-up tool calls.

`summary` includes latest price, period returns, moving-average snapshots, MA60 deviation and slope (a least-squares fit over the MA), drawdown from peak, and a bounded ZigZag key-turn list (`key_turns`). Each turn carries its peak/trough kind, percent change from the previous turn, and a confirmed/tentative flag (`is_confirmed`) — the trailing turn is the in-progress swing's provisional endpoint, not yet reversed past the threshold.

**Warm-up policy and `summary.coverage`** — `summary` is computed over a deeper warm-up window than the display window, so long moving averages, slope, and 1-year return stay populated even when `count` is small. The default policy is "no `from` → auto warm-up; explicit `from` → skip warm-up"; the `with_warmup` parameter overrides it:

| Intent | Call | Behavior |
|---|---|---|
| "Recent picture" | omit `from` | Warm-up applied automatically |
| "Just this window" | explicit `from` | Warm-up skipped |
| "Trends inside this window" | explicit `from` + `with_warmup=true` | Warm-up forced |
| "Fastest raw read" | `with_warmup=false` | Warm-up skipped (long indicators may be null) |

`summary.coverage` is present on every response so the model can explain which indicators are null and why. It carries a `warmup_applied` flag, `analytical_bar_count`/`display_bar_count`, and a `status` map where each indicator is one of `ok`/`insufficient_data`/`disabled`. When something is insufficient, `note` carries a one-line hint such as "Narrow window — re-run with `with_warmup=true` or remove the date range to populate them."

The `context` block keeps the existing pre-computed analytics:

- `divergence_from_ma` — latest close vs each `ma:N` / `ema:N` indicator, as a percent.
- `volume.{latest,avg_20,ratio_20,avg_60,ratio_60}` — volume vs trailing averages.
- `drawdown.{period_high,period_high_date,current,pct}` — distance from the period high.
- `ma_trend` — direction of each MA over the last 5 bars (`"up"` / `"down"` / `"flat"`).
- `bullish_alignment` — `true` when shorter-period MAs sit above longer-period MAs.

### Multi-timeframe in one call

Pass a comma-separated `period_type` and the response wraps a `frames[]` array, one compact entry per timeframe. Each frame carries its own `summary` / `context`; use `output_mode: "export"` only when raw bars are needed:

```jsonc
// ls_get_chart shcode=005930 period_type="day,week,month" indicators=["ma:5","ma:20","ma:60"]
{
  "shcode": "005930",
  "output_mode": "analyze",
  "dataset_id": "ds_a8f3...",
  "period_types": ["day", "week", "month"],
  "frames": [
    { "period_type": "day",   "tr_cd": "t8410", "count": 60, "summary": {...}, "context": {...} },
    { "period_type": "week",  "tr_cd": "t8410", "count": 60, "summary": {...}, "context": {...} },
    { "period_type": "month", "tr_cd": "t8410", "count": 60, "summary": {...}, "context": {...} }
  ]
}
```

A single `period_type` keeps the flat shape (`summary`, `context`, `dataset_id` at top level).

### Rendering charts (`include_chart: true`)

From **v1.2** the chart surface is gated on how the connected host consumes chart payloads — the host, not the model, decides whether a chart is shown:

- **MCP Apps host** — advertises the SEP-1865 `io.modelcontextprotocol/ui` capability, or is a known legacy chart renderer: `include_chart` is offered, and passing `include_chart: true` / `output_mode: "display"` ships a Plotly v5 JSON spec under `structuredContent.chart`. Verified on AssistStudio, Claude Desktop Chat, Claude Cowork, VS Code Chat, and the ext-apps `basic-host` reference (the last after the PlotlyTemplate handshake 3-fix that landed before v1.5).
- **Text-only host** — `include_chart` is dropped from the tool schema and `structuredContent.chart` is stripped from results, so the model only ever reads the analytical summary. From **v1.5** the response also carries `_meta.render_status: "stripped_text_only"` so the model has a hard signal not to claim it drew a chart; `delivered` is shipped on the MCP-Apps side.

`structuredContent.chart` is a UI side-channel — never duplicated into the model-facing text, which carries no "a chart exists" marker. The server emits the spec only — no server-side image rendering, no charting library dependency.

**v1.5 self-synthesis ban.** Regardless of `render_status`, ServerInstructions forbids the model from routing around the server's chart by fetching raw OHLCV with `output_mode=export` and rendering a chart itself (Python / JavaScript / PNG) or forwarding the spec to a generic visualize MCP. Self-rendered indicators won't match `summary.moving_averages` because the server uses a specific adjustment mode, warm-up window, and formula. Layout-level requests (panel height, sizing) are identified as *host panel constraints* rather than routed around. Chart customization is constrained to `ls_add_indicator` / `ls_reframe_chart`. `output_mode=export` responses carry `_meta.data_purpose: "analysis_only"` + `_meta.do_not_render` as an inline reminder.

```jsonc
// ls_get_chart shcode=005930 period_type=day count=60 indicators=["ma:5","ma:20"] include_chart=true
// model-facing text — summary only, no chart spec, no marker
{
  "shcode": "005930",
  "period_type": "day",
  "output_mode": "display",
  "dataset_id": "ds_a8f3...",
  "summary": {...},
  "context": {...}
}

// structuredContent
{
  "chart": {
    "type": "plotly",
    "version": "5",
    "spec": {
      "data": [
        { "type": "candlestick", "name": "OHLC",   "x": [...], "open": [...], "high": [...], "low": [...], "close": [...], "increasing": { "line": { "color": "#E74C3C" } }, "decreasing": { "line": { "color": "#3498DB" } }, "yaxis": "y" },
        { "type": "scatter",     "name": "MA:5",   "x": [...], "y": [...], "mode": "lines", "line": { "color": "#F39C12" }, "yaxis": "y" },
        { "type": "scatter",     "name": "MA:20",  "x": [...], "y": [...], "mode": "lines", "line": { "color": "#27AE60" }, "yaxis": "y" },
        { "type": "bar",         "name": "Volume", "x": [...], "y": [...], "marker": { "color": ["#E74C3C", "#3498DB", ...] }, "yaxis": "y2" }
      ],
      "layout": {
        "title": { "text": "005930 — Daily" },
        "xaxis": { "type": "category", "rangeslider": { "visible": false } },
        "yaxis":  { "title": { "text": "Price"  }, "domain": [0.3, 1.0] },
        "yaxis2": { "title": { "text": "Volume" }, "domain": [0.0, 0.25] },
        "hovermode": "x unified",
        "showlegend": true
      }
    }
  }
}
```

Korean broker convention: rising candles/bars are red (`#E74C3C`), falling are blue (`#3498DB`). This is the opposite of the US/European convention where green indicates rising and red falling.

Indicator handling in the chart:
- `ma:N`, `ema:N`, `bb:N,SD` → drawn as overlays on the price subplot.
- `rsi:N`, `macd:F,S,Sig` → available for computation but **not** drawn (they need their own subplot scale; future enhancement). Full series are returned in text only with `output_mode: "export"`.

Minimal HTML snippet to render the spec with Plotly.js:

```html
<!DOCTYPE html>
<html>
<head>
  <script src="https://cdn.plot.ly/plotly-2.35.2.min.js"></script>
</head>
<body>
  <div id="chart" style="width: 900px; height: 500px;"></div>
  <script>
    // `structuredContent` here is the UI side-channel returned by ls_get_chart.
    const { data, layout } = structuredContent.chart.spec;
    Plotly.newPlot("chart", data, layout, { responsive: true });
  </script>
</body>
</html>
```

Clients that don't embed Plotly.js can ignore `structuredContent.chart` — the text `summary` / `context` payload remains the model-facing source of truth.

### Portfolio (local-only, no broker sync)

These tools persist user-supplied data to a SQLite store at `%LOCALAPPDATA%\RedoxNet\LsOpenApi\portfolio.db` (override with `LSOPENAPI_DB_PATH`). They do not read or write any brokerage account; every entry is manual. Quote-enriching list responses fall back to a `quote_error` envelope when LS credentials are unavailable, but saved data still returns.

#### Accounts — `ls_account(action)`

| `action` | Purpose |
| --- | --- |
| `list` | Registered accounts with holdings counts and the `is_default` flag (empty array when none). The default is derived from this flag. |
| `upsert` | Create or update an account by `account_number` (+ unique `nickname`). `set_default=true` promotes it; the first account auto-promotes. `rename_broker_from` mode relabels a broker across every matching account. |
| `remove` | Two-step cascade — `confirm=false` returns `RequiresConfirmation` with a holding-count + market-value preview when holdings exist; `confirm=true` proceeds. Removing the default auto-promotes the oldest remaining account (id ASC). |

#### Holdings — `ls_holding(action)` + two standalone tools

The write path is the `ls_holding` dispatcher; `shcode` is required for every action.

| `ls_holding` action | Purpose |
| --- | --- |
| `set` | Replace a position with `(quantity, avg_price)` — initial registration. `quantity=0` is rejected (use `remove`). |
| `buy` | Record an incremental buy. Cost basis merges via `new_avg = (old.qty*old.avg + qty*price) / (old.qty + qty)`. Storage is integer fractional won (×10000) so split↔reverse-split round-trips are exact. |
| `sell` | Subtract from the position. Auto-removes the row at zero. Raises `InsufficientQuantity` when the requested quantity exceeds the position. |
| `remove` | Drop a holding row outright. Returns `removed=false` when the symbol is not held in any account. |
| `corporate_action` | Open-enum corporate action — `type ∈ {split, reverse_split, bonus}` (+ `ratio`); the enum is reserved for `stock_dividend` / `spin_off` / `merger`. With no `account`, applied across every account holding the symbol; reverse-split rejects non-divisible quantities. |

| Standalone tool | Purpose |
| --- | --- |
| `ls_holdings_list` | Holdings grouped by account with per-account `summary` and `total_summary`. Optional `account`, `theme_code` (exact), `theme_keyword` (LIKE on theme name), `industry` (FICS substring) filters; AND-combine. Envelope includes a top-level `metadata_freshness` block plus `matched_themes` / `matching_industries` echoes. Kept standalone — the read path is the single most common portfolio intent. |
| `ls_stocks_refresh_metadata(shcodes?, kinds?)` | Synchronous refresh for theme + FICS industry caches. Default scope = holdings ∪ watchlist symbols when `shcodes` omitted; `kinds` ∈ `themes` / `industry`. Blocks until LS calls finish (≈N seconds for N symbols at 1 TPS) and echoes per-symbol update flags. |

#### Watchlists & themes — `ls_watchlist(action)` + `ls_watched_themes(action)`

| `ls_watchlist` action | Purpose |
| --- | --- |
| `list` | Entries grouped by watchlist group, enriched with live quotes when credentials are present. `scope="groups"` returns group metadata only. Optional `group_name` filter. |
| `add` / `remove` | Add or drop a saved stock entry. `add` does a lazy `t8407` metadata fetch + `t1532` theme enrichment when credentials are present. |
| `group_upsert` | Create a group, update its description, or rename one (`rename_from`); rejects a rename onto an existing name. |
| `group_delete` | Delete a group; cascades its items. |

| `ls_watched_themes` action | Purpose |
| --- | --- |
| `list` / `add` / `remove` | Track LS theme codes (`t1531` tmcode such as `0064`); `list` carries each theme's `change_pct` (avgdiff). |

#### Index + industry

| Tool | Purpose |
| --- | --- |
| `ls_get_index_quote(index_code)` | Single Korean index snapshot via `t1511`. Aliases `kospi`/`kosdaq`/`kospi200`/`krx100`. Envelope includes value, change %, OHLC with timestamps, 52-week + YTD range, market breadth, 4 related auxiliary indices. |
| `ls_get_global_market_quote(kind?, symbol?)` | Overseas index / FX / futures snapshot via `t3521`. Aliases include `nasdaq`, `sp500`, `dow`, `soxx`, `usdkrw`, `wti`, `gold`; raw LS symbols like `NAS@IXIC` are accepted. |
| `ls_get_index_history(index_code, period_type?, count?, cts_date?, verbosity?, output_mode?, dataset_id?, from?, to?, recent_n?)` | Daily/weekly/monthly index time series via `t1514` — per-bar OHLC, volume, breadth, foreign/institutional net flow. `verbosity` (summary/compact/full) shapes the inline payload; `output_mode=export` caches the whole series under a `dataset_id` and a follow-up call with that id slices it (`from`/`to`/`recent_n`) with no further API call. |
| `ls_get_industry_indices(market, limit)` | Top-N industry indices sorted by change %. `t8424` + `t1511` fanout, 60s in-process cache (cold-cost ≈2.5s for KOSPI's ~25 codes; `limit=5` and `limit=30` reuse one fanout). |
| `ls_get_industry_stocks(upcode \| industry_keyword, market, limit)` | Stocks inside one industry + the industry's index summary. `t1516` body-based continuation paging. Keyword resolution against cached t8424: 0/1/N matches → `IndustryNotFound` / `resolved` echo / `AmbiguousIndustry` with candidates. |
| `ls_get_market_funds_trend(market?, count?)` | Market-liquidity time series via `t8428`. Per day the index plus 고객예탁금 / 신용잔고 / 미수금 / 선물예수금 and equity / mixed / bond / MMF fund money. All monetary fields in 억원. |

#### Screeners & per-stock analytics

| Tool | Purpose |
| --- | --- |
| `ls_get_fundamentals_rank(field, market?, limit?)` | Rank Korean stocks by one fundamental metric via `t3341`: `per` / `pbr` / `peg` / `eps` / `bps` / `roe` / `sales_growth` / `operating_income_growth` / `ordinary_income_growth` / `debt_to_equity` / `retained_earnings_ratio`. PER / PBR / PEG are LS-side forced ascending. Each row carries every metric so two-metric comparisons need no follow-up call. |
| `ls_get_investor_flow(shcode?, …)` | Investor-type flow across `t1601` (intraday market-wide) + `t1702` (single-stock daily) dispatcher. 12 investor categories (개인 / 외국인 / 기관계 / 증권 / 투신 / 은행 / 보험 / 종금 / 기금 / 국가 / 기타 / 사모펀드). No shcode → market snapshot per segment; with shcode → daily series with `metric`/`direction`/`cumulative` toggles. |
| `ls_get_stock_events(shcode, from?, to?, kinds?)` | Per-stock corporate-action / shareholder-meeting calendar via `t3202`. All 14 LS event types (dividends, AGMs, rights / bonus issues, capital changes, mergers/splits, stock-option exercises, CB conversions). TBD entries (`recdt='00000000'`) survive date filtering. |
| `ls_get_market_warnings(kinds?, shcodes?, market?, limit?)` | KRX surveillance list via `t1404` + `t1405`. 13 designations (관리 / 매매정지 / 정리매매 / 단기과열 / 투자위험 + 8 more). Default queries the five most operationally critical kinds. `shcodes` clips against holdings; `limit` (default 50) caps the rows. |
| `ls_get_analyst_opinions(shcode, count?)` | Per-stock brokerage (sell-side) investment-opinion history via `t3401`. Each entry carries the rating and target price before/after the change, the broker, and the opinion-day close, plus a current-price snapshot. |
| `ls_get_short_selling_trend(shcode, from?, to?, count?)` | Per-stock daily short-selling (공매도) trend via `t1927`. Short volume / value (백만원), short ratio, average short price, cumulative short volume, and the uptick-rule applied vs. exempt split. |
| `ls_get_high_low_stocks(direction?, period?, maintained?, market?, limit?, exclude_etf?)` | New-high / new-low (신고가 / 신저가) screener via `t1442`. `direction` high/low, `period` look-back (52w default), `maintained` 돌파유지 vs 일시돌파; ETF/ETN dropped by default. |

#### Saved-signal screeners — Q-Click (v1.4+)

LS curates ~99 saved condition expressions ("Q-Click signals") spanning chart / indicator / market-pattern / investor-flow templates (golden-cross alignment, MA breakouts, short-side trend, foreign-flow streaks, etc.). The three tools below expose the catalog as discoverable, runnable, and *combinable* screeners — they accept an exact name, the 4-character `search_cd`, or a Korean keyword. Ambiguous keywords return a candidate list plus the matching group's full mini-catalog (beta policy) so a follow-up call can target an exact id without an extra discovery round trip.

| Tool | TR | Purpose |
| --- | --- | --- |
| `ls_list_screeners(group?)` | `t1826` | Enumerate the Q-Click catalog (process-lifetime cached). Optional `group` filter: `core` / `indicator` / `market_trend` / `investor_trend` / `rapid_change`. |
| `ls_run_screener(query, limit?)` | `t1825` | Run one signal and return matching stocks (default row cap via `limit`). Rapid-change names whose price reads `0` for thinly-traded stocks are tagged `rapid_change_noise` rather than silently dropped. |
| `ls_combine_screeners(queries[], mode, limit?)` | `t1825` × N | Execute 2-8 signals and combine by shcode set operation — `mode=and` (intersection) or `mode=or` (union). Each row carries `signals_matched[]` so the model can narrate cross-signal confirmation. Duplicate inputs are deduped. This expresses compound conditions no single HTS screen offers. |

#### Program trading

| Tool | Purpose |
| --- | --- |
| `ls_get_program_trading(scope?, market?, period?, sort?, measure?, limit?, shcode?, name?, include_chart?, chart_view?)` | Program-trading (프로그램매매) flow over `t1662` / `t1633` / `t1636` / `t1637`. `scope=market` — market-wide intraday (t1662 — a ~1-minute 차익 / 비차익 series with the KOSPI200 index and basis) or daily (t1633); `scope=ranking` — the stocks programs are net buying / selling right now (t1636), with `mktcap_ratio` as a size-normalized footprint metric; `scope=stock` — one stock's intraday / daily flow (t1637). `include_chart=true` ships an inline Plotly v5 chart. |
| `ls_analyze_program_flow(shcode, name?, window?)` | Analysis-Layer program-trading footprint classifier for one stock — a regime (accumulation / distribution / churn / neutral), a 0–1 direction confidence, signals (buy-day persistence and streak, churn ratio, intensity, intraday pace, price coupling), and plain-language `evidence` ready to narrate. Built on `t1637`. |

#### LS themes

| Tool | Purpose |
| --- | --- |
| `ls_get_theme_stocks(theme_code \| theme_keyword, limit)` | Stocks inside one LS curated theme + summary (tmcnt/upcnt/uprate). `t1537` header-based `tr_cont`/`tr_cont_key` paging. Keyword resolution same 0/1/N branches as the industry tool. |
| `ls_get_stock_themes(shcode)` | Reverse lookup — every theme a stock belongs to via `t1532`. Empty array is a valid response (not every stock is themed). |

#### Portfolio I/O — `ls_portfolio_io(action)`

| `action` | Purpose |
| --- | --- |
| `export` | Versioned JSON snapshot (schema v1) of accounts/holdings/watchlists/watched themes. Default path `<db-parent>/exports/portfolio-<timestamp>.json`. Stock and theme metadata caches are intentionally excluded — rebuilt by enrichment after import. |
| `import` | `mode=merge` (default) skips duplicates with per-domain reason codes (`duplicate_account_number`, `duplicate_theme_code`, …); `mode=replace` requires `confirm=true` and writes a `before-import-*.json` auto-backup before the destructive wipe. Unsupported `schema_version` → `ImportSchemaMismatch`. |

#### Ambiguity policy

When a write tool does not specify `account`:

| Call | 0 accounts | 1 account | 2+ accounts |
| --- | --- | --- | --- |
| `ls_holdings_list` / `ls_account(action="list")` | empty | auto | grouped (default first) |
| `ls_holding(action="set"\|"buy")` | `RequiresAccount` | auto + `applied_to` echo | `AmbiguousAccount` (writes need an explicit target) |
| `ls_holding(action="sell"\|"remove")` | error / `removed=false` | auto + echo when the symbol is held in exactly one account | `AmbiguousAccount` when the symbol is in multiple accounts |
| `ls_holding(action="corporate_action")` | n/a | auto + echo | applies across every account holding the symbol; explicit `account` narrows to one |

Every write response includes `applied_to` — a single `{account_number, nickname, broker, is_default}` for one-account writes, or an array of before/after snapshots for corporate actions across multiple accounts.

#### Error envelopes

| `error` | Carries | When |
| --- | --- | --- |
| `RequiresAccount` | `message` | No accounts registered. |
| `AmbiguousAccount` | `message`, `candidates[]` | More than one valid target. |
| `AccountNotFound` | `message`, `identifier`, `candidates[]` | Account identifier did not resolve. |
| `RequiresConfirmation` | `message`, `account`, `holding_count`, `market_value` (account-remove) OR `source_path`, `accounts_in_file`, `holdings_in_file` (import replace) | `ls_account(action="remove")` without `confirm=true` when holdings exist; or `ls_portfolio_io(action="import", mode="replace")` without `confirm=true`. |
| `InsufficientQuantity` | `message`, `shcode`, `current_quantity`, `requested_quantity`, `applied_to` | `ls_holding(action="sell")` requested more shares than held. |
| `IndustryNotFound` / `ThemeNotFound` | `message`, `candidates[]` | Industry/theme keyword matched 0 catalog entries. |
| `AmbiguousIndustry` / `AmbiguousTheme` | `message`, `candidates[]` | Keyword matched 2+ catalog entries. |
| `ImportSchemaMismatch` | `message`, `file_schema_version`, `supported_schema_version` | Import file declares an unsupported `schema_version`. |
| `ValidationError` | `message` | Schema or domain rule violation (zero quantity, malformed shcode, non-divisible reverse split, nickname collision, unknown corporate-action type, …). |

Every envelope is structured so the LLM can recover automatically — `candidates` is populated wherever an account had to be selected, so the model can immediately re-call with a valid identifier without prompting the user.

#### Split / bonus warning

When `current_price / avg_price` diverges by 5× or more in either direction, the holding row carries a `warning` field hinting at a missed corporate action ("분할/무상증자 가능성: 현재가/평단 비율 N배"). The model can then suggest `ls_holding(action="corporate_action", type="split", ratio=…)` or a manual `ls_holding(action="set")` correction.

### Indicator specs (for `ls_get_chart`)

| Spec | Effect |
| --- | --- |
| `ma:N` | Simple moving average over N periods. |
| `ema:N` | Exponential moving average. |
| `rsi:N` | Relative Strength Index. |
| `macd:F,S,Sig` | MACD (fast/slow/signal). Returns `.macd`, `.signal`, `.histogram`. |
| `bb:N,SD` | Bollinger Bands. Returns `.lower`, `.middle`, `.upper`. |

## Building from source

```bash
dotnet restore mcp-lsopenapi.slnx
dotnet build mcp-lsopenapi.slnx -c Release
dotnet test mcp-lsopenapi.slnx -c Release
```

Local invocation:
```bash
dotnet run --project src/RedoxNet.Mcp.LsOpenApi --framework net8.0
```

## Status

Current release line:

- [x] **v0.10.1** — Functional v1.0 release candidate. Published to NuGet and the MCP Registry; includes the compressed 32-tool `standard` surface, token-budget fixes, and the MCP registry manifest fix.
- [x] **v1.0.0** — First stable release. Freezes the MCP tool surface, model-facing parameter names, and default response shapes, with reflection-based pin tests guarding them. Completes the row-cap `limit` normalization (`ls_get_etf_holdings` + `ls_get_industry_indices`), adds MCP server-instructions routing guidance, and ships NuGet credential metadata.
- [x] **v1.1.0** — Program-trading support, additive on the v1.0 stable line. Adds `ls_get_program_trading` (market / ranking / stock scopes over t1662 / t1633 / t1636 / t1637, with inline Plotly charts) and `ls_analyze_program_flow` (deterministic program-trading footprint analysis). Tool surface 32 → 34 (`standard`) / 35 → 37 (`all`); no existing tool, parameter, or response shape changes.
- [x] **v1.2.0** — MCP Apps capability negotiation. Chart-emitting tools now gate their inline Plotly payload on what the connected host can actually render — a correctness slice that stops chart-less hosts from receiving a payload they would bury in the model's context, and from being told a chart exists when none can be shown.
- [x] **v1.3.0** — First-class overseas stock support: `ls_search_overseas_stock`, `ls_get_overseas_quote`, and `ls_get_overseas_chart`, plus catalog fallback for the nine g31xx/g32xx overseas-stock TRs.
- [x] **v1.4.0** — Two additive slices: (A) **Date envelope** — date-bearing tools accept `query_date` and echo `data_as_of` + `query_date_resolution` (`used` / `weekend` / `holiday` / `future_date` / `pre_market`), so non-trading-day fallbacks are explicit to the model (wired on `ls_get_market_funds_trend` + `ls_get_short_selling_trend`; remaining ~10 follow in v1.5+). (B) **Q-Click signal screeners** — `ls_list_screeners` / `ls_run_screener` / `ls_combine_screeners` over t1825/t1826, exposing LS's ~99-signal curated catalog with AND/OR set combination.
- [x] **v1.5.0** — Fidelity-first chart narration. Chart-emitting tools now ship `_meta.render_status` (`delivered` / `stripped_text_only`) so the model has a hard signal whether the host received the chart; ServerInstructions forbids self-synthesis fallbacks (Python / JavaScript / PNG / visualize-MCP forward) regardless of `render_status`; chart customization is constrained to `ls_add_indicator` / `ls_reframe_chart`; layout-level requests are identified as host panel constraints. `output_mode=export` responses carry `_meta.data_purpose: "analysis_only"` + `_meta.do_not_render`. Surface unchanged (40/43). See [docs/SPEC-v1.5.md](docs/SPEC-v1.5.md).
- [x] **v1.6.0** — Read-only access to your actual LS Securities account: current holdings, balance, order history, daily P&L, performance, BEP, orderable capacity, credit limit. Ten new `ls_account_*` tools pull live broker state on every call — never cached. **Schema-split**: paper portfolios live in the `accounts` table (`broker` = display label only), the auto-discovered LS broker row lives in the new `ls_accounts` table — physically separate so a paper "LS증권" label never shadows the live broker echo. `ls_account_*` tools take no `account` parameter (LS REST routes through the authenticated session, not a client-side selector). Surface 40 → 50 standard / 43 → 53 all. See [docs/SPEC-v1.6.md](docs/SPEC-v1.6.md).

Planned:

- [ ] **v1.7.0** — Order placement (place / modify / cancel) with multi-layer safety. Paper-trading is the default; live trading requires explicit consent across multiple gates. Every order requires a `ls_preview_order` step first that validates against your balance, holdings, market state, and your own trading rules — the LLM cannot place an order without a valid preview ID. Optional user-defined trading rules (`ls_trading_policy`) act as a hard block (e.g. "never place orders above 5 million KRW"). Every order attempt is logged locally. See [docs/SPEC-v1.7.md](docs/SPEC-v1.7.md).
- [ ] **Daily-candle SQLite cache + saved screener macros** — deferred until usage evidence justifies the permanent-state cost. See [docs/SPEC-v1.5.md §1.5](docs/SPEC-v1.5.md) Appendix A / B.

Major milestones:

- [x] **v0.4.0** — Token-efficient chart payloads, dataset-handle follow-up tools, ZigZag swing detection, and `IndicatorCoverage`.
- [x] **v0.5.0-v0.7.0** — Local portfolio notes, multi-account holdings, watchlists, watched themes, JSON backup / restore, index / industry / theme context, screeners, stock events, market warnings, and FICS industry enrichment.
- [x] **v0.8.0** — Overseas index / FX / futures snapshots, analyst opinions, short-selling, new-high / new-low, and market-funds wrappers.
- [x] **v0.9.0** — Response-shape / token-economy refactor: `ls_get_index_history`, `ls_get_stock_info`, and `ls_holdings_list` defaults cut common payloads by 66-97%.
- [x] **v0.10.0** — Tool-surface compression: 48 tools collapsed to 32 in the default `standard` profile / 35 in `all`; portfolio writes moved to action-routed dispatchers; list/screener caps standardized on `limit`.
Realtime WebSocket feeds are not currently planned.

Full changelog: [RELEASENOTES.Mcp.md](RELEASENOTES.Mcp.md) · [RELEASENOTES.Core.md](RELEASENOTES.Core.md).

## License

MIT.
