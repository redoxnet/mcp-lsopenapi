# Release Notes — RedoxNet.LsOpenApi.Core

## v0.3.0 (2026-05-14)

Five market-ranking TRs added to the catalog; the chart-spec builders now live here.

### Added

- **Five 주식상위종목 ranking TRs in the embedded catalog** — `t1441` (등락율 상위), `t1444` (시가총액 상위), `t1452` (거래량 상위), `t1463` (거래대금 상위), `t1466` (전일동시간대비 거래급증), each with full InBlock / OutBlock schemas and `idx` continuation metadata. The catalog now covers 16 TRs.
- **`Charting/` chart-spec builders** — `PlotlyChartBuilder` and `EtfHoldingsChartBuilder` (Plotly v5 candlestick + volume specs, ETF-holdings treemap specs) moved here from the MCP server. They depend only on Core types (`Candle`, `IndicatorSpec`) and `System.Text.Json`, so Core is their natural home. Both are `internal`, exposed to the MCP server via `InternalsVisibleTo` — **no public API surface change**.

### Changed

- Chart-spec output: Naver-style evenly-spaced date ticks (`tickvals` / `ticktext` over the verbatim category x), the Korean MA palette (green / red / orange / purple), period high/low annotations, white-on-deep-blue ETF treemap labels, and an optional stock name in the candlestick title.

> Versioned independently from `RedoxNet.Mcp.LsOpenApi`; the shared 0.3.0 is a coincidence.

## v0.2.0 (2026-05-14)

Packaging fix. **No code or API changes** — the SDK is equivalent to v0.1.0.

### Fixed

- **Package README missing on NuGet.** v0.1.0 was pushed without its README, so the NuGet package page showed no documentation. v0.2.0 ships with the README included.

> The version is bumped to 0.2.0 to land alongside the `RedoxNet.Mcp.LsOpenApi` 0.2.0 release. The two packages are versioned independently — they only happen to share this number; there is no lockstep policy.

## v0.1.0 (2026-05-13)

Initial public release of the LS증권 OpenAPI SDK.

### Included

- **Auth** — OAuth 2.0 `client_credentials` token issuer with a two-tier cache (in-memory + SQLite WAL). Cache key is `SHA256(appkey):market` so the raw app key never lives on disk. Refresh fires 5 min before expiry; concurrent callers share a single in-flight issuance via a per-issuer semaphore. POSIX `chmod 0600` applied to the database file and its WAL/SHM siblings.

- **HTTP client** — `LsApiClient.CallTrAsync` with Polly retries (3 attempts × exponential back-off on 408/429/5xx), per-TR rate limiter (`TrRateLimiter`), and dual continuation modes: header-based `tr_cont_key` for newer CSPAQ-style TRs, body-based field continuation for legacy stock TRs (`t8410` / `t8412` / `t1301`).

- **TR catalog** — 13 testbed-verified seed entries across five categories (시세 / 차트 / ETF / 종목조회 / 기타). Full InBlock / OutBlock schemas with field-by-field Korean and English descriptions: `t1101`, `t1102`, `t8407`, `t8410`, `t8412`, `t1301`, `t1901`, `t1902`, `t1903`, `t1904`, `t9945`, `t8430`, `t8436`. Catalog shipped as an embedded resource and exposed via `TrCatalog.Default` with `Find` / `Search` helpers.

- **Indicators** — `IndicatorService` over `Skender.Stock.Indicators` (SMA, EMA, RSI, MACD, Bollinger Bands). Spec parser accepts compact strings (`ma:12`, `bb:20,2`, `macd:12,26,9`). `ChartContextBuilder` produces the pre-computed analysis context (divergence from each MA, volume averages, drawdown from period high, trend per MA, tristate `bullish_alignment` — `null` during MA warm-up).

- **Models** — `Candle`, `Quote`, `ChartContext` with sub-records (`VolumeContext`, `DrawdownInfo`). All records, all snake-case JSON via the shared `LsCoreJson` options.

- **Hygiene** — `SecretMasker.Mask("...XYZW") → "****XYZW"`. App key and access token are masked in every log line; the app secret is never logged in any form. `LsCredentials` is a record but explicitly not intended for serialization.

### Targets

`net8.0` library. Distributed as a NuGet package; consumers add a single `PackageReference` and use `services.AddLsOpenApiCore()` to wire everything into `Microsoft.Extensions.DependencyInjection`.

### Verified

234 unit and integration tests pass on .NET 8 and .NET 10. Live-verified against the LS 모의투자 server on 2026-05-13: token issuance + cache hit/miss, the full catalog of seed TRs, indicator computation against real candle data, and the chart context builder against tristate alignment scenarios.
