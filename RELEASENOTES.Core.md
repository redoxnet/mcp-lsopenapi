# Release Notes — RedoxNet.LsOpenApi.Core

## v0.7.0 (2026-05-18)

Seven new TRs in the embedded catalog, no public API changes.
Versioned in lockstep with `RedoxNet.Mcp.LsOpenApi` 0.7.0.

### Added — Catalog rows (7 new TRs)

- **`t3320` (FNG_요약 / 투자정보 요약)** under `/stock/investinfo`.
  Per-stock 시장구분 + 회사 프로필 + 펀더멘털 (PER/PBR/EPS/ROE 등) +
  FICS 산업분류 (`upgubunnm`, "FICS " prefix가 붙은 풀-네임). 1 TPS
  per symbol. Input is 6-char shcode (LS doc lists 7-char "A"+code
  but live verification confirmed 6-char only). Consumed by the Mcp
  package's A1 industry enrichment path; reachable via `ls_call_tr`
  for raw access.
- **`t3341` (재무순위종합)** under `/stock/investinfo`. PER / PBR /
  PEG / EPS / BPS / ROE + 매출액/영업이익/세전계속이익 증가율 +
  부채비율 + 유보율 ranking. PER/PBR/PEG are forced ascending
  LS-side. Pagination via `idx`. Consumed by `ls_get_fundamentals_rank`.
- **`t1601` (투자자별 종합)** under `/stock/investor`. Real-time
  market-wide investor-type flow — 6 OutBlocks × 12 investor types
  (개인/외국인/기관계/증권/투신/은행/보험/종금/기금/국가/기타/사모펀드).
  LS does not label which OutBlock corresponds to which market segment;
  consumers should surface them as unlabeled.
- **`t1702` (외인기관 종목별 동향)** under `/stock/investor`.
  Single-stock daily investor flow history. `volvalgb` controls
  unit (금액/수량/단가), `msmdgb` selects net/buy/sell, `gubun`
  toggles per-day vs cumulative. Consumed by `ls_get_investor_flow`.
- **`t3202` (종목별 증시일정)** under `/stock/investinfo`. 14 LS
  corporate-action types (유무상증자 / 배당 / 감자 / 합병/분할 /
  매수청구 / 실권주 / 액면교체 / 주주총회 / 상호변경 / 국내/해외 CB
  전환 / 해외 BW 행사 / 스톡옵션행사). `recdt='00000000'` indicates
  TBD entries. Consumed by `ls_get_stock_events`.
- **`t1404` (관리/불성실공시/투자유의)** + **`t1405` (투자경고/
  매매정지/정리매매/단기과열 …)** under `/stock/market-data`. Two
  surveillance screens together cover 13 KRX designations. Both use
  `cts_shcode` body-paging. Consumed by `ls_get_market_warnings`.

### Changed

- Catalog count **26 → 33 TRs**.

> Versioned in lockstep with `RedoxNet.Mcp.LsOpenApi` 0.7.0. No public
> Core API changes — consumers that read the catalog or call TRs through
> `LsApiClient` get the new rows automatically.

## v0.6.0 (2026-05-16)

Six new TRs in the embedded catalog under the new `/indtp/market-data` (업종) path, plus one rate-limit correction. Catalog-only release — the public Core surface (`LsApiClient`, `TrCatalog`, indicator pipeline, etc.) is unchanged from v0.5.

### Added — Catalog rows (6 new TRs)

- **`t1511` (업종현재가)** under `/indtp/market-data`. Single-index snapshot — current value, OHLC with timestamps, 52-week + YTD range, market breadth (advancers/decliners/limit), four related auxiliary indices in `firstj*..fourthj*` blocks. Consumed by the Mcp package's `ls_get_index_quote` wrapper.
- **`t8424` (전체업종)** under `/indtp/market-data`. Industry/index code catalog (code + name only, no quotes). Consumed internally by `ls_get_industry_indices` fanout and by `industry_keyword` resolution in `ls_get_industry_stocks`.
- **`t1516` (업종별종목시세)** under `/indtp/market-data`. Stocks inside one industry + the industry's index summary. Body-based continuation paging (last `shcode` echoed). Consumed by `ls_get_industry_stocks`.
- **`t1537` (테마종목별시세조회)** under `/stock/sector`. Stocks inside one LS curated theme + theme summary (tmcnt/upcnt/uprate). Header-based `tr_cont` / `tr_cont_key` paging. Consumed by `ls_get_theme_stocks`.
- **`t1485` (예상지수)** under `/indtp/market-data`. Pre-market expected index. Catalog-only — reachable via `ls_call_tr`.
- **`t1514` (업종기간별추이)** under `/indtp/market-data`. Industry daily/weekly/monthly time series. Catalog-only — reachable via `ls_call_tr`. v0.7 wrapper candidate.

### Changed

- **`t1511` `rate_limit_per_sec`: 1 → 10**. The conservative v0.6 placeholder was corrected against the LS [업종] 시세 guide which publishes t1511 at 10 req/sec while keeping the other four TRs in the category at 1. Impact: `ls_get_industry_indices` cold-cache fanout drops from ~25s to ~2.5s for KOSPI (≈25 upcodes).
- Catalog count **18 → 24 TRs**.

> Versioned in lockstep with `RedoxNet.Mcp.LsOpenApi` 0.6.0. No public Core API changes — consumers that read the catalog or call TRs through `LsApiClient` get the new rows automatically.

## v0.5.0 (2026-05-15)

Two theme classification TRs added to the embedded catalog. The Mcp package's portfolio sector quote enrichment depends on `t1531`.

### Added

- **`t1531` (테마별종목)** and **`t1532` (종목별테마)** under `/stock/sector` with full InBlock / OutBlock schemas. `tmname` / `tmcode` on `t1531` are marked optional — an empty input returns the full theme list (LS confirmed behavior).

### Changed

- Catalog count **16 → 18 TRs**.

> Versioned in lockstep with `RedoxNet.Mcp.LsOpenApi` 0.5.0; the public Core surface is unchanged beyond the catalog rows.

## v0.4.0 (2026-05-15)

A compact analytical-summary builder + ZigZag swing detector. Additive — the existing public surface is unchanged; the MCP server in 0.4.0 builds on this layer.

### Added

- **`AnalyticalSummary` + `InflectionPoint` + `PivotKind` + `IndicatorCoverage` records** (`Models`). A token-efficient model-facing snapshot for chart data: latest close + period-relative change %, period-aware moving-average snapshots, MA60 deviation/slope, drawdown from peak, a bounded ZigZag-based key-turn list, and a per-indicator availability map. Usable standalone by any consumer that wants a structured swing/trend summary instead of raw OHLCV.
- **`AnalyticalSummaryBuilder`** (`Indicators`). Computes the summary above from a candle window. Picks MA periods by chart period (`MA5..MA200` for day; `MA20/60/120` for month/year; `MA20/60` for week), classifies MA60 slope by a **least-squares fit** over the look-back window (robust to endpoint noise), and emits an `IndicatorCoverage` with status flags (`ok` / `insufficient_data` / `disabled`) + a human-readable note when a window is too narrow. `displayBarCount` and `warmupApplied` flow through so callers can distinguish "narrow window the user pinned" from "this period doesn't support 1Y/5Y change."
- **`ZigZag` swing detector + `ZigZagOptions` + `ZigZagPivot`** (`Indicators`). Threshold-reversal pivot detection with two modes — `Percent` (literal fraction, e.g. `0.12` = 12%) and `AtrMultiple` (Wilder ATR-normalized for adaptive volatility). Triggers on the *close* but emits pivot prices from the actual swing high/low, so a single wide-range bar cannot self-trigger. Pivots strictly alternate peak/trough; the trailing pivot is `IsConfirmed=false` at the latest bar — the in-progress swing's provisional endpoint, observable to callers.

### Internal

- `JsonStringEnumConverter` is wired through `RedoxNet.Mcp.LsOpenApi`'s tool serializer so `PivotKind` serializes as `"peak"` / `"trough"`; Core itself keeps its existing `LsCoreJson` options untouched.

> Versioned independently from `RedoxNet.Mcp.LsOpenApi`; the shared 0.4.0 is intentional this time — both packages release together for the chart-tool reshape.

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
