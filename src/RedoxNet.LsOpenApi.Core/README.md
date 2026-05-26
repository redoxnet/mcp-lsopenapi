# RedoxNet.LsOpenApi.Core

.NET SDK for the **LS증권 OpenAPI** (REST). Provides the auth, HTTP, TR catalog, and indicator primitives used by the [`RedoxNet.Mcp.LsOpenApi`](https://www.nuget.org/packages/RedoxNet.Mcp.LsOpenApi/) MCP server, and usable on its own as a Korean and US/overseas stock market data SDK.

> Unofficial third-party SDK. Not affiliated with or endorsed by LS Securities Co., Ltd. (LS증권). Read-only market-data scope.

## Install

```bash
dotnet add package RedoxNet.LsOpenApi.Core
```

## Quick start

Wire the services into the DI container, then resolve `LsApiClient` and call any TR:

```csharp
using Microsoft.Extensions.DependencyInjection;
using RedoxNet.LsOpenApi.Core;
using RedoxNet.LsOpenApi.Core.Http;
using System.Text.Json.Nodes;

var services = new ServiceCollection();
services
    .AddLogging()
    .AddLsOpenApiCore()
    .ConfigureLsOptionsFromEnvironment();   // reads LS_APPKEY / LS_APPSECRETKEY / LS_MARKET

var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<LsApiClient>();

// Call any TR (here: t1101 — 주식 현재가 호가조회 for Samsung Electronics).
var response = await client.CallTrAsync(
    "t1101",
    new JsonObject { ["shcode"] = "005930" });

if (response.IsSuccess)
{
    var quote = response.GetBlock("t1101OutBlock");
    // ... read fields from quote ...
}
```

## What's in the box

| Layer | What it provides |
|---|---|
| **Auth** | `LsTokenIssuer` — OAuth2 `client_credentials`, with `LsTokenCache` (SQLite WAL, key = `SHA256(appkey):market` — raw app key never on disk). Auto-refresh 5 min pre-expiry; concurrent issuance is serialized. |
| **HTTP** | `LsApiClient.CallTrAsync` with Polly retries on 408/429/5xx + per-TR rate limiter + dual continuation modes (header `tr_cont_key` and body field continuation). |
| **Catalog** | `TrCatalog.Default` — 62-TR catalog as an embedded resource (시세 / 차트 / 지수 / 업종 / 테마 / ETF / 스크리너 / 종목조회 / 해외주식시세 / 해외주식차트 / 기타). `Search` ranks by exact-code, name, category, description, and field-level matches. |
| **Indicators** | `IndicatorService` over `Skender.Stock.Indicators` (SMA, EMA, RSI, MACD, Bollinger). Compact spec parser (`ma:5`, `bb:20,2`, `macd:12,26,9`). |
| **Chart context** | `ChartContextBuilder` — pre-computed analysis block (divergence from each MA, volume averages, drawdown from period high, MA trend, tristate `bullish_alignment` with `null` during MA warm-up). |
| **Analytical summary** | `AnalyticalSummaryBuilder` — token-efficient model-facing snapshot (period-aware MA snapshots, MA60 deviation + slope via least-squares fit, drawdown from peak, 1Y/5Y change, ZigZag-based `key_turns` with strict peak/trough alternation, and an `IndicatorCoverage` block that reports each indicator as `ok` / `insufficient_data` / `disabled` with a human-readable note when a window is too narrow). |
| **ZigZag** | Threshold-reversal swing detector (`ZigZag.Compute`) with `Percent` or `AtrMultiple` modes. Triggers on the close (no intrabar self-trigger), strictly alternating peak/trough pivots, and a trailing tentative pivot at the latest bar for the in-progress swing. |
| **Hygiene** | `SecretMasker.Mask("...XYZW") → "****XYZW"`. App secret never logged. POSIX `chmod 0600` on the token cache file + WAL/SHM siblings. |

## Credentials

`LS_APPKEY` and `LS_APPSECRETKEY` are read from the process environment via `ConfigureLsOptionsFromEnvironment()`. By design there is no other input path: MCP elicitation is explicitly avoided for static secrets, since prompting through chat would either log them or train callers to share them in transcripts.

## Documentation & source

- Project home: https://github.com/redoxnet/mcp-lsopenapi
- Release notes: https://github.com/redoxnet/mcp-lsopenapi/blob/main/RELEASENOTES.Core.md
- TR inventory: https://github.com/redoxnet/mcp-lsopenapi/blob/main/docs/LS-TR-INVENTORY.md
- License: MIT
