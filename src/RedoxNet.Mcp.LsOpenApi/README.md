# RedoxNet.Mcp.LsOpenApi

MCP server for the **LS증권 OpenAPI** — exposes Korean stock market data as MCP tools so AI assistants can query quotes, charts, and ETF data in natural language. **v0.5 adds a local-only portfolio module** for tracking holdings across multiple brokerage accounts, watchlists, and themed sectors, all through conversation.

> Unofficial third-party MCP server. Not affiliated with or endorsed by LS Securities Co., Ltd. (LS증권). v0.x.x scope: read-only market data + local portfolio notes (manual entry; no broker sync, no order placement).

## Install

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
| `LS_BASEURL` | no | Override REST base URL (rarely needed). |
| `LS_LOG_LEVEL` | no | `Trace`/`Debug`/`Information`/`Warning`/`Error`/`Critical`/`None` (default `Information`). |
| `LSOPENAPI_DB_PATH` | no | Override the local portfolio SQLite path. Default: alongside `token.db`. |

Credentials are accepted **only** through the process environment — never through chat, tool arguments, or MCP elicitation. Prompting for them in conversation would either log them or train callers to share them in transcripts, so that input path is intentionally closed off.

Local data lives at `%LOCALAPPDATA%\RedoxNet\LsOpenApi\` on Windows and `~/.local/share/redoxnet/lsopenapi/` on Linux/macOS: `token.db` (auth cache, SHA-256 keyed) and `portfolio.db` (user-supplied holdings/watchlists; never read or written by tools outside the portfolio family).

## Tools (v0.5.0)

### Market data (LS-backed, credentials required)

| Tool | TR | Purpose |
|---|---|---|
| `ls_search_tr` | — | Search the embedded TR catalog by Korean / English keyword. |
| `ls_describe_tr` | — | Full InBlock / OutBlock schema for a specific TR. |
| `ls_call_tr` | any | Invoke any TR with a caller-supplied `in_block`. |
| `ls_get_quote` | `t1101` | Current price + 10-level order book. |
| `ls_get_multi_quote` | `t8407` | Up to 50 stocks per call. Accepts 6-character codes (digits, optionally one uppercase letter for ETFs e.g. `0117V0`). |
| `ls_get_top_stocks` | `t1441` / `t1444` / `t1452` / `t1463` / `t1466` | Top gainers/losers, market cap, volume, trading value, and volume-surge screeners. |
| `ls_get_stock_info` | `t1102` | PER/PBR/EPS, quarterly financials, 52-week + YTD ranges, top-5 brokerages, foreign-investor activity, SPAC / 관리종목 flags. |
| `ls_get_chart` | `t8410` / `t8412` / `t1301` | OHLCV (day/week/month/year/min/tick), indicators (SMA/EMA/RSI/MACD/BB), token-efficient `summary` + `dataset_id`, multi-timeframe in one call, optional Plotly v5 chart spec. Raw bars only with `output_mode='export'`; `with_warmup` toggles the summary warm-up; `summary.coverage` explains any null indicators. |
| `ls_add_indicator` | (handle cache + chart TR) | Adds an indicator to a `dataset_id` returned by `ls_get_chart` and returns the updated `summary` + chart spec. Example: *"add MA200 too"*. |
| `ls_reframe_chart` | (handle cache + chart TR) | Reframes a `dataset_id` to a new period/count using the cached symbol. Example: *"이걸 일봉으로 바꿔서 최근 6개월만 보여줘"*. |
| `ls_search_stock` | `t8436` | Name → code search with `instrument` filter (`all` / `stock` / `etf`). |
| `ls_get_etf_info` | `t1901` | ETF/ETN snapshot — NAV, 괴리율, 추적오차율, reference index, AUM, LP list. |
| `ls_get_etf_holdings` | `t1904` | ETF PDF (구성종목) with optional `top_n` cap. |

### Portfolio (local-only, no broker sync — v0.5)

Manual entries persisted to `portfolio.db` next to `token.db`. List responses fall back to a `quote_error` envelope when LS credentials are missing, but saved data still returns.

| Tool | Purpose |
|---|---|
| `ls_accounts_list` / `ls_account_get` | List all accounts with holdings counts; get the default account (`null` when none). |
| `ls_account_upsert` | Create or update an account by `account_number`. `nickname` must be unique; `set_default=true` promotes (auto-promotes when no default exists). |
| `ls_account_set_default` / `ls_account_remove` | Toggle default; two-step `confirm` cascade for removal with auto-succession of the next account (id ASC) when the default goes. |
| `ls_broker_rename` | Rename a broker label across every matching account. |
| `ls_holdings_list` | Holdings grouped by account with per-account + total summary. Optional `account` filter. Each row carries a `warning` field when `current_price/avg_price` ratio diverges 5×+ (likely missed corporate action). |
| `ls_holdings_set` / `_buy` / `_sell` / `_remove` | Initial state / weighted-average merge on incremental buys / partial-or-full sell with auto-remove on zero / outright delete. `_sell` raises `InsufficientQuantity` above the position. |
| `ls_holdings_split` / `_reverse_split` / `_bonus` | Corporate actions (액면분할 / 액면병합 / 무상증자). With no `account`, applied across every account holding the symbol. Reverse-split rejects non-divisible quantities. |
| `ls_watchlist_groups_list` / `_group_create` / `_group_delete` / `_group_rename` | Watchlist group CRUD. |
| `ls_watchlist_add` / `_remove` / `_list` | Stock entries inside groups. List enriches with live quotes when credentials are present. |
| `ls_watched_sectors_add` / `_remove` / `_list` | Track theme/sector codes (`t1531` tmcode such as `0012`); list response carries each theme's avg percent change. |

**Ambiguity policy.** Reads fall back; writes require an explicit target when ambiguous. 0 accounts → `RequiresAccount`; 1 account → auto with `applied_to` echo; 2+ → `AmbiguousAccount` with `candidates[]` so the model can re-call without prompting the user. Every mutation response includes `applied_to` (single account) or `applied_to[]` with before/after snapshots (corporate actions).

**Error envelopes.** `RequiresAccount` / `AmbiguousAccount` / `AccountNotFound` / `RequiresConfirmation` / `InsufficientQuantity` / `ValidationError` — all carry structured fields (candidates, identifier, holding count + market value, etc.) so the LLM can recover automatically.

Full release notes: https://github.com/redoxnet/mcp-lsopenapi/blob/main/RELEASENOTES.Mcp.md

## Documentation & source

- Project home: https://github.com/redoxnet/mcp-lsopenapi
- TR inventory: https://github.com/redoxnet/mcp-lsopenapi/blob/main/docs/LS-TR-INVENTORY.md
- SDK package: https://www.nuget.org/packages/RedoxNet.LsOpenApi.Core/
- License: MIT
