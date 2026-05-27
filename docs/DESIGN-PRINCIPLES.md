# Design Principles — mcp-lsopenapi

설계 결정의 *why*를 보존하는 문서. 새 기능을 추가하거나 기존 디자인을 의심할 때 *이미 한 번 거쳐간 분석*을 재발견하지 않도록 박아둔 원칙들. 구체적인 LS API 버그/quirks는 [LS-API-QUIRKS.md](./LS-API-QUIRKS.md)에, 특정 버전의 슬라이스 디자인은 [SPEC-vX.Y.md](./)에. 이 문서는 *그 둘 사이의 일관성*을 잡는 layer.

각 항목: **원칙 → 근거 → 어떻게 적용 → 재고 조건**.

Status legend: ✅ established (실제 디자인에 반영됨) · ⚠️ being adopted (다가오는 슬라이스에 반영 예정) · 💭 documented for future reference.

Last updated: 2026-05-27.

---

## 1. MCP-어시스턴트 패러다임

### 1.1 LLM은 episodic, MCP는 request-response ✅

**원칙**: MCP 서버의 사용 패턴은 *사용자가 채팅에서 요청 → LLM이 도구 호출 → 응답 → 사용자 대화 idle*. 도구 호출은 *순간*이고, 호출 사이엔 서버가 사용자에게 들리지 않음.

**근거**: 이게 streaming / push / 실시간 wrapper와 *근본적으로* 다른 패러다임. 자동매매 봇은 24/7 듣고 sub-second에 반응 — MCP 어시스턴트는 사용자 prompt가 ignition. 우리가 streaming infrastructure를 빌드해도 *듣는 LLM이 없는 동안의 데이터*는 그저 디스크에 쌓일 뿐이고, 사용자가 채팅 켰을 땐 *어차피* 최신 상태가 필요함.

**어떻게 적용**:
- 새 도구는 *snapshot-by-call*로 설계. 입력 → 응답 → 끝.
- 응답에 `_meta.data_as_of` 같은 timestamp 명시 — LLM이 신선도 판단 가능.
- "real-time", "live", "streaming" 요구가 들어오면 *어떤 latency가 실제로 필요한지* 먼저 묻기. "방금 체결됐어?" 정도면 REST poll로 충분.

**재고 조건**: 사용자 패턴이 "한 번 쿼리 후 5분 monitor" 같은 모니터링 모드로 변화 (production telemetry로 확인) — 그때만 검토. 단, 그 경우에도 cron/scheduler/외부 자동화에 위임이 적합할 가능성이 높음.

---

### 1.2 Polling이 streaming보다 *안전* ✅

**원칙**: 같은 데이터가 REST poll로 조회 가능하면, WebSocket push보다 REST poll을 *항상* 선호.

**근거**:
- **Push + downtime = confidently wrong context**. 서버가 transition event를 놓치면 영영 잘못된 상태로 응답. 예: 사이드카 발동 frame을 daemon이 놓치면 그날 내내 "정상 거래 중" 응답 — *적극적 misinformation*. REST poll은 *매 호출마다 fresh*, wrongness window가 *호출 빈도*로 한정됨.
- **Push는 stateful, polling은 stateless**. Stateful 인프라는 (a) 항상 켜져있어야 하고 (b) 죽으면 catch-up 메커니즘 필요하고 (c) 두 호스트가 동시에 listen 하면 fan-out 설계 필요. Polling은 다 회피.
- **Push는 수신 지점이 우리고, polling은 발신 지점이 우리**. LLM이 우리 코드를 호출하는 *그 순간*에만 우리가 작동하면 됨 — stdio MCP 본질과 일치.

**어떻게 적용**:
- 새 데이터 source를 wrapping할 때 REST endpoint 존재 여부를 *먼저* 확인. 있으면 그걸로 끝.
- WebSocket-only인 데이터는 "그 데이터가 정말 필요한가?" 질문 — 대부분 다른 방식으로 LLM의 요구 충족 가능.
- 예: order fill 이벤트 WebSocket vs `t0425` REST polling — 같은 사실, REST가 안전.

**재고 조건**:
- REST equivalent이 *전혀* 없는 push-only 신호 + 반복되는 *LLM use case* (자동화 use case 아님) + 사용자 demand가 강함 → 그때만 좁게 검토.
- MCP spec이 push primitive (resources/subscribe 같은) 를 1st-class 지원 + 호스트들이 광범위 구현 → 재평가.

---

### 1.3 Daemon-less, stdio, single-binary가 정체성 ✅

**원칙**: mcp-lsopenapi는 *호스트가 stdio로 spawn하는 단일 실행 파일*. 별도 service / sidecar / 명명 파이프 / Windows Service 인스톨러 / schtasks 등록 — 빌드하지 않음.

**근거**:
- 호스트 (Claude Desktop / Cursor / Codex / VS Code Chat) 의 자연스러운 lifecycle: 호스트 켜짐 → MCP spawn → 사용 → 호스트 꺼짐 → MCP 종료. 그 모델에 정확히 부합.
- 사용자 설치 cost = 0 (호스트 설정 한 줄). 별도 service 등록 / 권한 prompt / OS-specific 인스톨러는 진입 장벽.
- 다중 호스트 동시 사용 시 충돌 zero (각자 자기 stdio 인스턴스).

**어떻게 적용**:
- 새 기능 설계 시 "이게 long-running 프로세스를 요구하나?" 질문에 default = NO.
- Cron-like 동작이 필요하면 *우리 안에서* 풀지 말고 *외부 스케줄러*에 위임 (cron / Routines / Cowork 자동화 등).
- 사용자 환경의 영속 상태는 portfolio.db (사용자 자기 디스크의 단일 파일) 까지만.

**재고 조건**:
- 호스트 환경 자체가 변하면 (예: 호스트들이 MCP 서버를 systemd 같은 patterns로 직접 관리하는 표준이 정립) — 그때 재평가.
- 새로운 MCP transport (streamable HTTP 등) 가 *어시스턴트 use case에서* 진가가 입증되면 — 그땐 stdio 외 추가 transport 검토 가능. 단 그 자체로 *daemon 도입을 정당화하지는 않음* — transport 변화와 lifecycle 변화는 독립.

---

## 2. 데이터 출처 (source of truth) 원칙

### 2.1 외부 source가 들고 있는 것은 캐시하지 않음 ✅

**원칙**: LS 브로커가 source of truth로 들고 있는 데이터 (계좌 잔고 / 미체결 / 거래내역 / 수익률 등) 는 *우리가 캐시 보관하지 않음*. 모든 조회는 LS REST 호출.

**근거**:
- **Staleness = 실수**. 계좌 데이터는 money와 직결 — 우리 캐시가 LS와 다르면 사용자가 *잘못된 결정*. 다른 종류 데이터에선 staleness가 "약간 늦는다" 정도지만, 계좌에선 거짓말 수준.
- **LS가 이미 영구 보관**. 거래내역 (`CDPCQ04700`), 주문체결내역 (`CSPAQ13700`), 수익률 (`FOCCQ33600`) 모두 LS 측에서 수년 단위 유지. 우리가 또 복제할 이유 없음.
- **Sync 코드 부재 = 버그 부재**. 복제하면 sync 버그 / TTL 정책 / cleanup job / WAL pragma 등 ongoing 부담.

**어떻게 적용**:
- LS REST 도구 응답에 `_meta.source: "live"` 명시 — *우리 캐시 아님*을 LLM에 신호.
- LS 응답의 timestamp를 `_meta.data_as_of`로 echo — 사용자에게 *언제 시점의 진실*인지 명확.
- 성능 우려 (반복 호출): LS rate limit (~1-2/sec) 이 LLM 채팅 흐름에 충분.

**재고 조건**:
- LS가 *특정 데이터를 영구 보관 안 함*이 증명된 케이스 — 우리가 그 차이를 메우려면 캐시. 단 그 경우에도 영속화는 *우리 자기 데이터* (audit) 이지 LS 데이터 복제는 아님.
- 사용자 LS API rate limit 도달이 *실제 production에서* 관찰 → bounded TTL 캐시 검토. 현재 미관찰.

---

### 2.2 사용자가 들고 있는 것은 broker와 자동 동기화하지 않음 ✅

**원칙**: portfolio.db의 사용자 수동 입력 (watchlist, holdings, watched_sectors, themes) 은 LS 실계좌와 *자동 sync 하지 않음*. 두 출처는 *의도적으로 다를 수 있음*.

**근거**:
- 사용자가 portfolio.db에 기록하는 holdings는 *paper portfolio* (가설 테스트) 또는 *멀티 브로커 트래킹* (LS 외 키움 / 미래에셋 등) 용도. LS 실계좌와 일치할 이유 없음.
- 자동 sync을 시도하면 (a) "어느 방향으로 sync?" 정책 결정 필요 (b) 사용자 의도를 *덮어쓸* 위험 (c) ongoing sync 코드 유지 부담.

**어떻게 적용**:
- 도구 prefix로 출처 명시 ([§3.1](#31-도구-네이밍--출처-prefix)).
- ServerInstructions에 *두 출처가 다를 수 있음*을 명시 — LLM이 헷갈리지 않게.

**재고 조건**: 사용자가 명시적으로 "내 LS 잔고를 portfolio.db에 import해줘" 요청하는 패턴이 빈번 → manual import 도구 검토 (자동 sync 아님).

---

### 2.3 LLM이 이미 아는 것은 빌드하지 않음 ✅

**원칙**: LLM 훈련 데이터에 포함된 정적 지식 (한국 / 미국 공휴일, KRX 거래시간, 동시호가 시각, 시간외 단일가 시각 등) 을 우리가 데이터화하지 않음.

**근거**:
- LLM (Claude / GPT / Gemini) 의 훈련 데이터는 한국 공휴일, KRX 스케줄, 미국 holiday calendar 등 *reliable 하게 알고 있음*.
- 우리가 정적 캘린더 테이블을 만들면: (a) 매년 KRX 공휴일 발표마다 update (b) 대체공휴일 / 임시휴장 누락 위험 (c) LLM 지식보다 *덜 정확할* 가능성 (예: 주말만 아는 캘린더는 한국 공휴일 모름 — LLM은 알음).
- "오늘이 휴장일인가?" 같은 분류는 *LLM이 빈 데이터 + 자기 지식*으로 더 정확히 추정.

**어떻게 적용**:
- 새 도구가 정적 분류 (`weekend` / `holiday` / `pre_market` 같은) 를 제공하려는 충동 → 거부. 대신 *raw 사실*만 노출 (timestamp, 실제 데이터).
- 응답에 empty data를 정직히 반환. LLM이 "한글날이라 휴장" 추정 — 우리 라벨 없이도 잘 동작.
- ServerInstructions에 "장 운영 상태는 LLM의 시계 + 캘린더 지식으로 판단" 명시.

**재고 조건**:
- LLM 모델들이 *공통적으로* 특정 정보를 모르는 게 발견 (예: 매우 specific 한 KRX 임시휴장) + 그 정보가 사용자에게 *반복적으로* 가치 → 그때만 narrow 보강.
- 일반 정적 캘린더 (한글날 / 추석 등) 는 *영구적으로* 우리 영역 아님.

---

## 3. 도구 네이밍 컨벤션

### 3.1 도구 네이밍 — 출처 prefix ✅

**원칙**: 같은 개념 (예: holdings) 의 출처가 다르면 *prefix로 구분*. LLM이 헷갈리지 않게.

**현재 prefix**:

| Prefix | 출처 | 예시 |
|---|---|---|
| `ls_holding*`, `ls_portfolio_*`, `ls_watch*`, `ls_account(action=...)` | portfolio.db (사용자 수동) | `ls_holding`, `ls_holdings_list`, `ls_portfolio_io`, `ls_watchlist`, `ls_watched_themes` |
| `ls_account_*` | LS 실계좌 (REST live) | `ls_account_holdings`, `ls_account_balance`, `ls_account_orders`, ... |
| `ls_get_*` / `ls_search_*` / `ls_list_*` | LS 시장 데이터 (REST live) | `ls_get_quote`, `ls_get_chart`, `ls_search_stock`, `ls_list_screeners` |
| `ls_place_*` / `ls_amend_*` / `ls_cancel_*` | LS 발주 (side effect) | `ls_place_order`, `ls_amend_order`, `ls_cancel_order` |

ServerInstructions에 disambiguation 단락 항상 포함.

---

### 3.2 동사 prefix = side effect, 명사 prefix = read-only ✅

**원칙**: 도구 이름의 첫 단어가 *동사*면 호출이 *부작용*을 일으킴 (주문 발주 / 정정 / 취소). *명사*면 read-only.

**현재**:
- 동사 prefix: `ls_place_*`, `ls_amend_*`, `ls_cancel_*` — actuator
- 명사 prefix: `ls_get_*`, `ls_account_*`, `ls_holding*`, `ls_search_*`, `ls_list_*` — read-only

**근거**: LLM이 도구 이름만 보고도 *부작용 위험*을 직관적으로 인지. ServerInstructions의 confirm 패턴이 동사 prefix에만 적용되는 것도 이 컨벤션 기반.

**재고 조건**: 미래에 *동사+read* 같은 mixed semantic 도구가 필요하면 — 가능하면 *두 도구로 분리*. 한 도구가 read와 write를 동시에 하는 패턴은 피함.

---

## 4. Actuator 안전 패턴

### 4.1 2-step confirm을 *도구 단에서 강제* ⚠️

**원칙**: 부작용 있는 도구는 *반드시 2-step confirm*. ServerInstructions로 LLM에 가이드하는 것 *외에* 도구 자체가 `confirm` required 인자로 강제.

**근거**: LLM이 ServerInstructions를 따르지 않는 케이스 (환각 / jailbreak / 모델별 약함) 에서 *도구 단 validation이 최후 방어선*. 두 layer 모두 작동해야 진짜 안전.

**어떻게 적용**:
- `confirm` 인자가 *required*. 누락 시 `RequiresConfirmation` envelope.
- envelope의 `intent_summary`는 *한국어로 사용자에게 보여줄 준비 완료* — LLM이 한 번 더 가공하는 단계 없이 그대로 전달.
- 사용자 명시적 동의 ("응" / "OK") → LLM이 *같은 호출에 `confirm=true` 추가*하여 재호출.

---

### 4.2 Idempotency dedup ⚠️

**원칙**: 부작용 도구는 *중복 호출에 대해 두 번째를 거절*. LLM 환각이 진짜 돈 손해로 가지 않게.

**어떻게 적용**:
- 자연스러운 dedup key: (account + symbol + side + qty + price + N분 window) hash
- 명시 override: LLM이 `idempotency_key` 직접 전달 가능 (사용자가 *진짜* 다른 주문임을 confirm한 후)
- portfolio.db `order_audit.idempotency_key` UNIQUE 제약으로 강제

---

### 4.3 Paper-trading default + 이중 신호 ⚠️

**원칙**: 실투 (real money) 는 *환경 설정 + 도구 호출 인자 둘 다 명시*해야 발동. 한쪽만 live면 거절.

**근거**: 환경 설정은 *운영자 의도*, 도구 인자는 *LLM 의도*. 둘 다 일치해야 사용자 안전. 단일 토글이면 LLM 환각 또는 잘못된 default가 곧 실손.

**어떻게 적용**:
- env `LSOPENAPI_VIRTUAL` + per-call `live: true` 둘 다 일치해야 실투
- 응답 `_meta.mode` 항상 명시 — *virtual* / *live*

---

### 4.4 Sanity warnings — block 안 하고 *알림* ⚠️

**원칙**: 임계값 (큰 주문 / 비정상 가격 / 큰 수량) 위반 시 *block은 안 하되 `_meta.warnings`로 LLM에 신호*. 사용자에게 전달할 책임은 LLM에 위임.

**근거**: 어떤 거래가 "비정상"인지는 사용자 맥락 의존 (헤지 / 청산 / 신규 진입 등). 자동 block은 false positive 위험. 알림은 false positive에도 비용 낮음.

---

### 4.5 Audit log — 우리 자기 action 기록 ⚠️

**원칙**: 우리가 보낸 모든 발주 시도 (성공 / 실패 / 거절 / 에러) 를 portfolio.db `order_audit` 에 기록. LS data의 cache 아님 — *우리 자기 action 의 영구 기록*.

**근거**:
- 사용자가 "방금 그 주문 뭐였지?" 질문 회수 가능
- LS API 변경 시 디버깅 (raw request payload 보관)
- 어느 LLM 세션이 어느 주문 보냈는지 (best-effort)
- 법적 분쟁 시 *우리* 측 기록

---

### 4.6 Preview → Commit 분리 ⚠️

**원칙**: Side-effect 도구는 *별도의 read-only preview 도구*를 거친 후에만 호출 가능. preview는 *실 데이터 위에서 계산*된 의도+결과를 보여주고 `preview_id`를 발급, commit 도구는 그 ID 없이는 실행 불가.

**근거**:
- 단일 도구의 2-step `confirm` 패턴은 LLM이 한 호출로 우회 시도 가능 (`confirm=true` 즉시 호출)
- *별도 도구*면 LLM이 두 호출 사이에서 *진짜* 사용자 확인 단계를 거치도록 강제됨 (tool schema 단의 자연스러운 분리)
- Preview는 *라이브 데이터로 계산* — 잔고/현재가/미체결/policy 모두 확인한 후 결과 노출. LLM이 *추측하지 못함*
- `preview_id`는 *그 의도의 fingerprint + 시간 윈도우* — 두 호출이 같은 의도임을 *암호학적으로 보장*

**어떻게 적용**:
- `ls_preview_*` 도구가 검증 + ID 발급 (5분 valid)
- `ls_place_*` / `ls_modify_*` 가 ID 소비. bare invocation 거절
- `dry_run` 모드는 *simulation*용 escape hatch (실 발주 권한 부여 X)
- Cancel은 *긴급 행위*가 잦으므로 예외 — `confirm=true`만으로 즉시 실행 허용

**재고 조건**:
- 사용자 friction이 production에서 *압도적*으로 관찰 (LLM이 preview를 잘 활용하지 못함) → preview를 더 부드럽게 (자동 inline) 또는 confirm-only fallback 검토. 현재 미관찰.

---

### 4.7 Local trading policy — 사용자 hard rule ⚠️

**원칙**: 사용자가 portfolio.db에 *명시적으로 설정한 trading 정책*은 모든 actuator 호출에서 자동 평가, 위반 시 *block* (warning 아님). LLM이 자체적으로 policy를 변경하지 못함.

**근거**:
- Sanity warnings (§4.4 — *우리* 디폴트 보호망) 와 다른 layer가 필요. 사용자 자기 의도가 있는 경우:
  - "오늘은 100만원 이상 안 사겠다"
  - "특정 종목 blacklist"
  - "실투는 일단 보류"
- 이런 사용자 의도는 *우리 default보다 우선*해야 하고 *LLM이 자체적으로 깰 수 없어야* 함
- 정책 변경은 *사용자가 명시적으로 요청*할 때만 — ServerInstructions에 명시

**어떻게 적용**:
- portfolio.db `trading_policy` 테이블 (key-value 저장)
- `ls_trading_policy(action="get|set|remove|reset")` action-routed 도구
- 모든 actuator (preview/place/modify/cancel) 가 policy 자동 평가 → 위반 시 `PolicyViolation` envelope
- ServerInstructions: "LLM은 *자체 판단으로* policy 변경 안 함. 사용자가 *명시적으로* 변경 요청 후만 set 호출"

**재고 조건**: 사용자가 *너무 자주* policy를 푸는 패턴이 관찰 → policy 자체가 friction이 된 것. 그땐 *더 stronger한 hard limit* (env var 기반 immutable limit) 검토. 현재 미관찰.

---

## 5. 패러다임 분업 — 어시스턴트 vs 워크플로우 엔진

### 5.1 두 패러다임의 분업 💭

**원칙**: 같은 LS API 위에서 *두 가지 사용자 needs*가 존재하며, 그 둘은 *경쟁이 아니라 분업*. 우리는 *어시스턴트* 패러다임을 선택하고 다른 패러다임 영역은 빌드하지 않음.

**MCP 어시스턴트 패러다임** (우리):
- LLM이 *호스트 전체*에서 idle/active 사이클
- 사용자 prompt가 ignition
- 도구는 snapshot-by-call
- 결정 주체: LLM이 매 턴
- 적합: ad-hoc 시장 조사 / 분석 / 의사결정 보조

**워크플로우 엔진 패러다임** (다른 진영):
- LLM이 *워크플로우 노드 한 칸 안에서만* 추론
- Cron / 스케줄 / event가 ignition
- 도구는 streaming / push 활용 가능 (always-on 프로세스라)
- 결정 주체: 사전에 그려놓은 DAG
- 적합: 자동매매 / monitor-and-react / 반복 작업 자동화

**적용**:
- 새 기능 검토 시 "이게 어느 패러다임의 use case인가?" 질문. 워크플로우 영역이면 우리에 빌드하지 않고 외부 도구 권유.
- 사용자가 "자동으로 X 하면 Y 알려줘" 요청 → 우리는 *그 자체로 답하지 않음*. cron / scheduler / 외부 자동화에 위임 안내.

---

### 5.2 워크플로우 엔진 진영의 *안전 패턴* 차용 ✅

**원칙**: 워크플로우 엔진 진영이 자동매매 봇 운영에서 사용하는 안전 패턴 (rate limiting, max iterations, throttle, stateless 등) 은 컨셉으로 가치 있음. 우리는 그 *철학*을 도구-호출 layer에서 적용.

**경험적 차용**:
- Stateless per call — 각 도구 호출이 독립, 세션 state 안 누적
- Rate limiting / cooldown — actuator의 idempotency window (§4.2)
- Max iterations / token budget — LLM 호스트가 알아서 (우리 영역 아님)
- Throttle on real-time source — *우리는 그 source 자체를 안 다룸* (§1.2)

**우리가 차용하지 않는 것**: 그쪽 *프레임워크 / runtime / orchestration / 워크플로우 빌더 / 노드 그래프 GUI*. 그건 그 패러다임의 본체이고 우리 강점 (호스트 어디서나 plug, daemon-less, 표준 충실) 과 충돌.

---

### 5.3 합류 가능 시나리오 💭

미래에 MCP 서버가 워크플로우 엔진의 *도구 공급자*로 자연 편입될 수 있음:
- 워크플로우 엔진이 외부 MCP 서버를 도구로 호출하는 기능을 표준화하면, 우리 mcp-lsopenapi는 *그 자체로* 워크플로우 엔진의 LS 도구 supplier
- 우리는 *변경 없이* 양쪽 paradigm에 자연 편입 (MCP 어시스턴트 호스트 + 워크플로우 엔진 노드)
- MCP 표준 호환성 유지가 그 호환성의 길

**전략적 함의**: MCP 표준에 *plug 깊이 박혀있는* 것이 우리 강점. 우리만의 비표준 통합 / 자체 GUI / 자체 워크플로우 빌드는 그 강점을 깎음. 표준에 충실하면 미래 통합이 *자동*.

---

## 6. 응답 envelope 일관성

### 6.1 `_meta` 블록으로 metadata 표준화 ⚠️

**원칙**: 모든 도구 응답에 `_meta` 블록 — 데이터 자체와 *메타데이터*를 분리.

**현재 `_meta` 필드**:
- `data_as_of` — 데이터 시점 timestamp (가능한 곳)
- `query_date_echo` — 사용자 입력 반향 (date-bearing 도구)
- `account_used` — 어떤 계좌가 조회됐는지 (account 도구)
- `mode` — virtual / live (actuator)
- `source` — live / cache / static (현재는 거의 모두 live)
- `warnings` — sanity / advisory 배열
- `render_status` — chart 도구 (v1.5 §7.5)
- `render_hints` — chart customization (v1.5)
- `audit_id` — actuator audit log id

LLM이 `_meta`를 읽어 *컨텍스트화*하고 사용자에게 적절히 전달. data 필드는 *순수 사실*.

---

### 6.2 분류는 LLM에 위임, 우리는 *사실*만 ✅

**원칙**: 응답 필드는 *사실* (timestamp, 가격, 수량, 종목코드 등) 만. *분류 / 라벨 / 해석* (`weekend` / `holiday` / `pre_market` / `bull` / `bear` 등) 은 우리가 하지 않음.

**근거**:
- LLM이 사용자 맥락 + 자기 지식으로 *우리보다 정확히* 분류함 ([§2.3](#23-llm이-이미-아는-것은-빌드하지-않음))
- 우리 분류는 *시간이 지나며 부정확해질 위험* (정책 변경, 캘린더 update 등) — LLM은 trainings 데이터 + 사용자 컨텍스트로 *현재* 답
- 분류 layer 유지 비용 (테스트, 정책 결정, 사용자 expectation) 이 가치보다 큼

**어떻게 적용**:
- 응답에 *enum 라벨*을 추가하려는 충동 → 거부. 대신 사용자에게 *raw 사실*을 전달.
- 도구 description에 LLM이 자기 지식으로 분류해도 됨을 명시.

---

## 7. 비범위 — 명시적으로 안 빌드하는 것들

| 항목 | 이유 |
|---|---|
| WebSocket / push / streaming / sidecar daemon | §1.2, §1.3 |
| LS 데이터 캐시 (계좌 / 거래 / 수익률 등) | §2.1 |
| portfolio.db ↔ LS 자동 sync | §2.2 |
| KRX / NYSE 휴장 캘린더 테이블 | §2.3 |
| 시장 운영 상태 분류 (`weekend`, `holiday`, `pre_market` 등) | §2.3, §6.2 |
| 자동 매매 / algo bot / stop-loss 모니터링 | §5.1 (워크플로우 영역) |
| 우리 자체 GUI / 워크플로우 빌더 / 대시보드 | §5.3 (표준 충실성) |
| Background scheduling / cron-like 동작 | §1.3 (외부 위임) |
| 응답 필드의 enum 분류 / 라벨링 | §6.2 |

각 항목은 사용자 demand가 *반복적으로* 들어오고 *외부 대안이 부적합*함이 증명되어야 재고. default = NO.

---

## 8. 진화 안내

이 문서는 *살아있는 원칙 모음*이지 *고정된 헌법*은 아님. 새 디자인 결정이 기존 원칙과 충돌하면:

1. 충돌이 *원칙의 한계*인가 *제안의 잘못*인가 판단
2. 원칙의 한계면 — 재고 조건 (각 항목 마지막) 충족 여부 검토 후 *원칙 update + 근거 추가*
3. 제안의 잘못이면 — 제안 폐기 + 검토 narrative를 [LS-API-QUIRKS.md](./LS-API-QUIRKS.md) 같은 곳에 보존 (다음 세션이 같은 함정 안 빠지게)

원칙 update는 *큰 사건*. 가볍게 하지 않음. PR description에 "이 원칙 update를 정당화하는 근거" 명시.

---

## 9. 이력 — 큰 원칙이 정립된 세션

| 날짜 | 정립 | 트리거 | 보존 위치 |
|---|---|---|---|
| 2026-05-21 | 기본 패러다임 (read-only 시장 데이터 + 로컬 portfolio) | v1.0.0 ship | README |
| 2026-05-26 | Date envelope 첫 시도 (이후 §2.3 / §6.2에서 폐기) | v1.4 design | SPEC-v1.4 |
| 2026-05-27 | §1.1 / §1.2 / §1.3 (MCP-어시스턴트 패러다임, daemon-less 영구화) | WebSocket / news / 트레이딩 전수 검토 | LS-API-QUIRKS §7.9-7.11 |
| 2026-05-27 | §2.1 / §2.2 (source of truth) | 트레이딩 슬라이스 디자인 | SPEC-v1.6, SPEC-v1.7 |
| 2026-05-27 | §2.3 / §6.2 (LLM 지식 존중) | Date envelope 재검토 | SPEC-v1.6 |
| 2026-05-27 | §4.1-4.7 (Actuator 안전 패턴, preview → commit 분리, local policy hard block 포함) | 트레이딩 슬라이스 디자인 + 워크플로우 진영 안전 패턴 차용 | SPEC-v1.7 |
| 2026-05-27 | §3.1 / §3.2 (네이밍 컨벤션) | 트레이딩 슬라이스 디자인 | SPEC-v1.6 §2.2, SPEC-v1.7 §5 |
| 2026-05-27 | §5.1 / §5.2 / §5.3 (패러다임 분업) | 다른 진영 ecosystem 비교 | (이 문서에 처음 명시) |

원칙 업데이트 / 신규 항목 추가 시 이 표에 한 줄 추가.
