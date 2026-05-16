# SPEC: v0.6 — Market Context, Theme Wrappers, Portfolio I/O

- **상태**: Draft
- **작성일**: 2026-05-15
- **대상 버전**: v0.6.0
- **작성자**: Jong Hyun
- **선행**: [SPEC-portfolio-multi-account.md](./SPEC-portfolio-multi-account.md) (v0.5)

## 1. 컨텍스트

v0.5에서 로컬 포트폴리오 모듈이 안정화되면서 다음 UX 갭이 드러남:

1. **시장 컨텍스트 부재.** *"오늘 코스피 어땠어?"*, *"오늘 강한 업종은?"*, *"전기전자 업종 종목 비교"* 같은 가장 흔한 시장 질의에 직접 답할 도구가 없음. KODEX 200(`069500`)로 간접 우회 가능하지만 *"-1.2%"* 답에 *"KODEX 200 NAV 기준"* 같은 어색한 단서가 따라붙음. 보유종목 분석에서 *"오늘 코스피는 -1.2%인데 LG전자만 +10.83%"* 같은 시장-상대 컨텍스트가 빠짐.
2. **테마 자연 질의 미지원.** *"AI 테마에 어떤 종목 있어?"* / *"삼성전자는 어떤 테마에 묶여 있어?"* — 테마 wrapper가 없어 `ls_call_tr` raw 호출만 가능.
3. **포트폴리오 데이터 락인.** v0.5에서 유저 실데이터가 쌓이기 시작했는데 export 경로가 없음. SQLite 파일 직접 복사로만 백업/이관 가능. 스키마 변경(v0.6 → v0.7 등) 시 마이그레이션 안전망 없음.
4. **도메인 간 단절.** v0.5 `watched_sectors`(실제로는 LS 테마 t1531 코드 보관)와 holdings가 stocks 테이블을 공유하지만 `stocks.krx_sector`(업종 / KRX 표준 산업분류)는 placeholder 그대로. *"내 보유 중 반도체 업종"* 도 *"내 보유 중 2차전지 테마"* 도 안 됨.
5. **명명 혼선.** v0.5에서 "sector"를 LS 테마 의미로 사용했는데 v0.6에서 진짜 업종 도메인이 들어오면 충돌. 두 개념은 LS에서 명확히 분리되므로 코드 측도 정리해야 함:
   - **업종 (industry)** = KRX 표준 산업분류, t1102의 `krx_sector` 필드 — 전기전자/화학/금융 등 ~30종. 업종 전체 등락률 TR은 v0.7로 이연 (§6 OUT).
   - **테마 (theme)** = LS 사이트 큐레이션 그룹, t1531/t1532/t1537의 4자리 tmcode — AI/2차전지/반도체장비 등 ~수백

이 패치는 위 5가지를 v0.6에서 동시에 정리한다. v0.5 작업의 자연스러운 후속이며 도구 추가 6개 + 기존 도구 surface rename / 확장.

## 2. 결정

| # | 결정 | 비고 |
|---|------|------|
| 1 | 명명 규약: **업종(industry)** vs **테마(theme)** 분리 | DB / 도구 / 응답 envelope / 파라미터명 모두 일관. 자세한 매핑 §2.1 |
| 2 | 지수·업종 도구 3개 추가 | `ls_get_index_quote` (t1511) 단건 지수, `ls_get_industry_indices` (t8424 + 병렬 t1511 + 60s 캐시) 업종 전체 등락률 array sorted, `ls_get_industry_stocks` (t1516) 업종 안의 종목 + 업종 지수 요약. 모두 `/indtp/market-data` |
| 3 | 테마 wrapper 2개 추가 | `ls_get_theme_stocks` (t1537) 테마→종목, `ls_get_stock_themes` (t1532) 종목→테마 |
| 4 | 포트폴리오 export/import 2개 추가 | 버전드 JSON 단일 파일. import는 `mode=merge` 기본, `replace` 옵션 |
| 5 | 자동 enrichment 2종 — 쓰기 경로 fire-and-forget | (a) `stocks.krx_sector` ← t1102 (업종), (b) `stock_themes` ← t1532 (테마 멤버십). 읽기 경로는 캐시만 |
| 6 | `ls_holdings_list` 시그니처 확장 | `account?` 외에 `industry?`, `theme_code?`, `theme_keyword?` 3개 optional 필터 추가. AND 결합. 자세한 §4.5 |
| 7 | v0.5 `watched_sectors` → `watched_themes` 일괄 rename | DB 테이블, 필드(sector_code→theme_code, sector_name→theme_name), 도구(`ls_watched_sectors_*` → `ls_watched_themes_*`), 모델, 응답 envelope, export JSON. Schema v3 migration |
| 8 | TR 카탈로그에 5개 추가 | t1511, t1485 (catalog only), t8424 (catalog only, fanout 내부 사용), t1514 (catalog only), t1516, t1537. t1532는 v0.5 catalog에 이미 존재 — v0.6에서 wrapper 승격 |

### 2.1 명명 규약

| 의미 | 한국어 | 도구/필드명 | LS TR | 예시 |
|---|---|---|---|---|
| KRX 표준 산업분류 (~30) | 업종 | `industry`, `krx_sector` (DB 컬럼명 유지) | t1102 (현재 — 종목별 업종 enrichment), 업종 전체 등락률 TR은 v0.7로 (§10 Q2) | "전기전자", "화학", "금융" |
| LS 큐레이션 그룹 (~수백) | 테마 | `theme`, `theme_code` (4-char tmcode), `theme_name` | t1531 / t1532 / t1537 | "AI", "2차전지", "반도체 장비" (0012, 0064 등) |

v0.5에서 `watched_sectors`로 부르던 것은 실제로 **테마** — 마이그레이션으로 `watched_themes`로 이름 바로잡음. `stocks.krx_sector` DB 컬럼명은 그대로 유지하되 외부 노출(파라미터/응답)은 `industry`로 통일.

### 2.2 도구 surface 변경 요약

```
v0.5: 37 tools
v0.6: 37 + 7 신규 − 5 (Tier 1 압축) = 39 tools
      signature 변경: ls_holdings_list +3 optional params (industry, theme_code, theme_keyword)
      rename: ls_watched_sectors_{add,remove,list} → ls_watched_themes_{add,remove,list}
      삭제 (breaking): ls_account_get, ls_account_set_default
      합치기 (breaking): ls_holdings_{split,reverse_split,bonus} → ls_holdings_corporate_action
```

신규 (7):
- 지수·업종: `ls_get_index_quote`, `ls_get_industry_indices`, `ls_get_industry_stocks`
- 테마: `ls_get_theme_stocks`, `ls_get_stock_themes`
- 포트폴리오 I/O: `ls_portfolio_export`, `ls_portfolio_import`

압축 (-5 = -2 + -3):
- `ls_account_get` 삭제 — `ls_accounts_list` 응답의 `is_default` 플래그로 동일 정보. 모델이 array 필터링.
- `ls_account_set_default` 삭제 — `ls_account_upsert(set_default=true)`로 동일 효과.
- `ls_holdings_{split,reverse_split,bonus}` 3 → 1 `ls_holdings_corporate_action(type, ratio)`. 셋 다 같은 수학 family, `type` 열린 enum으로 출발 — v0.7+에 `stock_dividend` / `spin_off` / `merger` 등 추가 시 enum만 확장 (도구 surface 증가 없음). §4.5 상세.

v0.7로 미룬 후보 (§6 OUT): `ls_get_index_history` (t1514 wrapper — 업종 시계열), `ls_stocks_refresh_metadata` (sync enrichment).

v1.0 목표: 도구 수 40~45 유지 — v0.7~v0.8 신규 추가를 Tier 2~3 압축으로 상쇄.

## 3. 데이터 / 카탈로그 변경

### 3.1 카탈로그 v0.6.0 추가

LS 공식 가이드(`todo/t1485 t1511.txt`, `todo/t8424.txt`, `todo/t1514.txt`, `todo/t1516.txt`, `todo/t1537.txt`) 기준 확정. 모두 wrapper 있음 또는 catalog-only로 명시:

- **t1511 — 업종현재가** (wrapper). Path `/indtp/market-data`. Input: `upcode` (3-char). Output: 현재가 + 시고저 + 52주/연중 최고/최저 + 거래량/거래대금 + 시장 폭(상승/상한/보합/하락/하한 종목수) + 관련 보조지수 4개. 확정 코드 매핑: `001`=KOSPI, `101`=KOSPI200, `301`=KOSDAQ, `501`=KRX100. (KOSPI 50/KRX 300 등 추가 코드는 §10 Q1.)
- **t8424 — 전체업종** (catalog only, `ls_get_industry_indices` 내부에서 사용). Path `/indtp/market-data`. Input: `gubun1` (string, 시장/구분 토글). Output: 업종 코드 + 이름 array. *주의: 등락률 없음. 단순 catalog/discovery TR.* `ls_get_industry_indices`가 이걸로 upcode list 받아 t1511 fanout.
- **t1514 — 업종기간별추이** (catalog only). Path `/indtp/market-data`. Input: `upcode` + `gubun2` (1=일/2=주/3=월) + `cts_date` + `cnt` + `rate_gbn`. Output: 업종 시계열 (지수/거래량/거래대금/시고저/시장 폭/외인·기관 순매수). v0.6에 wrapper 없음 — `ls_call_tr`로만. wrapper `ls_get_index_history`는 v0.7로 (§6 OUT).
- **t1516 — 업종별종목시세** (wrapper). Path `/indtp/market-data`. Input: `upcode` + `gubun` (1=코스피업종/2=코스닥업종/3=섹터지수) + `shcode` (페이징). Output: `t1516OutBlock` (업종 자체의 지수/등락) + `t1516OutBlock1` (종목 array: shcode/hname/price/change/diff/volume/PER/시가총액/외인·기관 순매수). **페이징은 body-based** — 마지막 행 `shcode`를 다음 호출 InBlock에 넣음 (LS 가이드 "처음 조회시는 Space 연속 조회시에 이전 조회한 OutBlock의 shcode 값으로 설정").
- **t1485 — 예상지수** (catalog only). Path `/indtp/market-data`. 장전 예상지수. wrapper 없음 — `ls_call_tr`로만.
- **t1537 — 테마종목별시세조회** (wrapper). Path `/stock/sector`. Input: `tmcode` (4-char). Output: `t1537OutBlock` (테마 요약: tmname/tmcnt/upcnt/uprate) + `t1537OutBlock1` (종목 array). **페이징은 header-based** — InBlock에 shcode 같은 body 키 없음. LS 표준 `tr_cont=Y` + 응답 헤더의 `tr_cont_key`를 다음 호출 헤더에 echo (v0.5 CSPAQ 패턴과 동일).

### 3.2 portfolio.db 스키마 v3 마이그레이션

```sql
-- v3.1: watched_sectors → watched_themes (테이블 + 컬럼 rename).
-- 인덱스/제약 함께 갱신. v0.5 데이터(예: 0012, 0064 등 tmcode) 그대로 보존.
ALTER TABLE watched_sectors RENAME TO watched_themes;
ALTER TABLE watched_themes RENAME COLUMN sector_code TO theme_code;
ALTER TABLE watched_themes RENAME COLUMN sector_name TO theme_name;

-- v3.2: stock_themes 테이블 신규.
-- t1532(종목별테마) 응답을 stock 단위로 캐싱해서 holdings × theme 교차 쿼리에 사용.
CREATE TABLE stock_themes (
    symbol      TEXT NOT NULL REFERENCES stocks(symbol),
    theme_code  TEXT NOT NULL,
    theme_name  TEXT NOT NULL,
    updated_at  TEXT NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (symbol, theme_code)
);
CREATE INDEX idx_stock_themes_theme_code ON stock_themes(theme_code);
CREATE INDEX idx_stock_themes_theme_name ON stock_themes(theme_name);
```

`stocks.krx_sector` 컬럼은 이름 그대로 유지(외부 노출 시 `industry`로 매핑). 컬럼 추가 없음 — enrichment는 기존 NULL을 채우기만.

### 3.3 Export JSON 스키마 (`schema_version: 1`)

```json
{
  "schema_version": 1,
  "exported_at": "2026-05-15T12:34:56+09:00",
  "exporter_version": "0.6.0",
  "accounts": [
    {
      "account_number": "AAA",
      "nickname": "한투",
      "broker": "한국투자",
      "is_default": true,
      "created_at": "2026-05-15T...",
      "holdings": [
        { "shcode": "066570", "quantity": 64, "avg_price": 115801, "notes": null, "updated_at": "..." }
      ]
    }
  ],
  "watchlist_groups": [
    {
      "name": "반도체-AI",
      "description": null,
      "sort_order": 1,
      "created_at": "...",
      "items": [
        { "shcode": "005930", "notes": "core", "added_at": "..." }
      ]
    }
  ],
  "watched_themes": [
    { "theme_code": "0012", "theme_name": "반도체 장비", "notes": null, "added_at": "..." }
  ]
}
```

`stocks` 및 `stock_themes` 캐시는 export 안 함 (시세 enrichment에서 자동 재구성 가능, 디스크 트래픽만 늘림). `_schema_version` 테이블 안 함 (importer가 자체적으로 결정).

**Timestamp 정책.** v0.5 schema가 모든 행에 `created_at` / `added_at` / `updated_at`을 `NOT NULL DEFAULT (datetime('now'))`로 강제하므로 export 행에는 항상 timestamp가 있다. import 시 누락된 timestamp(외부 편집된 파일)는 import 시점의 `datetime('now')`로 채움. round-trip(export → import) 동등성 보장.

## 4. 도구 surface

### 4.1 지수·업종 (3개)

| 도구 | 인자 | 동작 |
|---|---|---|
| `ls_get_index_quote` | `index_code` (default `"001"` = KOSPI) | t1511. 단일 지수. `index_code` 별칭: `kospi`→`001`, `kosdaq`→`301`, `kospi200`→`101`, `krx100`→`501`. 모르는 별칭은 LS에 그대로 전달 후 에러 envelope으로 안내 |
| `ls_get_industry_indices` | `market` (`kospi`/`kosdaq`/`all`, default `kospi`), `top_n` (default 30) | **Fanout aggregator.** 1) t8424로 upcode 목록 받음 (시장별 필터). 2) 각 upcode에 t1511 호출(rate-limited). 3) 등락률 desc 정렬. 4) `top_n`으로 슬라이스. **60s 캐시**는 전체 array 저장 — 캐시 hit 시 `top_n`만 슬라이스해 응답하므로 같은 캐시로 *"top 5"* vs *"top 30"* 두 호출 모두 즉시. 캐시 만료 시 첫 호출 cold cost = §7 참조 |
| `ls_get_industry_stocks` | `upcode?` **또는** `industry_keyword?`, `market` (default `"1"` = 코스피업종), `top_n` (default 30) | t1516 wrapper. **Code 또는 name 양쪽 수용** — `upcode` 직접 또는 `industry_keyword`로 name LIKE 매치. 후자는 서버가 t8424 catalog 캐시로 해석. 응답: `industry: {upcode, name, value, change_pct}` + `stocks: [...]` (shcode/name/price/change/change_pct/volume/value/marketcap/PER/외인순매수/기관순매수). **페이징: `t1516InBlock.shcode` body-based continuation** — 마지막 행의 shcode를 다음 호출에 넣어 `top_n` 충족할 때까지 fetch. `top_n` 슬라이스. 해석 정책 §4.1.1 참조 |

#### 4.1.1 Keyword 해석 정책 (지수·업종 공통)

`industry_keyword`(또는 `theme_keyword`)가 들어오면:

1. **0개 매치** → `IndustryNotFound` (또는 `ThemeNotFound`) error envelope. message + 시작 후보 몇 개 동봉.
2. **1개 매치** → 해당 code로 자동 호출 + 응답에 `resolved: {upcode/theme_code, name, matched_via: "keyword"}` 에코 (v0.5 `applied_to` 패턴).
3. **2개+ 매치** → `AmbiguousIndustry` (또는 `AmbiguousTheme`) error envelope. `candidates: [{upcode, name}, ...]` 전부 동봉, 모델이 사용자에게 되묻거나 가장 가까운 걸로 재호출.

Catalog는 process-local 60s 캐시 (v0.5 `SectorCacheEntry` 재사용). 첫 호출은 t8424/t1531 1회 fanout, 이후 캐시 hit.

`upcode`/`theme_code`와 `industry_keyword`/`theme_keyword`가 동시 지정 시 code가 우선.

응답 envelope (`ls_get_index_quote`, t1511 출력 매핑):

```json
{
  "index_code": "001",
  "name": "종       합",
  "value": 2610.62,
  "previous_close": 2601.36,
  "change": 9.26,
  "change_pct": 0.36,
  "open":  { "value": 2617.43, "change_pct": 0.62, "time": "090030" },
  "high":  { "value": 2617.58, "change_pct": 0.62, "time": "090040" },
  "low":   { "value": 2610.40, "change_pct": 0.35, "time": "090740" },
  "volume": {
    "today": 263165, "previous": 569620, "change": -306455, "ratio": 46.20
  },
  "value_million_won": {
    "today": 3884240, "previous": 9383535, "change": -5499295, "ratio": 41.39
  },
  "range_52w": {
    "high": { "value": 2662.04, "date": "20220607", "change": -1.93 },
    "low":  { "value": 2134.77, "date": "20220930", "change": 22.29 }
  },
  "range_ytd": {
    "high": { "value": 2601.38, "date": "20230602", "change":  0.36 },
    "low":  { "value": 2180.67, "date": "20230103", "change": 19.72 }
  },
  "market_breadth": {
    "up": 606, "limit_up": 0, "unchanged": 91, "down": 253, "limit_down": 0
  },
  "related_indices": [
    { "code": "001", "name": "종       합", "value": 2610.62, "change":  9.26, "change_pct": 0.03 },
    { "code": "002", "name": "대   형  주", "value": 2611.97, "change":  7.26, "change_pct": 0.28 },
    { "code": "003", "name": "중   형  주", "value": 2760.88, "change": 22.71, "change_pct": 0.83 },
    { "code": "004", "name": "소   형  주", "value": 2393.35, "change": 14.01, "change_pct": 0.59 }
  ],
  "timestamp": "2026-05-15T17:02:40+09:00"
}
```

**LS 필드 → envelope 매핑** (가이드 t1511OutBlock 기준):

| envelope | LS 필드 | 비고 |
|---|---|---|
| `value` | `pricejisu` | 현재지수 |
| `previous_close` | `jniljisu` | 전일지수 — `value - previous_close == change` 검산 가능 |
| `change` / `change_pct` | `change` / `diffjisu` | 부호는 `sign` 필드로 적용 (1/2 상승, 4/5 하락, 3 보합) |
| `open` / `high` / `low` | `openjisu` + `opendiff` + `opentime` (3쌍) | nested obj로 합침 |
| `range_52w` / `range_ytd` | `whjisu`/`whchange`/`whjday` 등 (52주 4필드 + ytd 4필드) | nested obj로 정리 |
| `market_breadth` | `highjo`/`upjo`/`unchgjo`/`lowjo`/`downjo` | 시장 폭 |
| `volume` / `value_million_won` | `volume`/`jnilvolume`/`volumechange`/`volumerate` (4쌍씩) | today/previous/change/ratio nested |
| `related_indices[]` | `firstjcode`/`firstjname`/`firstjisu`/`firsign`/`firchange`/`firdiff` × 4쌍 | 4 보조지수 (종합/대형/중형/소형) |

t1511의 `firstjcode`/`secondjcode`/`thirdjcode`/`fourthjcode` 4쌍을 `related_indices` 배열로 합칩니다 — 코스피 종합(001) 조회 시 종합/대형주/중형주/소형주가 같이 나오는 점이 *"오늘 코스피 +0.36%인데 대형주 +0.28% vs 중형주 +0.83%"* 같은 시장 폭 해석에 그대로 쓰임. `market_breadth`도 같은 목적.

### 4.2 테마 wrapper (2개)

| 도구 | 인자 | 동작 |
|---|---|---|
| `ls_get_theme_stocks` | `theme_code?` (4-char tmcode) **또는** `theme_keyword?`, `top_n` (default 30) | t1537 wrapper. **Code 또는 name 양쪽 수용** — `theme_keyword`는 서버가 t1531 catalog 캐시 (v0.5 sector enrichment에서 이미 사용 중)로 LIKE 매치. 응답: `theme: {code, name, stock_count, up_count, up_rate}`, `stocks: [...]`. **페이징: 헤더 기반 `tr_cont` / `tr_cont_key`** — t1537은 InBlock에 shcode 같은 body 키가 없고, 큰 테마(2차전지 200+)는 LS 표준 헤더 continuation으로 페이지 fetch. `top_n` 충족할 때까지 반복 호출. 키워드 해석 정책 §4.1.1과 동일 |
| `ls_get_stock_themes` | `shcode` | t1532. 응답: 종목이 속한 테마 array (theme_code/theme_name/avgdiff). 빈 배열 가능 (어떤 테마에도 안 속한 종목) |

자연 질의 매핑 (사용자가 watched theme 등록 여부 무관):

- *"2차전지 테마 종목 보여줘"* → `ls_get_theme_stocks(theme_keyword="2차전지")` 직접. 다중 매치(2차전지 셀/소재/장비)면 `AmbiguousTheme` envelope으로 후보 노출 → 모델이 사용자에게 *"어느 2차전지?"* 되묻거나 가장 일반적인 것 선택
- *"AI 테마 종목"* → `ls_get_theme_stocks(theme_keyword="AI")` → 다중 매치 → 후보 동봉
- *"테마 0064"* → 코드 직접: `ls_get_theme_stocks(theme_code="0064")`
- *"삼성전자는 어떤 테마에 묶여 있어?"* → `ls_get_stock_themes(shcode="005930")`

### 4.3 포트폴리오 I/O (2개)

| 도구 | 인자 | 동작 |
|---|---|---|
| `ls_portfolio_export` | `path?` | 모든 accounts/holdings/watchlist_groups+items/watched_themes를 §3.3 형식 JSON으로 직렬화. `path` 생략 시 OS별 default 경로(아래) 자동 생성. 응답: §4.3.1 export envelope. |
| `ls_portfolio_import` | `path`, `mode` (`merge` default / `replace`), `confirm` (replace 시 필수) | JSON 읽어서 적용. `merge`/`replace` 동작 §4.3.2 참조. 응답: §4.3.3 import envelope. 신규/지원 안 하는 `schema_version` → `ImportSchemaMismatch` error envelope. |

#### Default export path (OS별)

- Windows: `%LOCALAPPDATA%\RedoxNet\LsOpenApi\exports\portfolio-YYYY-MM-DDTHHmmss.json`
- Linux/macOS: `~/.local/share/redoxnet/lsopenapi/exports/portfolio-YYYY-MM-DDTHHmmss.json`

`token.db` / `portfolio.db`와 같은 부모 디렉토리 아래 `exports/` 하위 폴더. 폴더 자동 생성.

#### 4.3.1 Export 응답 envelope

```json
{
  "path": "C:\\Users\\diluc\\AppData\\Local\\RedoxNet\\LsOpenApi\\exports\\portfolio-2026-05-15T210234.json",
  "schema_version": 1,
  "counts": {
    "accounts": 2,
    "holdings": 9,
    "watchlist_groups": 1,
    "watchlist_items": 2,
    "watched_themes": 4
  },
  "size_bytes": 12345
}
```

#### 4.3.2 Import 모드별 동작

- **`merge`** (default): 충돌 정책은 도메인별로:
  - `accounts`: 같은 `account_number` 존재 시 skip. nickname 충돌은 별도 skip(`account_number`는 다른데 nickname이 같을 때).
  - `holdings`: 같은 `(account_id, shcode)` 존재 시 skip (덮어쓰지 않음 — `_set` 의도 깨짐 방지).
  - `watchlist_groups`: 같은 `name` 존재 시 skip.
  - `watchlist_items`: 같은 `(group, shcode)` 존재 시 skip.
  - `watched_themes`: 같은 `theme_code` 존재 시 skip.
- **`replace`** (요구 `confirm=true`):
  - **Wipe 대상**: `accounts`, `holdings`, `watchlist_groups`, `watchlist_items`, `watched_themes`. Export 파일이 들고 있는 도메인 전부.
  - **Wipe 제외**: `stocks` 캐시, `stock_themes` 캐시 — quote enrichment 재구축 비용을 피하기 위해 유지. import 후 차후 quote 호출 시 자동 갱신.
  - **자동 백업**: wipe 직전에 `exports/before-import-YYYY-MM-DDTHHmmss.json` 생성. 사용자가 원복 가능.

#### 4.3.3 Import 응답 envelope

```json
{
  "mode": "merge",
  "source_path": "C:\\...\\portfolio-2026-05-15T210234.json",
  "schema_version": 1,
  "imported": {
    "accounts": 2,
    "holdings": 7,
    "watchlist_groups": 1,
    "watchlist_items": 2,
    "watched_themes": 4
  },
  "skipped": {
    "accounts": [
      { "account_number": "AAA", "nickname": "한투", "reason": "duplicate_account_number" }
    ],
    "holdings": [
      { "account_number": "AAA", "shcode": "005930", "reason": "duplicate_account_holding" }
    ],
    "watchlist_groups": [],
    "watchlist_items": [],
    "watched_themes": []
  },
  "auto_backup_path": null
}
```

`replace` 모드에서는 `auto_backup_path`가 생성된 백업 파일 경로. `skipped` 객체의 모든 array는 빈 배열 (전체 교체이므로 충돌 불가).

### 4.4 도메인 간 enrichment

#### 자동 enrichment 2종 (쓰기 경로 fire-and-forget)

`ls_holdings_set` / `_buy` / `ls_watchlist_add` 호출 시, 응답 반환 후 background task로:

1. **t1102 → `stocks.krx_sector`** (업종 채우기). 단건 호출.
2. **t1532 → `stock_themes` upsert** (이 종목이 속한 모든 테마 캐시). 응답 array를 stock_themes 테이블에 UPSERT (theme_code 단위 PRIMARY KEY).

읽기 경로(`_list` 및 새 필터)는 캐시된 값만 사용 — 느려지지 않음. 첫 호출 직후 곧바로 list하면 캐시가 still empty일 수 있다.

#### 4.4.1 Enrichment freshness hint

위 "캐시 still empty" 상황을 모델이 사용자에게 자연어로 안내할 수 있도록 `ls_holdings_list` 응답에 freshness 블록 추가:

```json
{
  "accounts": [
    {
      "holdings": [
        {
          "shcode": "005930",
          "name": "삼성전자",
          "krx_sector": null,
          "krx_sector_status": "pending",
          "themes": [],
          "themes_status": "pending",
          "...": "..."
        }
      ]
    }
  ],
  "metadata_freshness": {
    "fully_enriched": false,
    "pending": { "krx_sector": 3, "themes": 2 },
    "hint": "방금 등록한 종목의 업종/테마 정보는 다음 호출에서 채워집니다."
  }
}
```

- 행별 `*_status`: `"ok"` / `"pending"` / `"failed"`. `"ok"`일 때는 status 필드 자체를 생략(payload 절약).
- 모두 `"ok"`일 때 `metadata_freshness.fully_enriched: true`이고 `pending` 빈 객체, `hint`도 생략.
- `"failed"`는 t1102/t1532가 LS-side 에러를 반환했거나 자격증명이 없는 상태. 다음 쓰기 시 재시도.

### 4.5 Holdings 도구 압축 (v0.5 → v0.6 breaking)

v0.5의 `ls_holdings_split` / `_reverse_split` / `_bonus` 3개를 단일 도구 — **corporate action family**의 진입점 — 으로 통합. 도구 이름이 의도적으로 일반적(`corporate_action`): 향후 같은 family에 들어올 케이스를 새 도구 추가 없이 enum 확장만으로 흡수.

```
ls_holdings_corporate_action(
  shcode,
  type: "split" | "reverse_split" | "bonus",   // v0.6, 향후 확장 가능
  ratio: number,
  account?: string
)
```

#### v0.6 지원 type (3종)

| `type` | `ratio` 의미 | 수학 |
|---|---|---|
| `split` | 정수 ≥ 2 (예: `10` = 1:10 분할) | qty × ratio, avg ÷ ratio |
| `reverse_split` | 정수 ≥ 2 (예: `5` = 5:1 병합) | qty ÷ ratio (정수 미분배 시 `ValidationError`), avg × ratio |
| `bonus` | 0 < ratio (예: `0.1` = 10% 무상증자) | qty × (1 + ratio), avg ÷ (1 + ratio) |

#### Future types (v0.7+, enum 확장만으로 처리)

같은 도구 schema에 `type` enum 항목만 추가하면 됨 — 도구 이름·시그니처·응답 envelope 모양 그대로. 후보:

| `type` | 한글 | 수학 / 비고 |
|---|---|---|
| `stock_dividend` | 주식배당 | qty × (1 + ratio), avg ÷ (1 + ratio). 수학은 `bonus`와 동일하지만 회계상 출처가 다름(이익잉여금 vs 자본잉여금) — 모델/사용자 의도 분리를 위해 별도 type |
| `spin_off` | 인적분할 | 한 종목 → 두 종목. 단순 `ratio` 부족 — `new_shcode`/`keep_ratio`/`new_ratio`/`new_avg_basis` 등 optional 파라미터 추가 필요. 도구 시그니처는 conditional optional로 흡수 (type=spin_off일 때만 사용) |
| `merger` | 합병 | 한 종목 → 다른 종목으로 교환. spin_off와 유사한 conditional 파라미터 |

원칙: corporate action family의 새 케이스가 들어와도 (a) 새 도구 추가 X, (b) 기존 type의 schema 변경 X, (c) 새 optional 파라미터는 conditional documentation. v0.6 spec은 이 family를 *열린* enum으로 출발시키는 첫 발.

#### 응답 envelope

`applied_to`에 type + ratio 에코. 향후 spin_off 등 추가 출력 필드는 `applied_to`의 nested object를 type별로 확장.

```json
{
  "shcode": "452200",
  "applied_to": [
    {
      "account_number": "AAA",
      "type": "split",
      "ratio": 10,
      "before": { "quantity": 450, "avg_price": 2293 },
      "after":  { "quantity": 4500, "avg_price": 229.3 }
    }
  ]
}
```

`account` 미지정 시 보유한 모든 계좌에 일괄 적용 (v0.5 정책 그대로). 알 수 없는 `type` 값은 `ValidationError` envelope에 *"지원하는 type: split/reverse_split/bonus. 추가 타입은 향후 릴리스에서 enum 확장 예정"* 안내.

### 4.6 `ls_holdings_list` 시그니처 확장

```
ls_holdings_list(account?, industry?, theme_code?, theme_keyword?)
```

| 파라미터 | 동작 |
|---|---|
| `industry` | `stocks.krx_sector LIKE '%{value}%'`. *"반도체"*가 *"반도체장비"*, *"반도체부품"* 모두 매치 |
| `theme_code` | 정확 일치. holdings 행 중 `stock_themes.theme_code = ?` 조건 만족 |
| `theme_keyword` | `stock_themes.theme_name LIKE '%{value}%'`. *"2차전지"*가 *"2차전지수혜주"* 매치 |

여러 파라미터 동시 지정 시 **AND 결합**. 모두 null/빈 → 기존 동작 (전체 보유종목).

필터가 1개 이상 활성화되면 응답에 매치된 unique 값 리스트를 동봉. LIKE 매치의 false positive를 사용자가 즉시 확인 가능:

```json
{
  "accounts": [ ... ],
  "total_summary": { ... },
  "filter": { "industry": "반도체", "theme_keyword": "AI" },
  "matched_industries": ["반도체", "반도체 장비", "반도체 부품"],
  "matched_themes": ["AI", "AI 반도체", "온디바이스 AI"]
}
```

필터 미지정 도메인의 `matched_*` 필드는 응답에서 생략 (industry만 필터 시 `matched_themes` 없음).

자연 질의 매핑:

- *"내 보유종목 중 반도체 업종만"* → `ls_holdings_list(industry="반도체")`
- *"내 보유종목 중 2차전지 테마"* → `ls_holdings_list(theme_keyword="2차전지")`
- *"내 한투 계좌의 반도체 업종 보유 중 AI 테마인 것"* → `ls_holdings_list(account="한투", industry="반도체", theme_keyword="AI")`

### 4.7 에러 envelope

기존 v0.5 envelope 형식 유지(`error` + `message` + 도메인별 hint 필드). 신규 코드:

- `IndexNotFound` — `ls_get_index_quote(index_code="999")` 미존재. message + 알려진 코드 일부 동봉.
- `IndustryNotFound` / `ThemeNotFound` — `industry_keyword` / `theme_keyword`로 매치되는 코드 0개. message + 시작 후보 몇 개 (`candidates: [{upcode, name}]` 또는 `[{theme_code, theme_name}]`).
- `AmbiguousIndustry` / `AmbiguousTheme` — keyword에 매치되는 코드 2개+. `candidates` 전체 동봉. v0.5 `AmbiguousAccount` 패턴 동일.
- `ImportSchemaMismatch` — `schema_version`이 importer 지원 범위 밖.
- `ImportConflict` — `merge` 모드에서 충돌 발생. 어느 항목이 충돌했는지 list (§4.3.3 `skipped` 블록으로 정상 응답에 흡수 — 별도 에러 envelope 아님).

## 5. 마이그레이션 영향

- **호스트(MCP 클라이언트)**:
  - 도구 추가 7개 (호환).
  - `ls_holdings_list`에 optional 파라미터 3개 추가 (호환).
  - `ls_watched_sectors_*` 3개 → `ls_watched_themes_*` rename (**breaking**). 파라미터 `sector_code` → `theme_code`, `sector_name` → `theme_name`. 사용자 측 직접 호출 없고 모델이 도구 description 기반으로 라우팅하므로 영향 작음.
  - **Tier 1 압축 (breaking, total −5):**
    - `ls_account_get` 삭제 — `ls_accounts_list`의 `is_default` 플래그로 대체.
    - `ls_account_set_default` 삭제 — `ls_account_upsert(set_default=true)`로 대체.
    - `ls_holdings_split` / `_reverse_split` / `_bonus` 3개 → `ls_holdings_corporate_action(type, ratio)` 1개. `type` enum으로 분기.
  - **총 도구 수**: 37 + 7 − 5 = **39**.
- **카탈로그**: 18 → 23 TRs (t1485, t1511, t8424, t1514, t1516, t1537 추가. t1531/t1532는 v0.5 카탈로그에 이미 존재). path 분포: `/indtp/market-data` 신규(t1485, t1511, t8424, t1514, t1516), `/stock/sector` 기존(t1531/t1532/t1537).
- **데이터**: portfolio.db schema v2 → v3. `watched_sectors` 테이블/컬럼 rename, `stock_themes` 신규. v0.5 사용자 데이터는 자동 마이그레이션으로 그대로 보존.
- **응답 envelope**: v0.5 `SectorListResult`/`WatchedSectorWithQuote` → v0.6 `ThemeListResult`/`WatchedThemeWithQuote`. 필드명 `sector_code/sector_name` → `theme_code/theme_name`.
- **NuGet**: Core 0.5.0 → 0.6.0 (카탈로그만), Mcp 0.5.0 → 0.6.0 (도구 surface 확장 + rename).

## 6. v0.6 스코프

### IN

- A. 지수·업종 도구 3개 — `ls_get_index_quote` (t1511), `ls_get_industry_indices` (t8424+t1511 fanout, 60s cache), `ls_get_industry_stocks` (t1516). 카탈로그에 t1511/t8424/t1514/t1516/t1485 추가 (t1485·t1514·t8424는 wrapper 없는 catalog).
- B. 테마 wrapper 2개 (`ls_get_theme_stocks`, `ls_get_stock_themes`) + t1537 카탈로그 추가, t1532 wrapper 승격
- C. Portfolio export/import + JSON schema v1
- D. 자동 enrichment 2종 (`stocks.krx_sector` ← t1102 업종, `stock_themes` ← t1532 테마)
- E. `ls_holdings_list` 3-필터 확장 (`industry?`, `theme_code?`, `theme_keyword?`)
- F. **v0.5 명명 정정**: `watched_sectors` → `watched_themes` (테이블/필드/도구/모델/envelope 일괄 rename, schema v3 migration)
- G. **Tier 1 도구 압축** (−5): `ls_account_get` 삭제, `ls_account_set_default` 삭제, `ls_holdings_{split,reverse_split,bonus}` 3→1 `ls_holdings_corporate_action`. 통합 도구는 **열린 enum**(`type`)으로 출발 — v0.7+에 `stock_dividend`/`spin_off`/`merger` 등 추가 시 도구 surface 증가 없이 enum만 확장. LLM 라우팅 부담 완화 — v1.0에서 도구 수 40~45 안에 마감하려는 전략의 첫 발

### OUT (v0.7+)

- **`ls_get_index_history`** (t1514 wrapper) — v0.7. 업종 시계열 (일/주/월). 단건 지수의 chart 격. v0.6에는 t1514 catalog만 등록해 `ls_call_tr`로 호출은 가능. wrapper 도입 시 `ls_get_chart`와의 일관성(period_type, count, indicators 지원 여부 등) 결정 필요해서 v0.7로 미룸.
- **`ls_stocks_refresh_metadata`** 동기 명시 갱신 도구 — v0.7. 결정 근거: v0.6의 fire-and-forget enrichment(§4.4)가 실제 사용에서 얼마나 stale을 만드는지 측정해야 명시 갱신 도구의 시그니처(`shcodes?` 선택, batched 정책, 우선순위 큐)가 정해진다. v0.6 lazy 정책의 실측 마찰 데이터를 보고 v0.7에서 도입.
- `ls_get_fundamentals_rank` (t3341) — v0.7
- `ls_get_investor_flow` (t1601 / t1702) — v0.7
- `ls_get_stock_events` (t3202) / `ls_get_market_warnings` (t1404/t1405) — v0.7
- 자동 백업 스냅샷 (risky ops 전) — v0.9 RC
- 뉴스(t3102) — v0.8 또는 v1.0 후

## 7. 함정 / 리스크

- **t1511 `upcode` 별칭 dict의 sparse 노출.** 가이드 기준 `001`/`101`/`301`/`501` 4종만 v0.6에 명시. KOSPI 50/KRX 300 등 추가 코드는 testbed로 확인 후 dict에 추가 — 사용자가 *"오늘 KRX 300"* 요청 시 모르는 별칭이면 LS 에러를 friendly envelope으로 변환.
- **`ls_get_industry_indices` cold-cache fanout 비용 — 미확정 두 변수.** 첫 호출 latency는 `N × (1 / TPS)`로 결정. 
  - **N = t8424 반환 upcode 수**. 가이드 example이 3개(001/002/820)만 보여줘 분포 모름. `market="kospi"`로 좁히면 KOSPI 업종 ~20-30개 추정, `all`은 800+까지 갈 수 있음 (`820` "KQ150 L KP200 0.5 S" 같은 합성지수 포함).
  - **TPS = LS 사이드 t1511 rate limit**. 우리 catalog default는 1 TPS (보수적). 실제 LS는 보통 더 관대(3~10 TPS). testbed로 측정 필요.
  - 시나리오 표 (KOSPI 25개 코드 가정):

    | LS 실제 TPS | cold-cache latency | steady-state (60s 캐시) |
    |---|---|---|
    | 1 (catalog default) | ~25s | 0 |
    | 5 | ~5s | 0 |
    | 10 | ~3s | 0 |

  완화책: (1) 60s 캐시 (`SectorCacheEntry` v0.5 패턴 재사용) — 첫 호출만 비용, 분석 세션 안 4~5번 호출에서 1번만 부담, (2) `market` default = `kospi` — 사용자가 명시적으로 `all` 줘야 전체 fanout, (3) testbed에서 LS 실제 TPS 측정 후 병렬화 강화 여부 결정. 첫 출시는 catalog의 rate_limiter가 직렬화하므로 보수적으로 두고, 측정 후 catalog의 `rate_limit_per_sec`를 실측값으로 갱신.
- **t8424 upcode 범위 미확정.** 가이드 example response가 001/002/820만 보여줘서 실제 범위 불명. 추정: 1xx KOSPI 지수계, 2xx 코스피 업종(섹터), 3xx KOSDAQ 지수계, 4xx KOSDAQ 업종(섹터), 5xx KRX·테마성, 800+ 합성/레버리지. testbed 호출로 분포 확정 + `ls_get_industry_indices`의 default 필터 범위 결정.
- **`ls_get_theme_stocks`의 stock_count 페이징.** t1537이 한 테마에 200+ 종목 있는 케이스(e.g. *"2차전지"*)에서 continuation 지원하는지 미확인. 일단 `top_n=50` 클라이언트 측 캡으로 v0.6 통과, 페이징은 testbed 보고 결정.
- **Export/import 파일 크기.** 사용자가 수년 누적 시 holdings/watchlist 수백 행 가능 — 압축은 v0.6 out, 단일 JSON으로 충분.
- **Import `replace` 모드의 위험.** 자동 백업으로 안전망. confirm=true 명시 요구.
- **Background enrichment의 lifecycle.** MCP stdio 서버는 호스트가 stdin 닫으면 종료. fire-and-forget task가 db write 중에 종료될 수 있음. → write를 짧게(t1102/t1532 1콜 + UPSERT 1콜), 트랜잭션화. 종료 직전 호출 시 그 enrichment만 누락 — 다음 세션 첫 list 호출 때 lazy 재시도하면 됨. 즉 lazy + best-effort 정책.
- **단일 프로세스 내 동시 enrichment 결정**: 같은 종목에 대해 t1102 task와 t1532 task가 동시 실행될 수 있으나 stocks/stock_themes 테이블이 분리돼 있어 충돌 없음. 같은 종목이 두 번 빠르게 등록될 경우(e.g. `_buy` 직후 `_buy`)에는 두 t1102 task가 같은 `stocks` 행을 동시 update — `INSERT OR REPLACE`(UPSERT) 사용으로 last-write-wins. 단일 프로세스 race는 이걸로 종결.
- **`industry` 필터의 LIKE 매치 false positive.** 사용자가 *"전자"*로 필터하면 *"전자부품"*, *"의료전자"* 등 의도 외 종목 다수. 정확 일치 vs LIKE 트레이드오프. v0.6은 LIKE로 가고, 너무 시끄러우면 v0.7에 `match_mode` 파라미터 추가.
- **`theme_keyword` vs `theme_code` 모호성.** *"2차전지"* 같이 같은 키워드의 테마가 여럿 존재(예: "2차전지 셀", "2차전지 소재", "2차전지 장비") — `theme_keyword`로 필터하면 다 합쳐짐. 정밀하게 한 테마만 원하면 `theme_code` 사용을 도구 description에 안내.
- **테마 멤버십의 시간 가변성.** LS가 테마 분류를 수시로 갱신 — `stock_themes` 캐시는 stale 가능. v0.6은 쓰기 경로에서만 lazy 갱신이고 사용자가 명시적 refresh를 못 호출함. v0.7 `ls_stocks_refresh_metadata`까지의 트레이드오프.

## 8. 테스트 계획

### 8.1 단위 / 픽스처

- t1511 / t8424 / t1516 / t1537 / t1532 fixture (testbed JSON 그대로 핀). t1485 / t1514는 카탈로그 등록만 검증, 도구 wrapper 없음.
- `ls_get_index_quote` 별칭 매핑 (kospi→001, kosdaq→301, kospi200→101, krx100→501)
- `ls_get_index_quote` 응답 envelope shape: `related_indices` 4개 row, `market_breadth` 5필드, `range_52w`/`range_ytd` nested, 52주 데이터 형변환 (LS의 `whjisu`/`whjday` → 우리 envelope)
- `ls_get_industry_indices` fanout 동작: stub t8424 (3개 코드만) + stub t1511 (각 코드 응답) → 등락률 desc 정렬 + 60s 캐시 (두 번째 호출은 stub 안 건드림)
- `ls_get_industry_indices` `top_n` 슬라이스: 캐시에 25개 있어도 `top_n=5` 호출은 5개만 반환. 같은 캐시 hit에서 `top_n=30` 다시 호출 시 즉시 25개 (전체)
- `ls_get_industry_stocks` 응답 shape + body-based 페이징: stub이 `t1516InBlock.shcode` echo → top_n 채울 때까지 호출 반복
- `ls_get_industry_stocks` keyword 해석: 1개 매치(`resolved` echo) / 0개(`IndustryNotFound`) / 2개+(`AmbiguousIndustry` + candidates)
- `ls_get_theme_stocks` 응답 shape + header-based 페이징: stub이 응답 헤더 `tr_cont=Y` + `tr_cont_key` echo → 다음 호출 헤더 echo 검증, top_n 충족 시 중단
- `ls_get_theme_stocks` keyword 해석: 동일 3가지 분기 (1/0/2+)
- `code` + `keyword` 동시 지정 → code 우선
- **압축 회귀**: v0.5 `ls_account_get` 호출 → tool not found 에러. `ls_accounts_list`의 `is_default` 플래그로 동일 정보 추출 가능
- **압축 회귀**: v0.5 `ls_account_set_default(account)` 호출 → tool not found. `ls_account_upsert(account_number=X, set_default=true)`로 대체 동작 검증
- **`ls_holdings_corporate_action` type 분기**: `split(ratio=10)`, `reverse_split(ratio=5)` non-divisible 거부, `bonus(ratio=0.1)` 무상증자 비율 — 셋 다 v0.5 split/reverse_split/bonus 테스트 케이스를 type 파라미터로 합쳐 회귀
- **`ls_holdings_corporate_action` 미지원 type 거부**: `type="stock_dividend"` 같은 v0.7 후보 → `ValidationError` envelope, message에 *"지원: split/reverse_split/bonus"* + *"추가 타입은 향후 릴리스에서 enum 확장 예정"* 안내 (열린 enum 정책 가시화)
- t1511 필드 매핑: `pricejisu`→`value`, `jniljisu`→`previous_close`, `whjisu`/`whjday`→`range_52w` nested, 4 보조지수 → `related_indices[]` 배열 (응답 디테일 4.1 참조)
- Schema v3 migration round-trip: v2 데이터(watched_sectors with rows)가 v3에서 watched_themes로 정확히 이행되는지
- Enrichment trigger 2종: 쓰기 후 background task가 `stocks.krx_sector`와 `stock_themes`를 모두 업데이트
- `ls_holdings_list` 3-필터 정확성:
  - `industry`만: LIKE 동작
  - `theme_code`만: exact match
  - `theme_keyword`만: LIKE on theme_name
  - 3개 동시: AND 결합
- Export → Import round-trip: data 동등성, schema_version 검증, conflict detection in merge mode, replace flag enforcement, `watched_themes` 키 인식

### 8.2 통합 (portfolio-smoke.py + 신규 chart-style smoke)

기존 28 케이스 + 신규:

1. 지수: KOSPI / KOSDAQ / KOSPI 200 / KRX 100 별칭 호출 + 응답 shape (related_indices, market_breadth, range_52w 포함)
2. 업종 전체 등락률 array — `ls_get_industry_indices(market="kospi")` → 첫 호출 직렬 fanout, 두 번째 호출 캐시 hit
3. 업종 안의 종목: `ls_get_industry_stocks(upcode="...")` → 업종 지수 요약 + top_n=20 종목
4. 테마 stocks: 0064 (2차전지) 호출 후 stock_count > 5
4. 종목 themes: 005930 호출 후 result에 *"반도체"* 포함
5. v0.5 → v0.6 migration: pre-existing `watched_sectors` 행이 `watched_themes`로 보존되는지
6. `ls_watched_themes_*` 신규 도구 동작 (v0.5 케이스 회귀)
7. Export 후 임시 DB에 Import → 데이터 동등성 (특히 `watched_themes` 키)
8. Import schema_version mismatch → ImportSchemaMismatch
9. `ls_holdings_list(industry="반도체")` → enrichment 후 SK하이닉스만 매치
10. `ls_holdings_list(theme_keyword="2차전지")` → stock_themes 캐시 기반 매치
11. `ls_holdings_list(industry=X, theme_code=Y)` AND 결합 정확성
12. Enrichment 비동기 동작: set 직후 list → krx_sector / stock_themes 아직 비어 있음, 잠시 후 list → 채워짐

### 8.3 E2E (수동)

- *"오늘 코스피 어땠어?"* → `ls_get_index_quote` 직접 호출, ETF 우회 안 함
- *"오늘 강한 업종은?"* → `ls_get_industry_indices(market="kospi")` 호출 후 상위 5 정렬 보고
- *"전기전자 업종 종목 비교"* → 모델이 `ls_get_industry_stocks(industry_keyword="전기전자")` 직접 호출 (서버가 t8424 catalog로 자동 해석). 다중 매치 시 `AmbiguousIndustry` envelope → 모델이 후보 보여주거나 가장 일반적인 걸로 재호출
- *"AI 테마 종목 비교"* → `ls_get_theme_stocks(theme_keyword="AI")` 직접 호출. 사용자가 AI를 watched theme으로 등록 안 한 케이스도 동작
- *"2차전지 테마 종목들 오늘 등락률 비교"* → `ls_get_theme_stocks(theme_keyword="2차전지")` 직접. 다중 매치(2차전지 셀/소재/장비 등) 시 모델이 `AmbiguousTheme` 후보 보고 의도 확인
- *"내 보유 중 반도체 업종"* vs *"내 보유 중 2차전지 테마"* → 모델이 `industry` vs `theme_keyword` 파라미터를 정확히 라우팅하는지
- *"내 포트폴리오 백업"* → `ls_portfolio_export` 응답에서 path 확인 + 모델이 파일 위치 안내
- *"새 머신에 옮길래"* → export → manual copy → import (merge) → 동등성

## 9. 작업 순서

1. **카탈로그 v0.6 추가** (t1511, t1485, t8424, t1514, t1516, t1537) + testbed 검증. t1485 / t1514 / t8424는 catalog만 (wrapper 없음). t1511/t8424/t1514/t1516 path = `/indtp/market-data`, t1537 path = `/stock/sector` 확정 (가이드 기준).
2. **Schema v3 migration** (watched_sectors rename + stock_themes 신규)
3. **모델/도구 rename** (WatchedSector → WatchedTheme, `ls_watched_sectors_*` → `ls_watched_themes_*`, envelope 필드, export JSON 키) — 기존 테스트 회귀로 모두 통과 확인
4. **지수·업종 도구 3개** + fixture tests. `ls_get_industry_indices` fanout 로직과 60s 캐시는 v0.5 `SectorCacheEntry` 패턴 재사용.
5. **테마 wrapper 2개** + fixture tests
6. **Enrichment 2종** (t1102/`krx_sector`, t1532/`stock_themes`) + background task + repository 메서드
7. **`ls_holdings_list` 3-필터 확장** + 테스트
8. **Portfolio export/import** + round-trip 테스트 + 자동 백업 헬퍼
9. **Tier 1 압축** (v0.5 breaking): `ls_account_get` / `ls_account_set_default` 도구 제거 (`PortfolioTools.cs`에서 [McpServerTool] 메서드 삭제), `ls_holdings_{split,reverse_split,bonus}` 3개 → 단일 `ls_holdings_corporate_action(type, ratio)`. Service / Repository 레이어는 그대로 두고 도구 surface만 정리.
10. **portfolio-smoke.py 확장** (압축 회귀 포함)
11. **README / TR INVENTORY / NuGet README 갱신** — 도구 수 37 → 39 명기, 압축 사유(LLM 라우팅 부담 완화) 한 줄
12. **버전 bump 0.5.0 → 0.6.0**

## 10. Open questions

- **Q1: t1511 추가 `upcode` 매핑.** 가이드에서 `001`/`101`/`301`/`501` 4종 확정. KOSPI 50, KRX 300 같은 추가 코드 존재 여부 testbed로 확인 — 존재 시 별칭 dict에 추가. 잘못된 코드 보냈을 때 LS rsp_cd 패턴도 같이 캡처해 friendly error 메시지화.
- **Q2: t8424 `gubun1` 의미 + upcode 분포.** 가이드가 `gubun1` 값 매핑을 비워둠 (빈 문자열 example만). 추정: ""=전체, "1"=KOSPI 업종, "2"=KOSDAQ 업종, "3"=섹터지수. testbed로 확인 + `ls_get_industry_indices(market=…)`의 매핑 확정. example response의 upcode "820" 같은 합성/레버리지 지수가 fanout 대상에 포함되는지도 결정 (default 제외 권장).
- **Q3: 테마 stock_count 페이징.** t1537 continuation 지원 여부. 200+ 종목 테마 케이스 (전기차/2차전지) 확인.
- **Q4: 다중 호스트 동시 portfolio.db 접근.** Claude Desktop + Codex CLI를 같은 PC에서 동시 실행하면 두 프로세스가 같은 portfolio.db에 동시 write 가능. SQLite WAL 모드로 read는 안전하지만 두 호스트의 background enrichment task가 같은 stocks 행을 race할 수 있음. 게다가 holdings_set이 동시에 같은 (account, symbol)로 들어오면 의도 외 마지막-쓰기 승. 빈도 낮은 시나리오라 v0.6에서는 명문화만 하고 lock 도입은 보류 — 발생 빈도 보고 v0.7+에서 advisory lock(`PRAGMA locking_mode=EXCLUSIVE` 또는 별도 lock 파일) 검토. (단일 프로세스 내 race는 §7에서 UPSERT로 종결.)
- **Q5: `industry` 필터의 정확 일치 옵션.** v0.6은 LIKE만 (§4.5 + `matched_industries` echo로 false positive 가시화). 실 사용에서 false positive가 많으면 v0.7에 `match_mode: contains | exact` 파라미터 추가 결정.
- **Q6: Import의 stocks/stock_themes 캐시 부재 영향.** Export에 캐시 안 담으므로 import 직후 `_list` 호출은 enrichment 전이라 이름/업종/테마가 비어 있음(§4.4 freshness hint로 안내). 첫 쓰기 또는 quote enrichment 호출 후 정상화. 사용자가 import 직후 list 보고 *"내 종목이 다 사라진 것 같다"*고 오해할 가능성 — import 응답에 *"메타데이터가 차후 자동 갱신됩니다"* hint 한 줄 추가하면 완화.

---

## 큰 그림: v1.0 로드맵

### 도구 수 예산

| 버전 | 도구 수 | 변화 |
|---|---|---|
| v0.5 | 37 | baseline |
| **v0.6** | **39** | +7 신규 / −5 Tier 1 압축 (net +2) |
| v0.7 (목표) | 41 | +5 신규 / −3 Tier 2 압축 (net +2) |
| v0.8 (옵션) | 41 | +3 신규 / −3 Tier 3 압축 또는 v0.9로 이연 |
| **v1.0** | **40~45** | Tier 2/3 압축이 신규 추가를 상쇄 |

v1.0 도구 수 상한 45개로 설정. 매 minor 추가 시 같은 minor에서 자연스러운 압축 후보를 함께 정리.

### v0.6 (위 spec)

포트폴리오 완성 + 시장 컨텍스트 + 포트폴리오 I/O + Tier 1 도구 압축.

### v0.7 — 시장 신호 / 펀더멘털 (목표: +2)

| 도구 | TR | 자연 질의 |
|---|---|---|
| `ls_get_fundamentals_rank` | t3341 | *"PER 낮은 종목"*, *"ROE 상위"* |
| `ls_get_investor_flow` | t1601, t1702 | *"오늘 외인 매수 상위"*, *"기관 수급 들어오는 종목"*, *"삼성전자 최근 외인 동향"* |
| `ls_get_stock_events` | t3202 | *"다음 실적 발표 언제"*, *"내 보유종목 다음 일정"* |
| `ls_get_market_warnings` | t1404, t1405 | *"내 보유 중 관리종목 있어?"*, *"매매정지 종목"* |
| `ls_stocks_refresh_metadata` | t1102 | 동기 명시 갱신 (v0.6 enrichment 보완) |

**Tier 2 압축 후보 (−3)**:
- `ls_watchlist_group_rename` → `ls_watchlist_group_create`를 upsert로 + optional `rename_from`
- `ls_watchlist_groups_list` → `ls_watchlist_list(group?)` 흡수 (group 없으면 group 요약, 있으면 items)
- `ls_broker_rename` → `ls_call_tr` 우회 또는 upsert 패턴으로

**Corporate action enum 확장 (도구 추가 0)**: 사용자 시장 사건이 발생하면 `ls_holdings_corporate_action`의 `type` enum에 `stock_dividend` / `spin_off` / `merger` 등 추가. 도구 surface 그대로.

스키마 변경 없음. 도구만 추가 5 + 압축 −3 = 순증 +2.

### v0.8 (옵션) — 뉴스 + 잔여 screener

| 도구 | TR | 비고 |
|---|---|---|
| `ls_get_news` | t3102 | LS 뉴스 본문 fetch. 라이브 데이터 품질 ROI 확인 후 결정 |
| `ls_get_high_low` | t1442 | 신고/신저가 screener |
| `ls_get_short_interest` | t1927 | 공매도 일별 추이 |

v0.7 안정성 점검 후 v0.8 진행 결정. 시간 부족 시 v0.9 RC로 직행 가능.

### v0.9 RC — Freeze + 품질

- **이름 / 스키마 freeze.** 도구 이름, 파라미터, 응답 envelope shape — v2.0까지 안 깸 약속.
- **MCP host compatibility matrix.** Claude.ai / Desktop / Code / Cowork / Codex / VS Code / AssistStudio × {도구 호출, 인라인 차트, MCP Apps iframe, env 전달} 표.
- **portfolio.db schema 정책 문서화.** Column add는 minor OK, drop은 major. `_schema_version` 보장.
- **자동 백업 스냅샷.** `ls_account_remove(confirm=true)` 등 risky ops 직전에 silent snapshot to `snapshots/` 디렉토리.
- **NuGet publish pipeline 점검.** `scripts/publish-nuget.ps1` 실행 rehearsal, `.mcp/server.json` registry 등록 검증.
- **SECURITY.md v1.0 기준 업데이트.** "latest 0.x minor" → "latest 1.x minor" 정책 전환.
- **Live smoke 전면 갱신.** v0.6/0.7/0.8 surface 전체 커버, deterministic.
- **README / RELEASENOTES / INVENTORY 일제 정리.**

### v1.0 — Stable

**정의: "read-only 시세 + 로컬 노트패드 포트폴리오를 자연어로 안전하게 다루는 도구"**.

SemVer commitment:
- Tool 이름, 파라미터, 응답 envelope shape — v2.0까지 breaking change 없음.
- portfolio.db column 추가는 1.x OK, drop은 2.x major bump.
- 모든 mutation 응답의 `applied_to` 필드 유지 (v0.5 약속 그대로).
- credentials env-only 정책 유지.

명시적 OUT (post-v1.0 별도 패키지로):
- **`RedoxNet.Mcp.LsOpenApi.Realtime`** — WebSocket realtime 시세 + 호가 + 체결 + 지수.
- **`RedoxNet.Mcp.LsOpenApi.Trading`** — 실 brokerage 계좌 조회 + 주문 발주. Elicitation/confirmation guard 필수.

### v1.x 이후 후속

- 포트폴리오 거래 이벤트 로그(`holding_events` 테이블) — 현재 snapshot-only 모델을 transaction 기반으로 진화.
- 옵션/선물 TR 카탈로그 확장.
- ELW 별도 패키지 (`.Elw`).

---

### Q1~Q5 결정 결과 (이전 라운드)

1. ✅ `ls_stocks_refresh_metadata` v0.7 이연. 결정 근거 §6 OUT에 명문화.
2. ✅ 자동 enrichment fire-and-forget 유지.
3. ✅ Export default: OS별 cross-platform path 적용 (§4.3 "Default export path").
4. ✅ `industry` 필터 LIKE.
5. ✅ 응답에 `matched_industries` / `matched_themes` 동봉 (§4.5).

남은 의사결정은 §10 Open questions의 Q1-Q6 — 모두 testbed 검증 또는 실측 데이터를 요하는 항목이라 spec 단계에서 결정 불가.
