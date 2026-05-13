<p align="right">
  <strong>English</strong> · <a href="README.md">한국어</a>
</p>

# mcp-lsopenapi

MCP server for **LS증권 OpenAPI** — exposes Korean stock market data as MCP tools so AI assistants can query quotes, charts, and indicators in natural language.

> v1.0 is **read-only Korean stock market data**. Realtime feeds, accounts/balances, and orders are scheduled for later releases.

## Disclaimer

This is an **unofficial third-party MCP server**. It is not affiliated with, endorsed by, or sponsored by LS Securities Co., Ltd. (LS증권). "LS증권" and related marks belong to their respective owners.

This tool provides **market data access for informational purposes only**. It does not constitute investment advice or a solicitation to trade. Trading carries risk, including loss of principal. All trading decisions and any resulting gains or losses are solely the user's responsibility.

When using the API, please comply with the LS OpenAPI [terms of service](https://openapi.ls-sec.co.kr/howto-use).

## Packages

| Package | Type | Purpose |
| --- | --- | --- |
| `RedoxNet.LsOpenApi.Core` | Library | SDK: auth (OAuth2 client_credentials), HTTP client, TR catalog, indicators. |
| `RedoxNet.Mcp.LsOpenApi` | dotnet tool | MCP server over stdio. |
| `RedoxNet.LsOpenApi.Core.Catalog.Builder` | Dev tool | Scrapes the LS docs site to (re)generate the embedded TR catalog. Not shipped. |

## Quick start (when published to NuGet)

```jsonc
{
  "mcpServers": {
    "lsopenapi": {
      "command": "dnx",
      "args": ["RedoxNet.Mcp.LsOpenApi", "--yes"],
      "env": {
        "LS_APPKEY": "...",
        "LS_APPSECRETKEY": "...",
        "LS_MARKET": "virtual"
      }
    }
  }
}
```

### Environment variables

| Name | Required | Description |
| --- | --- | --- |
| `LS_APPKEY` | yes | LS OpenAPI app key. |
| `LS_APPSECRETKEY` | yes | LS OpenAPI app secret key. |
| `LS_MARKET` | no | `real` or `virtual` (default `virtual`). |
| `LS_BASEURL` | no | Override REST base URL (rarely needed). |

Tokens are cached at:
- Windows: `%LOCALAPPDATA%\RedoxNet\LsOpenApi\token.db`
- Linux/macOS: `~/.local/share/redoxnet/lsopenapi/token.db`

The cache is a SQLite database (WAL mode). Cache keys are `SHA256(appkey):market`, so the raw app key never lives on disk. Tokens auto-refresh 5 minutes before expiry.

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

Pass `include_chart: true` to receive a Plotly v5 JSON spec in the response. Clients that embed Plotly.js (e.g. AssistStudio) render inline charts; other clients fall back to the structured `candles`/`indicators`/`context` payload.

## Tools

### Meta

| Tool | Purpose |
| --- | --- |
| `ls_search_tr` | Search the embedded TR catalog by Korean/English keyword. |
| `ls_describe_tr` | Full input/output schema for one TR code. |
| `ls_call_tr` | Invoke any TR with a caller-supplied `in_block` JSON object. |

### Semantic (market data)

| Tool | TR | Purpose |
| --- | --- | --- |
| `ls_get_quote` | `t1101` | Current price + 10-level order book for a single Korean stock. |
| `ls_get_multi_quote` | `t8407` | Compact price snapshot for **up to 50 stocks in one call** — price/OHLC/volume/best ask·bid/총잔량/체결강도. Use for side-by-side comparison or watchlists. |
| `ls_get_stock_info` | `t1102` | Company profile + fundamentals: PER/PBR/EPS, quarterly financials and growth rates, 52-week + YTD ranges, top-5 buy/sell brokerages, foreign-investor activity, status flags (SPAC/관리종목). |
| `ls_get_chart` | `t8410` / `t8412` / `t1301` | OHLCV candles (day/week/month/year/min/tick), optional indicators (SMA, EMA, RSI, MACD, Bollinger), pre-computed analysis context, **multi-timeframe in one call** (`period_type: "day,week,month"`). |
| `ls_search_stock` | `t8436` | Find KOSPI/KOSDAQ codes by name fragment; surfaces SPAC + 관리종목 flags. |

### `ls_get_chart` context metadata

Every response carries a `context` block of pre-computed analytics so the LLM doesn't have to recompute means / drawdowns / divergences from raw OHLCV. Fields:

- `divergence_from_ma` — latest close vs each `ma:N` / `ema:N` indicator, as a percent.
- `volume.{latest,avg_20,ratio_20,avg_60,ratio_60}` — volume vs trailing averages.
- `drawdown.{period_high,period_high_date,current,pct}` — distance from the period high.
- `ma_trend` — direction of each MA over the last 5 bars (`"up"` / `"down"` / `"flat"`).
- `bullish_alignment` — `true` when shorter-period MAs sit above longer-period MAs.

### Multi-timeframe in one call

Pass a comma-separated `period_type` and the response wraps a `frames[]` array, one entry per timeframe, each with its own candles / indicators / context:

```jsonc
// ls_get_chart shcode=005930 period_type="day,week,month" indicators=["ma:5","ma:20","ma:60"]
{
  "shcode": "005930",
  "period_types": ["day", "week", "month"],
  "frames": [
    { "period_type": "day",   "tr_cd": "t8410", "count": 60, "candles": [...], "indicators": {...}, "context": {...} },
    { "period_type": "week",  "tr_cd": "t8410", "count": 60, "candles": [...], "indicators": {...}, "context": {...} },
    { "period_type": "month", "tr_cd": "t8410", "count": 60, "candles": [...], "indicators": {...}, "context": {...} }
  ]
}
```

A single `period_type` keeps the flat shape (`candles`, `indicators`, `context` at top level) for backward compatibility.

### Rendering charts (`include_chart: true`)

Pass `include_chart: true` and the response carries a Plotly v5 JSON spec under `chart`. The server emits the spec only — no server-side image rendering, no charting library dependency. Clients pass the spec straight to Plotly.js.

```jsonc
// ls_get_chart shcode=005930 period_type=day count=60 indicators=["ma:5","ma:20"] include_chart=true
{
  "shcode": "005930",
  "period_type": "day",
  "candles": [...],
  "indicators": {...},
  "context": {...},
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
        "title": { "text": "005930 — 일봉" },
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

Korean broker convention: rising candles/bars are red (`#E74C3C`), falling are blue (`#3498DB`).

Indicator handling in the chart:
- `ma:N`, `ema:N`, `bb:N,SD` → drawn as overlays on the price subplot.
- `rsi:N`, `macd:F,S,Sig` → returned in `indicators` but **not** drawn (they need their own subplot scale; future enhancement).

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
    // `response` here is the JSON returned by ls_get_chart.
    const { data, layout } = response.chart.spec;
    Plotly.newPlot("chart", data, layout, { responsive: true });
  </script>
</body>
</html>
```

Clients that don't embed Plotly.js can ignore the `chart` field — the structured `candles` / `indicators` / `context` payload remains the source of truth.

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

- ✅ M1 — Core scaffold + auth (OAuth2 client_credentials, SQLite WAL token cache, secret masking).
- ✅ M2 — Embedded TR catalog with 9 testbed-verified seed entries (`t1101`, `t1102`, `t8407`, `t8410`, `t8412`, `t1301`, `t8430`, `t8436`, `t9945`).
- ✅ M3 — TR execution (`LsApiClient.CallTrAsync`) with Polly retries, per-TR rate limiter, header + body continuation modes.
- ✅ M4 — MCP stdio server with 3 meta tools (`ls_search_tr`, `ls_describe_tr`, `ls_call_tr`).
- ✅ M5 (partial) — 5 semantic tools: `ls_get_quote`, `ls_get_multi_quote`, `ls_get_stock_info`, `ls_get_chart` (+ indicators, context metadata, multi-timeframe, Plotly v5 spec via `include_chart`), `ls_search_stock`.
- ✅ Live verified against the LS virtual server (v0.1.0-alpha.1).
- ⏳ Next release — Realtime (WebSocket), accounts/balances, orders.

## License

MIT.
