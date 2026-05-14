# RedoxNet.Mcp.LsOpenApi

MCP server for the **LS증권 OpenAPI** — exposes Korean stock market data as MCP tools so AI assistants can query quotes, charts, and ETF data in natural language.

> Unofficial third-party MCP server. Not affiliated with or endorsed by LS Securities Co., Ltd. (LS증권). Read-only market-data scope (실시간 / 계좌 / 주문 deferred).

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

## Environment variables

| Name | Required | Description |
|---|---|---|
| `LS_APPKEY` | yes | LS OpenAPI app key. |
| `LS_APPSECRETKEY` | yes | LS OpenAPI app secret key. |
| `LS_MARKET` | no | `real` or `virtual` (default `virtual`). |
| `LS_BASEURL` | no | Override REST base URL (rarely needed). |

Credentials are accepted **only** through the process environment — never through chat, tool arguments, or MCP elicitation. See [ADR-001](https://github.com/redoxnet/mcp-lsopenapi/blob/main/docs/ADR-001-credential-management.md) for the rationale.

## Tools (v0.2.0)

| Tool | TR | Purpose |
|---|---|---|
| `ls_search_tr` | — | Search the embedded TR catalog by Korean / English keyword. |
| `ls_describe_tr` | — | Full InBlock / OutBlock schema for a specific TR. |
| `ls_call_tr` | any | Invoke any TR with a caller-supplied `in_block`. |
| `ls_get_quote` | `t1101` | Current price + 10-level order book. |
| `ls_get_multi_quote` | `t8407` | Up to 50 stocks per call. |
| `ls_get_stock_info` | `t1102` | PER/PBR/EPS, quarterly financials, 52-week + YTD ranges, top-5 brokerages, foreign-investor activity, SPAC / 관리종목 flags. |
| `ls_get_chart` | `t8410` / `t8412` / `t1301` | OHLCV (day/week/month/year/min/tick), indicators (SMA/EMA/RSI/MACD/BB), pre-computed analysis context, multi-timeframe in one call, optional Plotly v5 chart spec. |
| `ls_search_stock` | `t8436` | Name → code search with `instrument` filter (`all` / `stock` / `etf`). |
| `ls_get_etf_info` | `t1901` | ETF/ETN snapshot — NAV, 괴리율, 추적오차율, reference index, AUM, LP list. |
| `ls_get_etf_holdings` | `t1904` | ETF PDF (구성종목) with optional `top_n` cap. |

Full release notes: https://github.com/redoxnet/mcp-lsopenapi/blob/main/RELEASENOTES.Mcp.md

## Documentation & source

- Project home: https://github.com/redoxnet/mcp-lsopenapi
- TR inventory: https://github.com/redoxnet/mcp-lsopenapi/blob/main/docs/LS-TR-INVENTORY.md
- SDK package: https://www.nuget.org/packages/RedoxNet.LsOpenApi.Core/
- License: MIT
