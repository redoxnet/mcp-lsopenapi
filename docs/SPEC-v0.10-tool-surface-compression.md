# SPEC: v0.10.0 — Tool surface compression + routing economy

- **상태**: Approved
- **작성일**: 2026-05-20 (`todo/SPEC-v0.9-tool-surface-compression.md` 초안) · 리뷰/재작성: 2026-05-20
- **대상 버전**: v0.10.0 (단일 breaking minor)
- **작성자**: Jong Hyun + Codex
- **선행**: [SPEC-v0.9-response-shapes.md](./SPEC-v0.9-response-shapes.md)
- **범위**: MCP `tools/list`에 노출되는 도구 수, 도구 설명/schema 토큰, 모델 라우팅 부담을 줄인다.
- **비고**: 초안은 압축을 v0.9.1 / v0.9.2 / v0.10.0에 분산했으나, breaking 변경을
  0.x 패치에 넣지 않는다는 semver 규율(→ SPEC-v0.9 §5.1)에 따라 **전부 v0.10.0
  한 번의 breaking minor로 통합**한다. v0.9.0(응답-shape Phase 1)은 이미 완료.

## 1. 컨텍스트

v0.9.0 기준 도구 수는 **48개**다 (v0.9.0은 payload를 reshape했을 뿐 도구 수는 그대로). v0.6/v0.7에서 Tier 1/2 compression으로 일부 포트폴리오 변형 도구를 접었지만, 이후 시장 컨텍스트와 스크리너가 추가되며 soft target인 ~45개를 다시 초과했다.

`SPEC-v0.9-response-shapes.md`는 **도구 호출 결과 payload**를 줄이는 문서다. 하지만 사용자가 실제로 겪는 부담은 두 층이다.

1. **Result payload token**: 특정 도구 호출 결과가 너무 큼. Response-shape SPEC이 담당.
2. **Tool surface token / routing burden**: 호출 전 `tools/list`에 48개 도구의 이름·설명·JSON schema가 실리고 모델이 그중 하나를 골라야 함. 이 SPEC이 담당.

이 둘은 독립 문제다. `ls_get_index_history`가 summary 기본값으로 9k → 300 tokens가 되어도, `tools/list`에는 여전히 48개 도구가 노출된다.

## 2. 목표

### 2.1 정량 목표

v0.10.0에서 다음을 달성한다.

| Surface | 현재 (v0.9.0) | v0.10.0 목표 |
|---|---:|---:|
| `standard` profile 노출 도구 | 48 | **~32** |
| `all` profile 노출 도구 | 48 | **~35** (catalog tools 포함, dispatcher 병합 반영) |
| `tools/list` token (standard) | 측정 전 | **현재 대비 30%+ 감소** |
| Portfolio 로컬 도구 | 20 | **7** |

**count는 proxy일 뿐, 진짜 게이트는 `tools/list` token delta + routing 정확도다.** dispatcher 하나가 N개 도구의 파라미터 union을 거대한 optional schema로 갖게 되면 도구 수는 줄어도 `tools/list` token은 거의 안 줄 수 있다. 따라서:

- **catalog profile-hiding(§5.1)은 확실한 승리** — 도구 3개가 통째로 리스트에서 사라진다.
- **dispatcher 병합(§5.2–5.6)은 각각 measured tools/list-token 승리를 게이트로** 건다. 병합 후 token이 안 줄면 그 병합은 보류한다.

모든 compression commit은 **tool count delta + tools/list token delta(cl100k_base)**를 함께 기록한다.

### 2.2 비목표

- LS REST TR catalog 수를 줄이지 않는다. `ls_call_tr`로 접근 가능한 catalog coverage는 별도 문제다.
- Response payload shape는 이 SPEC의 주제가 아니다 (→ SPEC-v0.9).
- 실계좌/주문/실시간 WebSocket surface는 v2.x 범위이며, 본 SPEC의 압축 후보가 아니다.

## 3. 결정 원칙

### 3.1 Semantic tools 우선

사용자가 자연어로 가장 자주 부르는 read-only semantic tools는 `standard`에 남긴다 — `ls_search_stock`, `ls_get_quote`, `ls_get_chart`, `ls_get_stock_info`, `ls_get_top_stocks`, `ls_get_index_quote`, `ls_get_index_history`, `ls_holdings_list` 등.

### 3.2 Catalog tools는 `all` profile에서만

`ls_search_tr` / `ls_describe_tr` / `ls_call_tr`은 일반 투자/분석 질문의 첫 라우팅 후보로 상시 노출될 필요가 낮다. `standard`에서 숨기고 `all`에서 노출한다. 이 catalog 3종이 `standard`와 `all`의 **유일한 차이**다(아래 §4).

### 3.3 Dispatcher는 도메인 경계 안에서만

무조건 하나의 `ls_dispatch(action, ...)`로 합치지 않는다. 이름이 너무 일반적인 도구는 오히려 라우팅 품질을 떨어뜨린다.

허용되는 dispatcher 경계: `ls_account`, `ls_watchlist`, `ls_holding`, `ls_watched_themes`, `ls_portfolio_io` — 각각 단일 도메인 객체.
비권장: 모든 포트폴리오를 `ls_portfolio(action=...)` 하나로, 모든 market-data를 `ls_market(action=...)` 하나로 합치기.

### 3.4 Write path 안전성

압축 대상 중 write 성격이 있는 도구는 안전성을 해치면 안 된다. 로컬 포트폴리오 write는 실주문은 아니지만 사용자 데이터 변경이다. dispatcher로 합칠 때:

- destructive action은 `confirm=true` 유지
- validation error envelope 유지
- action별 required fields를 명확히 검사
- error가 `missing_required_for_action`처럼 모델이 자동 복구 가능한 shape를 갖도록 함

## 4. Tool profiles

### 4.1 v0.10.0가 ship하는 프로파일

```
LS_TOOL_PROFILE = standard | all      (기본값: standard)
```

| Profile | 설명 | 예상 tools |
|---|---|---:|
| `standard` | 기본값. catalog 3종을 제외한 v0.10.0 전체 표면. | ~32 |
| `all` | catalog 3종 포함 v0.10.0 전체 표면. | ~35 |

**중요**: `all`은 "v0.8/v0.9 호환"이 *아니다*. dispatcher 병합(§5.2–5.6)은 profile과 무관한 v0.10.0 영구 변경이므로, `all`로도 병합 전 도구 이름은 복원되지 않는다. profile 축은 **catalog 3종의 노출 여부만** 제어한다.

`core` / `portfolio` / `catalog` 같은 추가 subset 프로파일은 각자 별도의 subset 규칙 설계가 필요하고 현재 수요 근거가 없다 — **v0.10.0 비범위, §9 open question으로 보류**.

### 4.2 구현 위치

서버는 `Program.cs`에서 `WithToolsFromAssembly()`로 모든 `[McpServerTool]`을 등록하고, 이미 `WithRequestFilters(filters => filters.AddListToolsFilter(...))` 파이프라인을 사용한다(`Program.cs` 확인됨). 같은 파이프라인에 profile filter를 추가해 `standard`에서 catalog 3종을 `tools/list` 응답에서 제거한다.

`tools/list`에서 숨긴 도구를 `tools/call`로 직접 호출했을 때의 동작:
- 기본: 숨긴 도구도 내부적으로 호출 가능 (host가 정상적으로는 호출하지 않음).
- 옵션 `LS_TOOL_PROFILE_STRICT=true`: profile 밖 tool call에 `ToolNotAvailableInProfile` error 반환.

## 5. Compression candidates

### 5.1 Catalog tools — profile-gated (확실한 승리)

| 현재 | 변경 | standard delta |
|---|---|---:|
| `ls_search_tr`, `ls_describe_tr`, `ls_call_tr` | `standard`에서 숨김, `all`에서 노출 | −3 |

Rationale: 일반 투자/분석 질문에서는 semantic tool이 우선. catalog tools는 개발자 fallback으로 유용하지만 routing 후보 상시 노출 필요는 낮음.
Risk: 모델이 모르는 신규 TR을 직접 찾아 호출하는 능력이 `standard`에서 약해짐 → README/오류 메시지에 `LS_TOOL_PROFILE=all` 안내 필요.

### 5.2 Account dispatcher

| 현재 | 변경 | Delta |
|---|---|---:|
| `ls_accounts_list`, `ls_account_upsert`, `ls_account_remove` | `ls_account(action="list"\|"upsert"\|"remove")` | −2 |

Safety: `remove`는 `confirm=true` 유지. (`ls_broker_rename`은 이미 존재하지 않는 도구 — broker rename은 `ls_account_upsert`에 접혀 있으며 dispatcher에서도 `upsert` action이 그대로 흡수.)

### 5.3 Watchlist dispatcher

| 현재 | 변경 | Delta |
|---|---|---:|
| `ls_watchlist_group_create`, `ls_watchlist_group_delete`, `ls_watchlist_add`, `ls_watchlist_remove`, `ls_watchlist_list` | `ls_watchlist(action="list"\|"add"\|"remove"\|"group_upsert"\|"group_delete")` | −4 |

기존 `scope="groups"` 개념은 `action="list", scope="groups"`로 유지.

### 5.4 Watched themes dispatcher

| 현재 | 변경 | Delta |
|---|---|---:|
| `ls_watched_themes_add`, `ls_watched_themes_remove`, `ls_watched_themes_list` | `ls_watched_themes(action="list"\|"add"\|"remove")` | −2 |

watched themes는 watchlist와 분리 유지 — LS theme code(tmcode)와 stock shcode가 다른 도메인 객체.

### 5.5 Portfolio I/O dispatcher

| 현재 | 변경 | Delta |
|---|---|---:|
| `ls_portfolio_export`, `ls_portfolio_import` | `ls_portfolio_io(action="export"\|"import")` | −1 |

Safety: `import mode="replace"`는 `confirm=true` 유지.

### 5.6 Holdings — F1 (conservative split)

Holdings는 가장 큰 surface지만 가장 위험하다. read path는 명시 이름을 유지하고 write path만 접는다.

| 현재 | 변경 | Delta |
|---|---|---:|
| `ls_holdings_list` | 유지 (v0.9.0 response-shape 작업 반영 — themes_limit / include_*) | 0 |
| `ls_holdings_set`, `_buy`, `_sell`, `_remove`, `_corporate_action` | `ls_holding(action="set"\|"buy"\|"sell"\|"remove"\|"corporate_action")` | −4 |

read path `ls_holdings_list`는 "내 보유 보여줘" 같은 가장 흔한 intent라 generic 이름으로 숨기면 routing이 나빠진다. 또한 v0.9.0에서 막 reshape한 도구이므로 그대로 둔다 (full dispatcher F2 안은 기각).

### 5.7 `ls_stocks_refresh_metadata` — 독립 유지

theme/industry 캐시를 동기 refresh하는 도구. 특정 holding이 아니라 심볼/캐시를 대상으로 하므로 account/holding/watchlist 어느 dispatcher 도메인에도 자연스럽게 들어가지 않는다 → **standalone 유지**. (포트폴리오 도구 7개 중 1개.)

### 5.8 Quote tools — v0.10.0 비범위

`ls_get_quote`(1종목 10단계 호가)와 `ls_get_multi_quote`(최대 50종목 snapshot)는 응답 shape가 다르고, 통합 시 `shcode` vs `shcodes` 파라미터 혼동 위험이 있다. v0.10.0에서는 둘 다 유지. 후속 버전에서 `ls_get_quotes(shcodes, depth?)` 신설 + 측정 후 재검토.

## 6. v0.10.0 구현 순서

단일 릴리스 안의 내부 작업 순서 (response-shape와 충돌 적은 순):

1. **Profile filter** — `LS_TOOL_PROFILE=standard|all`, default `standard`. `tools/list` 필터로 catalog 3종 숨김. baseline `tools/list` token 측정 + 두 profile budget pin.
2. **Account dispatcher** (−2).
3. **Watched themes dispatcher** (−2).
4. **Portfolio I/O dispatcher** (−1).
5. **Watchlist dispatcher** (−4).
6. **Holdings write dispatcher F1** (−4).

각 dispatcher 단계는 독립 commit + tool-count delta + tools/list token delta 기록. 병합 후 token이 안 줄면 그 단계는 보류(§2.1).

예상 결과: `standard` 48 → ~32, `all` → ~35, 포트폴리오 20 → 7.

## 7. 측정

### 7.1 Tool-list token budget

`tools/list` 필터를 in-process로 구동해 결과 tool-list JSON을 직렬화하고 cl100k_base로 측정하는 테스트 지원을 추가한다. 첫 구현 commit이 현재 48-tool baseline을 기록하고 두 profile budget을 pin한다.

| Profile | Budget |
|---|---|
| `standard` | baseline(현재 48-tool) × 0.70 이하 |
| `all` | baseline 대비 감소 요구 없음 (dispatcher 병합분만큼 자연 감소) |

### 7.2 Routing smoke tests

각 profile에 대해:
- 기대 노출 도구가 `tools/list`에 나타남
- catalog 3종이 `standard`에선 빠지고 `all`에선 보임
- dispatcher로 접힌 옛 이름이 부재
- dispatcher 도구가 action별 누락 인자에 구조화된 validation error 반환

기존 `scripts/portfolio-smoke.py`(Tier 1/2 compression 체크 보유)를 Tier 3용으로 확장한다.

## 8. Migration matrix

v0.10.0는 의도적 BREAKING minor. 모든 변경이 한 릴리스에 들어간다.

| v0.9 tool | v0.10.0 | 비고 |
|---|---|---|
| `ls_search_tr` / `ls_describe_tr` / `ls_call_tr` | 이름 동일 | `standard`에서 숨김, `LS_TOOL_PROFILE=all`로 노출 |
| `ls_accounts_list` | `ls_account(action="list")` | |
| `ls_account_upsert` | `ls_account(action="upsert")` | |
| `ls_account_remove` | `ls_account(action="remove", confirm=true)` | |
| `ls_watchlist_*` (5종) | `ls_watchlist(action=...)` | |
| `ls_watched_themes_*` (3종) | `ls_watched_themes(action=...)` | |
| `ls_portfolio_export` / `_import` | `ls_portfolio_io(action=...)` | |
| `ls_holdings_{set,buy,sell,remove,corporate_action}` | `ls_holding(action=...)` | |
| `ls_holdings_list` | 이름 동일 | 변경 없음 |
| `ls_stocks_refresh_metadata` | 이름 동일 | 변경 없음 |

접힌 옛 이름을 직접 호출한 경우: 미등록이면 일반 MCP unknown-tool error, `LS_TOOL_PROFILE_STRICT=true`면 `ToolNotAvailableInProfile`. release notes에 친절한 마이그레이션 안내 필수.

## 9. Open questions

1. `core` / `portfolio` / `catalog` subset 프로파일을 v0.11+에서 추가할지, 아니면 `standard`/`all` 2개로 영구 고정할지.
2. profile 선택을 환경 변수 외에 CLI arg(`--tool-profile`)로도 받을지.
3. dispatcher 도구 description에 "이 도구는 X/Y/Z를 대체" 마이그레이션 주석을 v0.10.x 동안 넣을지.
4. dispatcher 병합 중 measured token 승리가 없는 후보가 나오면(§2.1 게이트) 그 도구군은 v0.10.0에서 제외할지, naming만 정리할지.

## 10. 결정

- **v0.9.0**: 응답-shape Phase 1 (완료). 추가 압축 없음.
- **v0.10.0**: 본 SPEC 전체를 한 번의 breaking minor로. profile filter + 5개 도메인 dispatcher. `standard` 기본 48 → ~32.

payload-shape breaking(v0.9.0)과 tool-surface breaking(v0.10.0)을 서로 다른 릴리스로 분리해, 한 릴리스에 두 종류의 breaking을 섞지 않는다.
