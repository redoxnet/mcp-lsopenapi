# SPEC v0.8 — Amendments (delta only)

- **상태**: Review
- **작성일**: 2026-05-18
- **기준 문서**: [SPEC-v0.8-response-shapes.md](./SPEC-v0_8-response-shapes.md)
- **목적**: SPEC v0.8 초안에 대한 추가/변경/제안 사항만 정리. 본문에 미포함된 항목만 다룸.

우선순위: **[MUST]** = v0.8.0 ship 전 반영 권고. **[NICE]** = v0.8.x 또는 release notes 정리.

---

## A1. TokenEstimator — cl100k_base 즉시 도입 [MUST]

**문제**: SPEC §4.1의 `char.Length / 3.5`는 영문 휴리스틱. 한글은 cl100k_base 기준 글자당 1.5–2.5 토큰이라 §5.2의 ±15%보다 실측 오차 큼. v0.8에서 budget 잡아놓고 v0.9에서 tokenizer 정밀화하면 budget 전부 재조정해야 함.

**변경**: `Microsoft.ML.Tokenizers` (NuGet) 단일 패키지로 v0.8.0에 바로 통합.

```csharp
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

    /// <summary>
    /// Exact token count via cl100k_base.
    /// Preferred for budget assertions.
    /// </summary>
    /// <param name="json">Response JSON string to measure.</param>
    /// <returns>Token count.</returns>
    public static int Count(string json) =>
        _tokenizer.CountTokens(json);

    /// <summary>
    /// Char-count fallback. ±20% error on Korean-heavy payloads.
    /// Use only when <see cref="Count"/> is unavailable.
    /// </summary>
    /// <param name="json">Response JSON string to measure.</param>
    /// <returns>Approximate token count.</returns>
    public static int Estimate(string json) =>
        (int)Math.Ceiling(json.Length / 3.5);
}
```

**SPEC §4.1 patch**: `Estimate` → `Count`로 default 변경. §5.2 첫 단락 ("측정 오차 ±15%") 삭제.

---

## A2. `verbosity` enum 의미 정합성 — 도메인 axis 우선 [MUST]

**문제**: SPEC §4.2 (`index_history`)와 §4.3 (`holdings_list`)의 verbosity enum 값이 다름:
- `index_history`: `"summary" | "compact" | "full"`
- `holdings_list`: `"brief" | "compact" | "full"`

§5.3이 "공통 axis 이름은 진짜 공통 의미를 갖는 곳만 통일"이라 했는데, 그렇다면 enum 값도 도구별로 다른 게 자연스러움. 그러나 enum이 비슷한 모양이면 모델은 통일된 의미로 학습할 위험.

**제안 (B안)**: `holdings_list`에서 `verbosity` 제거, 도메인 axis만 사용.

```
ls_holdings_list(
    account?, theme_code?, theme_keyword?, industry?,
    themes_limit?: int = 5,            // per-holding 최대 themes 노출
    include_industry?: bool = true,    // industry 메타 포함 여부
    include_quote?: bool = true        // 현재가/거래량 포함 여부
)
```

`verbosity` 통일은 시계열(`index_history`)처럼 자연 절감 축이 빈약한 도구에만 한정.

**SPEC §2.2 보강**: "공통 enum(`verbosity`)은 자연 절감 축이 없는 도구에만 도입. 자연 축(`themes_limit`, `sections`, `include_*`)이 있으면 그것만 사용."

---

## A3. Truncation marker — array in-band 금지 [MUST]

**문제**: SPEC §4.3 themes 배열 안에 `{"truncated": 7}` 객체 삽입. C# `Theme[]` deserializer 입장에서 polymorphic shape 처리가 까다로움(JsonElement 분기 필요).

**변경**: 카운트 메타를 형제 필드로 분리.

```json
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
```

`themes_count`(전체) + `themes_shown`(반환 길이) + `themes`(균질 배열) 3-tuple은 §2.2 Echo 컨벤션과 일관.

**적용 범위**: A 패턴 절단이 발생하는 모든 도구(holdings_list, stock_info sections, fundamentals_rank metrics 등).

---

## A4. Migration matrix를 release notes에 명시 [MUST]

**문제**: SPEC §5.1에서 v0.8을 intentional BREAKING으로 결정하는 건 옳음. 다만 기존 사용자가 "v0.7과 동일하게" 호출하는 단일 파라미터 셋이 명시되어야 마이그레이션 부담 가시화.

**추가**: SPEC §7 또는 별도 `MIGRATION-v0.8.md`에 표 추가.

| 도구 | v0.7 동작 | v0.8 default | Full output 복원 |
|---|---|---|---|
| `ls_holdings_list` | 모든 themes 노출 | `themes_limit=5` | `themes_limit=0` |
| `ls_get_stock_info` | 모든 sections | `sections=["snapshot","fundamentals"]` | `sections=["snapshot","fundamentals","periods","brokers","foreign","flags"]` |
| `ls_get_index_history` | full points only | `verbosity="compact"` (summary 추가) | `verbosity="full"` |

`themes_limit=0` 의미는 §A6 참고.

---

## A5. `output_mode` × `verbosity` 상호작용 명시 [NICE]

**문제**: SPEC §4.6에서 `ls_get_index_history`에 `output_mode`와 `verbosity` 둘 다 부여 예정. 두 축의 우선순위 불명확.

**추가 (§4.6 말미)**:

> `output_mode="export"`일 때 `verbosity`는 무시되고 dataset handle만 반환. `output_mode="summary"` (default)에서만 `verbosity`가 응답 shape를 결정. `output_mode="display"`는 `verbosity="compact"`와 동등 처리.

---

## A6. `themes_limit: 0` semantics 명확화 [NICE]

**문제**: SPEC §4.3에서 "0 = 생략"으로 적혀있으나 `null`(default 사용)과 `0`(실제 0개)의 구분이 모호.

**제안**:
- `themes_limit: null` 또는 미지정 → default(5) 적용.
- `themes_limit: 0` → `themes` 배열 자체를 응답에서 omit (`themes_count`만 emit).
- `themes_limit: N (N > 0)` → 최대 N개 반환.

**역방향 (full)**: `themes_limit: -1` 또는 큰 정수(예: 1000)로 풀 노출. v0.7 호환 호출자는 후자 사용 권장.

---

## A7. Phase 1 순서 — stock_info를 holdings_list보다 먼저 [NICE]

**현재**: index_history (B) → holdings_list (A) → stock_info (A).

**제안**: index_history (B) → **stock_info (A)** → **holdings_list (A)**.

**근거**: `stock_info`의 `sections` 패턴이 A의 가장 순수한 케이스(단일 종목, 단일 응답, 명확한 카테고리). `holdings_list`는 A + 리스트 + 테마 폭발 합쳐진 복합 케이스. 컨벤션 학습 곡선상 단순→복잡 순서가 후속 도구 적용 시 재사용성 높음.

---

## A8. Summary 블록 단위 메타 [NICE]

**문제**: SPEC §4.2 `summary` 블록의 `breadth_avg` 평균 단위(종목 수 / 비율?)와 `flows_total` 통화 단위(원 / 백만원?) 모호.

**제안**:

```json
{
  "summary": {
    "period": {"from": "20260219", "to": "20260518", "trading_days": 60},
    "breadth_avg": {"advance": 380, "decline": 470, "unchanged": 65},
    "flows_total": {"foreign_net": -891234, "institution_net": 234567},
    "_meta": {
      "breadth_unit": "stocks_per_day",
      "flows_unit": "million_krw"
    }
  }
}
```

또는 필드명에 suffix: `breadth_avg_stocks_per_day`, `flows_total_million_krw`.

응답 토큰 +30~50, 모델 해석 모호성 완전 제거.

---

## A9. Budget 테이블에 Summary tier 추가 [NICE]

**현재 (SPEC §2.4)**: Default / Verbose 2-tier.

**제안**: Summary / Default / Verbose 3-tier. `verbosity="summary"` 지원 도구만 Summary 컬럼 채움.

| 도구 | Summary budget | Default budget | Verbose budget |
|---|---:|---:|---:|
| `ls_get_quote` | — | 600 | 1500 |
| `ls_get_index_history` (60d) | 500 | 2500 | 5000 |
| `ls_get_investor_flow` daily (30d) | 600 | 2500 | 8000 |
| `ls_holdings_list` (10 holdings) | 800 | 3000 | 6000 |
| `ls_get_stock_info` | — | 800 | 2500 |

3-tier로 가면 모델한테도 "summary 모드 = ~500 토큰, full = ~5000 토큰" 식의 명확한 신호.

---

## A10. DatasetHandleCache 확장 시 cache key 정책 [NICE]

**문제**: SPEC §5.4에서 v0.8.0 ship에는 안 들어가지만, Phase 3 진입 시 동시 호출 cache key 안정성 문제 미해결.

**제안 (§5.4 추가)**:

```
cache_key = SHA-256(canonical_json({
    "tool": "ls_get_index_history",
    "params": <sorted parameter map excluding output_mode>
}))
```

- canonical_json: 키 정렬 + 공백 제거 + 숫자 정규화.
- `output_mode` 자체는 key에서 제외 (export/summary/display가 같은 underlying 데이터 가리킴).
- TTL: chart의 기존 정책 재사용(60분 가정).
- 프로세스 재시작 시 in-process이므로 자연 invalidation.

이 정도만 §5.4에 한 단락 추가하면 Phase 3 진입 시 인터페이스 변동 없음.

---

## A11. AssistStudio Specialist 사용 패턴 동기 보강 [NICE]

**제안 (§1 또는 §3 말미 1줄 추가)**:

> v0.8 절감은 단일 호출 비용만이 아니라 AssistStudio Specialist(Critique/RedTeam/DevilsAdvocate)에서 LsOpenApi 도구를 fan-out으로 호출할 때 곱셈으로 누적됨. v0.8 ship 후 Specialist 실측 데이터로 v0.9 budget 재교정.

LsOpenApi 단독 motivation으로 충분하지만, 상위 시스템(AssistStudio)의 multi-specialist 패턴에서 효과 가시화되면 v0.9 우선순위 재논의 시 근거 확보.

---

## 반영 우선순위 요약

| Amendment | 우선순위 | v0.8.0 ship 영향 |
|---|---|---|
| A1 TokenEstimator cl100k_base | MUST | 인프라 — phase 1 시작 전 |
| A2 verbosity 정합성 (holdings_list) | MUST | API 표면 — phase 1 작업 중 |
| A3 Truncation marker 형제 필드 | MUST | API 표면 — phase 1 작업 중 |
| A4 Migration matrix | MUST | release notes |
| A5 output_mode × verbosity | NICE | SPEC 문서만 |
| A6 themes_limit: 0 semantics | NICE | SPEC + API 표면 |
| A7 Phase 1 순서 조정 | NICE | 작업 순서 |
| A8 Summary 단위 메타 | NICE | API 표면 |
| A9 Budget 3-tier | NICE | 테스트 + SPEC |
| A10 cache key 정책 | NICE | SPEC §5.4 보강 |
| A11 AssistStudio 동기 | NICE | SPEC §1 |

**Ship 게이트**: A1 + A2 + A3 + A4 반영 후 SPEC 본문 갱신 → 카드 1번(BREAKING 정책) 의결 → Phase 1 작업 착수.
