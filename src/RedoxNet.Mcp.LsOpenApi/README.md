<!-- mcp-name: io.github.redoxnet/lsopenapi -->

# RedoxNet.Mcp.LsOpenApi

MCP server for the **LS증권 OpenAPI** — exposes Korean stock market data as MCP tools so AI assistants can query quotes, charts, ETF data, market screeners, and index / industry / theme context in natural language, alongside a local-only portfolio module (multi-account holdings, watchlists, watched themes, JSON backup / restore).

> Unofficial third-party MCP server. Not affiliated with or endorsed by LS Securities Co., Ltd. (LS증권). v0.x.x scope: read-only market data + local portfolio notes (manual entry; no broker sync, no order placement).

## Install

**Prerequisite.** `dnx` is the dotnet tool launcher that ships with **.NET SDK 10 or later**. Install from [.NET downloads](https://dotnet.microsoft.com/download/dotnet/10.0) if you don't have it yet. Verify with `dnx --help`.

`dnx` fetches the latest published version from NuGet on every launch — no separate install step. Wire it into your MCP host:

### Claude Desktop / Claude Code

`claude_desktop_config.json` (Claude Desktop) or `.mcp.json` at your workspace root (Claude Code):

```jsonc
{
  "mcpServers": {
    "lsopenapi": {
      "command": "dnx",
      "args": ["RedoxNet.Mcp.LsOpenApi", "--yes"],
      "env": {
        "LS_APPKEY": "...",
        "LS_APPSECRETKEY": "...",
        "LS_MARKET": "virtual"  // "virtual" (paper) or "real" (live)
      }
    }
  }
}
```

### Codex CLI

`%USERPROFILE%\.codex\config.toml` (Windows) or `~/.codex/config.toml` (macOS / Linux):

```toml
[mcp_servers.lsopenapi]
command = "dnx"
args = ["RedoxNet.Mcp.LsOpenApi", "--yes"]

[mcp_servers.lsopenapi.env]
LS_APPKEY = "..."
LS_APPSECRETKEY = "..."
LS_MARKET = "virtual"  # "virtual" (paper) or "real" (live)
```

### VS Code

Workspace `.vscode/mcp.json`:

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
        "LS_MARKET": "virtual"  // "virtual" (paper) or "real" (live)
      }
    }
  }
}
```

### FieldCure AssistStudio

**Settings → Connect → Add MCP Server**, then fill the dialog:

| Field | Value |
|---|---|
| Server Name | Any label, e.g. `LS Open Api` |
| Description (for AI) | Leave blank — auto-filled from the server on first connect |
| Transport | `Stdio` |
| Command | `dnx` |
| Arguments | `RedoxNet.Mcp.LsOpenApi --yes` &nbsp;— space-separated, **no quotes or commas** |
| Environment Variables | one `KEY=VALUE` per line (see below) |

```
LS_APPKEY=...
LS_APPSECRETKEY=...
LS_MARKET=virtual
```

AssistStudio renders the optional Plotly chart spec from `ls_get_chart` inline in the
chat — call it with `include_chart=true` (single timeframe) to get a candlestick chart
directly in the conversation.

## Environment variables

| Name | Required | Description |
|---|---|---|
| `LS_APPKEY` | yes | LS OpenAPI app key. |
| `LS_APPSECRETKEY` | yes | LS OpenAPI app secret key. |
| `LS_MARKET` | no | `real` or `virtual` (default `virtual`). |
| `LS_TOOL_PROFILE` | no | `standard` (default — hides the 3 catalog tools from `tools/list`) or `all` (exposes them). |
| `LS_TOOL_PROFILE_STRICT` | no | `true` rejects a `tools/call` for a profile-hidden tool instead of honoring it (default `false`). |
| `LS_BASEURL` | no | Override REST base URL (rarely needed). |
| `LS_LOG_LEVEL` | no | `Trace`/`Debug`/`Information`/`Warning`/`Error`/`Critical`/`None` (default `Information`). |
| `LSOPENAPI_DB_PATH` | no | Override the local portfolio SQLite path. Default: alongside `token.db`. |

Credentials are accepted **only** through the process environment — never through chat, tool arguments, or MCP elicitation. Prompting for them in conversation would either log them or train callers to share them in transcripts, so that input path is intentionally closed off.

Local data lives at `%LOCALAPPDATA%\RedoxNet\LsOpenApi\` on Windows and `~/.local/share/redoxnet/lsopenapi/` on Linux/macOS: `token.db` (auth cache, SHA-256 keyed) and `portfolio.db` (user-supplied holdings/watchlists; never read or written by tools outside the portfolio family).

## Tools (32 in the `standard` profile)

v0.10 (BREAKING) compresses the tool surface: the twenty v0.9 portfolio tools fold into **five action-routed dispatchers** (`ls_account`, `ls_watchlist`, `ls_watched_themes`, `ls_portfolio_io`, `ls_holding`), and the new `LS_TOOL_PROFILE` env var hides the three catalog tools in the default `standard` profile. Surface 48 → **32** (`standard`) / 35 (`all`). Additive in the same release: `ls_get_stock_info` gains an opt-in `foreign` ownership section (t1716); list / screener tools unify their row cap to `limit` and emit `total_available`; `ls_get_index_history` gains `output_mode=export` with a `dataset_id` drill.

### Market data (LS-backed, credentials required)

*`ls_search_tr` / `ls_describe_tr` / `ls_call_tr` are catalog tools — hidden in the default `standard` profile; set `LS_TOOL_PROFILE=all` to expose them.*

| Tool | TR | Purpose |
|---|---|---|
| `ls_search_tr` | — | Search the embedded TR catalog by Korean / English keyword. |
| `ls_describe_tr` | — | Full InBlock / OutBlock schema for a specific TR. |
| `ls_call_tr` | any | Invoke any TR with a caller-supplied `in_block`. |
| `ls_get_quote` | `t1101` | Current price + 10-level order book. |
| `ls_get_multi_quote` | `t8407` | Up to 50 stocks per call. Accepts 6-character codes (digits, optionally one uppercase letter for ETFs e.g. `0117V0`). |
| `ls_get_top_stocks` | `t1441` / `t1444` / `t1452` / `t1463` / `t1466` | Top gainers/losers, market cap, volume, trading value, and volume-surge screeners. |
| `ls_get_stock_info` | `t1102` + `t1716` | PER/PBR/EPS, quarterly financials, 52-week + YTD ranges, top-5 brokerages, SPAC / 관리종목 flags, and an opt-in `foreign` ownership-level section. Pick blocks with `sections` (default `snapshot`+`fundamentals`). |
| `ls_get_chart` | `t8410` / `t8412` / `t1301` | OHLCV (day/week/month/year/min/tick), indicators (SMA/EMA/RSI/MACD/BB), token-efficient `summary` + `dataset_id`, multi-timeframe in one call, optional Plotly v5 chart spec. Raw bars only with `output_mode='export'`; `with_warmup` toggles the summary warm-up; `summary.coverage` explains any null indicators. |
| `ls_add_indicator` | (handle cache + chart TR) | Adds an indicator to a `dataset_id` returned by `ls_get_chart` and returns the updated `summary` + chart spec. Example: *"add MA200 too"*. |
| `ls_reframe_chart` | (handle cache + chart TR) | Reframes a `dataset_id` to a new period/count using the cached symbol. Example: *"이걸 일봉으로 바꿔서 최근 6개월만 보여줘"*. |
| `ls_search_stock` | `t8436` | Name → code search with `instrument` filter (`all` / `stock` / `etf`). |
| `ls_get_etf_info` | `t1901` | ETF/ETN snapshot — NAV, 괴리율, 추적오차율, reference index, AUM, LP list. |
| `ls_get_etf_holdings` | `t1904` | ETF PDF (구성종목) — per-holding weight / valuation. `limit` caps the rows (default 20; `limit=-1` for the full list). |
| `ls_get_global_market_quote` | `t3521` | Overseas index / FX / futures snapshot. Aliases include `nasdaq`, `sp500`, `dow`, `soxx`, `usdkrw`, `wti`, `gold`; raw LS symbols like `NAS@IXIC` are accepted. |

### Index + industry (LS-backed)

| Tool | TR | Purpose |
|---|---|---|
| `ls_get_index_quote` | `t1511` | Single Korean index snapshot. Aliases: `kospi`/`kosdaq`/`kospi200`/`krx100`. Returns value, change %, OHLC with timestamps, 52-week + YTD range, market breadth, and 4 related auxiliary indices. |
| `ls_get_index_history` | `t1514` | Daily/weekly/monthly index time series — per-bar OHLC, volume, breadth, foreign/institutional net flow. `verbosity` shapes the payload; `output_mode=export` caches the whole series under a `dataset_id` for no-API-call drill (`from` / `to` / `recent_n`). |
| `ls_get_industry_indices` | `t8424` + `t1511` fanout | Top-N industry indices sorted by change %. 60s cache so repeated calls with different `top_n` reuse one fanout. |
| `ls_get_industry_stocks` | `t1516` | Stocks inside one industry + the industry's index summary. Body-based paging. Accepts `upcode` or `industry_keyword` (LIKE on cached t8424 catalog). |
| `ls_get_market_funds_trend` | `t8428` | Market-liquidity time series — 고객예탁금, 신용잔고, 미수금, 선물예수금, and equity/mixed/bond/MMF fund money (억원). |

### LS themes (LS-backed)

| Tool | TR | Purpose |
|---|---|---|
| `ls_get_theme_stocks` | `t1537` | Stocks inside one LS curated theme + summary (tmcnt/upcnt/uprate). Header-based `tr_cont` paging. Accepts `theme_code` or `theme_keyword`. |
| `ls_get_stock_themes` | `t1532` | Reverse lookup — every theme a stock belongs to. Empty array is a valid response. |

### Screeners & per-stock analytics (LS-backed)

| Tool | TR | Purpose |
|---|---|---|
| `ls_get_fundamentals_rank` | `t3341` | Rank stocks by a fundamental metric: `per` / `pbr` / `peg` / `eps` / `bps` / `roe` / 매출액·영업이익·세전계속이익 증가율 / 부채비율 / 유보율. PER/PBR/PEG forced ascending. Each row carries the full fundamental snapshot so two metrics on the same stock are visible in one call. |
| `ls_get_investor_flow` | `t1601` + `t1702` | Investor-type flow across 12 categories (개인 / 외국인 / 기관계 / 증권 / 투신 / 은행 / 보험 / 종금 / 기금 / 국가 / 기타 / 사모펀드). No `shcode` → intraday market-wide snapshot (six unlabeled segments). With `shcode` → single-stock daily time series with metric (`volume`/`value`/`price`) + direction (`net`/`buy`/`sell`) + cumulative toggle. |
| `ls_get_stock_events` | `t3202` | Per-stock corporate-action / 주주총회 calendar covering all 14 LS event types. `kinds` accepts English snake_case, Korean labels, or raw two-char upgu codes. TBD entries survive date filtering. |
| `ls_get_market_warnings` | `t1404` + `t1405` | Union of the two KRX surveillance screens (13 designations: 관리 / 불성실공시 / 투자유의 / 투자환기 / 투자경고 / 매매정지 / 정리매매 / 투자주의 / 투자위험 / 위험예고 / 단기과열지정 / 이상급등 / 상장주식수부족). `shcodes` clips against holdings for "내 보유 중 관리종목" queries. |
| `ls_get_analyst_opinions` | `t3401` | Per-stock brokerage (sell-side) investment-opinion history — rating + target price before/after each change, broker, opinion-day close, plus a current-price snapshot. |
| `ls_get_short_selling_trend` | `t1927` | Per-stock daily short-selling (공매도) — short volume/value (백만원), short ratio, average short price, cumulative short volume, uptick-applied vs. exempt split. |
| `ls_get_high_low_stocks` | `t1442` | New-high / new-low (신고가 / 신저가) screener. `direction`, `period` (52w default), `maintained` (돌파유지 vs 일시돌파); ETF/ETN excluded by default. |

### Portfolio (local-only, no broker sync)

Manual entries persisted to `portfolio.db` next to `token.db`. v0.10 folds the twenty v0.9 portfolio tools into **five action-routed dispatchers** — each takes an `action` argument and validates the per-action required parameters — plus two standalone tools. List responses fall back to a `quote_error` envelope when LS credentials are missing, but saved data still returns.

| Tool | Actions | Purpose |
|---|---|---|
| `ls_account` | `list` / `upsert` / `remove` | Registered accounts. `upsert` also renames a broker label across accounts (`rename_broker_from` mode); `remove` is a two-step `confirm` cascade with auto-succession of the default (id ASC). |
| `ls_holding` | `set` / `buy` / `sell` / `remove` / `corporate_action` | Holding writes — initial state, weighted-average buy merge, partial/full sell (auto-remove at zero, `InsufficientQuantity` above the position), outright delete, and the open-enum corporate action (`type` ∈ split / reverse_split / bonus). |
| `ls_holdings_list` | — | Holdings grouped by account with per-account + total summary. Optional `account`, `theme_code`, `theme_keyword`, `industry` (FICS substring) filters (AND-combine). Standalone — the read path is the most common portfolio intent. |
| `ls_stocks_refresh_metadata` | — | Synchronous refresh for theme / FICS-industry caches. Default scope = holdings ∪ watchlist symbols when `shcodes` omitted. Standalone. |
| `ls_watchlist` | `list` / `add` / `remove` / `group_upsert` / `group_delete` | Saved watchlist items and their groups. `list` takes `scope` (`items` / `groups`); `group_upsert` creates, updates, or renames a group (`rename_from`). |
| `ls_watched_themes` | `list` / `add` / `remove` | Track LS theme codes (`t1531` tmcode such as `0064`); `list` carries each theme's avg percent change. |
| `ls_portfolio_io` | `export` / `import` | Versioned JSON snapshot (schema v1) of accounts/holdings/watchlists/watched themes. `import mode=replace` requires `confirm=true` and writes a `before-import-*.json` auto-backup. |

**Ambiguity policy.** Reads fall back; writes require an explicit target when ambiguous. 0 accounts → `RequiresAccount`; 1 account → auto with `applied_to` echo; 2+ → `AmbiguousAccount` with `candidates[]` so the model can re-call without prompting the user. Every mutation response includes `applied_to` (single account) or `applied_to[]` with before/after snapshots (corporate actions).

**Error envelopes.** `RequiresAccount` / `AmbiguousAccount` / `AccountNotFound` / `RequiresConfirmation` / `InsufficientQuantity` / `ValidationError` — all carry structured fields (candidates, identifier, holding count + market value, etc.) so the LLM can recover automatically.

Full release notes: https://github.com/redoxnet/mcp-lsopenapi/blob/main/RELEASENOTES.Mcp.md

## Documentation & source

- Project home: https://github.com/redoxnet/mcp-lsopenapi
- TR inventory: https://github.com/redoxnet/mcp-lsopenapi/blob/main/docs/LS-TR-INVENTORY.md
- SDK package: https://www.nuget.org/packages/RedoxNet.LsOpenApi.Core/
- License: MIT
