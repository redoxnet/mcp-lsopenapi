# SPEC: v0.9 — Response shape taxonomy + token economy

- **상태**: Approved
- **작성일**: 2026-05-18 (v0.8 초안 + amendments) · 통합 / v0.9 리네임: 2026-05-20
- **대상 버전**: v0.9.0
- **작성자**: Jong Hyun
- **선행**: [SPEC-v0.7.md](./SPEC-v0.7.md)
- **비고**: v0.8 초안(`SPEC-v0.8-response-shapes.md`)과 amendments(`SPEC-v0_8-amendments.md`)를
  단일 문서로 통합. v0.8.0은 도구/카탈로그만 출시(2026-05-20)했고 응답-shape 작업은 v0.9로 이관.
  amendments A1–A11은 모두 본문에 반영 완료. §5.1 BREAKING 정책 의결 완료(→ §5.1).

## 1. 컨텍스트

v0.7 출시(43 tools)에서 두 가지 응답-크기 이슈가 라이브 E2E (`todo/Test_v0.7.0.txt`)로 드러남:

1. **`ls_get_investor_flow` daily mode 12k 토큰 폭주.** 30일 × 12 investor types × `{kind, korean_label, value}` 객체 배열 = 한 호출에 ~12,100 토큰. v0.7 commit `6f8d86e`에서 surgical fix: default 3종 + `flows` array→map shape + summary 블록 + 중복 메타 제거 → 약 75% 절감 (~2.5k).
2. **`ls_get_market_warnings` 기본 5종 fan-out + cursor stuck dedup 부재.** 사용자가 *"오늘 관리종목"* 한 줄 물었는데 wrapper가 5종 × 6 페이지 = 600 raw row 반환 (실제 unique 100). v0.7 commit `2fe5a28`에서 dedup + default 1종 + cap 6→3으로 정리.

**진단**: 두 사례 모두 *"N × K 매트릭스 응답"* 패턴이지만 v0.7까지는 도구마다 즉흥적으로 다뤄옴. 명시적 분류 + 공통 컨벤션이 없으면 신규 도구마다 같은 함정.

LS API 래퍼 전반에 깔린 N×K 응답 매트릭스(차트, 트리맵, 스크리닝, 호가창, 업종/테마 등락률, 시간대별 분포 등)에 대한 **응답 shape 분류 + 절감 전략 표준화**가 v0.9의 1순위.

> v0.9 절감은 단일 호출 비용만이 아니라 AssistStudio Specialist(Critique / RedTeam / DevilsAdvocate)에서 LsOpenApi 도구를 fan-out으로 호출할 때 곱셈으로 누적됨. v0.9 ship 후 Specialist 실측 데이터로 후속 budget 재교정.

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
| Projection (A) | 도메인 자연어 이름 — `sections`, `themes_limit`, `include_*`, `metrics`, `fields` | 도구마다 다르게 OK | "narrow default" — 가장 흔한 부분집합 |
| Aggregation (B) | `verbosity?: "summary" \| "compact" \| "full" = "compact"` | 통일 | `"compact"` (summary + 핵심 N개 row) |
| Reference (C) | `output_mode?: "summary" \| "export" \| "display"`, `dataset_id?: string` | 통일 (ls_get_chart 패턴) | `"summary"` |
| Pagination (D) | `limit?: int`, `cursor?: string` | 통일 | `limit` 도메인별 (예: 10~30) |

**공통 enum vs 도메인 axis 우선순위 (amendment A2).** 공통 enum(`verbosity`)은 *자연 절감 축이 빈약한 도구에만* 도입한다. 도구에 자연 projection 축(`themes_limit`, `sections`, `include_*`, `metrics`)이 존재하면 그것만 사용하고 `verbosity`는 넣지 않는다. 이유: enum 모양이 도구 간 비슷하면 모델이 통일된 의미로 오학습할 위험이 있고, 도메인 axis가 항상 더 정확하다. → `ls_get_index_history`(시계열, 자연 축 빈약)만 `verbosity`를 갖고, `ls_holdings_list` / `ls_get_stock_info`는 도메인 axis만 사용.

**Echo 컨벤션** (응답에 caller가 무엇을 받았는지 명시):
- A: `<projection_axis>_shown` (예: `sections_shown: ["snapshot","fundamentals"]`)
- A 절단 발생 시: `<axis>_count`(전체) + `<axis>_shown`(반환 길이) + `<axis>`(균질 배열) **3-tuple** — 절단 마커를 배열 안에 in-band로 넣지 않는다 (amendment A3, → §4.4).
- B: `summary: { ... }` 블록 always present (단, `output_mode`가 export일 때 omit 가능)
- C: `dataset_id`, `output_mode` echo
- D: `next_cursor?: string`, `total_available?: int`

### 2.3 도구별 분류 매트릭스 (v0.8 surface 기준 48개)

v0.8.0에서 5개 래퍼가 추가됨(43 → 48). 아래는 refactor 후보가 있는 도구 위주의 선별 목록.

| 도구 | 패턴 | 현재 상태 | v0.9 작업 |
|---|---|---|---|
| `ls_get_quote` | A (10 levels × 6 fields) | 전체 dump | order_book 옵션 / 단계 limit |
| `ls_get_multi_quote` | D (50종목 list) | 전량 dump | `fields?` 카테고리 |
| `ls_get_top_stocks` | D + A | top_n cap 있음 | cursor 추가 검토 |
| `ls_get_stock_info` | **A (~50 fields)** | 전체 dump | `sections?` — **Phase 1** |
| `ls_get_chart` | **C (reference impl)** | 완비 | 변경 없음 |
| `ls_add_indicator` | A on dataset | 완비 | — |
| `ls_reframe_chart` | C drill-down | 완비 | — |
| `ls_search_stock` | D | head only | cursor + limit 노출 |
| `ls_get_etf_info` | A | 전체 dump | `sections?` |
| `ls_get_etf_holdings` | D + A | top_n cap | cursor 검토 |
| `ls_get_index_quote` | A (related 4 indices 포함) | 전체 dump | `include_related?: bool = true` |
| `ls_get_index_history` | **B 누락** (60 bars × 14 fields) | 전체 dump | `summary` 블록 + `verbosity` — **Phase 1** |
| `ls_get_industry_indices` | D | top_n cap | cursor 검토 |
| `ls_get_industry_stocks` | D | top_n cap | cursor + summary 검토 |
| `ls_get_theme_stocks` | D | top_n cap | 동일 |
| `ls_get_stock_themes` | A (N themes) | 전체 dump | `theme_limit?` |
| `ls_get_fundamentals_rank` | D + A | count param | `metrics_shown?` 옵션 (현재 14 metric × N row) |
| `ls_get_investor_flow` daily | A + B ✓ | v0.7 완비 | 변경 없음 |
| `ls_get_investor_flow` intraday | A ✓ | v0.7 완비 | summary 추가 검토 |
| `ls_get_stock_events` | D (자연스럽게 작음) | OK | — |
| `ls_get_market_warnings` | D + A | dedup ✓, default 1종 | cursor 노출 |
| `ls_holdings_list` | **A 누락** (N×themes 폭발) | 전체 dump | `themes_limit?` + `include_*` — **Phase 1** |
| `ls_get_global_market_quote` | A (t3521 다시장 스냅샷) | 전체 dump | `markets?` 필터 검토 |
| `ls_get_analyst_opinions` | D + B (t3401 변경 이력) | continuation cursor ✓ | `limit` 노출 검토 |
| `ls_get_short_selling_trend` | B (t1927 일별 시계열) | 전체 dump | `summary` 블록 검토 |
| `ls_get_market_funds_trend` | B (t8428 자금 시계열) | 전체 dump | `summary` 블록 검토 |
| `ls_get_high_low_stocks` | D (t1442 스크리너) | 전량 dump | `limit` + `cursor` |
| (portfolio 쓰기 / watchlist / accounts) | — | 이미 작음 | — |

### 2.4 측정 인프라

v0.7까지 토큰 절감은 *추정*. v0.9부터 테스트로 pin. `TokenEstimator` 헬퍼는 cl100k_base 토크나이저를 사용해 정밀 측정한다 (amendment A1, → §4.1). 헬퍼는 테스트 전용(`tests/.../TestSupport/`)이며 신규 NuGet 의존성 `Microsoft.ML.Tokenizers`는 **Tests csproj에만** 추가 — 출시되는 Core / Mcp 패키지에는 영향 없음.

```csharp
[Fact]
public async Task GetIndexHistory_Default60Days_FitsTokenBudget()
{
    string result = await GetIndexHistoryTool.GetIndexHistory(client, "kospi", count: 60);
    result.ShouldFitTokenBudget(2500);
}
```

**도구별 token budget 테이블 (3-tier, amendment A9).** Summary 컬럼은 `verbosity="summary"` 등 최소 모드를 지원하는 도구만 채움. 아래 수치는 **provisional** — char/3.5 휴리스틱 기준 추정값이며, cl100k_base 실측 후 첫 Phase 1 도구에서 pin하고 나머지를 재교정한다.

| 도구 | Summary budget | Default budget | Verbose budget |
|---|---:|---:|---:|
| `ls_get_quote` | — | 600 | 1500 |
| `ls_get_index_history` (60d) | 500 | 2500 | 5000 |
| `ls_get_investor_flow` daily (30d) | 600 | 2500 | 8000 |
| `ls_holdings_list` (10 holdings) | 800 | 3000 | 6000 |
| `ls_get_stock_info` | — | 800 | 2500 |

3-tier로 가면 모델한테도 "summary 모드 ≈ 500 토큰, full ≈ 5000 토큰" 식의 명확한 신호.

## 3. 우선순위 (v0.9 ship target)

v0.8 surface 48 tools 전부를 한꺼번에 refactor하는 건 risk 큼. **3-5개 high-impact 도구**로 시작.

### Phase 1 — 고임팩트 3개 + 인프라 (v0.9.0)

작업 순서는 단순→복잡 (amendment A7): A 패턴의 가장 순수한 케이스(`stock_info`)를 먼저 해서 컨벤션을 확정한 뒤, 복합 케이스(`holdings_list`)에 재사용한다.

1. **공통 인프라**: `TokenEstimator`(cl100k_base) 테스트 헬퍼 + budget 테이블. (→ §4.1)
2. **`ls_get_index_history`** (패턴 B 누락) — 60일 dense 시계열. `summary` 블록 + `verbosity` 추가. 가장 깔끔한 첫 케이스. (→ §4.2)
3. **`ls_get_stock_info`** (패턴 A) — 단일 종목 ~50 fields. `sections` 카테고리 필터. A의 가장 순수한 케이스. (→ §4.3)
4. **`ls_holdings_list`** (패턴 A 누락) — 보유 종목당 themes 평균 15개로 폭발. `themes_limit` + `include_*` 추가. A + 리스트 + 테마 폭발 복합 케이스. (→ §4.4)

### Phase 2 — 리스트성 도구 D 컨벤션 정착 (v0.9.x)

5. `ls_search_stock`, `ls_get_industry_stocks`, `ls_get_theme_stocks`, `ls_get_market_warnings`, `ls_get_fundamentals_rank`, `ls_get_high_low_stocks` — `limit` + `cursor` 공통화.
6. `ls_get_top_stocks` — cursor 노출 (현재 내부 페이징만).

### Phase 3 — C 후보 추가 검토 (v0.9.x or v0.10)

7. `ls_get_index_history` 1년+ 호출 → chart의 `dataset_id` cache 재사용 (인프라 공유, → §4.6 / §5.4).
8. `ls_get_fundamentals_rank` 전 종목 (count=1000+) → dataset_id + drill.

기타 도구는 v0.10 이후 점진.

## 4. 도구 / 동작 변경 상세

### 4.1 공통 — TokenEstimator + budget 테스트 헬퍼 (amendment A1)

`char.Length / 3.5` 휴리스틱은 영문 기준이며 한글은 cl100k_base 기준 글자당 1.5–2.5 토큰이라 실측 오차가 큼. budget을 휴리스틱으로 잡아두면 후속 정밀화 시 전부 재조정해야 하므로, v0.9.0에서 바로 `Microsoft.ML.Tokenizers`(NuGet) 단일 패키지로 cl100k_base를 통합한다.

```csharp
// tests/RedoxNet.Mcp.LsOpenApi.Tests/TestSupport/TokenEstimator.cs

/// <summary>
/// Token estimator for LS OpenAPI response budget assertions.
/// Uses cl100k_base tokenizer (Microsoft.ML.Tokenizers) for ~2% accuracy
/// on mixed Korean/English JSON. Char-count fallback retained for offline runs
/// where the tokenizer model file is unavailable.
/// </summary>
public static class TokenEstimator
{
    private static readonly Tokenizer _tokenizer =
        TiktokenTokenizer.CreateForModel("gpt-4");

    /// <summary>Exact token count via cl100k_base. Preferred for budget assertions.</summary>
    public static int Count(string json) => _tokenizer.CountTokens(json);

    /// <summary>Char-count fallback. ±20% error on Korean-heavy payloads.
    /// Use only when <see cref="Count"/> is unavailable.</summary>
    public static int Estimate(string json) => (int)Math.Ceiling(json.Length / 3.5);
}

public static class FluentAssertionsExtensions
{
    public static void ShouldFitTokenBudget(this string json, int maxTokens)
        => TokenEstimator.Count(json).Should().BeLessThan(maxTokens);
}
```

`Microsoft.ML.Tokenizers`는 `tests/RedoxNet.Mcp.LsOpenApi.Tests/RedoxNet.Mcp.LsOpenApi.Tests.csproj`에만 `PackageReference`로 추가.

### 4.2 `ls_get_index_history` — B 패턴 적용

```
ls_get_index_history(
    index_code, period_type?, count?, cts_date?,
    verbosity?: "summary" | "compact" | "full" = "compact"
)
```

**verbosity 의미**:
- `"summary"`: `points` 생략, `summary` 블록만.
- `"compact"` (default): `summary` + `points[]`.
- `"full"`: `summary` 없이 `points[]`만 (v0.8 동작과 동등 — 역호환 복원 모드).

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
    "flows_total":  {"foreign_net": -891234, "institution_net": 234567},
    "_meta": {
      "breadth_unit": "stocks_per_day",
      "flows_unit": "million_krw"
    }
  }
}
```

**단위 메타 (amendment A8)**: `breadth_avg`(종목 수 / 비율?)와 `flows_total`(원 / 백만원?) 단위 모호성 제거를 위해 `summary._meta`에 단위를 명시한다. 응답 토큰 +30~50, 모델 해석 모호성 완전 제거.

토큰 예상: 60일 default (compact) ~2.3k → `verbosity="summary"` ~400 토큰.

### 4.3 `ls_get_stock_info` — A 패턴 적용

A의 가장 순수한 케이스(단일 종목, 단일 응답, 명확한 카테고리) — Phase 1에서 `holdings_list`보다 먼저 작업해 `sections` 컨벤션을 확정.

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

각 섹션은 응답에서 별도 키로 emit. 미선택 섹션은 응답에서 omit. 응답에 `sections_shown: [...]` echo.

토큰 예상: 전체 호출 ~2.5k → `sections=["snapshot","fundamentals"]` ~800 토큰.

### 4.4 `ls_holdings_list` — A 패턴 적용 (amendments A2 / A3 / A6)

현재 응답에서 가장 무거운 부분은 **per-holding `themes` 배열** (대형주 평균 15+ 테마). A2에 따라 `verbosity` enum 대신 도메인 자연 축만 사용한다.

```
ls_holdings_list(
    account?, theme_code?, theme_keyword?, industry?,
    themes_limit?: int = 5,            // per-holding 최대 themes 노출 (A6 참고)
    include_industry?: bool = true,    // FICS industry 메타 포함 여부
    include_quote?: bool = true        // 현재가/거래량/PnL 포함 여부
)
```

> 주의: 기존 `industry` 파라미터는 *필터*(보유 종목을 industry로 좁힘)이고, 신규 `include_industry`는 *projection 토글*(출력에 industry 메타를 실을지)이다. 둘은 별개 축.

**themes_limit 의미 (amendment A6)**:
- `null` / 미지정 → default `5` 적용.
- `0` → `themes` 배열 자체를 응답에서 omit (`themes_count`만 emit).
- `N` (N > 0) → 최대 N개 반환.
- `-1` → 전량 노출 (v0.7/v0.8 호환 복원 모드).

**가장 가벼운 호출**(구 "brief" 등가): `themes_limit=0, include_industry=false, include_quote=false` → holdings 행 + 계좌별/전체 summary만.

**Truncation marker — 형제 필드 (amendment A3)**: 절단 카운트를 배열 안에 in-band 객체(`{"truncated": 7}`)로 넣지 않는다. C# `Theme[]` deserializer가 polymorphic shape를 분기 처리해야 하므로. 대신 형제 필드로 분리:

```json
{
  "holdings": [
    {
      "shcode": "000660",
      "themes_count": 12,
      "themes_shown": 5,
      "themes": [
        {"code": "0155", "name": "반도체 대표주(생산)"},
        {"code": "0307", "name": "시스템반도체"},
        {"code": "0411", "name": "HBM"},
        {"code": "0512", "name": "AI 반도체"},
        {"code": "0633", "name": "메모리"}
      ]
    }
  ]
}
```

`themes_count`(전체) + `themes_shown`(반환 길이) + `themes`(균질 배열) 3-tuple은 §2.2 Echo 컨벤션과 일관. 이 형제-필드 규칙은 A 패턴 절단이 발생하는 모든 도구(holdings_list, stock_info sections, fundamentals_rank metrics 등)에 동일 적용.

토큰 예상: 10 holdings default ~6k → `themes_limit=0, include_*=false` ~800 토큰.

### 4.5 D 패턴 cursor 공통화 (Phase 2)

대상: `ls_search_stock`, `ls_get_industry_stocks`, `ls_get_theme_stocks`, `ls_get_market_warnings`, `ls_get_fundamentals_rank`, `ls_get_high_low_stocks`.

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

### 4.6 C 후보 추가 — index_history 장기 (Phase 3)

`ls_get_chart`의 `DatasetHandleCache`(`src/RedoxNet.Mcp.LsOpenApi/Tools/DatasetHandleCache.cs`, 존재 확인됨)를 generic 패턴으로 재사용. `ls_get_index_history`도 1년+ 호출일 때 같은 cache로 `dataset_id` 반환.

```
ls_get_index_history(...,
    output_mode?: "summary" | "export" | "display" = "summary"
)
```

**`output_mode` × `verbosity` 상호작용 (amendment A5)**:
- `output_mode="export"`: `verbosity`는 무시되고 dataset handle만 반환. cache에 저장되어 후속 `ls_add_indicator(dataset_id=...)`가 chart와 동일 인터페이스로 동작.
- `output_mode="summary"` (default): `verbosity`가 응답 shape를 결정.
- `output_mode="display"`: `verbosity="compact"`와 동등 처리.

이 부분은 Phase 3 — 실제 요청 패턴 측정 후 추가.

## 5. 위험 / 미해결 질문

### 5.1 BREAKING 정책 — default 좁히기 (의결 완료 2026-05-20)

**결정**: v0.9를 **의도적 BREAKING release**로 잡고 default 좁히기를 통합한다.

- v0.9 default: `ls_holdings_list` `themes_limit=5`, `ls_get_stock_info` `sections=["snapshot","fundamentals"]`, `ls_get_index_history` `verbosity="compact"`.
- 근거: opt-in only(default=null=전량) 안은 모델이 default로 호출하는 한 실제 토큰 절감이 일어나지 않음. 토큰 economy가 v0.9의 1순위 목적이므로 default를 좁혀야 효과 발생.
- 0.x semver 정책상 minor에서 BREAKING 허용. v0.7 C-4 사례(market_warnings 5종→1종)에서 이미 동일 패턴의 breaking narrow를 한 선례 있음.
- 마이그레이션 부담은 §8 매트릭스로 가시화. 통합 release notes에 BREAKING 섹션 명시.

### 5.2 측정 정확도

`TokenEstimator`는 cl100k_base(`Microsoft.ML.Tokenizers`)로 정밀 측정 — 혼합 한/영 JSON에서 오차 ~2% (amendment A1, → §4.1). char-count `Estimate`는 토크나이저 모델 파일을 쓸 수 없는 offline run 전용 fallback으로만 남김. §2.4 budget 수치는 휴리스틱 추정값이므로 Phase 1 첫 도구에서 cl100k 실측으로 pin 후 나머지를 재교정한다.

### 5.3 verbosity vs sections vs themes_limit — 컨벤션 충돌

`verbosity` enum은 직관적이지만 도구마다 의미 경계가 달라짐. `sections`(stock_info), `themes_limit`/`include_*`(holdings_list), `metrics`(fundamentals_rank)처럼 도메인 친화 파라미터가 더 정확함.

**원칙 (§2.2 반영)**: 공통 axis 이름(`verbosity`, `limit`, `cursor`)은 도구가 진짜 공통 의미를 갖는 곳만 통일. 도메인 projection은 도구별 자연어 이름 유지. 자연 절감 축이 빈약한 도구(시계열 `index_history`)에만 `verbosity` 도입.

### 5.4 `dataset_id` cache 정책

기존 `DatasetHandleCache`는 chart 전용. Phase 3에서 index_history 등으로 확장하려면:
- TTL 정책 통일 (chart 기존 정책 재사용, 60분 가정)
- 메모리 압박 시 LRU eviction
- 프로세스 재시작 시 invalidation (in-process이므로 자연 invalidation)
- 동시 호출 시 cache key 안정성

**cache key 정책 (amendment A10)**:

```
cache_key = SHA-256(canonical_json({
    "tool": "ls_get_index_history",
    "params": <sorted parameter map excluding output_mode>
}))
```

- canonical_json: 키 정렬 + 공백 제거 + 숫자 정규화.
- `output_mode` 자체는 key에서 제외 (export/summary/display가 같은 underlying 데이터를 가리킴).

Phase 3에서 본격 검토. v0.9.0 ship에는 안 들어감.

## 6. 출시 순서

```
SPEC 확정 (이 문서) + 사용자 리뷰  ✓
   ↓
TokenEstimator(cl100k_base) 헬퍼 + budget 테이블 (인프라)
   ↓
ls_get_index_history B 적용 (가장 깔끔, 학습용)
   ↓
ls_get_stock_info sections (A pattern 순수 케이스 — 컨벤션 확정)
   ↓
ls_holdings_list A 적용 (가장 큰 절감, 복합 케이스)
   ↓
v0.9.0 출시 (Phase 1) + MIGRATION 매트릭스 release notes
   ↓
Phase 2 — D 패턴 cursor 공통화 (v0.9.1+)
   ↓
Phase 3 — C 후보 검토 (v0.9.x or v0.10)
```

각 단계는 독립 commit + token budget 테스트 통과.

## 7. 출시 / SemVer

- **v0.9.0**: §3 Phase 1 (인프라 + B 1개 + A 2개). 의도적 BREAKING — release notes에 BREAKING 섹션 + §8 마이그레이션 매트릭스.
- v0.9.1~: §3 Phase 2 (D cursor 공통화).
- **v1.0 로드맵**: 48 tools 안착 + 응답 shape 컨벤션 표준화. v0.9까지의 절감/컨벤션이 v1.0 안정성의 기반.
- **NuGet publish**: A/B만 적용된 경우 Mcp-only patch 가능. C 인프라가 Core로 들어가면 Core → Mcp 순서.

## 8. 마이그레이션 매트릭스 (amendment A4)

v0.9는 의도적 BREAKING이므로 기존 사용자가 v0.8과 동일 출력을 복원하는 호출을 명시한다.

| 도구 | v0.7 / v0.8 동작 | v0.9 default | Full output 복원 |
|---|---|---|---|
| `ls_holdings_list` | 모든 themes 노출 | `themes_limit=5` | `themes_limit=-1` |
| `ls_get_stock_info` | 모든 sections | `sections=["snapshot","fundamentals"]` | `sections=["snapshot","fundamentals","periods","brokers","foreign","flags"]` |
| `ls_get_index_history` | full points only | `verbosity="compact"` (summary 추가) | `verbosity="full"` |

> 주의: `ls_holdings_list`의 full 복원은 `themes_limit=-1`이다. `themes_limit=0`은 `themes` 배열을 **완전히 omit**하므로(§4.4 / A6) full 복원이 아니다.
