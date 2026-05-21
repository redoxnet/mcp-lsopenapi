# SPEC: v0.10.0 — 마지막 0.x 마이너 (compression + foreign data + pagination + dataset cache)

- **상태**: Final — v0.10.0 출시 완료, v1.0.0에서 도구 표면 동결
- **작성일**: 2026-05-20 (`todo/SPEC-v0.9-tool-surface-compression.md` 초안 → compression SPEC → 본 종합 SPEC으로 확장)
- **대상 버전**: v0.10.0 — **마지막 0.x minor**, 이후 [v1.0.0](#10-v100-로드맵--open-questions)
- **작성자**: Jong Hyun + Claude
- **선행**: [SPEC-v0.9-response-shapes.md](./SPEC-v0.9-response-shapes.md)
- **범위**: post-v0.9.0 SPEC 백로그 4개 묶음을 **한 번의 breaking minor로 통합 청산** —
  ① tool-surface compression ② t1716 외국인 보유 데이터 ③ Phase 2 리스트 도구 정규화
  ④ Phase 3 dataset 핸들 캐시 일반화. v0.10.0을 마지막 0.x로 닫고 v1.0.0(안정화)로 간다.

## 1. 컨텍스트 & 범위

### 1.1 출발점 — v0.9.0 완료 상태

v0.9.0(2026-05-20 출시, tag `v0.9.0`)이 끝낸 것: 응답-shape Phase 1(`index_history` B / `stock_info` A / `holdings_list` A) + `TokenEstimator`·`ResponseShape` 인프라 + ModelContextProtocol 1.3.0 + `net8.0;net10.0` + index_history verbosity 강화 + t1102 정정. 도구 수는 **48개** 그대로(payload만 reshape).

### 1.2 v0.10.0가 청산하는 백로그

| 묶음 | 출처 | 성격 |
|---|---|---|
| **A. Tool-surface compression** | (구) `SPEC-v0.10-tool-surface-compression.md` | **breaking** |
| **B. t1716 외국인 보유 데이터** | SPEC-v0.9 §4.3 "2단계 후속" | additive |
| **C. Phase 2 — 리스트 도구 정규화** | SPEC-v0.9 §7 Phase 2 | minor breaking (param rename) |
| **D. Phase 3 — dataset 핸들 캐시 일반화** | SPEC-v0.9 §7 Phase 3 | additive(외부) + 내부 리팩터 |

### 1.3 왜 한 릴리스로 묶는가

A(compression)는 도구 이름이 바뀌는 **breaking** 변경이라 어차피 사용자 마이그레이션이 1회 필요하다. B/C/D는 전부 순수 additive — 따로 비파괴 v0.9.x로 내면 사용자가 v0.9.x → v0.10.0 으로 **두 번** 마이그레이션하게 된다. v0.10.0이 breaking 릴리스이므로 additive 항목을 같이 태우면 마이그레이션 1회로 끝난다.

또한 v0.10.0은 **마지막 0.x 마이너**다. post-v0.9 SPEC 백로그를 여기서 전부 닫고, v1.0.0은 새 기능·breaking 없이 안정화·문서·테스트에 집중한다(→ §10).

### 1.4 비범위

- 실계좌 / 주문 / 실시간 WebSocket surface — v2.x.
- LS REST TR catalog **수 축소** — 비목표. t1716 **추가**는 본 SPEC 범위(B), catalog coverage 일반은 별개.
- 응답 payload shape 추가 재설계 — v0.9에서 Phase 1 완료, 본 SPEC은 C(pagination)·D(dataset)만 다룬다.

---

## 2. 작업 묶음 A — Tool-surface compression

> (구) `SPEC-v0.10-tool-surface-compression.md`의 본문. v0.10.0 종합 SPEC으로 흡수하며 절 번호만 재배치.

### 2.1 문제

사용자가 겪는 토큰 부담은 두 층이다.

1. **Result payload token** — 호출 *결과*가 큼. SPEC-v0.9가 담당(완료).
2. **Tool surface token / routing burden** — 호출 *전* `tools/list`에 48개 도구의 이름·설명·schema가 실리고 모델이 그중 하나를 골라야 함. **본 묶음이 담당.**

`ls_get_index_history`가 summary 기본값으로 9k → 300 tokens가 되어도 `tools/list`에는 여전히 48개가 노출된다 — 독립 문제.

### 2.2 정량 목표

| Surface | 현재 (v0.9.0) | v0.10.0 목표 |
|---|---:|---:|
| `standard` profile 노출 도구 | 48 | **~32** |
| `all` profile 노출 도구 | 48 | **~35** |
| `tools/list` token (standard) | 측정 전 | **현재 대비 30%+ 감소** |
| Portfolio 로컬 도구 | 20 | **7** |

**count는 proxy일 뿐, 진짜 게이트는 `tools/list` token delta + routing 정확도다.** dispatcher 하나가 N개 도구의 파라미터 union을 거대한 optional schema로 갖게 되면 도구 수는 줄어도 token은 거의 안 줄 수 있다. 따라서:

- **catalog profile-hiding(§2.5)은 확실한 승리** — 도구 3개가 통째로 사라진다.
- **dispatcher 병합(§2.6)은 각각 measured tools/list-token 승리를 게이트로** 건다. 병합 후 token이 안 줄면 그 병합은 보류한다.

모든 compression commit은 **tool count delta + tools/list token delta(cl100k_base)**를 함께 기록한다.

### 2.3 결정 원칙

- **Semantic tools 우선** — 자연어로 자주 부르는 read-only semantic tools는 `standard`에 남긴다 (`ls_search_stock`, `ls_get_quote`, `ls_get_chart`, `ls_get_stock_info`, `ls_get_top_stocks`, `ls_get_index_quote`, `ls_get_index_history`, `ls_holdings_list` 등).
- **Catalog tools는 `all`에서만** — `ls_search_tr`/`ls_describe_tr`/`ls_call_tr`은 일반 투자/분석 질문의 첫 라우팅 후보가 아니다. `standard`에서 숨기고 `all`에서 노출.
- **Dispatcher는 도메인 경계 안에서만** — `ls_dispatch(action,...)` 같은 과도한 통합은 라우팅 품질을 떨어뜨린다. 허용 경계: `ls_account`·`ls_watchlist`·`ls_holding`·`ls_watched_themes`·`ls_portfolio_io` (각각 단일 도메인 객체). 비권장: 모든 market-data를 `ls_market(action=...)` 하나로.
- **Write path 안전성** — destructive action은 `confirm=true` 유지, validation error envelope 유지, action별 required fields 검사, error는 `missing_required_for_action`처럼 모델이 자동 복구 가능한 shape.

### 2.4 Tool profiles

```
LS_TOOL_PROFILE = standard | all      (기본값: standard)
```

| Profile | 설명 | 예상 tools |
|---|---|---:|
| `standard` | 기본값. catalog 3종 제외 v0.10.0 전체 표면. | ~32 |
| `all` | catalog 3종 포함 v0.10.0 전체 표면. | ~35 |

**중요**: `all`은 "v0.8/v0.9 호환"이 *아니다*. dispatcher 병합(§2.6)은 profile과 무관한 영구 변경 — `all`로도 병합 전 이름은 복원되지 않는다. profile 축은 **catalog 3종 노출 여부만** 제어한다.

`core`/`portfolio`/`catalog` 추가 subset 프로파일은 v0.10.0 비범위 — v1.0.0 안정화 정책상 **`standard`/`all` 2개로 영구 고정**(→ §10).

**구현 위치**: 서버는 `Program.cs`에서 `WithToolsFromAssembly()`로 모든 `[McpServerTool]`을 등록하고 이미 `WithRequestFilters(filters => filters.AddListToolsFilter(...))` 파이프라인을 쓴다(`SchemaNormalizer` + `UiResources.PatchToolMeta...` 2-pass, 확인됨). 같은 파이프라인에 profile filter를 추가해 `standard`에서 catalog 3종을 `tools/list` 응답에서 제거한다.

숨긴 도구를 `tools/call`로 직접 호출 시: 기본은 내부 호출 가능. 옵션 `LS_TOOL_PROFILE_STRICT=true`면 `ToolNotAvailableInProfile` error.

### 2.5 Catalog tools — profile-gated (확실한 승리)

| 현재 | 변경 | standard delta |
|---|---|---:|
| `ls_search_tr`, `ls_describe_tr`, `ls_call_tr` | `standard`에서 숨김, `all`에서 노출 | −3 |

Risk: 모델이 모르는 신규 TR을 `standard`에서 직접 찾는 능력이 약해짐 → README/오류 메시지에 `LS_TOOL_PROFILE=all` 안내 필요.

### 2.6 Domain dispatchers

각 dispatcher는 독립 commit + **tool-count delta + tools/list token delta** 기록. 병합 후 token이 안 줄면 그 단계는 보류(§2.2).

| § | 현재 | 변경 | Delta |
|---|---|---|---:|
| 2.6.1 Account | `ls_accounts_list`, `ls_account_upsert`, `ls_account_remove` | `ls_account(action="list"\|"upsert"\|"remove")` | −2 |
| 2.6.2 Watchlist | `ls_watchlist_group_create`, `_group_delete`, `_add`, `_remove`, `_list` | `ls_watchlist(action="list"\|"add"\|"remove"\|"group_upsert"\|"group_delete")` | −4 |
| 2.6.3 Watched themes | `ls_watched_themes_add`, `_remove`, `_list` | `ls_watched_themes(action="list"\|"add"\|"remove")` | −2 |
| 2.6.4 Portfolio I/O | `ls_portfolio_export`, `ls_portfolio_import` | `ls_portfolio_io(action="export"\|"import")` | −1 |
| 2.6.5 Holdings write (F1) | `ls_holdings_set`, `_buy`, `_sell`, `_remove`, `_corporate_action` | `ls_holding(action="set"\|"buy"\|"sell"\|"remove"\|"corporate_action")` | −4 |

- **Account**: `remove`는 `confirm=true` 유지. broker rename은 `upsert` action이 흡수.
- **Watchlist**: 기존 `scope="groups"`는 `action="list", scope="groups"`로 유지.
- **Watched themes**: watchlist와 분리 유지 — theme code(tmcode) ≠ stock shcode.
- **Portfolio I/O**: `import mode="replace"`는 `confirm=true` 유지.
- **Holdings (F1 conservative split)**: read path `ls_holdings_list`는 "내 보유 보여줘" 최빈 intent라 명시 이름 유지(+ v0.9.0 reshape 그대로). write path 5종만 `ls_holding`으로 접는다. full dispatcher F2 안은 기각.
- `ls_stocks_refresh_metadata`는 심볼/캐시 대상이라 어느 dispatcher 도메인에도 안 맞음 → **standalone 유지** (포트폴리오 7개 중 1개).
- **Quote tools 비범위**: `ls_get_quote`/`ls_get_multi_quote`는 응답 shape가 다르고 `shcode`/`shcodes` 혼동 위험 — v0.10.0 유지.

---

## 3. 작업 묶음 B — t1716 외국인 보유 데이터

### 3.1 배경

v0.9.0의 t1102 정정 패스가 `ls_get_stock_info`에서 가짜 `foreign` 섹션을 제거했다(t1102엔 외국인 보유 데이터가 없음 — `abscnt`=유동주식수 오매핑이었음). 진짜 외국인 **보유 잔량**은 **t1716**(`/stock/frgr-itt`)에 있다.

**흐름(flow) vs 잔량(level) 구분이 핵심**:
- 일별 외국인 *순매수*(flow) → 이미 `ls_get_investor_flow`(t1702)가 처리.
- 외국인 *보유 잔량*(level) — `fsc_listing`(보유주식수)·`fsc_sjrate`(소진율) — **현재 어떤 도구로도 답 못 함.** 이게 v0.9.0이 떨어낸 `foreign` 섹션의 진짜 대체물이다.

t1716이 t1702와 겹치는 부분(일별 외인/기관 순매수)은 재구현하지 않는다. t1716의 고유 가치는 `fsc_listing`/`fsc_sjrate` 두 필드뿐.

### 3.2 t1716 카탈로그 등록

`src/RedoxNet.LsOpenApi.Core/Catalog/TrCatalog.json`(hand-maintained embedded resource — builder는 stub)에 t1716 엔트리를 추가한다. 기존 t1702 엔트리 shape를 따른다.

- 도메인: `/stock/frgr-itt`, POST, `t1716InBlock`(9 필드) / `t1716OutBlock`(Object Array, 16 필드).
- 원천 스펙: `todo/t1702, t1716, t1717.txt`.
- 효과: `all` profile에서 `ls_describe_tr t1716` / `ls_call_tr t1716` 접근 가능.
- t1717(단가 변형)은 본 SPEC 범위 아님 — 추가하지 않는다.

### 3.3 `ls_get_stock_info` — `foreign` 섹션 (6섹션 복귀)

`sections`에 6번째 값 `"foreign"`을 추가한다. **opt-in** — 기본값 `["snapshot","fundamentals"]`은 변함없다.

```
ls_get_stock_info(
    shcode,
    sections?: ("snapshot"|"fundamentals"|"periods"|"brokers"|"flags"|"foreign")[]
            = ["snapshot","fundamentals"]
)
```

`foreign` 섹션이 선택될 때만 stock_info가 **t1102 + t1716 두 번** 호출한다(기본 경로는 1회 그대로 — 추가 비용 없음). t1716 호출 파라미터:

```
gubun="0"(일간), todt=오늘, fromdt=오늘-약 10영업일(패딩),
prapp=0, prgubun="0", orggubun="0", frggubun="0", exchgubun="U"
```

응답에서 가장 최근 행(`t1716OutBlock[0]`)을 잔량 스냅샷으로 쓴다.

### 3.4 `foreign` 섹션 응답 shape

```json
{
  "foreign": {
    "as_of": "20260519",
    "held_shares": 3134788284,
    "ownership_percent": 52.34,
    "exhaustion_rate_percent": 52.51
  }
}
```

| 필드 | 출처 | 의미 |
|---|---|---|
| `as_of` | t1716 `date` | 잔량 기준일 |
| `held_shares` | t1716 `fsc_listing` | 금감원 외인 보유주식수 (주) |
| `ownership_percent` | **derived** | 외국인 지분율 = `held_shares / (listing × 1000) × 100` |
| `exhaustion_rate_percent` | t1716 `fsc_sjrate` (직접 %) | 외국인 소진율 |

- **derived 지분율**: `listing`은 t1102 `fundamentals.capital.shares_in_thousands`(천주). `listing`이 0/누락이면 `ownership_percent`는 `null`. — 이 계산이 stock_info를 단순 passthrough가 아닌 **derived metric provider**로 만든다.
- **`exhaustion_rate_percent` — normalized 단일 값**: t1716 `fsc_sjrate`는 직접 퍼센트 값이다(LS catalog `6.2` 포맷 — 소수 2자리, 예 "48.21" = 48.21%). LS 문서의 canned 샘플 "5251.00"은 mis-scaled placeholder였고 실 API는 퍼센트를 그대로 보낸다(2026-05-20 E2E, 005930) — 따라서 `÷100` 없이 raw를 그대로 emit한다. raw를 병기하지 않고 정규화 단일 값만 내보내는 건 코드베이스 관례(`diff→change_percent`)와 일치하며, raw escape hatch는 `ls_call_tr`.
- t1716 행이 비면(거래 정지 등) `foreign` 섹션은 필드를 `null`로 채우고 `sections_shown`에는 그대로 echo.

### 3.5 데이터 주의 — E2E 검증 항목

- `fsc_sjrate` 스케일 — **E2E로 정정(2026-05-20)**: 이전 세션의 "÷100" 가정은 LS 문서 canned 샘플("5251.00")에 오도된 것이었다. 실 t1716(005930)은 `fsc_sjrate`를 직접 퍼센트(≈48%)로 보낸다 → 구현에서 `÷100` 제거. catalog `6.2` 포맷(소수 2자리)과도 일치.
- testbed 샘플은 정적(canned doc sample)이라 값 진실 검증에 못 쓴다 — shape 핀 용도만.

---

## 4. 작업 묶음 C — Phase 2 리스트 도구 정규화

SPEC-v0.9 §4.5 / §7 Phase 2 이관. **2026-05-20 구현 재평가**: 7개 대상 도구를 전부
정독한 결과 forward `cursor`는 도구마다 5가지 다른 연속 메커니즘(in-process offset /
LS `idx` / `cts_shcode` / `shcode`-echo / 헤더 `tr_cont_key`)을 가지며,
`ls_get_market_warnings`는 멀티-kind fan-out이라 단일 cursor가 의미적으로 깨진다.
모든 대상 도구가 이미 내부 multi-page로 cap(100~200)을 채우므로 forward cursor의
실가치는 낮다 — §5.4(fundamentals_rank dataset_id 제외)와 동일한 판단.

→ Phase 2는 pagination의 **싸고 가치 있는 절반만** 한다: **`limit` 파라미터 통일 +
`total_available` emit**. forward `cursor` / `next_cursor`는 도입하지 않는다.

### 4.1 `limit` 파라미터 통일

리스트성 도구의 행수 제한 파라미터를 전부 `limit`으로 통일한다.

| 도구 | 현재 | v0.10.0 |
|---|---|---|
| `ls_search_stock` | `limit` | `limit` (변경 없음) |
| `ls_get_high_low_stocks` | `top_n` | `limit` |
| `ls_get_top_stocks` | `top_n` | `limit` |
| `ls_get_fundamentals_rank` | `count` | `limit` |
| `ls_get_industry_stocks` | `top_n` | `limit` |
| `ls_get_theme_stocks` | `top_n` | `limit` |
| `ls_get_market_warnings` | (없음 — 무제한) | `limit` 신설 (기본 50, 1~200) |

MCP 도구 소비자는 LLM이 매 호출마다 live schema를 읽으므로 파라미터 rename의 실질
breaking은 약하다 — 그래도 §9 migration matrix에 명시한다.

### 4.2 `total_available` emit

응답이 "전체 중 일부"임을 caller에게 알리도록, 도구가 싸게 알 수 있을 때 전체 건수를
`total_available: int`로 emit한다(모르면 omit).

- `ls_search_stock` — 필터된 전체 매치 수(전체 유니버스를 어차피 fetch하므로 무비용).
- `ls_get_fundamentals_rank` — t3341 `cnt` (이미 emit 중).
- `ls_get_market_warnings` — `limit` 적용 전 전체 행 수.
- `ls_get_theme_stocks` — 이미 `theme.stock_count`로 전체 수 노출 → 중복 emit 안 함.
- `ls_get_high_low_stocks` / `_top_stocks` / `_industry_stocks` — TR이 전체 수를 주지
  않음 → `total_available` 생략. `count` + `limit` echo로 충분.

---

## 5. 작업 묶음 D — Phase 3 dataset 핸들 캐시 일반화

SPEC-v0.9 §4.6 / §5.4 / §7 Phase 3 이관. **본 SPEC에서 가장 위험·논쟁적인 묶음** — 아래 §5.4는 사용자 리뷰 포인트.

### 5.1 현 상태

`DatasetHandleCache`(`src/RedoxNet.Mcp.LsOpenApi/Tools/DatasetHandleCache.cs`)는 **chart 전용**이다 — `ChartDataset`(Shcode + Candle frames + indicators + ChartContext)만 저장. `ls_get_chart` / `ls_add_indicator` / `ls_reframe_chart`가 사용. 캐시 정책: count-LRU(`MaxDatasets=16`) + per-dataset 5MB 캡. **시간 TTL 없음**(SPEC-v0.9 §5.4의 "60분 TTL 가정"은 실제 코드와 불일치 — 정정: count-LRU 유지, 시간 TTL 도입 안 함).

### 5.2 일반화 설계 — kind-tagged 핸들 캐시

`DatasetHandleCache`를 payload 타입에 무관한 kind-tagged 저장소로 리팩터:

- `Add(string kind, object payload)` → opaque handle. `TryGet<T>(string id, out T?)` — kind/타입 불일치 시 false.
- 공유 인프라(handle 생성, count-LRU, 5MB 캡, lock)는 그대로. payload 타입만 다형.
- `kind="chart"` → 기존 `ChartDataset`. chart 3종 도구는 `Add("chart", ...)` / `TryGet<ChartDataset>(...)`로 호출부만 변경 — **기존 chart 테스트(`GetChartToolDatasetHandleTests` 등)는 전부 green 유지가 게이트.**

### 5.3 신규 consumer — `ls_get_index_history` export mode

```
ls_get_index_history(..., output_mode?: "summary" | "export" = "summary")
```

- `output_mode="summary"`(기본): 현 동작 — `verbosity`가 응답 shape 결정.
- `output_mode="export"`: 전체 봉 시계열을 `kind="index_history"` dataset으로 캐시하고 `dataset_id` + summary 블록만 반환. 5년+ 일봉처럼 `verbosity="full"`조차 거대한 경우의 C 패턴 해법.
- **drill**: `ls_get_index_history(dataset_id=..., from?, to?, recent_n?)` — 캐시된 봉을 추가 API 호출 없이 슬라이스. 자체 완결형 drill — chart의 indicator 파이프라인에 배선하지 않는다.
- cache key: SPEC-v0.9 §5.4 A10 정책(canonical-json SHA-256, `output_mode` 제외) 재사용.

### 5.4 `ls_get_fundamentals_rank` dataset_id — v0.10.0 제외 (확정 2026-05-20)

SPEC-v0.9 §7 Phase 3 item 8은 fundamentals_rank를 dataset_id 후보로 들었으나 **v0.10.0에서 제외**한다 (사용자 확정 2026-05-20):

- 1000행 ranking을 random-access로 슬라이스하는 수요는 사변적. 현실 질의는 "PER 상위 20"(`limit=20`)이 대부분이고, `limit`(1~200, 내부 multi-page)이 이를 커버한다.
- SPEC-v0.9 §2.1 C 원칙: "C는 정말 무거운 1~2개 도구만". fundamentals_rank는 그 바를 넘지 않는다.

→ index_history만 C 패턴 적용. fundamentals_rank dataset_id는 §10 open question으로 v1.x 이후 재검토.

---

## 6. 신규/변경 도구 — v0.10.0 최종 표면 요약

| 변경 | 묶음 |
|---|---|
| catalog 3종 `standard`에서 숨김 (profile filter) | A |
| 5개 domain dispatcher 병합 (account/watchlist/watched_themes/portfolio_io/holding) | A |
| `ls_get_stock_info` — `foreign` 섹션 추가 (6섹션) | B |
| t1716 catalog 등록 | B |
| 7개 리스트 도구 — `limit` 통일 + `total_available` | C |
| `DatasetHandleCache` 일반화 + `ls_get_index_history` `output_mode` | D |

도구 *수*: `standard` 48 → ~32, `all` → ~35. 신규 semantic 도구는 0개(t1716은 stock_info 섹션으로 흡수) — compression 릴리스 일관성 유지.

---

## 7. 구현 순서

위험도·의존성 기준. 각 단계 독립 commit + 테스트 통과.

1. **t1716 catalog + `foreign` 섹션** (B) — additive·자체완결, t1102 후속 종결. E2E로 `fsc_sjrate` 스케일 1회 확인.
2. **Phase 2 리스트 정규화** (C) — `limit` 통일 + `total_available`, 7개 도구.
3. **Profile filter** (A §2.4–2.5) — request-filter 추가, baseline `tools/list` token 측정 + 두 profile budget pin.
4. **Domain dispatchers** (A §2.6) — account → watched_themes → portfolio_io → watchlist → holdings. 각 단계 token delta 게이트.
5. **Dataset cache 일반화 + index_history export** (D) — 가장 위험(chart 도구 영향). 마지막에 배치해 chart 테스트 green을 최종 게이트로.
6. **릴리스 준비** — csproj/`.mcp/server.json` 0.10.0, RELEASENOTES BREAKING 섹션 + §9 마이그레이션, README profile 안내.

> additive 묶음(1·2·5)을 먼저, breaking 묶음(3·4)을 표면 확정 직전에 둔다. 5는 외부적으론 additive지만 내부 리팩터 위험이 커서 맨 끝.

## 8. 측정 / 테스트

- **Tool-list token budget**: `tools/list` 필터를 in-process 구동 → tool-list JSON 직렬화 → cl100k_base 측정. 첫 compression commit이 48-tool baseline 기록 + 두 profile budget pin. `standard` ≤ baseline × 0.70.
- **Routing smoke tests**: profile별 기대 노출, catalog 3종 standard 부재/all 노출, 병합된 옛 이름 부재, dispatcher의 action별 누락 인자 구조화 validation error. 기존 `scripts/portfolio-smoke.py` 확장.
- **t1716/foreign**: stock_info `foreign` 섹션 fixture 테스트 + `ownership_percent` derived 계산 + `fsc_sjrate` 정규화 단위 테스트.
- **Phase 2**: 각 도구 `limit` 클램프 + `total_available` 정확도 테스트.
- **Phase 3**: kind-tagged 캐시 단위 테스트, index_history export→drill round-trip, **기존 chart 데이터셋 테스트 전부 green**.
- 모든 토큰 budget은 cl100k_base 실측으로 pin (SPEC-v0.9 §2.4 관례).

## 9. Migration matrix

v0.10.0는 의도적 BREAKING minor. breaking은 **묶음 A뿐** — B/C/D는 additive(기존 호출 그대로 동작).

| v0.9 tool | v0.10.0 | 비고 |
|---|---|---|
| `ls_search_tr` / `ls_describe_tr` / `ls_call_tr` | 이름 동일 | `standard`에서 숨김, `LS_TOOL_PROFILE=all`로 노출 |
| `ls_accounts_list` / `ls_account_upsert` / `ls_account_remove` | `ls_account(action=...)` | `remove`는 `confirm=true` |
| `ls_watchlist_*` (5종) | `ls_watchlist(action=...)` | |
| `ls_watched_themes_*` (3종) | `ls_watched_themes(action=...)` | |
| `ls_portfolio_export` / `_import` | `ls_portfolio_io(action=...)` | `import replace`는 `confirm=true` |
| `ls_holdings_{set,buy,sell,remove,corporate_action}` | `ls_holding(action=...)` | |
| `ls_holdings_list`, `ls_stocks_refresh_metadata` | 이름 동일 | 변경 없음 |
| `ls_get_stock_info` | 이름 동일 | `foreign` 섹션 추가(opt-in) — 기존 호출 영향 없음 |
| `ls_get_{high_low_stocks,top_stocks,industry_stocks,theme_stocks}` | 이름 동일 | 행수 파라미터 `top_n` → `limit` rename |
| `ls_get_fundamentals_rank` | 이름 동일 | 행수 파라미터 `count` → `limit` rename |
| `ls_get_market_warnings` | 이름 동일 | `limit` 파라미터 신설(기본 50) — 무제한 → 캡 |
| `ls_get_index_history` | 이름 동일 | `output_mode` 추가(기본 `summary`) — 기존 호출 영향 없음 |

접힌 옛 이름 직접 호출: 미등록이면 일반 MCP unknown-tool error, `LS_TOOL_PROFILE_STRICT=true`면 `ToolNotAvailableInProfile`. release notes에 친절한 마이그레이션 안내 필수.

## 10. v1.0.0 로드맵 + open questions

v0.10.0가 **마지막 0.x 마이너**다. v1.0.0은:

- **표면 동결** — 도구 표면·모델 노출 파라미터 이름·응답 shape를 v1.0.0에서 동결하고, reflection 기반 핀 테스트(`ToolSurfaceFreezeTests`)로 가드. 단, v0.10.0 `limit` 정규화가 누락한 `ls_get_etf_holdings`·`ls_get_industry_indices` 2개를 `top_n` → `limit`로 rename해 정규화를 완성 — 이 2건이 v1.0.0의 유일한 breaking.
- 안정화·문서·테스트 커버리지·E2E 정합에 집중.
- profile은 `standard`/`all` **2개로 영구 고정** — `core`/`portfolio`/`catalog` subset 프로파일은 도입하지 않음(수요 근거 없음, 안정성 우선).

**Open questions** (v0.10.0에서 닫거나 v1.x로 명시 이월):

1. ~~subset 프로파일~~ → 닫음: `standard`/`all` 고정.
2. profile 선택을 CLI arg(`--tool-profile`)로도 받을지 — v1.x, 환경변수로 v0.10.0 출시.
3. dispatcher description에 "이 도구는 X/Y/Z 대체" 마이그레이션 주석을 둘지 — release notes로 충분, 도구 description엔 미포함.
4. dispatcher 병합 중 measured token 승리가 없는 후보(§2.2 게이트) → 그 도구군은 naming만 정리하고 병합 보류.
5. **`ls_get_fundamentals_rank` dataset_id** (§5.4) — v0.10.0 제외, v1.x에서 실제 수요 측정 후 재검토.

## 11. 결정

- **v0.10.0** = 4개 묶음(A compression + B foreign + C cursor + D dataset cache)을 한 번의 breaking minor로. breaking은 A뿐.
- t1716 외국인 데이터는 `ls_get_stock_info` `foreign` 섹션으로 — 신규 도구 0개, derived 지분율 포함.
- Phase 3는 dataset 캐시 일반화 + index_history export로 한정, fundamentals_rank dataset_id 제외(§5.4).
- v0.10.0가 마지막 0.x — 이후 v1.0.0은 안정화 전용.
