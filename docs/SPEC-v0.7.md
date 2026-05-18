# SPEC: v0.7 — Storage precision, ETF cache hygiene, screeners

- **상태**: Draft
- **작성일**: 2026-05-18
- **대상 버전**: v0.7.0
- **작성자**: Jong Hyun
- **선행**: [SPEC-v0.6-market-context.md](./SPEC-v0.6-market-context.md)

## 1. 컨텍스트

v0.6 출시 후 E2E 테스트(`Test_v0.6.0.txt` / `Test_v0.6.0_afterFix.txt`)에서 다음 잔존 이슈와 갭이 누적됨:

1. **저장 정밀도 손실 (B1).** `holdings.avg_price`가 SQLite `REAL` (IEEE 754 double)이라 split(10) → reverse_split(10) round-trip이 `1_003_502 → 100_350.20000000001 → 1_003_502.0000000001`로 drift. 1e-10 수준의 cosmetic 오차지만 반복 코퍼레이트 액션마다 누적, exact 동등 비교/비용기준 감사를 어렵게 함.
2. **ETF perpetual `themes_status: "pending"` (B2).** `stock_themes` 캐시는 enrichment 완료 여부를 "행 존재 여부"로 판정. ETF는 LS t1532가 빈 array를 반환 → 0행 삽입 → `ContainsKey` false → 다음 list 호출마다 60s 쿨다운 통과 후 재시도. 모델이 두 차례 독립 플래그한 일관된 noise 신호.
3. **업종 필터 미구현 (A1).** v0.6에서 `stocks.krx_sector` enrichment 소스를 t1102로 가정했으나 KRX 산업분류 필드 없음이 구현 중 확인됨. v0.6 출시 후 **t3320 (FNG_요약)의 `upgubunnm`**이 종목 단위 산업분류 이름을 직접 제공함이 확인됨 — 단 라이브 검증 결과(2026-05-18) 이 값은 KRX 표준이 아니라 **FICS (Financial Industry Classification System)** 분류이며 "FICS " prefix가 붙은 풀-네임 형식임 (예: `FICS 반도체 및 관련장비`). v0.7는 이 사실을 받아들이고 컬럼명을 `industry_*`로 교체, 원본 라벨 + 정규화 라벨 둘 다 저장 (§4.5).
4. **업종 시계열 wrapper 부재 (A2).** v0.6 카탈로그에 t1514가 있지만 wrapper 없음. *"코스피 최근 한 달 추이"* 같은 자연 질의는 여전히 `ls_call_tr` raw.
5. **수동 enrichment refresh 부재 (A3).** v0.6 fire-and-forget 경로가 ETF perpetual-pending(B2)을 비롯해 stale 가능 — 사용자가 명시적으로 "지금 새로 가져와" 할 진입점이 없음.
6. **펀더멘털·수급·이벤트·관리종목 wrapper 부재 (C).** *"PER 낮은 종목"*, *"오늘 외인 매수 상위"*, *"다음 실적 발표 언제"*, *"내 보유 중 관리종목"* — v0.7의 자연 질의 surface 확장.
7. **Surface budget 압박.** v1.0 로드맵상 40~45 tools 유지가 목표. v0.7 신규 +6을 일부 Tier 2 압축(−3)으로 흡수해야 함.

## 2. 결정

| # | 결정 | 비고 |
|---|------|------|
| 1 | **B1 — `holdings.avg_price` storage를 INTEGER fractional won(×10000)으로 마이그레이션** | Schema v4. API 응답(`avg_price` double)은 unchanged — non-breaking. 분할/병합은 rational `qtyNum/qtyDen`로 exact 라운드트립 (§4.1) |
| 2 | **B2 — `stock_themes` sentinel-row로 ETF perpetual-pending 차단** | `ReplaceStockThemesAsync(symbol, [])` 호출 시 `(symbol, "__NONE__", "")` 한 줄 삽입. 조회 projection이 sentinel filter, 캐시 hit 판정은 행 존재만 본다. (§4.2) |
| 3 | **A2 — `ls_get_index_history` (t1514 wrapper)** | 얇은 wrapper. `upcode + period_type + count + cts_date?`. `ls_get_chart` 와 인자 명명 일치. 차트 파이프라인(`ls_add_indicator` / `ls_reframe_chart`) 통합은 v0.8로 (§4.3) |
| 4 | **A3 — `ls_stocks_refresh_metadata` (synchronous refresh)** | Blocking 호출. `shcodes?` 생략 시 holdings + watchlist 전체. enrichment kind 별 (`themes`, `industry` 등) — A1과 함께 confirm. 응답: `refreshed`, `errors`. (§4.4) |
| 5 | **A1 — t3320 기반 FICS industry enrichment + `industry_*` 컬럼 도입** | `stocks.krx_sector` 컬럼 폐기, `industry_raw` (FICS 원본 라벨, 예: "FICS 반도체 및 관련장비") + `industry` (정규화 라벨, "FICS " prefix 제거) + `industry_fetched_at` 신규. `ls_holdings_list(industry?)`는 **정규화 라벨에 case-insensitive substring** 매치. ETF/SPAC 같은 회사 프로필 없는 종목은 fetched-but-empty로 기록. (§4.5) |
| 6 | **C — 스크리너 4개 wrapper 추가** | `ls_get_fundamentals_rank` (t3341), `ls_get_investor_flow` (t1601 + t1702 dispatcher), `ls_get_stock_events` (t3202), `ls_get_market_warnings` (t1404 + t1405 dispatcher). (§4.6) |
| 7 | **Tier 2 압축 −3 (BREAKING)** | `ls_watchlist_group_rename` → `ls_watchlist_group_create(rename_from?)`. `ls_watchlist_groups_list` → `ls_watchlist_list(scope="groups")`. `ls_broker_rename` → `ls_account_upsert(rename_broker_from?)`. (§4.7) |
| 8 | **B3 — KRX 100 firdiff non-self 슬롯**: 이번 릴리스에서는 client-side 보정 안 함, LS support 리포트 + as-is | v0.6 `d0b765e` 자기참조 override 유지. 다른 upcode 응답 안의 KRX 100 entry는 LS-as-shipped (§5.1) |

### 2.1 도구 surface 변경 요약

```
v0.6: 40 tools (실측)
v0.7: 40 + 6 신규 − 3 (Tier 2 압축) = 43 tools
      signature 변경: ls_holdings_list +1 optional param (industry)
                      ls_watchlist_group_create +1 optional (rename_from)
                      ls_watchlist_list +1 optional (scope)
                      ls_account_upsert +1 optional (rename_broker_from)
      삭제 (breaking): ls_watchlist_group_rename, ls_watchlist_groups_list, ls_broker_rename
      내부 schema 변경: schema v4 — holdings.avg_price REAL → INTEGER (×10000)
                       schema v5 — stocks.krx_sector DROP,
                                   stocks.industry_raw / industry / industry_fetched_at ADD,
                                   stock_themes sentinel-row 정책 (B2는 동작 변경만, 컬럼 없음)
```

신규 (6):
- 시계열: `ls_get_index_history`
- 메타데이터: `ls_stocks_refresh_metadata`
- 스크리너: `ls_get_fundamentals_rank`, `ls_get_investor_flow`, `ls_get_stock_events`, `ls_get_market_warnings`

압축 (−3): §4.7

v1.0 목표: 43 → 40~45 안에 머무름. v0.8 추가 surface는 Tier 3 압축으로 상쇄.

## 3. 데이터 / 카탈로그 변경

### 3.1 카탈로그 v0.7.0 추가

`todo/` 디렉터리 staged spec sheet 기준 wrapper 승격:

- **t3341 — 종목순위(우량,저평가 등)** (wrapper, A4-C). 펀더멘털 기반 종목 랭킹.
- **t1601 — 시간대별 매매주체별 매매수량** (wrapper, A4-C). 일중 외인/기관/개인 흐름.
- **t1702 — 일별주체별 종목투자동향** (wrapper, A4-C). 일별 외인/기관 누적 수급.
- **t3202 — 종목별 IR(투자정보) 일정** (wrapper, A4-C). 다음 실적/주총/배당 일정.
- **t1404 — 관리/투자유의/단기과열 종목조회** (wrapper, A4-C).
- **t1405 — 매매정지/거래정지 종목조회** (wrapper, A4-C).
- **t1514 — 업종기간별추이** (wrapper 승격, v0.6에 catalog only).
- **t3320 — FNG_요약 (투자정보)** (catalog + A1 내부 사용). Path `/stock/investinfo`. Input: `gicode` (6-char shcode — LS 가이드 7-char 표기는 잘못, 라이브 검증 2026-05-18). Output: FICS 산업분류명(`upgubunnm`, 예: "FICS 반도체 및 관련장비") + 시장구분 + 회사 프로필 + 시가총액/외국인비율 + 펀더멘털(PER/PBR/EPS/ROE 등). **1 TPS 제한** — bulk refresh는 holdings + watchlist 범위로 제한 권장. v0.7 A1의 종목→industry 직접 매핑 유일 소스 (KRX 표준 분류는 LS OpenAPI 어디에도 직접 제공되지 않음).

### 3.2 Schema migrations

#### v4 — B1 avg_price 정수 마이그레이션 (이미 commit)

```sql
CREATE TABLE holdings_new (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
    symbol     TEXT NOT NULL REFERENCES stocks(symbol),
    quantity   INTEGER NOT NULL CHECK(quantity >= 0),
    avg_price  INTEGER NOT NULL CHECK(avg_price >= 0),  -- 원 × 10000
    notes      TEXT,
    updated_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(account_id, symbol)
);
INSERT INTO holdings_new (...)
SELECT ..., CAST(ROUND(avg_price * 10000) AS INTEGER), ...
FROM holdings;
DROP TABLE holdings;
ALTER TABLE holdings_new RENAME TO holdings;
CREATE INDEX IF NOT EXISTS idx_holdings_symbol ON holdings(symbol);
```

#### v5 — A1 industry 컬럼 교체

`stocks.krx_sector`는 v0.5에 placeholder로 추가했으나 LS OpenAPI가 KRX 표준 산업분류를 직접 제공하지 않음이 v0.6→v0.7에서 확정. t3320이 제공하는 것은 FICS 분류이므로 의미가 다르고 컬럼명도 misleading. v5에서 교체:

```sql
-- 신규 컬럼 추가 (모두 nullable, 콜드 fill 전엔 비어 있음)
ALTER TABLE stocks ADD COLUMN industry_raw         TEXT;  -- t3320.upgubunnm 원본, "FICS 반도체 및 관련장비"
ALTER TABLE stocks ADD COLUMN industry             TEXT;  -- 정규화, "FICS " prefix 제거, "반도체 및 관련장비"
ALTER TABLE stocks ADD COLUMN industry_fetched_at  TEXT;  -- freshness 추적
-- 폐기 컬럼 제거 (v0.6에서 모두 NULL이었으므로 데이터 손실 없음)
ALTER TABLE stocks DROP COLUMN krx_sector;
```

마이그레이션은 forward-only. v0.6 바이너리로 다운그레이드 불가. `_schema_version` 진입은 v5.

### 3.3 stock_themes sentinel-row 정책 (B2)

- `ReplaceStockThemesAsync(symbol, themes)`:
  - `themes` 비어 있으면 → `INSERT (symbol, "__NONE__", "", datetime('now'))` 한 줄.
  - `themes` 비어 있지 않으면 → 기존 동작 그대로 (정상 멤버십 행 + sentinel은 자동 제거).
- `GetStockThemesBatchAsync(symbols)`:
  - SELECT 그대로 반환, 단 `theme_code = "__NONE__"` 행은 제외.
- `themesMap.ContainsKey(symbol)`:
  - sentinel을 가진 심볼도 캐시 hit → 재dispatch 없음. ETF perpetual-pending 차단.
- migration: 기존 캐시는 v0.7 첫 enrichment cycle에서 자연스럽게 sentinel로 채워짐. retroactive 채움은 없음 (정확도 손실 없음, 단 ETF 미사용 동안에는 한 번 더 t1532 호출 가능).

## 4. 도구 / 동작 변경 상세

### 4.1 B1 — avg_price fractional won storage

이미 구현 (이 SPEC과 함께 commit). 정리:

- **C# 모델.** `Holding.AvgPriceFr` (long) 신규 + `Holding.AvgPrice` (double) computed = `AvgPriceFr / 10000`. API 응답 shape 변경 없음.
- **Repository.** `SetHoldingAsync(double avgPrice)` 보존 — 내부에서 `ToFractionalWon` 변환. `BuyHoldingAsync` weighted-avg는 integer arithmetic, round-half-up.
- **Corporate action API 변경 (internal).** `IPortfolioRepository.ApplyCorporateActionAsync(... double qtyMultiplier, double priceMultiplier ...)` → `(... long qtyNum, long qtyDen ...)`. 가격은 비율의 역수로 자동 계산되어 비용기준이 정확히 보존됨.
- **MCP 도구 surface (`ls_holdings_corporate_action`).** 변경 없음. 사용자 입력은 여전히 `ratio: double`. 내부 dispatch:
  - split(N): qtyNum=N, qtyDen=1
  - reverse_split(N): qtyNum=1, qtyDen=N
  - bonus(r): `(1+r)` decimal을 `(num, den)` 분해 후 dispatch
- **Round-trip 보증.** split↔reverse_split with integer ratio가 starting fr value의 약수 관계일 때 exact. 일반적인 KRX 가격(원 단위)은 모두 ×10000 → 1을 약수로 가지므로 보장.

테스트 추가: `CorporateAction_SplitReverseSplitRoundTrip_ExactInIntegerStorage`.

### 4.2 B2 — stock_themes sentinel-row

코드 변경 surface 좁음:

- `SqlitePortfolioRepository.ReplaceStockThemesAsync`: `themes.Count == 0` 분기에서 sentinel insert.
- `SqlitePortfolioRepository.GetStockThemesBatchAsync`: SELECT 절에 `WHERE theme_code != '__NONE__'` 추가 (또는 후처리 필터).
- `PortfolioService.ListHoldingsAsync`: 변경 없음 — `themesMap.ContainsKey` 판정이 sentinel 포함 행에서도 true로 평가됨.
- 응답 envelope: ETF 등 빈 enrichment 결과는 `themes: []` 가 아니라 `themes` 키 자체가 omit (v0.6과 일관). `themes_status` 도 omit (pending이 아니므로).

테스트:
- `ReplaceStockThemes_EmptyArray_InsertsSentinelRow`
- `ETFHolding_AfterEnrichment_DoesNotReportPending`

### 4.3 A2 — ls_get_index_history (t1514 wrapper)

```
ls_get_index_history(
    upcode: string,        // 3-char index code (예: "001"=KOSPI)
    period_type?: "day"|"week"|"month" = "day",
    count?: int = 60,      // 가장 최근 N개
    cts_date?: string      // optional cursor (YYYYMMDD), 페이징 시
) → {
    upcode: string,
    period_type: string,
    points: [
        { date, close, change, change_pct, volume, value, high, low, open,
          breadth: { advance, decline, unchanged, limit_up, limit_down } | null,
          flows: { foreign_net, institution_net } | null },
        ...
    ],
    cts_date: string | null   // 다음 페이지가 있을 때만 emit
}
```

명명 규약은 `ls_get_chart`와 일관. `period_type`는 t1514 `gubun2` (1=일/2=주/3=월) 의 enum 형태. `flows`는 t1514가 제공하는 일별 매매주체 순매수.

차트 파이프라인 (`ls_add_indicator`, `ls_reframe_chart`)은 v0.7에서 지원하지 않음 — 단발 wrapper만. dataset-handle integration은 v0.8.

### 4.4 A3 — ls_stocks_refresh_metadata

```
ls_stocks_refresh_metadata(
    shcodes?: string[],   // 없으면 holdings + watchlist 전체
    kinds?: ("themes" | "industry")[]   // default = 둘 다
) → {
    refreshed: [{ shcode, themes_updated, industry_updated }, ...],
    errors:    [{ shcode, kind, error }, ...]
}
```

- **Blocking.** Fire-and-forget 경로(`PortfolioService.FireAndForgetEnrich`)와 같은 dedup/cooldown backstop을 공유하지만, 호출자는 결과를 await.
- **Scope 기본값**: holdings ∪ distinct(watchlist_items.symbol).
- **per-kind 응답**: A1 enrichment(industry) 가 합류하면 두 종류 데이터 동시 refresh.
- **Throttling**: t1532 / t3320은 LS rate limit이 작으므로 내부에서 직렬 + 짧은 backoff. UX 상 progress 보고는 v0.7에 안 함 (단발 응답).

### 4.5 A1 — t3320 FICS industry enrichment + filter

#### 4.5.1 소스 선택 (라이브 검증 후 확정)

v0.6 SPEC 작성 시점엔 t1102/마스터에 KRX 산업분류 필드가 없어 LS OpenAPI 전반에 부재한 것으로 가정했으나, v0.6 출시 후 **t3320 (FNG_요약) `upgubunnm`이 종목 단위 산업분류명을 직접 제공**함이 확인됨 (k-ebest-im 레퍼런스 + `todo/t3320.txt`). 2026-05-18 라이브 검증으로 추가 확인된 사실:

- 입력 `gicode`는 **6-char shcode만 동작**. "A"+6 형식은 rsp_cd=00000 거짓 OK + 빈 OutBlock.
- `upgubunnm`은 **FICS (Financial Industry Classification System) 분류** — "FICS " prefix가 붙은 풀-네임. 예: `FICS 반도체 및 관련장비`, `FICS 증권업`. KRX 표준 분류와는 별도 체계.
- **ETF/SPAC** 등 회사 프로필 없는 종목은 rsp_cd=00000으로 응답하지만 OutBlock 모든 필드가 빈 문자열/0. ETF detection은 `upgubunnm` 빈 문자열 여부로 판정.
- 같은 산업의 종목들은 **정확히 같은 라벨 문자열** — 005930과 000660 모두 `FICS 반도체 및 관련장비`. 정규화 라벨에 대한 substring 매치를 deterministic하게 수행 가능.

| 옵션 | 장점 | 단점 | 결정 |
|---|---|---|---|
| **(가) t3320 (`upgubunnm`)** | LS 공식 소스, 종목→이름 직접, 1 호출 | 1 TPS 제한 — bulk 시 N초. FICS 분류 (KRX 표준 아님), 산업코드 없음 | **채택 (v0.7 1차)** |
| (나) t8424 + t1516 reverse-lookup | 산업코드 + 이름 페어 자체 완결 | 30 upcode × t1516 paging 콜드 비용 (~5~10s), FICS ↔ KRX 매핑 별도 작업 | v0.8 보조 (필요 시) |
| (다) 정적 KRX 산업분류 JSON 번들 | 0 콜드 비용 | 연간 stale, 신규 상장 누락 | 폐기 |
| (라) LS 마스터 TR support inquiry | 가장 깨끗 | 응답 시점 불확실 | 폐기 — t3320으로 해결 |

#### 4.5.2 데이터 모델 (사용자 결정 반영)

`stocks.krx_sector` 컬럼은 v0.5 placeholder였고 한 번도 채워진 적 없음 — v0.7에서 폐기하고 세 컬럼으로 분리 저장:

| 컬럼 | 예시 값 | 용도 |
|---|---|---|
| `industry_raw` | `FICS 반도체 및 관련장비` | t3320.upgubunnm 원본 그대로. 디버그·LS 응답 audit 용. |
| `industry` | `반도체 및 관련장비` | "FICS " prefix 제거한 정규화 라벨. `ls_holdings_list(industry?)` 필터의 매치 대상. |
| `industry_fetched_at` | `2026-05-18 12:34:56` | freshness 추적. **빈 OutBlock 케이스(ETF/SPAC/상장폐지 임박)도 이 컬럼은 채우고** `industry_raw`/`industry`는 NULL 유지 → perpetual pending 방지 (B2 sentinel-row와 같은 "fetched-but-empty" 패턴). |

정규화 함수 (C# pseudo):
```csharp
static (string? Raw, string? Normalized) NormalizeFicsIndustry(string upgubunnm)
{
    string trimmed = (upgubunnm ?? "").Trim();
    if (string.IsNullOrEmpty(trimmed))
        return (null, null);
    string normalized = trimmed.StartsWith("FICS ", StringComparison.OrdinalIgnoreCase)
        ? trimmed.Substring(5).Trim()
        : trimmed;
    return (Raw: trimmed, Normalized: normalized);
}
```

#### 4.5.3 Enrichment 동작

- **Trigger**: holdings/watchlist 쓰기 경로 fire-and-forget + A3 `ls_stocks_refresh_metadata(kinds=["industry"])` 양쪽. 기존 v0.6 theme 경로(`PortfolioService.FireAndForgetEnrich`)에 industry kind 추가.
- **Cache hit 판정**: `industry_fetched_at IS NOT NULL`. `upgubunnm` 빈 응답(ETF/SPAC/상장폐지 임박 등 회사 프로필 없는 종목)은 `industry_raw` / `industry`를 NULL로 두되 `industry_fetched_at`는 현재 시각으로 기록 — 다음 list 호출에서 재dispatch 안 함. B2 stock_themes sentinel-row와 같은 "fetched-but-empty" 정책으로 ETF perpetual pending을 industry 쪽에서도 차단.
- **Per-stock 비용**: 1초 (LS 1 TPS). holdings + watchlist 합쳐 ≤30 종목 가정 시 cold fill 약 30초. A3 명시적 refresh 경로에서 진행 상황 UI는 v0.7에 안 함 (단발 응답).
- **Concurrency**: 기존 60s dedup/cooldown backstop 재사용. industry kind만 추가하면 됨.
- **TTL**: 기본 영구 (수동 refresh로만 갱신). 산업 변경은 분기/연 단위 이벤트라 자동 만료 비용 대비 가치 낮음. A3가 user-triggered refresh 진입점.

#### 4.5.4 필터 (ls_holdings_list)

```
ls_holdings_list(account?, theme_code?, theme_keyword?, industry?)
```

- **`industry` 파라미터**: case-insensitive substring 매치, **정규화 라벨(`stocks.industry`) 대상**.
- 매치 예시: `industry="반도체"` → "반도체 및 관련장비"에 포함된 종목 모두. `industry="증권"` → "증권업" 종목.
- `matching_industries` echo 블록: 결과 set에서 발견된 distinct `industry` 값 list. false-positive 검증 용도 (theme_keyword와 일관).
- AND 결합: `industry` + `theme_code` / `theme_keyword` 동시 적용 가능.
- **Pending 처리**: **`industry_fetched_at IS NULL`** 인 holding만 pending count에 들어감. `industry_fetched_at IS NOT NULL AND industry IS NULL` (ETF 등 영구 not-applicable) 케이스는 pending이 아니라 enrichment 완료된 "no industry"로 취급. Pending 발생 시 `MetadataFreshness.Pending["industry"]` 증가, `Hint`에 "industry enrichment 진행 중" 메시지.

#### 4.5.5 v0.8+ deferred

- t3320 이름 ↔ t8424 hname 매핑 검증. 결과 일치 시 *"전기전자 업종 전체 종목"* (산업코드 → 종목 list) 질의를 `ls_get_industry_stocks`에 위임 가능.
- FICS → KRX 표준 산업분류 매핑 (외부 KRX 데이터 필요). v0.7는 FICS 그대로 사용.
- `industry_code` 컬럼 추가 (t8424 reverse-index 결과).

### 4.6 C — 스크리너 wrapper 4개

```
ls_get_fundamentals_rank(
    field: "per"|"pbr"|"roe"|"eps"|"dividend_yield"|...,
    direction?: "asc"|"desc" = "asc",
    market?: "kospi"|"kosdaq"|"both" = "both",
    count?: int = 30
) → { field, direction, rows: [{ shcode, name, value, price, change_pct }, ...] }
```

```
ls_get_investor_flow(
    shcode?: string,                         // 종목 단위 흐름
    scope?: "intraday"|"daily" = "daily",   // t1601 vs t1702
    direction?: "buy"|"sell" = "buy",       // top-N의 정렬 기준
    investor?: "foreign"|"institution"|"individual" = "foreign",
    count?: int = 30
) → { scope, rows: [...] | { time_series: [...] } }
```

`shcode` 없으면 시장 전체 top-N. 있으면 단일 종목 시계열.

```
ls_get_stock_events(
    shcode: string,
    from?: string,    // YYYYMMDD
    to?: string,
    kinds?: ("earnings"|"dividend"|"agm"|"ir")[]
) → { shcode, events: [{ date, kind, summary }, ...] }
```

```
ls_get_market_warnings(
    kinds?: ("관리"|"투자유의"|"단기과열"|"매매정지"|"거래정지")[],
    shcodes?: string[]   // 보유종목 subset 필터 (없으면 전체 market)
) → { rows: [{ shcode, name, kind, since, note }, ...] }
```

각 wrapper는 응답 envelope를 통일된 shape로 펴고 LS 원본 필드 이름은 description 안에 명시.

### 4.7 Tier 2 압축 (BREAKING)

#### 4.7.1 `ls_watchlist_group_rename` → `ls_watchlist_group_create(rename_from?)`

- 새 시그니처: `ls_watchlist_group_create(name, description?, rename_from?)`.
- `rename_from` 있으면 → 기존 group을 새 이름으로 rename + description override.
- `rename_from` 없으면 → upsert (현재 동작).
- 충돌 정책: rename target name이 이미 다른 group에 존재하면 `ValidationError`.

#### 4.7.2 `ls_watchlist_groups_list` → `ls_watchlist_list(scope="groups")`

- 새 파라미터: `ls_watchlist_list(group?, scope?: "items"|"groups" = "items")`.
- `scope="groups"` → 그룹 메타만 (`{ groups: [{name, description, sort_order, item_count}] }`).
- `scope="items"` (default) → v0.6과 동일한 grouped item array.

#### 4.7.3 `ls_broker_rename` → `ls_account_upsert(rename_broker_from?)`

- 새 시그니처: `ls_account_upsert(account_number, nickname?, broker?, set_default?, rename_broker_from?)`.
- `rename_broker_from` 있으면 → 해당 broker 라벨을 가진 모든 account의 broker 필드를 `broker` 인자 값으로 update. 그 외 인자는 무시.
- `rename_broker_from` 없으면 → 기존 upsert 동작.
- *주의*: rename 모드일 때는 account_number / nickname / set_default 인자를 사용하지 않음. 도구 설명에 명시.

대안 검토: `ls_broker_rename` 자체를 남기고 `_groups_list`만 압축 (−2). 결정 — 압축 3개 모두 진행, MCP 호스트의 자연어 라우팅이 새 시그니처에서도 깨지지 않는지 description으로 가이드.

## 5. 위험 / 미해결 질문

### 5.1 B3 — KRX 100 firdiff non-self 슬롯

v0.6 `d0b765e` 자기참조 override(`firstjcode == queried upcode` 케이스만)는 유지. 다른 upcode가 KRX 100을 related로 포함할 때(예: 가상의 KRX 300 응답 안의 KRX 100 entry)는 top-level diffjisu 참조가 없으므로 client-side로 보정 불가. v0.7 액션:
- LS 측 버그 리포트 작성 (KOSPI 가이드 sample firdiff=0.03 vs diffjisu=0.36, KRX 100 raw firdiff=-0.65 vs diffjisu=-6.59 두 증거 첨부).
- v0.7 코드 변경 없음 — non-self 슬롯은 LS-as-shipped.
- LS 미회신 시 v0.8에서 N additional t1511 calls 재검토.

### 5.2 A1 콜드 비용

t3320은 종목 단위 1 TPS라 holdings + watchlist N개를 처음 채울 때 대략 N초가 든다. v0.7은 bulk 전체시장 enrichment를 하지 않고 사용자 로컬 universe(holdings ∪ watchlist)로만 제한한다. `industry_fetched_at`가 있으면 영구 hit으로 보고, 사용자가 명시적으로 `ls_stocks_refresh_metadata(kinds=["industry"])`를 호출할 때만 다시 가져온다. ETF/SPAC처럼 `upgubunnm`이 비어 있는 종목도 fetched-but-empty로 기록해 반복 호출을 막는다.

### 5.3 t3341 / t1601 / t1702 / t3202 / t1404 / t1405 spec sheet 검증

`todo/*.txt` 에 stage된 LS 가이드 시트의 InBlock/OutBlock 구조를 wrapper 작성 시 정확히 매핑. 빌드 직전에 raw call로 한 번씩 검증.

### 5.4 Tier 2 압축 자연어 라우팅

모델이 *"watchlist 그룹 목록"* 자연 질의를 `ls_watchlist_list(scope="groups")`로 라우팅하도록 description을 신중하게 작성. v0.6 압축(`ls_account_get` 삭제) 학습 — 명시적 use-case 예시가 효과적.

## 6. 출시 순서 (확정)

```
B1 (this commit)
   ↓
SPEC 문서 (this PR)
   ↓
B2 — sentinel-row
   ↓
A2 — ls_get_index_history
   ↓
A3 — ls_stocks_refresh_metadata
   ↓
A1 — FICS industry enrichment + industry filter
   ↓
Tier 2 압축 3개 + C wrapper 4개  ← 같은 release 윈도우, 별도 commit
   ↓
v0.7.0 출시
```

각 단계는 독립 commit + 통과 테스트. v0.6 → v0.7 마이그레이션은 forward-only schema v5 (다운그레이드 unsupported).

## 7. 출시 / SemVer

- **v0.7.0**: 위 6단계 합쳐 minor bump. Tier 2 압축 3개는 breaking이지만 0.x 라인 정책상 minor에서 허용. 통합 BREAKING NOTES 섹션을 RELEASENOTES.Mcp.md에 작성.
- **NuGet publish 순서**: t3320 catalog 추가로 Core 변경이 포함되면 Core → Mcp 순서. 구현이 Mcp-only로 축소되면 Mcp만 (memo `release_publish_order.md`).
- **v1.0 로드맵**: 43 tools 안착. v0.8 Tier 3 압축 후보 검토 (`ls_holdings_set` vs `_buy` merge? — v0.5 합의에서 분리 결정, 재검토 필요).
