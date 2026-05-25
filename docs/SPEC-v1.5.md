# SPEC: v1.5.0 — 일봉 캔들 캐시 + saved screener macros

- **상태**: Draft — v1.4-dev 후속 슬라이스 정의, 2026-05-25 작성
- **대상 버전**: v1.5.0
- **선행**: [SPEC-v1.4.md](./SPEC-v1.4.md), [`todo/4. AGENTS-PATCH-003-daily-candle-cache.md`](../todo/4.%20AGENTS-PATCH-003-daily-candle-cache.md), 메모리 [`next_overseas_stocks`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/next_overseas_stocks.md)
- **범위**: 세 슬라이스 —
  ① **일봉 캔들 SQLite 캐시**(인프라, todo/PATCH-003 채택)
  ② **Saved screener macros**(사용자 자기 조건 묶음, SQLite)
  ③ **Chart payload host adaptation**(렌더 힌트 / artifact callback / PNG 폴백 — v1.4 E2E + Cowork 분석으로 발견)
  ①·②는 기존 SQLite 인프라 확장(`SqlitePortfolioRepository`와 같은 위치) — 새 DB 의존성 없음. ③은 응답 페이로드의 `_meta.render_hints` 추가 + 옵션적 PNG 폴백.
- **선행 메모리 권장**: [`release_prep_convention`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/release_prep_convention.md), [`mcp_server_deploy_setup`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/mcp_server_deploy_setup.md)

---

## 1. 컨텍스트 & 범위

### 1.1 출발점 — v1.4.0 완료 상태

v1.4.0은 두 슬라이스를 묶어 ship 완료:
- **A**: Date-envelope 표준화 (2 도구: ls_get_market_funds_trend, ls_get_short_selling_trend). 나머지 ~10 daily-snapshot tool은 v1.5+로 후속 patch.
- **B**: Q-Click 시그널 카탈로그 + 복합 검색 (3 신규 도구: ls_list_screeners, ls_run_screener, ls_combine_screeners). LS-curated 99 시그널 + AND/OR 교집합/합집합 + 키워드 매칭 + β policy ambiguity.

자세한 슬라이스 정의와 E2E 검증 결과는 [`SPEC-v1.4.md`](./SPEC-v1.4.md)와 9 개 commit의 message 참조.

### 1.2 v1.5가 청산하는 백로그

| 슬라이스 | 출처 | 성격 |
|---|---|---|
| **A. 일봉 캔들 캐시** | `todo/4. AGENTS-PATCH-003-daily-candle-cache.md` (2026-05-15 작성, 미구현 상태로 보존) | 인프라 (SQLite 테이블 + 동기화 정책 + 도구 통합) |
| **B. Saved screener macros** | v1.4 E2E 중 발견 — HTS [1892] 자유조건 미노출의 진짜 보상 ([LS-API-QUIRKS §4.3](./LS-API-QUIRKS.md)) | 사용자 자기 매크로 (SQLite 테이블 + 도구 4-5개) |
| **C. Chart payload host adaptation** | v1.4-dev Cowork E2E + Cowork 측 LLM 분석 — chart spec이 native-renderer 없는 host에서 모델 컨텍스트를 *두 번* 지나가 ~3.4k 토큰을 소모하는 문제 | 응답 페이로드의 `_meta.render_hints` + (옵션) PNG 폴백 + ServerInstructions 보강 |

### 1.3 왜 한 릴리스로 묶는가

두 슬라이스 모두 **기존 SQLite 인프라 확장**:
- v0.5부터 `src/RedoxNet.Mcp.LsOpenApi/Portfolio/SqlitePortfolioRepository.cs`로 SQLite 사용 중
- `PortfolioServiceCollectionExtensions` DI 등록 패턴 그대로
- `PortfolioIoModels` export/import 패턴 그대로
- 캔들 캐시(슬라이스 A)는 *별도 DB 파일*(`candles.db`)이지만 같은 storage location 사용

두 슬라이스가 의존 관계는 없지만 **같은 인프라 패턴**을 두 번 적용하는 셈이라 묶음 ship 효율 큼. 사용자 가치도 결이 같음 — "매번 LS API에 전적으로 의존하지 않는 자기 로컬 상태".

대안: A만 v1.5.0, B를 v1.6.0으로 분리. **선택 기준**은 §5의 일정 추정에 달려있다.

### 1.4 비범위

- **실시간 WebSocket / 실계좌 / 주문** — v2.x.
- **분봉/틱 장기 캐시** — A 슬라이스는 일봉만. 분봉은 다음.
- **기업행위 전용 DB** — split, 합병, 권리락 데이터의 별도 적재. A는 raw / adjusted 분리 저장으로 *결과*만 캐시.
- **자동 백그라운드 시장 전체 동기화** — A는 *호출 시점 lazy fetch* 위주. 별도 daemon은 v1.6+.
- **saved screener의 백테스트 / 자동 실행** — B는 저장/실행/삭제/공유까지. 자동 실행 스케줄링은 v1.6+.

---

## 2. 슬라이스 A — 일봉 캔들 SQLite 캐시

### 2.1 문제

현재 `ls_get_chart` 등 차트 도구는 모든 요청을 LS `t8410`(일봉) / `g3204`(미장) 등으로 *매번* 보낸다. 결과:

- **API 호출량 폭증**: 사용자가 같은 종목 차트를 한 세션에 5-10번 본다 → 5-10번 LS 호출 → rate limit 압박.
- **지표 워밍업 낭비**: MA200 계산하려면 200봉 + display N봉 → 매번 200+N봉 fetch.
- **월봉/주봉 일관성 부재**: t8410 일봉과 t8410 월봉 응답이 약간 다를 수 있음(주가 보정 시점 / 마감 처리). 같은 종목인데 분석 기준이 흔들림.
- **분석 품질 저하**: 매번 LS round trip이 들어가니 모델이 *N봉만* 요청하고 끝 — 장기 추세 분석은 지표 한두 개로 단순화됨.

### 2.2 디자인 — PATCH-003 그대로 채택

`todo/4. AGENTS-PATCH-003-daily-candle-cache.md`에 충실한 설계 초안이 이미 있다. v1.5는 그 디자인을 그대로 구현. 핵심:

- **Storage**: `%LOCALAPPDATA%\RedoxNet\LsOpenApi\candles.db` (token.db와 *분리*; 보안 / 마이그레이션 / 삭제 정책이 다름).
- **Schema** (3 테이블):
  - `candles_daily` — `(environment, market, shcode, bar_date, adjustment_mode)` PK + OHLCV + raw units + source_tr + fetched_at_utc.
  - `candle_source_meta` — 단위/소스 메타 (정규화는 read layer로 위임).
  - `symbol_sync_state` — 종목별 first/latest date + last_full_refresh / last_incremental + needs_full_refresh flag.
- **PK**: `environment + market + shcode + bar_date + adjustment_mode` 5-key composite. raw / ADJ 분리 저장 (cross-contamination 방지).
- **단위 정책**: LS 원본 그대로 저장 (`volume_raw`, `value_raw`). 환산은 read layer.
- **동기화**: full refresh (최초/`needs_full_refresh=1`/명시) vs incremental (최근 N봉 overlap).

### 2.3 자세한 스키마 / 정책

→ [PATCH-003 §Storage Location, §Schema, §Sync Policy, §Resampling Policy 전부 참조](../todo/4.%20AGENTS-PATCH-003-daily-candle-cache.md). 본 SPEC은 그 디자인의 *수용 선언*이며, 추가 결정은 §2.4에만.

### 2.4 PATCH-003 Open Questions 답변 (v1.5 결정)

PATCH-003 §Open Questions의 6개 항목에 대한 v1.5 시점 답변:

1. **raw vs adjusted 정확한 LS 파라미터**: 구현 시 t8410 testbed에서 확정. v1.4에서 t8410은 raw / ADJ 옵션을 *어떻게* 받는지 명확치 않음 → 첫 마일스톤에 testbed 호출 1회 + 우리 측 비교.
2. **market 식별 (shcode만 받았을 때)**: 우리는 이미 `stocks_metadata` 캐시 (v0.5+)가 shcode → market mapping을 갖고 있음 (`ls_search_stock` 의존). 캔들 캐시는 그 결과를 *재사용*.
3. **ETF/ETN/ELW/미장 — 같은 테이블 or 분리?**: **같은 `candles_daily` 테이블 사용**. market 컬럼이 KOSPI/KOSDAQ/NASDAQ/NYSE/AMEX 등을 구분 → 자연스러운 partitioning. ELW는 v1.5 범위 외 (제외).
4. **Retention**: 옵션. 기본 20년, `CandleCacheOptions.RetentionYears`로 환경변수 조정 가능. 사용자 디스크 부담 → 기본 20년 ≈ 종목당 5000봉 × 평균 100바이트 ≈ 500KB. 2500종목 × 500KB = **약 1.2GB**. 디스크 부담 큰 사용자는 단축 가능.
5. **클리어/리빌드 도구**: ✅ `ls_candle_cache_admin(action="clear"|"rebuild"|"status", shcode?)` 신규 도구 1개로 묶음. v1.5에서 노출 (all profile only — 캘리브레이션 도구라 standard 프로파일 밖).
6. **Opt-in vs default-on**: **default-on**. 사용자가 disk 부담 시 `LS_CANDLE_CACHE=off` 환경변수로 끄기. cache miss 시 자연 fallback이므로 비활성화 시에도 동작은 동일.

### 2.5 도구 통합

`ls_get_chart` / `ls_reframe_chart` / `ls_add_indicator` 셋이 영향. PATCH-003 §API Integration Sketch 그대로:

```
1. 요청 parse (period/count/from/to/indicators/adjustment).
2. 필요 일봉 범위 계산 (지표 warm-up 포함).
3. candles_daily 읽기 → coverage 확인.
4. miss/old → t8410 fetch + upsert.
5. 일봉 또는 resampled 빌드.
6. 지표 계산 (warm-up 포함).
7. display count로 trim.
8. context / Plotly chart 빌드.
```

`BuildFrameAsync`(이미 v0.9에서 단일 진입점)를 cache-aware로 변경. single-timeframe / multi-timeframe 양쪽 공유.

### 2.6 도구 표면 영향

- **변경**: `ls_get_chart` / `ls_reframe_chart` / `ls_add_indicator`는 내부 동작만 변경 (description 변동 minimal, parameter schema 불변).
- **신규**: `ls_candle_cache_admin` (all profile only) — 캐시 status / clear / rebuild.
- standard 표면 변동 없음 (40 그대로). all 프로파일 43 → **44**.

### 2.7 테스트 전략

- **단위(mock)**: PATCH-003 §Implementation Checklist 6번 항목 모두.
- **통합**: 메모리 SQLite로 schema 생성 → upsert → read → overlap refresh → needs_full_refresh 표시.
- **resampling**: 일봉 fixture → 주봉/월봉 재계산 → 직접 t8410 월봉 응답과 일치 검증 (within tolerance).
- **회귀**: 기존 ls_get_chart 테스트 그대로 통과 + cache hit 경로 별도 테스트.

추가 테스트 수 ≈ **30-40개**. 단위 + 통합.

### 2.8 일정 추정

| 항목 | 시간 |
|---|---|
| 스키마 + Repository (Portfolio 패턴 복제) | 3h |
| Sync policy 구현 (full / incremental) | 2h |
| Resampling 일봉 → 주/월/년 | 2h |
| `BuildFrameAsync` cache-aware 통합 | 3h |
| `ls_candle_cache_admin` 도구 | 1h |
| 테스트 30-40개 | 4h |
| 문서 (SPEC §2 finalize + README hero 갱신) | 1h |
| **소계** | **~16h ≈ 2 work days** |

---

## 3. 슬라이스 B — Saved screener macros

### 3.1 무엇인가

v1.4 슬라이스 B (Q-Click)는 LS-curated 99 시그널을 노출하고 `ls_combine_screeners`로 AND/OR 묶음 실행을 가능케 했다. 그러나:

- 사용자가 *매일 같은 조합*을 쓸 때 매번 "MACD 0선 돌파 + 정배열 + 외인 3일 매수" 식 자연어 재구성 필요.
- HTS [1892] 자유조건의 *우리 측 보상*은 v1.4까지 절반만 — 시그널 카탈로그 노출은 됐지만 *사용자 자기 매크로 저장*은 아직.

v1.5 슬라이스 B는 그 절반을 채운다. 사용자가 자기 조합을 이름 붙여 저장하고, 한 줄로 재호출.

```
[1회 저장]
사용자: "이 조합을 '내 매수1'로 저장해줘"
모델:   ls_save_screener(name="내 매수1", signals=[6130, 6120, 6310], mode="and")
        → DB에 저장 + 즉시 1회 실행 preview 반환

[매일 실행]
사용자: "내 매수1 돌려봐"
모델:   ls_run_saved_screener(name="내 매수1")
        → DB lookup → ls_combine_screeners 내부 호출 → 결과
```

### 3.2 디자인

#### 3.2.1 SQLite 스키마

`%LOCALAPPDATA%\RedoxNet\LsOpenApi\portfolio.db` (기존)에 *새 테이블 2개*. 별도 DB 파일 X — saved screener는 user state 일부라 portfolio와 같은 위치 자연스러움.

```sql
-- One saved screener per (account or default-bucket, name).
-- name unique per bucket; case-insensitive comparison via collation.
CREATE TABLE IF NOT EXISTS saved_screeners (
    bucket           TEXT    NOT NULL DEFAULT 'default',  -- future: per-account split
    name             TEXT    NOT NULL,
    mode             TEXT    NOT NULL,                    -- 'and' | 'or'
    market           TEXT    NOT NULL DEFAULT 'all',      -- 'all' | 'kospi' | 'kosdaq'
    limit_default    INTEGER NOT NULL DEFAULT 20,
    note             TEXT    NULL,
    created_at_utc   TEXT    NOT NULL,
    updated_at_utc   TEXT    NOT NULL,
    last_run_at_utc  TEXT    NULL,
    last_match_count INTEGER NULL,
    PRIMARY KEY (bucket, name COLLATE NOCASE)
) WITHOUT ROWID;

-- One row per signal in a saved screener. order_index preserves user intent.
-- saved_name + id_at_save lets us detect catalog drift (LS renames or removes
-- a signal) at execution time.
CREATE TABLE IF NOT EXISTS saved_screener_signals (
    bucket       TEXT    NOT NULL,
    name         TEXT    NOT NULL,
    order_index  INTEGER NOT NULL,
    signal_id    TEXT    NOT NULL,        -- e.g. "6116"
    name_at_save TEXT    NOT NULL,        -- e.g. "이평 골든크로스(5,20)" snapshot
    saved_at_utc TEXT    NOT NULL,
    PRIMARY KEY (bucket, name COLLATE NOCASE, order_index),
    FOREIGN KEY (bucket, name) REFERENCES saved_screeners (bucket, name) ON DELETE CASCADE
) WITHOUT ROWID;
```

`bucket` 컬럼은 v1.5 시점에는 `'default'` 고정. 미래에 *계정별 분리* (예: 모의 vs 실서버)나 *사용자 간 export bundle 식별*에 활용.

#### 3.2.2 Catalog drift detection

저장 시 `signal_id` + `name_at_save`(스냅샷) + `saved_at_utc` 함께 저장. 실행 시 *현재 LS 카탈로그 캐시*와 비교:

| 변화 | 동작 |
|---|---|
| ID 동일, name 동일 | 정상 — 실행 |
| ID 동일, name 변경 (LS 측 rename) | warning + 실행. 응답에 `signals_drift: [{ id, name_at_save, name_now }]` 노출 |
| ID 없음 (LS가 시그널 삭제) | error + suggest update. `ls_run_saved_screener`가 부분 실행 또는 거부 (사용자 선택) |

이게 매크로의 *진짜 robustness*. v1.4에서 LS 카탈로그가 6001-6412 안정이지만 *언제든 변할 수 있으니* 우리 측 방어.

### 3.3 도구 설계 (5개 신규)

#### `ls_save_screener`
조합을 저장하고 *1회 즉시 실행 preview*를 반환 (검증 효과).

```
in: {
  name: string,                              // unique per bucket
  signals: [string],                         // 2-8 entries (combine 정책 그대로)
  mode: "and" | "or",
  market: "all" | "kospi" | "kosdaq",
  limit: int = 20,
  note: string?,                             // optional human description
  overwrite: bool = false,                   // existing name 있을 때 정책
}
out: {
  saved: { name, mode, market, signals_resolved: [...] },
  preview: { count, total_in_combination, results: [...] },  // ls_combine_screeners 1회 실행 결과
  source_tr: "t1825 x N (preview)"
}
```

#### `ls_list_saved_screeners`
사용자 매크로 목록.

```
out: {
  count: int,
  results: [
    { name, mode, market, signals_resolved: [...], note, created_at, updated_at, last_run_at, last_match_count }
  ]
}
```

#### `ls_run_saved_screener`
매크로 실행 — 내부적으로 `ls_combine_screeners` 호출.

```
in: { name: string, limit_override?: int, market_override?: string }
out: {
  screener: { name, mode, market, signals_resolved: [...] },
  // ls_combine_screeners와 동일한 envelope
  count, total_in_combination, data_as_of, query_date_resolution, results,
  signals_drift: [...]?  // catalog drift 발생 시
}
```

`limit_override` / `market_override`로 *저장된 default 위에 일회성 변형* 가능.

#### `ls_delete_saved_screener`
삭제 또는 rename.

```
in: { name: string, rename_to?: string }
out: { name, action: "deleted" | "renamed", new_name?: string }
```

#### `ls_export_saved_screeners` / `ls_import_saved_screeners`
Portfolio_io 패턴 그대로. JSON 파일로 사용자 매크로 백업/공유.

```
ls_export_saved_screeners(path?) → JSON dump (또는 stdout)
ls_import_saved_screeners(path, mode="merge"|"replace") → 가져오기
```

### 3.4 자연어 흐름 (E2E 시나리오)

```
[저장]
"MACD 0선 돌파 + 정배열 + 외인 3일 매수 조합을 '내 매수1'로 저장해줘"
  → 모델이 카탈로그에서 ID 확인 (6130, 6120, 6310)
  → ls_save_screener(name="내 매수1", signals=[6130,6120,6310], mode="and")
  → DB 저장 + preview 결과 (예: 0개) + "저장 완료"

[목록]
"내 매크로 뭐 있어?"
  → ls_list_saved_screeners()
  → 사용자 매크로 목록 + 각각 last_run_at

[실행]
"내 매수1 돌려봐"
  → ls_run_saved_screener("내 매수1")
  → 결과 + last_run_at 갱신

[변형]
"내 매수1 코스닥만 돌려봐"
  → ls_run_saved_screener("내 매수1", market_override="kosdaq")

[공유]
"내 매크로 백업해줘"
  → ls_export_saved_screeners() → JSON
사용자가 친구에게 보냄
"이 매크로 가져와줘"
  → ls_import_saved_screeners(path, mode="merge")

[catalog drift]
LS가 시그널 6310 이름을 "외인 3일연속 순매수"에서 "외국인 3일연속 순매수"로 변경했다면:
  → ls_run_saved_screener("내 매수1")
  → 실행 + warning: "signals_drift: [{ id: 6310, name_at_save: '외인...', name_now: '외국인...' }]"
  → 모델: "원래 저장하셨을 때는 '외인 3일연속 순매수'였는데 LS가 이름을 '외국인 3일연속 순매수'로 바꿨네요. 같은 시그널입니다."
```

### 3.5 도구 표면 영향

| 프로파일 | v1.4 | v1.5 (제안) |
|---|---|---|
| `standard` | 40 | **45** (+5: save / list / run / delete / export+import 통합 1?) |
| `all` | 43 | **49** (+5 + slice A의 cache_admin 1) |

5개가 많지만 *patterned* (portfolio_io의 5 도구와 1:1 대응). 사용자가 portfolio 도구 안 만큼 자연스럽게 익힘.

대안: export / import를 단일 도구 `ls_screener_io(action="export"|"import", ...)`로 묶어서 +4. 또는 save / delete를 `ls_screener_edit`로 묶어 +3. 도구 단순성 vs description 단순성 trade-off. 첫 디자인은 *명시적 5개* 권장 (portfolio와 일관성 ↑).

### 3.6 일정 추정

| 항목 | 시간 |
|---|---|
| 스키마 + Repository (Portfolio 패턴 복제) | 2h |
| Catalog drift detection 로직 | 1.5h |
| 도구 5개 구현 | 4h |
| 테스트 (~25개) | 3h |
| ServerInstructions + SPEC | 1h |
| README hero / RELEASENOTES | 30분 |
| **소계** | **~12h ≈ 1.5 work days** |

---

## 4. 슬라이스 C — Chart payload host adaptation

### 4.1 문제

v1.2 MCP Apps capability negotiation으로 chart-emitting tool들은 호스트에 맞춰 페이로드를 변형한다 — Apps-capable(SEP-1865)은 iframe, AssistStudio는 structuredContent 직접, 그 외(Claude Desktop / Cowork class)는 TextOnly. 그러나 v1.4 E2E + Cowork 측 모델 분석에서 **TextOnly 호스트도 실제로는 structuredContent를 받아서 외부 visualize MCP로 routing**하고 있다는 것이 드러났다. 그리고 **그 routing 비용이 정확히 spec을 두 번 결제**(~3.4k tokens)한다.

```
[현재 Cowork 흐름]
LS server ──(Plotly spec ~1.7k tokens)──▶ Claude context ──(spec 복붙 ~1.7k)──▶ mcp__visualize__show_widget
                                              ↓
                                       총 ~3.4k tokens (입+출)
```

이건 v1.4-dev 11번째 commit (`60468b2`)의 ServerInstructions hint로 *routing*은 자동화했지만, *토큰 비용 자체*는 그대로다.

### 4.2 Cowork 측 분석이 제시한 해결책

Cowork 환경에서는 **`create_artifact` + `callMcpTool` callback 패턴**이 가능하다:

```html
<!-- 모델 출력 ~600 tokens 만으로 끝남 -->
<div id="c" style="height:520px"></div>
<script src="https://cdnjs.cloudflare.com/ajax/libs/plotly.js/2.27.0/plotly.min.js"></script>
<script>
  const r = await window.cowork.callMcpTool('ls_get_chart', { shcode: '005930', ... });
  const spec = r.structuredContent.chart.spec;  // ← 브라우저 안에서, 모델 컨텍스트 통과 X
  Plotly.newPlot('c', spec.data, spec.layout, { responsive: true });
</script>
```

**Spec이 model context를 안 거침** → ~700 tokens (vs 3,400). + 영속 artifact라 재접속 시 자동 fresh data.

### 4.3 디자인 — 세 sub-feature

#### C.1: 응답 페이로드에 `_meta.render_hints` 추가 (**Recommended**)

chart-emitting tool 응답에 *호스트가 해석할 수 있는 렌더 힌트* 박기.

```jsonc
{
  "structuredContent": { "chart": { "type": "plotly", "version": "5", "spec": { ... } } },
  "content": [{ "type": "text", "text": "..." }],
  "_meta": {
    "render_hints": {
      "preferred": "structuredContent.chart",   // Apps-capable host
      "fallback": {
        "kind": "artifact_callback",            // Cowork-class host
        "callback": {
          "tool": "ls_get_chart",
          "args": { "shcode": "005930", "period_type": "day", "include_chart": true }
        },
        "html_template": "<div id='c' style='height:520px'></div><script src='https://cdnjs.cloudflare.com/ajax/libs/plotly.js/2.27.0/plotly.min.js'></script><script>const r = await window.HOST.callMcpTool(CALLBACK.tool, CALLBACK.args); const s = r.structuredContent.chart.spec; Plotly.newPlot('c', s.data, s.layout, {responsive: true});</script>"
      }
    }
  }
}
```

호스트가 `_meta.render_hints`를 인식하는지에 따라:
- AssistStudio (native renderer): `preferred` 그대로 사용. fallback 무시.
- Cowork: `fallback.html_template` + `fallback.callback`을 자기 artifact 도구에 패스. 모델은 hint 보고 한 줄 wrapping만.
- Claude Desktop SEP-1865: 기존 iframe app 그대로.
- text-only host: 그대로 무시.

`html_template`은 *Cowork에 특화된 글로벌 매크로 이름*(`window.cowork.callMcpTool`)이 아니라 일반화된 `window.HOST` placeholder로 작성. 호스트별 매크로 매핑은 *호스트 측 책임* (또는 v1.6+에 host-specific template 후보).

#### C.2: PNG 폴백 (옵션)

`ls_get_chart(..., format: "spec" | "png" | "auto")`. PNG 모드:
- Server-side Plotly render → base64 PNG 인라인
- `content: [{ type: "image", data: base64 }]`로 응답
- 토큰 ~1.6k (이미지 토큰 고정)

**비용**: server-side Plotly render는 PuppeteerSharp(Chromium 헤드리스) 또는 Plotly.NET 의존성 필요. 패키지 크기 +50MB. 빌드 시간 +10초. 사용자 distribution 부담.

→ v1.5 범위 외 권고. v1.6+에서 *수요 검증*된 뒤 결정.

#### C.3: `summary_only` 일관화 (선택)

일부 도구는 이미 `summary_only` / `output_mode='analyze'` 등으로 *경량 응답* 모드를 갖고 있다. chart-emitting tool 전체에 *일관된 인자명*으로 노출:

| 현재 | v1.5 일관화 |
|---|---|
| `ls_get_industry_indices(mode="summary")` | `output_mode="summary"` (이미 있음) |
| `ls_get_chart(include_chart=false)` | `output_mode="analyze"` (이미 부분 지원) |
| `ls_get_etf_holdings`, `ls_get_program_trading` | 동일 패턴 도입 |

→ 작업량 적음(~2h). v1.5에 squeeze 가능. 또는 v1.5.1로 미루기.

### 4.4 도구 표면 영향

- **신규 도구**: 0개. 모든 변경은 응답 페이로드 / 옵션 인자.
- **standard / all 표면 변동 없음** (C에 한해).
- `_meta.render_hints`는 ToolSurfaceFreezeTests의 description 토큰 budget에 *전혀* 영향 없음 (응답 페이로드라 schema 측면 비공개).

### 4.5 테스트 전략

- **단위**: `RenderHintsBuilder.Build(toolName, args)` 헬퍼 → 기대 JSON 매칭.
- **회귀**: 기존 chart-emitting 도구 테스트가 응답에 새 `_meta.render_hints` 필드를 *허용*하도록 (강제는 안 함).
- **통합**: ServerInstructions가 모델에게 *언제 callback 패턴 사용할지* 안내 — 키워드 어셔션 추가 (`"artifact_callback"`, `"callback template"`).

### 4.6 일정 추정

| 항목 | 시간 |
|---|---|
| `_meta.render_hints` builder + chart 도구 6개 통합 | 3h |
| ServerInstructions hint 강화 (Cowork 캘리브레이션) | 30분 |
| `summary_only` 일관화 (선택) | 2h |
| 테스트 (~15개) | 2h |
| SPEC / README hero 갱신 | 30분 |
| **소계 (C.1만)** | **~6h** |
| **소계 (C.1 + C.3)** | **~8h** |

C.2 (PNG 폴백) 미포함.

---

## 5. 도구 표면 영향 (종합)

| 프로파일 | v1.4 | v1.5 (A only) | v1.5 (A + B) | v1.5 (A + B + C) |
|---|---|---|---|---|
| `standard` | 40 | 40 | **45** | **45** (C는 표면 영향 없음) |
| `all` | 43 | 44 | **49** | **49** |

`ToolSurfaceFreezeTests`는 (A+B 시) 45 / 49로 갱신. C 추가해도 동일.

---

## 5. 일정 종합

- 슬라이스 A (캔들 캐시): ~16h
- 슬라이스 B (saved screener): ~12h
- 슬라이스 C (chart payload adaptation, C.1만): ~6h
- 공통 (release prep, README, RELEASENOTES, csproj/server.json): 1h

**총 ≈ 35h ≈ 4-5 work days** (A + B + C.1).

대안 분리:
- v1.5.0 = A + C.1 (~22h) — 두 슬라이스 모두 *투명하게* 효과 발현(사용자 행동 변화 X). 자연스러운 묶음.
- v1.6.0 = B (~12h)

분리 권고 조건: B가 *사용자 매크로* 컨셉이라 v1.5에 들어가도 *사용자 행동 변화* 필요 (저장 → 실행 흐름 학습). A와 C는 *투명하게* 효과 (cache 자동 + chart 토큰 절약). 가치 결이 달라 분리해도 자연스러움.

**권고**: *기본은 A + B + C.1 묶음 v1.5.0*. 만약 v1.4 → v1.5 텀이 짧아야 한다면 **A + C.1 v1.5 + B v1.6**으로 분리.

---

## 6. 작업 순서 권장 (A + B + C.1 묶음 가정)

1. **킥오프** (30분): 본 SPEC 재확인 + §2.4 PATCH-003 Open Questions 답변 합의 + bucket 정책 합의 + §4.3 render_hints 스키마 합의.
2. **A 인프라** (3h): `CandleCacheRepository` + DI + schema bootstrap + 단위 테스트.
3. **A 동기화** (2h): full / incremental refresh + needs_full_refresh.
4. **A 통합** (3h): `BuildFrameAsync` cache-aware + ls_get_chart 회귀 통과.
5. **A 도구** (1h): `ls_candle_cache_admin`.
6. **A 테스트 합쳐서 통과** (1h).
7. **C.1 render_hints** (3h): `RenderHintsBuilder` + chart 도구 6개 통합 + 테스트 ~10개. *A 직후 자연스러움* — 캔들 캐시 통합 직후 chart payload 갱신.
8. **C.1 ServerInstructions** (30분): callback 패턴 hint 강화.
9. **C.1 회귀 + 신규 테스트** (1.5h).
10. **B 인프라** (2h): `SavedScreenerRepository` + DI + schema bootstrap + 단위 테스트.
11. **B drift detection** (1.5h).
12. **B 도구 5개** (4h).
13. **B 테스트** (3h).
14. **ServerInstructions / SPEC / README / RELEASENOTES** (1.5h).
15. **Release prep** (30분 사용자 commit).

각 단계 1-3시간 단위 — 컨텍스트 끊겨도 재개 비용 낮음.

---

## 7. 사용자 검증 — E2E 시나리오

릴리스 직전 호스트에서:

```
[Slice A]
"삼성전자 5년 일봉 차트" (1회)
  → cold cache miss → t8410 5000봉 fetch → upsert → display
  → next call: "삼성전자 1년 일봉" → cache hit (LS 호출 0회)
  → "삼성전자 월봉으로 바꿔줘" → cache의 일봉에서 resample (LS 호출 0회)

"여러 종목 차트 비교 (5개)" 식 사용
  → 첫 호출은 5회 fetch, 이후 같은 종목들은 모두 cache hit
  → 30분 사용 후 LS 호출량 80% 이상 감소 체감

[Slice B]
"MACD + 정배열 + 외인 3일 매수 조합을 '내 매수1'로 저장해줘"
  → ls_save_screener → "저장 완료, 현재 매칭 0개"

"내 매크로 뭐 있어?"
  → ls_list_saved_screeners → "내 매수1 (3 signals, AND, last_run: never)"

"내 매수1 돌려봐"
  → ls_run_saved_screener("내 매수1")
  → 매칭 종목 + last_run 갱신

"내 매수1을 코스닥만 돌려봐"
  → market_override="kosdaq" → 코스닥 매칭만

"내 매크로 백업"
  → ls_export_saved_screeners → JSON

[Catalog drift 시연 (인위적)]
(개발자가 SQLite에서 name_at_save를 다른 값으로 수정 → 실행 → drift 응답 확인)

[Slice C — Cowork 환경]
"삼성전자 일봉 보여줘"
  → ls_get_chart 호출 (호스트 cowork, native Plotly X)
  → 응답에 _meta.render_hints.fallback.html_template + callback args
  → 모델: render_hints 본 즉시 callback 패턴으로 artifact 생성 (~600 tokens 출력)
  → artifact 안 JS가 callMcpTool로 spec을 *직접* 받아 Plotly.newPlot
  → 차트 인라인 표시 + spec이 model context 통과 X (~700 tokens 총)
  → 다음 turn: "그 차트 어제와 비교"
  → 같은 artifact가 자동 fresh data 페치 (재호출 없이)

vs v1.4 흐름 (~3,400 tokens) 대비 ~77% 토큰 절약 + 영속성.
```

체이닝과 자연어 흐름이 *자동적으로* 자기 매크로 사용 흐름을 익히는지가 성공 기준.

---

## 8. Open Questions

1. **bucket 정책** — v1.5에서는 `'default'` 고정. 미래에 `LS_MARKET=real` / `virtual` 환경에 따라 별도 bucket? 또는 사용자 explicit `bucket` 인자? 결정 미룸.
2. **catalog drift 정책 — 부분 실행 vs 거부** — 시그널 일부가 삭제됐을 때 (a) 나머지로 실행 + warning, (b) 전체 거부 + suggest update. 첫 구현은 (b) 권장 (정확성). 사용자 피드백 후 (a)로 완화 가능.
3. **`ls_export_saved_screeners` 형식 — JSON vs YAML** — portfolio_io가 JSON. 일관성 위해 JSON. 확정.
4. **공유 시 ID 안정성** — 사용자 A의 export bundle을 사용자 B가 import 시, 둘 다 같은 LS 카탈로그를 보고 있다는 가정 — 안전. 다만 LS 카탈로그가 *시간 차이*로 변경됐다면 drift 발생. import 시점에 drift 검출 + warning.
5. **slice A의 retention 기본값** — 20년이 적절한가, 10년이 안전한가, 환경변수로 충분한가. 20년 권장 (장기 분석 가치 > 디스크 비용).
6. **slice A의 ETF/ETN 단위 정규화 시점** — read layer에서 환산할지, schema에 별도 컬럼으로 미리 저장할지. PATCH-003 디자인대로 *read layer 위임*. 단위 메타는 `candle_source_meta`에 보존.
7. **slice C의 `window.HOST` placeholder vs host-specific 매크로** — `html_template` 안에 `window.HOST.callMcpTool` 일반화로 쓸지, 또는 host별로 `window.cowork.callMcpTool` / `window.claude.callMcpTool` 등 분기. v1.5는 *일반화 placeholder + ServerInstructions에서 매핑 가이드* 권장. 다만 ServerInstructions가 모델에게 host 이름 알려야 하므로 결정 필요.
8. **slice C의 PNG 폴백 (C.2)** — 수요 검증 후 v1.6에 도입. PuppeteerSharp 의존성 +50MB.
9. **slice C의 `summary_only` 일관화 (C.3)** — v1.5 squeeze vs v1.5.1 패치. 작업량 작아서 v1.5 권장이지만 release 일정 압박 시 분리.

---

## 9. 릴리스 노트 초안 (참고)

```markdown
## v1.5.0

**Daily candle SQLite cache.** ls_get_chart, ls_reframe_chart, and
ls_add_indicator now read from a process-local SQLite candle cache
(`%LOCALAPPDATA%\RedoxNet\LsOpenApi\candles.db`) before falling back
to LS t8410. Indicator warm-up bars come from the cache; weekly /
monthly / yearly periods are resampled from cached daily bars so
all timeframes share the same source-of-truth. Cold session warms
up the cache; subsequent calls are typically zero-fetch. Disk usage
is ~500KB per symbol at the default 20-year retention; set
`LS_CANDLE_CACHE=off` to disable. New `ls_candle_cache_admin` tool
(all profile) covers status / clear / rebuild.

**Saved screener macros.** Five new tools — ls_save_screener,
ls_list_saved_screeners, ls_run_saved_screener,
ls_delete_saved_screener, ls_export_saved_screeners +
ls_import_saved_screeners — let the user name a combined Q-Click
condition (e.g. "MACD + 정배열 + 외인 3일연속 순매수") and re-run
it by name on subsequent days. Catalog drift detection (LS rename /
removal) surfaces via `signals_drift` in the response. Storage in
the existing portfolio.db. Export/import follows the portfolio_io
pattern for backup and sharing.

**Chart payload host adaptation.** Chart-emitting tools (ls_get_chart,
ls_reframe_chart, ls_add_indicator, ls_get_overseas_chart,
ls_get_etf_holdings, ls_get_program_trading) now ship a
`_meta.render_hints` envelope alongside the existing
`structuredContent.chart`. Hosts with a native Plotly renderer
(AssistStudio, SEP-1865 apps) ignore it; hosts without one
(Cowork-class, Claude Desktop with peer visualize MCP) read the
`html_template` + `callback` to mount a self-contained artifact that
re-fetches the chart spec via `callMcpTool` from within the browser
— so the spec never passes through the model's context. Empirical
token cost drops from ~3,400 to ~700 per chart on those hosts (~77%
savings) plus the artifact persists for fresh data on later turns.

Tool surface 40 → 45 standard (43 → 49 all). All additions are
non-breaking; existing tool signatures unchanged.
```

---

## 10. 참고

- 일봉 캐시 디자인 초안: [`todo/4. AGENTS-PATCH-003-daily-candle-cache.md`](../todo/4.%20AGENTS-PATCH-003-daily-candle-cache.md)
- Token-efficient chart payload (관련, v1.6+): [`todo/5. AGENTS-PATCH-004-token-efficient-chart-payloads.md`](../todo/5.%20AGENTS-PATCH-004-token-efficient-chart-payloads.md)
- HTS [1892] 자유조건 미노출 사실: [`LS-API-QUIRKS.md §4.3`](./LS-API-QUIRKS.md#43-q-click--씽큐스마트-is-ls-curated-not-user-authored-)
- Portfolio SQLite 패턴 (재사용 대상): `src/RedoxNet.Mcp.LsOpenApi/Portfolio/SqlitePortfolioRepository.cs`, `PortfolioServiceCollectionExtensions.cs`, `PortfolioIoModels.cs`
- v1.4 슬라이스 B 시그니처 (saved screener의 토대): [`SPEC-v1.4.md §3`](./SPEC-v1.4.md)
- 슬라이스 C 디자인 영감 출처: Cowork 호스트 측 LLM의 chart routing / 토큰 비용 분석 (v1.4-dev E2E 2026-05-25 기록). chart spec이 model context를 두 번 통과(~3.4k tokens)하는 *실증된 비용* + `create_artifact` + `callMcpTool` callback 패턴 제안. v1.4 commit `60468b2`에서 ServerInstructions hint로 routing은 자동화했지만, 본 슬라이스 C가 그 비용을 줄임.
- v1.2 MCP Apps capability negotiation (슬라이스 C의 base): [`SPEC-v1.2.md`](./SPEC-v1.2.md), [`ChartRenderingMode.cs`](../src/RedoxNet.Mcp.LsOpenApi/Apps/ChartRenderingMode.cs)
