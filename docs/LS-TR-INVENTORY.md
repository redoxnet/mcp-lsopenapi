# LS증권 OpenAPI TR Inventory

A reference catalog of every TR LS exposes on its OpenAPI service for **국내주식 (domestic stock)**, with our current implementation status against each.

**Source:** [openapi.ls-sec.co.kr/apiservice](https://openapi.ls-sec.co.kr/apiservice) listing (captured 2026-05-13).

**Implementation status last refreshed:** 2026-05-16 for v0.6.0 (added `t1485` / `t1511` / `t1514` / `t1516` / `t1537` / `t8424`; promoted `t1532` from catalog-only to wrapper; t1102/krx_sector enrichment deferred to v0.7 after confirming t1102 has no industry classification field).

**Use this doc when:** deciding what to add to the catalog next, mapping a user request to an underlying TR, or scoping the next release.

---

## Status legend

| Marker | Meaning |
| --- | --- |
| 🟢 | In catalog **and** wrapped by a dedicated MCP tool |
| 🔵 | In catalog (callable via `ls_call_tr`) — no semantic wrapper yet |
| 💡 | LS labels this TR as **"API용"** or **"API전용"** — preferred for OpenAPI consumers; when both a legacy and an API용 variant exist, semantic tools should prefer the API용 one |
| ⚪ | Not in catalog — candidate for a future v1.x patch |
| 🚧 | Reserved for a later release (realtime / accounts / orders) |
| 💎 | High-value candidate worth picking up early (analyst workflows, screening) |

---

## [주식] 시세 — Market data

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1101` | 주식 현재가 호가조회 | 🟢 | `ls_get_quote` — 10단계 호가 + per-level 직전대비 |
| `t1102` | 주식 현재가(시세)조회 | 🟢 | `ls_get_stock_info` — PER/PBR/EPS + 분기 재무 + 52주/연중 범위 + 거래원 상위 5 + 외인 동향 |
| `t1104` | 주식 현재가 시세메모 | ⚪ | |
| `t1105` | 주식 피봇/디마크 조회 | 🔵 💎 | In catalog (`ls_call_tr` only). Pivot + 1/2차 지지·저항 + DeMark levels, single snapshot. `ls_get_stock_info` section 흡수 후보. |
| `t1109` | 시간외 체결량 | ⚪ | |
| `t1301` | 주식 시간대별 체결조회 | 🟢 | `ls_get_chart period_type="tick"` — multi-field continuation |
| `t1302` | 주식 분별주가 조회 | ⚪ | Possibly overlap with t8412 |
| `t1305` | 기간별 주가 | 🔵 💎 | In catalog (`ls_call_tr` only). 일/주/월 OHLC + 외인·기관·개인 순매수 + 체결강도/소진율/회전율 per bar. `ls_get_chart`(t8410)·`ls_get_investor_flow`(t1702)와 겹쳐 wrapper화는 설계 보류. |
| `t1308` | 주식 시간대별 체결조회 차트 | ⚪ | |
| `t1310` | 주식 당일/전일 분틱 조회 | ⚪ | |
| `t1404` | 관리/불성실/투자유의 조회 | ⚪ | Pair with t8436's `bu12gubun` flag for screening |
| `t1405` | 투자경고/매매정지/정리매매 조회 | ⚪ | |
| `t1410` | 초저유동성 조회 | ⚪ | |
| `t1422` | 상/하한 | ⚪ | |
| `t1427` | 상/하한가 직전 | ⚪ | |
| `t1442` | 신고/신저가 | 🔵 💎 | In catalog (`ls_call_tr` only). 신고/신저 스크리너 — 기간(전일~년중)·유지여부·가격/거래량 필터·제외 비트마스크. `ls_get_top_stocks` kind 확장 후보. |
| `t1449` | 가격대별 매매비중 조회 | ⚪ | |
| `t1471` | 시간대별 호가잔량 추이 | ⚪ | |
| `t1475` | 체결강도 추이 | 🔵 💎 | In catalog (`ls_call_tr` only). 체결강도(VP) 추이 + 5/20/60 이평, 시간별/일별. t8407은 latest만 — 이건 시계열. `ls_get_stock_info` section 흡수 후보. |
| `t1486` | 시간별 예상체결가 | ⚪ | |
| `t1488` | 예상체결가 등락율 상위 조회 | ⚪ | |
| `t8407` | 주식 멀티 현재가 조회 API용 | 🟢 💡 | `ls_get_multi_quote` — up to 50 codes in one call |
| `t9945` | 주식 마스터 조회 API용 (slim) | 🔵 💡 | Slimmer than t8436; catalog only |

## [주식] 차트 — Charts

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1665` | 기간별 투자자매매 추이 (차트) | ⚪ | |
| `t8410` | 주식 차트 (일/주/월/년) API전용 | 🟢 💡 | `ls_get_chart period_type="day"|"week"|"month"|"year"` |
| `t8411` | 주식 차트 (틱/n틱) | ⚪ | We use t1301 for tick today; t8411 may offer richer history |
| `t8412` | 주식 차트 (N분) | 🟢 | `ls_get_chart period_type="min"` — multi-key continuation |

## [주식] 종목검색 — Stock search

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1809` | 신호 조회 | ⚪ | Likely tied to LS-side stock condition signals |
| `t1856` | 파일저장 조건 종목검색 | ⚪ | |

## [주식] 상위종목 — Rankings

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1441` | 등락율 상위 | 🟢 💎 | `ls_get_top_stocks kind="gainers"|"losers"|"unchanged"` |
| `t1444` | 시가총액 상위 | 🟢 💎 | `ls_get_top_stocks kind="market_cap"` |
| `t1452` | 거래량 상위 | 🟢 💎 | `ls_get_top_stocks kind="volume"` |
| `t1463` | 거래대금 상위 | 🟢 💎 | `ls_get_top_stocks kind="amount"` |
| `t1466` | 전일동시간대비 거래급증 | 🟢 💎 | `ls_get_top_stocks kind="volume_surge"` |
| `t1481` | 시간외 등락율 상위 | ⚪ | |
| `t1482` | 시간외 거래량 상위 | ⚪ | |
| `t1489` | 예상체결량 상위 조회 | ⚪ | |
| `t1492` | 단일가 예상등락율 상위 | ⚪ | |

## [주식] 거래원 — Brokerage flow

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1752` | 종목별 상위 회원사 | ⚪ | |
| `t1764` | 회원사 리스트 | ⚪ | |
| `t1771` | 종목별 회원사 추이 | ⚪ | |

## [주식] 투자자 — Investor breakdown

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1601` | 투자자별 종합 | ⚪ 💎 | Net buy/sell by investor type — high-signal for daily commentary |
| `t1602` | 시간대별 투자자매매 추이 | ⚪ | |
| `t1603` | 시간대별 투자자매매 추이 상세 | ⚪ | |
| `t1615` | 투자자매매 종합1 | ⚪ | |
| `t1617` | 투자자매매 종합2 | ⚪ | |
| `t1621` | 업종별 분별 투자자매매 동향 (차트) | ⚪ | |
| `t1664` | 투자자매매 종합 (차트) | ⚪ | |

## [주식] 외인/기관 — Foreign / institutional

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1702` | 외인기관 종목별 동향 | ⚪ 💎 | |
| `t1716` | 외인기관 종목별 동향 | ⚪ | |
| `t1717` | 외인기관 종목별 동향 | ⚪ | |

## [주식] 프로그램 — Program trading

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1631` | 프로그램매매 종합조회 | ⚪ | |
| `t1632` | 시간대별 프로그램매매 추이 | ⚪ | |
| `t1633` | 기간별 프로그램매매 추이 | ⚪ | |
| `t1636` | 종목별 프로그램매매 동향 | ⚪ | |
| `t1637` | 종목별 프로그램매매 추이 | ⚪ | |
| `t1640` | 프로그램매매 종합조회 (미니) | ⚪ | |
| `t1662` | 시간대별 프로그램매매 추이 (차트) | ⚪ | |

## [주식] 투자정보 — Fundamentals / news

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t3102` | 뉴스 본문 | 🔵 💎 | In catalog (`ls_call_tr` only). Body fetch by sNewsno. Discovery: NWS WebSocket push (`tr_cd=NWS`, `tr_key=NWS001` for all-news, or a 6/8-char shcode for per-stock filtering) — push payload's `realkey` (24 chars) is the sNewsno. WebSocket transport not yet implemented; pairs with NWS in v2.0. |
| `t3202` | 종목별 증시일정 | ⚪ | Earnings/event calendar |
| `t3320` | FNG 요약 | ⚪ | F&G index? |
| `t3341` | 재무순위 종합 | ⚪ 💎 | Fundamentals: PER/PBR/ROE — anchors fundamental analysis tooling |
| `t3401` | 투자의견 | 🔵 | In catalog (`ls_call_tr` only). 증권사 투자의견 변경 이력 — 의견·목표가 before/after. `ls_get_analyst_opinions` wrapper 후보. |
| `t3518` | 해외 실시간 지수 | 🔵 | In catalog (`ls_call_tr` only). Overseas index / FX / futures series (day/week/month/min/tick) with body continuation (`cts_date`, `cts_time`). |
| `t3521` | 해외지수 조회 (API용) | 🟢 💡 | `ls_get_global_market_quote` — one-shot overseas index / FX / futures snapshot. Covers major US indices (`nasdaq`, `sp500`, `dow`, `soxx`) and FX (`usdkrw`) aliases. |
| `t8428` | 증시주변자금 추이 | 🔵 | In catalog (`ls_call_tr` only). 일별 고객예탁금·신용잔고·미수금 + 펀드 자금(주식/혼합/채권/MMF). 매크로 유동성 시계열. |

## [주식] 섹터 — LS curated themes (path `/stock/sector`)

> Naming clarification (v0.6): LS's `[주식 섹터]` API category is entirely **theme** TRs (LS-curated tmcode groupings). v0.5 mis-named these "sectors"; v0.6 renames the watch-list surface to `ls_watched_themes_*` to match. True KRX industry classification is a separate concept covered by `[업종]` below.

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1531` | 테마별 종목 | 🔵 | In catalog. Consumed by the portfolio module's theme quote enrichment (`ls_watched_themes_list` returns each watched theme's `avgdiff` as `change_pct`) and by `ls_get_theme_stocks` keyword resolution; empty `tmname`/`tmcode` returns the full theme list. 60s in-process catalog cache shared between the quote enrichment and keyword-resolution paths. |
| `t1532` | 종목별 테마 | 🟢 | `ls_get_stock_themes` — every theme a stock belongs to. Also feeds the v0.6 fire-and-forget enrichment that populates the `stock_themes` cache on portfolio writes. |
| `t1533` | 특이 테마 | ⚪ | |
| `t1537` | 테마종목별 시세조회 | 🟢 | `ls_get_theme_stocks` — stocks inside one theme + summary (tmcnt/upcnt/uprate). Header-based `tr_cont` / `tr_cont_key` paging. |
| `t8425` | 전체 테마 | 🔵 | In catalog (`ls_call_tr` only). Lighter than t1531 (tmname + tmcode only, no quote). 시세 enrich 불필요한 테마 키워드 해석 내부 최적화 후보. |

## [업종] 시세 — KRX industry indices (path `/indtp/market-data`)

> v0.6 added category. **KRX industry classification** (전기전자 / 화학 / 금융 등 ~30) is distinct from LS themes above. None of these TRs return a stock→industry mapping; the v0.6 `stocks.krx_sector` enrichment is **deferred to v0.7** while a source is identified (LS 마스터 TR / t8424+t1516 reverse-lookup / static KRX table).

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1485` | 예상지수 | 🔵 | In catalog (`ls_call_tr` only). Pre-market expected index. |
| `t1511` | 업종현재가 | 🟢 | `ls_get_index_quote` — single index snapshot with aliases (kospi→001, kosdaq→301, kospi200→101, krx100→501). 52-week + YTD range, market breadth, 4 related indices. `rate_limit_per_sec=10` (confirmed via LS guide). |
| `t1514` | 업종기간별추이 | 🔵 | In catalog (`ls_call_tr` only). Industry daily/weekly/monthly time series. v0.7 candidate for `ls_get_index_history` wrapper. |
| `t1516` | 업종별종목시세 | 🟢 | `ls_get_industry_stocks` — stocks inside one industry + the industry's index summary. Body-based continuation paging (last shcode echo). Keyword resolution via cached t8424. |
| `t8424` | 전체업종 | 🔵 | In catalog. Internal: feeds the `ls_get_industry_indices` fanout. Also exposed as the catalog source for `industry_keyword` resolution in `ls_get_industry_stocks`. |

## [주식] ETF — Exchange-traded funds

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1901` | ETF 현재가 (시세) 조회 | 🟢 | `ls_get_etf_info` — NAV, 추적기준지수, 괴리율, AUM, LP 5개, 52주/연중 범위, 관련 선물 |
| `t1902` | ETF 시간별 추이 | ⚪ | |
| `t1903` | ETF 일별 추이 | ⚪ | |
| `t1904` | ETF 구성종목 조회 | 🟢 | `ls_get_etf_holdings` — PDF(구성종목) 리스트 + 비중 + 평가/시가총액 + ETF 요약(NAV/AUM/현금) |
| `t1906` | ETFLP 호가 | ⚪ | |

## [주식] ELW — Warrants

> 20 TRs (t19xx, t8431, t99xx). All ⚪ for v1.0. Likely a separate package `RedoxNet.Mcp.LsOpenApi.Elw` rather than core scope.
>
> Notable: `t9942` (ELW마스터조회 API용) 💡 mirrors the t9945 pattern for warrants.

## [주식] 기타 — Misc / metadata

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `CLNAQ00100` | 예탁담보융자 가능종목 현황 조회 | ⚪ | Margin |
| `t1403` | 신규상장 종목 조회 | 🔵 💎 | In catalog (`ls_call_tr` only). 상장월 범위별 IPO 목록 — 공모가·등록일 기준가/종가. `ls_get_new_listings` wrapper 후보. |
| `t1411` | 증거금율별 종목 조회 | ⚪ | |
| `t1638` | 종목별 잔량/사전공시 | ⚪ | |
| `t1921` | 신용거래 동향 | ⚪ | |
| `t1926` | 종목별 신용정보 | ⚪ | |
| `t1927` | 공매도 일별 추이 | 🔵 💎 | In catalog (`ls_call_tr` only). 일별 공매도 수량·대금·비중·평균단가 + 업틱룰 적용/예외. `ls_get_stock_info` section 흡수 후보. |
| `t1941` | 종목별 대차거래 일간 추이 | ⚪ | |
| `t8430` | 주식 종목조회 (legacy) | 🔵 | Catalog only; superseded by t8436 |
| `t8436` | 주식 종목조회 API용 | 🟢 💡 | `ls_search_stock` — includes SPAC + 관리종목 flags |

---

## Out of v1.0 scope — pinned for later releases

### [주식] 계좌 — Accounts (🚧 v1.1+ — "next release")

| TR | 이름 |
| --- | --- |
| `CDPCQ04700` | 계좌 거래내역 |
| `CSPAQ00600` | 계좌별 신용한도 조회 |
| `CSPAQ12200` | 현물계좌 예수금/주문가능금액/총평가 조회 |
| `CSPAQ12300` | BEP 단가 조회 |
| `CSPAQ13700` | 현물계좌 주문체결내역 조회 (API) |
| `CSPBQ00200` | 현물계좌 증거금률별 주문가능수량 조회 |
| `FOCCQ33600` | 주식계좌 기간별 수익률 상세 |
| `t0150` | 주식 당일매매일지/수수료 |
| `t0151` | 주식 당일매매일지/수수료 (전일) |
| `t0424` | 주식잔고2 |
| `t0425` | 주식체결/미체결 |

Notes:
- Multiple CSPAQxxx (modern) and t0xxx (legacy) variants — prefer the CSPAQ ones per the "API용" pattern seen elsewhere.
- `CSPAQ13700` likely the canonical "today's orders" TR; `t0425` is the legacy.

### [주식] 주문 — Order entry (🚧 v2.x `.Trading`)

| TR | 이름 |
| --- | --- |
| `CSPAT00601` | 현물 주문 |
| `CSPAT00701` | 현물 정정주문 |
| `CSPAT00801` | 현물 취소주문 |

Trading is high-risk surface — will get its own package with elicitation/confirmation guards.

### [주식] 실시간시세 — Realtime feed (🚧 v1.1 `.Realtime`)

WebSocket-based stream identifiers, not REST TRs. ~50 channels including:

| Channel | Description |
| --- | --- |
| `S3_` / `K3_` | KOSPI / KOSDAQ 체결 (trade ticks) |
| `H1_` / `HA_` | KOSPI / KOSDAQ 호가잔량 |
| `S2_` / `KS_` | KOSPI / KOSDAQ 우선호가 |
| `K1_` / `OK_` | KOSPI / KOSDAQ 거래원 |
| `PH_` / `PM_` / `KH_` / `KM_` | 프로그램매매 (종목별 / 전체) |
| `SHI`/`SHO`/`SHC`/`SHD` | 상/하한가 진입/이탈/근접 |
| `VI_` / `DVI` | 변동성완화장치(VI) 발동/해제 |
| `SC0`–`SC4` | 주식 주문 접수/체결/정정/취소/거부 — needed for Trading package's order status |
| `IJ_` / `YJ_` | 지수 / 예상지수 |
| `YS3` / `YK3` | KOSPI / KOSDAQ 예상체결 |
| `DS3` / `DK3` / `DH1` / `DHA` | 시간외 단일가 호가/체결 |
| `h2_ELW` / `h3_ELW` / `k1_ELW` / `s2_ELW` / `s3_ELW` / `s4_ELW` / `Ys3_ELW` / `ESN` | ELW realtime |
| `AFR` | 사용자 조건검색 실시간 |

---

## Implementation status summary (v0.6)

- **24 TRs in catalog** — `t1101`, `t1102`, `t1301`, `t1441`, `t1444`, `t1452`, `t1463`, `t1466`, `t1485`, `t1511`, `t1514`, `t1516`, `t1531`, `t1532`, `t1537`, `t1901`, `t1904`, `t8407`, `t8410`, `t8412`, `t8424`, `t8430`, `t8436`, `t9945`.
- **39 MCP tools** (37 v0.5 + 7 v0.6 new − 5 v0.6 Tier 1 compression):
  - Meta (3) — `ls_search_tr`, `ls_describe_tr`, `ls_call_tr`.
  - Market data (10) — `ls_get_quote`, `ls_get_multi_quote`, `ls_get_top_stocks`, `ls_get_stock_info`, `ls_get_chart`, `ls_add_indicator`, `ls_reframe_chart`, `ls_search_stock`, `ls_get_etf_info`, `ls_get_etf_holdings`.
  - **Index + industry (3, v0.6 new)** — `ls_get_index_quote`, `ls_get_industry_indices`, `ls_get_industry_stocks`.
  - **LS themes (2, v0.6 new)** — `ls_get_theme_stocks`, `ls_get_stock_themes`.
  - Portfolio (21, v0.5 + v0.6, local-only) — accounts: `ls_accounts_list`, `ls_account_upsert`, `ls_account_remove`, `ls_broker_rename`; holdings: `ls_holdings_list`, `ls_holdings_set`, `ls_holdings_buy`, `ls_holdings_sell`, `ls_holdings_remove`, `ls_holdings_corporate_action`; watchlists: `ls_watchlist_groups_list`, `ls_watchlist_group_create`, `ls_watchlist_group_delete`, `ls_watchlist_group_rename`, `ls_watchlist_add`, `ls_watchlist_remove`, `ls_watchlist_list`; watched themes: `ls_watched_themes_add`, `ls_watched_themes_remove`, `ls_watched_themes_list`; I/O: `ls_portfolio_export`, `ls_portfolio_import`.

> The portfolio module is local-only (SQLite at `%LOCALAPPDATA%\RedoxNet\LsOpenApi\portfolio.db`). v0.6 adds fire-and-forget `t1532` enrichment of the new `stock_themes` cache on write paths, plus an export/import JSON round-trip (schema_version=1) for backup and machine migration.

### v0.6 Tier 1 compression (BREAKING, −5 tools)

Goal: keep total tool count under ~45 by v1.0 so the LLM has less to route across. Service+repository layers stayed; only the MCP surface contracted.

| Removed v0.5 tool | Equivalent v0.6 path |
| --- | --- |
| `ls_account_get` | `ls_accounts_list[].is_default` flag |
| `ls_account_set_default(account)` | `ls_account_upsert(set_default=true)` |
| `ls_holdings_split(ratio)` | `ls_holdings_corporate_action(type="split", ratio)` |
| `ls_holdings_reverse_split(ratio)` | `ls_holdings_corporate_action(type="reverse_split", ratio)` |
| `ls_holdings_bonus(ratio)` | `ls_holdings_corporate_action(type="bonus", ratio)` |

The unified `corporate_action` is an open enum — v0.7+ adds `stock_dividend` / `spin_off` / `merger` by extending the enum without growing the tool surface.

## Recommended next batch (v0.7+ candidates)

Based on 💎 markers above + v0.6 deferrals, in rough priority order:

1. **`stocks.krx_sector` enrichment + `industry?` filter** — confirmed during v0.6 implementation that `t1102` doesn't carry KRX classification. Three candidate sources (per SPEC §10 Q7): LS 마스터/기본정보 TR (LS support inquiry), `t8424` + `t1516` reverse-lookup mapping, or static KRX industry table bundled with the package.
2. **`ls_get_index_history`** — wrap `t1514` (catalog-only in v0.6). Decide consistency with `ls_get_chart` (period_type / count / indicators) when added.
3. **`ls_stocks_refresh_metadata`** — synchronous explicit refresh tool. Adds a UX out for the fire-and-forget "pending themes" state once we measure how often it actually bites in practice.
4. **`t3341` fundamental ranking** — new `ls_get_fundamentals_rank` tool (cross-stock screener; complements per-stock `ls_get_stock_info`).
3. **`t1601` / `t1702` investor flow** — `ls_get_investor_flow` covering institutional/foreign net flow.
4. **`t1442` 신고/신저가 screener** + **`t1927` short interest** for screener fold-in.
5. **Theme semantic wrappers** — `ls_get_theme_stocks` (t1531) + `ls_get_stock_themes` (t1532). Currently callable via `ls_call_tr` only; portfolio sector enrichment consumes `t1531` internally.

## How to extend this inventory

When LS publishes new TRs or we verify a TR via the [testbed-console](https://openapi.ls-sec.co.kr/testbed-console):

1. Confirm path, OutBlock structure, continuation behavior with a testbed call.
2. Add the TR to `src/RedoxNet.LsOpenApi.Core/Catalog/TrCatalog.json`.
3. Update the status marker here (⚪ → 🔵 or 🟢) and add a fixture test in `tests/RedoxNet.Mcp.LsOpenApi.Tests/Tools/`.
4. If a semantic tool wraps it, document the mapping in [README.md](../README.md).
