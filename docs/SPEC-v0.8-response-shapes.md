# SPEC: v0.8 — Response shape taxonomy + token economy

- **상태**: Draft
- **작성일**: 2026-05-18
- **대상 버전**: v0.8.0
- **작성자**: Jong Hyun
- **선행**: [SPEC-v0.7.md](./SPEC-v0.7.md)

## 1. 컨텍스트

v0.7 출시(43 tools)에서 두 가지 응답-크기 이슈가 라이브 E2E (`todo/Test_v0.7.0.txt`)로 드러남:

1. **`ls_get_investor_flow` daily mode 12k 토큰 폭주.** 30일 × 12 investor types × `{kind, korean_label, value}` 객체 배열 = 한 호출에 ~12,100 토큰. v0.7 commit `6f8d86e`에서 surgical fix: default 3종 + `flows` array→map shape + summary 블록 + 중복 메타 제거 → 약 75% 절감 (~2.5k).
2. **`ls_get_market_warnings` 기본 5종 fan-out + cursor stuck dedup 부재.** 사용자가 *"오늘 관리종목"* 한 줄 물었는데 wrapper가 5종 × 6 페이지 = 600 raw row 반환 (실제 unique 100). v0.7 commit `2fe5a28`에서 dedup + default 1종 + cap 6→3으로 정리.

**진단**: 두 사례 모두 *"N × K 매트릭스 응답"* 패턴이지만 v0.7까지는 도구마다 즉흥적으로 다뤄옴. 명시적 분류 + 공통 컨벤션이 없으면 신규 도구마다 같은 함정.

LS API 래퍼 전반에 깔린 N×K 응답 매트릭스(차트, 트리맵, 스크리닝, 호가창, 업종/테마 등락률, 시간대별 분포 등)에 대한 **응답 shape 분류 + 절감 전략 표준화**가 v0.8의 1순위.

## 2. 결정

### 2.1 응답 shape 4분류 (A/B/C/D)

| # | 패턴 | 데이터 모양 | 적용 도구 예 | 절감 전략 | 절감 폭 |
|---|------|------------|------------|-----------|--------:|
| **A** | Projection / Selection | N×K dense 매트릭스, 사용자가 K 중 일부만 보통 봄 | investor_flow, chart(OHLCV 일부), 호가창, stock_info 카테고리 | `fields?`/도메인 projection 파라미터, map shape, 라벨/중복 메타 제거, 0/null 필터 | 70–85% |
| **B** | Aggregation / Summary | 시계열·분포 — 모델이 어차피 요약함 | 일/주/월봉 장기, 누적 매매, ETF sector roll-up | `summary` 블록 (period_totals, extremes, streaks, top_movers, marginals) | +30–50% (A와 누적) |
| **C** | Reference / Dataset (Heavy) | 진짜 큰 데이터셋, drill-down 빈번 | 5년+ 일봉, 분봉/틱, 전 종목 스크리닝 | `ls_get_chart` 스타일 `dataset_id` + `output_mode` + drill 도구 | 인프라 비용 큼 — 진짜 필요한 도구만 |
| **D** | Pagination / Window | 리스트성, 자연스러운 cutoff 존재 | 종목 검색, 스크리닝 N위, 뉴스, 거래원 리스트, 시장 경고 | `limit?` + `cursor?`, 시간 범위 절단 | 90%+ |

**원칙**: 대부분 도구는 **A + B 조합**으로 충분. C는 정말 무거운 1~2개, D는 리스트성 도구만.

### 2.2 공통 파라미터 컨벤션

| Axis | 파라미터 | 도메인 친화 변형 | 기본값 정책 |
|------|---------|----------------|------------|
| Projection (A) | 도메인 자연어 이름 — `investors`, `metrics`, `levels`, `kinds`, `fields` | 도구마다 다르게 OK | "narrow default" — 가장 흔한 부분집합 |
| Aggregation (B) | `summary` 블록은 항상 포함; `verbosity?: "summary"\|"compact"\|"full" = "compact"` 옵션 | 통일 | `"compact"` (summary + 핵심 N개 row) |
| Reference (C) | `output_mode?: "summary"\|"export"\|"display"`, `dataset_id?: string` | 통일 (ls_get_chart 패턴) | `"summary"` |
| Pagination (D) | `limit?: int`, `cursor?: string` | 통일 | `limit` 도메인별 (예: 10~30) |

**Echo 컨벤션** (응답에 caller가 무엇을 받았는지 명시):
- A: `<projection_axis>_shown` (예: `investors_shown: ["foreign","institution_total","individual"]`)
- B: `summary: { ... }` 블록 always present (단, mode가 export일 때 omit 가능)
- C: `dataset_id`, `output_mode` echo
- D: `next_cursor?: string`, `total_available?: int`

### 2.3 도구별 분류 매트릭스 (v0.7 surface 기준 43개)

| 도구 | 패턴 | 현재 상태 | v0.8 작업 |
|---|---|---|---|
| `ls_get_quote` | A (10 levels × 6 fields) | 전체 dump | order_book 옵션 / 단계 limit |
| `ls_get_multi_quote` | D (50종목 list) | 전량 dump | `fields?` 카테고리 |
| `ls_get_top_stocks` | D + A | top_n cap 있음 | cursor 추가 검토 |
| `ls_get_stock_info` | A (~50 fields) | 전체 dump | `sections?: ["fundamentals","brokers","foreign","periods"]` |
| `ls_get_chart` | **C** (reference impl) | 완비 | 변경 없음 |
| `ls_add_indicator` | A on dataset | 완비 | — |
| `ls_reframe_chart` | C drill-down | 완비 | — |
| `ls_search_stock` | D | head only | cursor + limit 노출 |
| `ls_get_etf_info` | A | 전체 dump | `sections?` |
| `ls_get_etf_holdings` | D + A | top_n cap | cursor 검토 |
| `ls_get_index_quote` | A (related 4 indices 포함) | 전체 dump | `include_related?: bool = true` |
| `ls_get_index_history` | **B 누락** (60 bars × 14 fields) | 전체 dump | `summary` 블록 신규 + `verbosity` |
| `ls_get_industry_indices` | D | top_n cap | cursor 검토 |
| `ls_get_industry_stocks` | D | top_n cap | cursor + summary 검토 |
| `ls_get_theme_stocks` | D | top_n cap | 동일 |
| `ls_get_stock_themes` | A (N themes) | 전체 dump | `theme_limit?` |
| `ls_get_fundamentals_rank` | D + A | count param | `metrics_shown?` 옵션 (현재 14 metric × N row) |
| `ls_get_investor_flow` daily | A + B ✓ | v0.7 완비 | 변경 없음 |
| `ls_get_investor_flow` intraday | A ✓ | v0.7 완비 | summary 추가 검토 |
| `ls_get_stock_events` | D (자연스럽게 작음) | OK | — |
| `ls_get_market_warnings` | D + A | dedup ✓, default 1종 | cursor 노출 |
| `ls_holdings_list` | **A 누락** (N×themes 폭발) | 전체 dump | `themes_limit?` 또는 `verbosity` |
| (portfolio 쓰기) | — | — | — (이미 작음) |

### 2.4 측정 인프라

v0.7까지 토큰 절감은 *추정*. v0.8부터 테스트로 pin:

```csharp
[Fact]
public async Task GetInvestorFlow_DefaultDaily_FitsTokenBudget()
{
    var result = await GetInvestorFlowTool.GetInvestorFlow(client, shcode: "005930");
    // ~30 trading days × default 3 investors. Floor of compact response.
    EstimateTokens(result).Should().BeLessThan(3000);
}
```

`EstimateTokens` 헬퍼 (`tests/.../TestSupport/TokenEstimator.cs`):
- Naive: `char.Length / 3.5` (Korean+JSON 평균)
- 정밀: 외부 tokenizer 사용 (v0.9 검토)

도구별 token budget 테이블 (예시):

| 도구 | Default budget | Verbose budget |
|---|---:|---:|
| `ls_get_quote` | 600 | 1500 |
| `ls_get_index_history` (60d) | 2500 | 5000 |
| `ls_get_investor_flow` daily (30d) | 2500 | 8000 |
| `ls_holdings_list` (10 holdings) | 3000 | 6000 |

## 3. 우선순위 (v0.8 ship target)

v0.7의 43 tools 모두 한꺼번에 refactor하는 건 risk 큼. **3-5개 high-impact 도구**로 시작:

### Phase 1 — 고임팩트 4개 (v0.8.0)

1. **`ls_get_index_history`** (패턴 B 누락) — 60일 dense 시계열. `summary` 블록 + `verbosity` 추가. 가장 깔끔한 첫 케이스.
2. **`ls_holdings_list`** (패턴 A 누락) — 보유 종목당 themes 평균 15개로 폭발. `themes_limit` + `verbosity` 추가.
3. **`ls_get_stock_info`** (패턴 A) — 단일 종목 ~50 fields. `sections` 카테고리 필터로 fundamentals / brokers / periods / foreign 부분 조회.
4. **공통 인프라**: token estimator 테스트 헬퍼 + budget 테이블.

### Phase 2 — 리스트성 도구 D 컨벤션 정착 (v0.8.x)

5. `ls_search_stock`, `ls_get_industry_stocks`, `ls_get_theme_stocks`, `ls_get_market_warnings`, `ls_get_fundamentals_rank` — `limit` + `cursor` 공통화.
6. `ls_get_top_stocks` — cursor 노출 (현재 내부 페이징만).

### Phase 3 — C 후보 추가 검토 (v0.8.x or v0.9)

7. `ls_get_index_history` 1년+ 호출 → chart의 dataset_id cache 재사용 (인프라 공유).
8. `ls_get_fundamentals_rank` 전 종목 (count=1000+) → dataset_id + drill.

기타 도구는 v0.9 이후 점진.

## 4. 도구 / 동작 변경 상세

### 4.1 공통 — TokenEstimator + budget 테스트 헬퍼

```csharp
// tests/RedoxNet.Mcp.LsOpenApi.Tests/TestSupport/TokenEstimator.cs
public static class TokenEstimator
{
    // Naive char-count → token estimate. Korean+JSON ≈ 3.5 chars/token average.
    // For exact counts use an external tokenizer (cl100k_base or equivalent).
    public static int Estimate(string json) => (int)Math.Ceiling(json.Length / 3.5);
}

public static class FluentAssertionsExtensions
{
    public static void ShouldFitTokenBudget(this string json, int maxTokens)
        => TokenEstimator.Estimate(json).Should().BeLessThan(maxTokens);
}
```

테스트 추가 패턴:
```csharp
[Fact]
public async Task GetIndexHistory_Default60Days_FitsTokenBudget()
{
    string result = await GetIndexHistoryTool.GetIndexHistory(client, "kospi", count: 60);
    result.ShouldFitTokenBudget(2500);
}
```

### 4.2 `ls_get_index_history` — B 패턴 적용

```
ls_get_index_history(
    index_code, period_type?, count?, cts_date?,
    verbosity?: "summary" | "compact" | "full" = "compact"
)
```

**verbosity 의미**:
- `"summary"`: `points` 생략, `summary` 블록만.
- `"compact"` (default): `summary` + `points[]` (현재 그대로).
- `"full"`: `summary` 없이 `points[]`만 (역호환 모드).

**`summary` 블록 신규** (compact / summary 모드에서 emit):

```json
{
  "summary": {
    "period": {"from": "20260219", "to": "20260518", "trading_days": 60},
    "open": 5677.25,
    "close": 7543.16,
    "change_total": 1865.91,
    "change_pct_total": 32.87,
    "high": {"date": "20260514", "value": 7981.41},
    "low":  {"date": "20260331", "value": 5052.46},
    "biggest_up":   {"date": "20260318", "change_pct": 5.04},
    "biggest_down": {"date": "20260303", "change_pct": -7.24},
    "breadth_avg":  {"advance": 380, "decline": 470, "unchanged": 65},
    "flows_total":  {"foreign_net": -891234, "institution_net": 234567}
  }
}
```

토큰 예상: 60일 default (compact) ~2.3k → `verbosity="summary"` ~400 토큰.

### 4.3 `ls_holdings_list` — A 패턴 적용

현재 응답에서 가장 무거운 부분은 **per-holding `themes` 배열** (대형주 평균 15+ 테마).

```
ls_holdings_list(
    account?, theme_code?, theme_keyword?, industry?,
    themes_limit?: int = 5,      // per-holding 최대 themes 노출, 0 = 생략
    verbosity?: "brief" | "compact" | "full" = "compact"
)
```

**verbosity 의미**:
- `"brief"`: holdings 행만 (themes / industry / quote 전부 생략), summary만.
- `"compact"` (default): themes는 `themes_limit`만큼 + count, quote 풀.
- `"full"`: 현재 v0.7 동작 (모든 themes).

응답 변경:
```json
{
  "holdings": [
    {
      "shcode": "000660",
      "themes_count": 12,
      "themes": [
        {"code": "0155", "name": "반도체 대표주(생산)"},
        {"code": "0307", "name": "시스템반도체"},
        // ... themes_limit개
        {"truncated": 7}
      ]
    }
  ]
}
```

토큰 예상: 10 holdings default ~6k → `verbosity="brief"` ~800 토큰.

### 4.4 `ls_get_stock_info` — A 패턴 적용

```
ls_get_stock_info(
    shcode,
    sections?: ("snapshot"|"fundamentals"|"periods"|"brokers"|"foreign"|"flags")[]
            = ["snapshot","fundamentals"]
)
```

**sections 의미**:
- `"snapshot"`: 현재가/거래량/OHLC.
- `"fundamentals"`: PER/PBR/EPS + 분기 재무 + 성장률.
- `"periods"`: 52주 / YTD 범위 + 이격도.
- `"brokers"`: top-5 매수/매도 거래원.
- `"foreign"`: 외인 동향 + 보유 비율.
- `"flags"`: SPAC / 관리종목 / 단기과열 등 상태 플래그.

각 섹션은 응답에서 별도 키로 emit. 미선택 섹션은 응답에서 omit.

토큰 예상: 전체 호출 ~2.5k → `sections=["snapshot","fundamentals"]` ~800 토큰.

### 4.5 D 패턴 cursor 공통화 (Phase 2)

대상: `ls_search_stock`, `ls_get_industry_stocks`, `ls_get_theme_stocks`, `ls_get_market_warnings`, `ls_get_fundamentals_rank`.

```
ls_search_stock(
    query, instrument?,
    limit?: int = 20,
    cursor?: string                   // 이전 응답의 next_cursor echo
) → {
    rows: [...],
    next_cursor: "...",               // 더 있을 때만 emit
    total_available?: int             // LS가 알려줄 때만
}
```

기존 `top_n` 같은 도구별 limit 이름은 deprecate 안 하고 `limit`로 점진 alias.

### 4.6 C 후보 추가 — index_history 장기

`ls_get_chart`의 `DatasetHandleCache`를 generic 패턴으로 추출 (`Tools/DatasetHandleCache.cs`는 이미 존재). v0.8에서 `ls_get_index_history`도 같은 cache reuse — 1년+ 호출일 때 dataset_id 반환.

```
ls_get_index_history(...,
    output_mode?: "summary" | "export" | "display" = "summary"
)
```

`"export"` 모드일 때 dataset_id를 cache에 저장하고, 후속 `ls_add_indicator(dataset_id=...)` 가 동작 (chart와 동일 인터페이스).

이 부분은 Phase 3 — 실제 요청 패턴 측정 후 추가.

## 5. 위험 / 미해결 질문

### 5.1 BREAKING 정책 — default 좁히기

v0.7 C-4 사례에서 default 5종→1종은 BREAKING이었고 통합 release notes에 정리됨. v0.8에서 `ls_holdings_list` themes_limit default를 5로 도입하면 또 BREAKING (v0.7까지는 모든 themes 풀 노출).

**제안**: v0.8을 의도적 BREAKING release로 잡고 default 좁히기 통합. 0.x 라인 정책상 minor에서 허용. v1.0으로 가기 전 한 번에 default 정돈.

대안: v0.8은 opt-in axes만 추가 (`themes_limit?` 파라미터, default null=풀 노출 유지). v0.9에서 default 좁히기 통합. 사용자 마이그레이션 부담은 작지만 실제 토큰 절감이 안 일어남 (모델이 default 의존).

이 결정은 v0.8 SPEC 확정 시 카드 1번에서 못박기.

### 5.2 측정 정확도

`TokenEstimator` char/3.5 비율은 영문 위주 휴리스틱. 한글 텍스트(commentary)는 자모당 1.5~2 토큰이라 더 무거움. 측정 오차 ±15% 정도 예상.

장기적으로는 `cl100k_base` 등 외부 tokenizer 통합 검토 (v0.9). v0.8은 char-count 기반 budget을 의도적으로 헐겁게 (실제 측정값 × 1.2) 잡아서 흡수.

### 5.3 verbosity vs sections vs fields — 컨벤션 충돌

`verbosity` enum이 직관적이지만 도구마다 의미가 달라짐 (`brief` vs `compact` 경계가 도구별로 다름). `sections` (stock_info), `themes_limit` (holdings_list), `metrics` (fundamentals_rank) 처럼 도메인 친화 파라미터가 더 정확함.

**제안**: 공통 axis 이름(`verbosity`, `limit`, `cursor`)은 진짜 도구가 공통 의미를 갖는 곳만 통일. 도메인 projection은 도구별 자연어 이름 유지. SPEC §2.2에 명시.

### 5.4 `dataset_id` cache 정책

기존 `DatasetHandleCache`는 chart 전용. v0.8에서 index_history 등으로 확장하려면:
- TTL 정책 통일 (현재 60분?)
- 메모리 압박 시 LRU eviction
- 프로세스 재시작 시 invalidation (현재 in-process)
- 동시 호출 시 cache key 안정성

Phase 3에서 본격 검토. v0.8.0 ship에는 안 들어감.

## 6. 출시 순서

```
SPEC 확정 + 사용자 리뷰
   ↓
TokenEstimator 헬퍼 + budget 테이블 (인프라)
   ↓
ls_get_index_history B 적용 (가장 깔끔, 학습용)
   ↓
ls_holdings_list A 적용 (가장 큰 절감)
   ↓
ls_get_stock_info sections (A pattern 표준 케이스)
   ↓
v0.8.0 출시 (Phase 1)
   ↓
Phase 2 — D 패턴 cursor 공통화 (v0.8.1+)
   ↓
Phase 3 — C 후보 검토 (v0.8.2 or v0.9)
```

각 단계는 독립 commit + token budget 테스트 통과.

## 7. 출시 / SemVer

- **v0.8.0**: §3 Phase 1 (B 1개 + A 2개 + 인프라). Default 좁히기 적용 시 minor에 BREAKING 섹션 (0.x 정책).
- v0.8.1~: §3 Phase 2 (D cursor 공통화).
- **v1.0 로드맵**: 43 tools 안착 + 응답 shape 컨벤션 표준화. v0.8까지의 절감/컨벤션이 v1.0 안정성의 기반.
- **NuGet publish**: A/B만 적용된 경우 Mcp-only patch 가능. C 인프라가 Core로 들어가면 Core → Mcp.
