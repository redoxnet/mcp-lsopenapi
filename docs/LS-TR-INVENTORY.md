# LS증권 OpenAPI TR Inventory

A reference catalog of every TR LS exposes on its OpenAPI service for **국내주식 (domestic stock)**, with our current implementation status against each.

**Source:** [openapi.ls-sec.co.kr/apiservice](https://openapi.ls-sec.co.kr/apiservice) listing (captured 2026-05-13).

**Implementation status last refreshed:** 2026-05-15 for v0.5.0 (added `t1531` / `t1532`; the portfolio module is layered on top of `t8407` / `t1531` rather than introducing new TRs).

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
| `t1105` | 주식 피봇/디마크 조회 | ⚪ 💎 | Pivot points and DeMark levels — TA analysis candidate |
| `t1109` | 시간외 체결량 | ⚪ | |
| `t1301` | 주식 시간대별 체결조회 | 🟢 | `ls_get_chart period_type="tick"` — multi-field continuation |
| `t1302` | 주식 분별주가 조회 | ⚪ | Possibly overlap with t8412 |
| `t1305` | 기간별 주가 | ⚪ 💎 | N-day price summary in one call — useful for divergence analysis |
| `t1308` | 주식 시간대별 체결조회 차트 | ⚪ | |
| `t1310` | 주식 당일/전일 분틱 조회 | ⚪ | |
| `t1404` | 관리/불성실/투자유의 조회 | ⚪ | Pair with t8436's `bu12gubun` flag for screening |
| `t1405` | 투자경고/매매정지/정리매매 조회 | ⚪ | |
| `t1410` | 초저유동성 조회 | ⚪ | |
| `t1422` | 상/하한 | ⚪ | |
| `t1427` | 상/하한가 직전 | ⚪ | |
| `t1442` | 신고/신저가 | ⚪ 💎 | Common screener target |
| `t1449` | 가격대별 매매비중 조회 | ⚪ | |
| `t1471` | 시간대별 호가잔량 추이 | ⚪ | |
| `t1475` | 체결강도 추이 | ⚪ 💎 | Time series of `chdegree` (today we only return latest from t8407) |
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
| `t3102` | 뉴스 본문 | ⚪ 💎 | News integration |
| `t3202` | 종목별 증시일정 | ⚪ | Earnings/event calendar |
| `t3320` | FNG 요약 | ⚪ | F&G index? |
| `t3341` | 재무순위 종합 | ⚪ 💎 | Fundamentals: PER/PBR/ROE — anchors fundamental analysis tooling |
| `t3401` | 투자의견 | ⚪ | Sell-side coverage |
| `t3518` | 해외 실시간 지수 | ⚪ | Realtime — defer to v1.1 |
| `t3521` | 해외지수 조회 (API용) | ⚪ 💡 | |
| `t8428` | 증시주변자금 추이 | ⚪ | Macro liquidity |

## [주식] 섹터 — Sectors / themes

| TR | 이름 | Status | Tool / Notes |
| --- | --- | --- | --- |
| `t1531` | 테마별 종목 | 🔵 | In catalog. Consumed by the portfolio module's sector quote enrichment (`ls_watched_sectors_list` returns each watched theme's `avgdiff` as `change_pct`); empty `tmname`/`tmcode` returns the full theme list. No dedicated semantic wrapper yet — direct calls go through `ls_call_tr`. |
| `t1532` | 종목별 테마 | 🔵 | In catalog (`ls_call_tr` only). Reverse lookup: which themes a stock belongs to. |
| `t1533` | 특이 테마 | ⚪ | |
| `t1537` | 테마종목별 시세조회 | ⚪ | |
| `t8425` | 전체 테마 | ⚪ | |

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
| `t1403` | 신규상장 종목 조회 | ⚪ 💎 | IPO list — common analyst query |
| `t1411` | 증거금율별 종목 조회 | ⚪ | |
| `t1638` | 종목별 잔량/사전공시 | ⚪ | |
| `t1921` | 신용거래 동향 | ⚪ | |
| `t1926` | 종목별 신용정보 | ⚪ | |
| `t1927` | 공매도 일별 추이 | ⚪ 💎 | Short interest |
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

## Implementation status summary (v0.5)

- **18 TRs in catalog** — `t1101`, `t1102`, `t1301`, `t1441`, `t1444`, `t1452`, `t1463`, `t1466`, `t1531`, `t1532`, `t1901`, `t1904`, `t8407`, `t8410`, `t8412`, `t8430`, `t8436`, `t9945`.
- **37 MCP tools**:
  - Meta (3) — `ls_search_tr`, `ls_describe_tr`, `ls_call_tr`.
  - Market data (10) — `ls_get_quote`, `ls_get_multi_quote`, `ls_get_top_stocks`, `ls_get_stock_info`, `ls_get_chart`, `ls_add_indicator`, `ls_reframe_chart`, `ls_search_stock`, `ls_get_etf_info`, `ls_get_etf_holdings`.
  - Portfolio (24, v0.5, local-only) — accounts: `ls_accounts_list`, `ls_account_get`, `ls_account_upsert`, `ls_account_set_default`, `ls_account_remove`, `ls_broker_rename`; holdings: `ls_holdings_list`, `ls_holdings_set`, `ls_holdings_buy`, `ls_holdings_sell`, `ls_holdings_remove`, `ls_holdings_split`, `ls_holdings_reverse_split`, `ls_holdings_bonus`; watchlists: `ls_watchlist_groups_list`, `ls_watchlist_group_create`, `ls_watchlist_group_delete`, `ls_watchlist_group_rename`, `ls_watchlist_add`, `ls_watchlist_remove`, `ls_watchlist_list`; sectors: `ls_watched_sectors_add`, `ls_watched_sectors_remove`, `ls_watched_sectors_list`.

> The portfolio module is local-only (SQLite at `%LOCALAPPDATA%\RedoxNet\LsOpenApi\portfolio.db`). It uses `t8407` for holdings/watchlist quote enrichment and `t1531` for watched-sector enrichment but does not introduce new TRs.

## Recommended next batch (v0.6+ candidates)

Based on 💎 markers above, in rough priority order:

1. **`t1102` enrichment fold-in** — populate `stocks.krx_sector` automatically on portfolio writes so the v0.6 cross-domain query "내 관심 섹터에 속한 보유 종목" works without manual sector tagging.
2. **`t3341` fundamental ranking** — new `ls_get_fundamentals_rank` tool (cross-stock screener; complements per-stock `ls_get_stock_info`).
3. **`t1601` / `t1702` investor flow** — `ls_get_investor_flow` covering institutional/foreign net flow.
4. **`t1442` 신고/신저가 screener** + **`t1927` short interest** for screener fold-in.
5. **Theme semantic wrappers** — `ls_get_theme_stocks` (t1531) + `ls_get_stock_themes` (t1532). Currently callable via `ls_call_tr` only; portfolio sector enrichment consumes `t1531` internally.

## How to extend this inventory

When LS publishes new TRs or we verify a TR via the [testbed-console](https://openapi.ls-sec.co.kr/testbed-console):

1. Confirm path, OutBlock structure, continuation behavior with a testbed call.
2. Add the TR to `src/RedoxNet.LsOpenApi.Core/Catalog/TrCatalog.json`.
3. Update the status marker here (⚪ → 🔵 or 🟢) and add a fixture test in `tests/RedoxNet.Mcp.LsOpenApi.Tests/Tools/`.
4. If a semantic tool wraps it, document the mapping in [README.md](../README.md).
