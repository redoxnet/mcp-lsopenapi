# SPEC: v1.7.0 — Trading actuators (preview-gated REST, daemon-less)

- **상태**: Draft (2026-05-27).
- **대상 버전**: v1.7.0
- **선행**: [SPEC-v1.6.md](./SPEC-v1.6.md) (account inquiry — v1.7의 *전제*), [DESIGN-PRINCIPLES.md](./DESIGN-PRINCIPLES.md) (§4 Actuator 안전 패턴).
- **범위**: 트레이딩 슬라이스. 5개 도구 (`ls_preview_order` / `ls_place_order` / `ls_modify_order` / `ls_cancel_order` / `ls_trading_policy`) + 다층 safety (preview-gating, local trading policy hard-block, idempotency, paper/live 이중 신호, sanity warnings, audit log).
- **메시지**: v1.7은 *돈이 움직이는* 슬라이스. 단일 도구 추가가 아니라 **"임의 주문 불가, preview 통과한 의도만 발주 가능"** 패턴이 핵심. v1.6 inquiry 위에서 진입.
- **비범위**: WebSocket order events (SC0-SC4 — [DESIGN-PRINCIPLES §1.2](./DESIGN-PRINCIPLES.md)), advanced order types (예약/조건부 — phase 2), 다중 LS 계정.

---

## 1. 컨텍스트

### 1.1 v1.6 → v1.7

v1.6이 *read-only LS 계좌 노출*로 진입했다면, v1.7은 *write actuator*. 두 슬라이스의 결정적 차이:

| | v1.6 (inquiry) | v1.7 (actuator) |
|---|---|---|
| 부작용 | 없음 (REST GET) | **실제 돈 움직임** |
| 실수의 비용 | 잘못된 응답 (정정 가능) | **잘못된 주문 = 실손** |
| Safety 필요 | 기본 (인증/권한) | **다층 가드 필수** |
| 운영 학습 | 시작 시점 | v1.6 사용 패턴 위에서 |

v1.6과 v1.7 사이 **1-2주 운영 관찰**: 사용자가 LLM 통해 계좌 데이터 조회하는 실제 패턴을 보고 v1.7 safety/UX 미세 조정. 디자인 결정의 일부는 *실측 후*에 굳히는 게 정직.

### 1.2 자연 워크플로우 — *LLM이 호출 순서로 표현*

자동매매 워크플로우 엔진들이 검증한 *매수/매도 흐름*은 다음과 같은 단계를 거침. 우리는 그 단계를 *LLM이 자연어 대화 속에서* 우리 도구를 차례로 호출하는 형태로 표현:

**매수 흐름**:
```
사용자: "RSI 30 이하 + 5일 이평 위에 있는 코스피 종목 중 내가 안 가진 거 사고 싶어"

LLM:
  1. ls_run_screener / ls_search_stock           → 후보 종목 N개
  2. ls_holdings_list                            → 이미 보유 중인 종목 제외
  3. ls_get_quote (각 후보)                      → 현재가
  4. (LLM이 position sizing 계산: 예산/수량/비중)
  5. ls_account_balance                          → 가용 예수금 확인
  6. ls_preview_order(side="buy", ...)           → 검증 + 예상 결과
  7. 사용자에게 preview 결과 보여주기
  8. 사용자 동의 → ls_place_order(preview_id=...) → 실제 발주
```

**매도 흐름**:
```
사용자: "삼전 손실 -5% 넘으면 손절해줘"

LLM:
  1. ls_account_holdings                         → 보유 종목 + BEP
  2. ls_get_quote("005930")                      → 현재가
  3. (LLM이 손익률 계산 / 손절 조건 평가)
  4. ls_preview_order(side="sell", ...)          → 검증
  5. 사용자에게 preview 결과 보여주기
  6. 사용자 동의 → ls_place_order(preview_id=...) → 실제 발주
```

핵심: **워크플로우 엔진은 이걸 DAG로 사전에 그려두는데, 우리는 LLM이 자연어 대화 속에서 동등하게 표현**. 우리 도구의 책임은 *각 단계가 안전하게 결합되도록* 만드는 것 — preview / policy / idempotency가 그 결합 안전성을 보장.

### 1.3 핵심 디자인 결정 — *임의 발주 불가*

`ls_place_order`는 *bare invocation을 거절*:
- **반드시 valid `preview_id`** (최근 5분 안에 `ls_preview_order`로 생성) 또는
- **명시적 `dry_run` 모드** (실제 발주 안 함, validation만)

이게 LLM이 환각으로 "그냥 사" 명령 받고 곧장 place 호출하는 시나리오를 *도구 단에서* 차단. ServerInstructions 가이드 *외에* 도구 자체가 강제.

추가로 **local trading policy** (`ls_trading_policy`) 가 *사용자 자기 룰*을 portfolio.db에 박아 모든 actuator 호출에 자동 적용 — 1회 최대 주문금액, 종목 allow/deny list, 실계좌 주문 허용 여부 등. policy 위반은 *block* (warning 아님).

---

## 2. 디자인 — Tools (5)

### 2.1 `ls_preview_order` — 주문 전 검증 + preview_id 발급

부작용 *없음*. 발주를 위한 모든 사전 검증을 한 호출로 수행하고 *5분 valid preview_id*를 반환.

| 파라미터 | 타입 | 필수 | 설명 |
|---|---|---|---|
| `account` | string | conditional | 계좌번호. 미지정 시 portfolio.db default |
| `symbol` | string | yes | 종목코드 (6자) |
| `side` | enum | yes | `"buy"` / `"sell"` |
| `quantity` | int | yes | 주문 수량 |
| `order_type` | enum | yes | `"market"` / `"limit"` |
| `price` | int | conditional | `order_type=limit`일 때 필수 (KRW) |
| `notes` | string | no | 사용자/LLM 메모 (preview 응답에 반향 + audit에 보존) |

**내부 동작 (1회 호출에 검증 수행)**:
1. `ls_account_balance(account)` 조회 → 예수금 / 주문가능금액
2. `ls_account_holdings(account)` 조회 → 현 잔고 + 보유 수량
3. `ls_account_orders(today, status="pending")` 조회 → 미체결 주문 (중복 의도 감지)
4. `ls_get_quote(symbol)` 조회 → 현재가 + 호가 + 시장 시각
5. `ls_account_max_order_qty(account, symbol)` 조회 → 최대 주문 가능 수량
6. (`ls_trading_policy` 활성 정책 평가 — 위반 시 `PolicyViolation`)
7. 종합 검증 + 분석 → `preview_id` 발급 + 풍부한 응답

**응답 envelope**:
```jsonc
{
  "preview_id": "prv_20260527152345_a7c3f1e9",     // 5분 valid
  "expires_at": "2026-05-27T15:28:45+09:00",
  "intent": {
    "account": "123-456789-01 (주식)",
    "symbol": "005930 (삼성전자)",
    "side": "buy",
    "quantity": 100,
    "order_type": "limit",
    "price": 75300,
    "estimated_cost": 7530000,                     // KRW
    "currency": "KRW"
  },
  "validation": {
    "account_balance_sufficient": true,
    "available_cash": 21500000,
    "cash_after_order": 13970000,
    "current_position": { "quantity": 50, "bep": 72100 },
    "position_after_order": { "quantity": 150, "estimated_bep": 73167 },
    "pending_orders_same_symbol": [],              // 비어있음 = OK
    "market_quote": { "last": 75200, "bid": 75200, "ask": 75300, "observed_at": "..." },
    "price_deviation_from_market": "+0.13%",       // 한도 내
    "max_orderable_quantity": 285
  },
  "policy_check": {
    "applied_policies": ["max_order_amount", "live_account_allowed"],
    "violations": [],
    "warnings": []
  },
  "advisories": [                                  // sanity warnings (있을 때만)
    { "code": "LARGE_ORDER", "human": "이 주문 (약 753만원) 은 가용 예수금의 35%입니다." }
  ],
  "_meta": {
    "tool": "ls_preview_order",
    "mode": "virtual",
    "issued_at": "2026-05-27T15:23:45+09:00"
  }
}
```

`PolicyViolation` 응답 시 *preview_id 발급 안 함* — 도구가 *명시적으로 거절*:
```jsonc
{
  "error": "PolicyViolation",
  "message": "Local trading policy 거부 — 1회 최대 주문금액 500만원 초과 (요청 753만원). policy 변경: ls_trading_policy(action=\"set\", key=\"max_order_amount\", value=10000000)",
  "violations": [
    { "policy": "max_order_amount", "limit": 5000000, "requested": 7530000 }
  ]
}
```

LLM은 violations를 사용자에게 한국어로 전달 → 사용자가 policy 변경 후 재시도.

### 2.2 `ls_place_order` — preview-gated 발주

부작용 있음 (실제 발주). bare invocation 거절.

| 파라미터 | 타입 | 필수 | 설명 |
|---|---|---|---|
| `preview_id` | string | conditional | `ls_preview_order`에서 받은 ID. 5분 안에 소비. *생략 시 dry_run=true 필수* |
| `dry_run` | bool | conditional | `true`면 preview_id 없이도 호출 가능 — 실제 발주 안 함, validation만 echo |
| `live` | bool | no | `true`면 실투. default `false` (모의). env `LSOPENAPI_VIRTUAL=0`과 둘 다 일치해야 함 |
| `idempotency_key` | string | no | 미지정 시 자동 생성 (preview_id 기반) |

**유효 호출 패턴**:
- `ls_place_order(preview_id="prv_...")` — 실 발주 (preview 5분 valid 안에)
- `ls_place_order(dry_run=true, side, symbol, qty, ...)` — validation only, preview 없이 가능

**거절 케이스**:
- bare invocation (preview_id도 dry_run도 없음) → `PreviewRequired` envelope
- 만료된 preview_id (5분 초과) → `PreviewExpired`
- 이미 소비된 preview_id → `PreviewAlreadyConsumed`
- env=virtual + `live=true` → `ModeMismatch`
- idempotency key 중복 → `IdempotencyViolation`
- policy 위반 (preview 후 policy 변경된 경우) → `PolicyViolation` (재발급 권장)

**TR**: `CSPAT00601`. preview의 intent를 그대로 전송.

응답:
```jsonc
{
  "order_no": "12345678",
  "status": "submitted",
  "preview_id": "prv_20260527152345_a7c3f1e9",
  "_meta": {
    "tool": "ls_place_order",
    "mode": "virtual",
    "submitted_at": "...",
    "audit_id": 42
  }
}
```

### 2.3 `ls_modify_order` — 주문 정정

| 파라미터 | 타입 | 필수 | 설명 |
|---|---|---|---|
| `account` | string | conditional | |
| `order_no` | string | yes | 정정할 주문번호 |
| `new_quantity` | int | no | 둘 중 하나는 필수 |
| `new_price` | int | no | 둘 중 하나는 필수 |
| `preview_id` | string | conditional | preview-gated. 정정도 preview 거침 |
| `dry_run` | bool | conditional | preview 우회 시 명시 |
| `live` | bool | no | |
| `idempotency_key` | string | no | |

내부적으로:
1. `ls_account_orders` 호출 → order_no 미체결 여부 확인. 이미 체결됐으면 `OrderAlreadyFilled` 즉시 거절.
2. preview_id 검증 (정정 의도가 preview에 기록된 modify intent와 일치)
3. **TR `CSPAT00701`** 실행

`ls_preview_order`가 `side="modify"`도 지원 — modify intent도 동일하게 검증 후 preview_id 발급.

### 2.4 `ls_cancel_order` — 주문 취소

| 파라미터 | 타입 | 필수 | 설명 |
|---|---|---|---|
| `account` | string | conditional | |
| `order_no` | string | yes | 취소할 주문번호 |
| `quantity` | int | no | 부분 취소 수량. 미지정 시 전량 |
| `preview_id` | string | conditional | |
| `dry_run` | bool | conditional | |
| `live` | bool | no | |

cancel은 보통 *긴급 행위* (잘못 낸 주문 빠르게 끄기) 라 preview 강제가 friction일 수 있음. **예외 정책**: cancel은 `confirm=true` 명시만으로도 즉시 실행 허용 (preview_id 불요). place/modify는 preview 강제.

```jsonc
ls_cancel_order(order_no="12345678", confirm=true)         // 즉시 취소 OK
ls_cancel_order(order_no="12345678", preview_id="prv_...")  // preview 거친 취소도 OK
ls_cancel_order(order_no="12345678")                       // 거절: RequiresConfirmation
```

내부적으로:
1. `ls_account_orders` 호출 → order_no 미체결 + 취소 가능한 수량 확인
2. **TR `CSPAT00801`** 실행

### 2.5 `ls_trading_policy` — 로컬 정책 관리

portfolio.db에 *사용자 자기 룰*을 저장. 모든 actuator (preview / place / modify / cancel) 호출에서 자동 적용.

action-routed (기존 `ls_account(action=...)` 패턴):

| Action | 파라미터 | 효과 |
|---|---|---|
| `get` | (none) | 현재 활성 정책 dump |
| `set` | `key`, `value` | 단일 정책 설정/갱신 |
| `remove` | `key` | 정책 제거 |
| `reset` | `confirm=true` | 모든 정책 초기화 (factory default) |

**정책 키 (built-in)**:

| Key | Type | Default | 의미 |
|---|---|---|---|
| `max_order_amount` | int (KRW) | unset (=무제한) | 1회 주문 최대 금액. 초과 시 `PolicyViolation` |
| `max_order_pct_of_balance` | float (0-1) | 0.5 | 1회 주문이 가용 예수금의 N% 이하 |
| `symbol_allow_list` | array[string] | `[]` (=전체 허용) | 비어있지 않으면 *whitelist*. 미포함 종목 거절 |
| `symbol_deny_list` | array[string] | `[]` | blacklist. 포함 종목 거절 |
| `live_account_allowed` | bool | `false` | `false`면 `live=true` 호출 모두 거절 (env와 무관) |
| `dry_run_only` | bool | `false` | `true`면 모든 actuator가 `dry_run=true`로 강제 변환 |
| `max_pending_orders` | int | 10 | 미체결 주문 동시 개수 제한 |

`get` 응답 예시:
```jsonc
{
  "policies": {
    "max_order_amount": { "value": 5000000, "set_at": "2026-05-20T...", "set_by_session": "..." },
    "symbol_deny_list": { "value": ["005380"], "set_at": "...", "set_by_session": "..." },
    "live_account_allowed": { "value": false, "set_at": "...", "set_by_session": "..." }
  },
  "active_defaults": {
    "max_order_pct_of_balance": 0.5,
    "max_pending_orders": 10,
    "dry_run_only": false
  }
}
```

ServerInstructions: LLM이 *자체적으로 policy를 설정하지 않음*. policy 변경은 *사용자가 명시적으로 요청*할 때만.

---

## 3. 디자인 — Preview-gate 패턴 (Sub-change 2 — 새 패턴)

### 3.1 왜 preview-gate인가

기존 단일 `confirm=true` 패턴 ([SPEC v1.7 초기 draft]) 의 한계:
- LLM이 confirm=true 즉시 호출 가능 (1-step 우회 시도)
- "이 주문이 *어떤 결과가 될지*" 정보가 confirm 직전엔 빈약 (전혀 안 보고 confirm 받을 위험)
- 사용자가 한국어로 보는 intent_summary는 LLM이 생성 — *환각 위험*

Preview-gate는 이걸 다르게 해결:
1. **두 단계가 *다른 도구*** — LLM이 한 호출로 우회 불가능
2. **Preview는 *실 데이터 위에서 계산*** — 잔고/시장가/현 잔고 등 라이브 확인. LLM이 추측 못 함
3. **preview_id는 *그 intent의 hash + 시간 윈도우*** — 두 번째 호출이 같은 intent임을 *암호학적으로 보장*

### 3.2 preview_id lifecycle

- 발급: `ls_preview_order` 호출 → memory + portfolio.db `order_preview` 임시 row
- valid window: **5분** (env override 가능 — `LSOPENAPI_PREVIEW_TTL_SEC`)
- 소비: `ls_place_order(preview_id=...)` 또는 `ls_modify_order(preview_id=...)`에서 한 번만
- 만료: 5분 경과 시 `PreviewExpired`. 사용자 의도가 stale 됐을 가능성 (가격 변동 등)
- 소비 후: row를 *consumed* 상태로 mark + audit_id linking

### 3.3 dry_run 우회 — *때로는 필요*

LLM이 "위 주문을 *실제로 보내기 직전 한 번 더* validation 결과 보여줘" 시나리오에서 preview를 두 번 만들기보다 dry_run이 자연:

```
LLM: ls_preview_order(...)             → preview_id X
사용자: "음, 잠깐만. 다른 종목도 같이 사고 싶어"
LLM: ls_preview_order(다른 종목)        → preview_id Y
사용자: "응 두 개 다 사"
LLM: ls_place_order(preview_id=X)
     ls_place_order(preview_id=Y)
```

vs

```
LLM: ls_place_order(dry_run=true, ...)  → "이렇게 보낼게요, 정말 OK?"
사용자: "응"  
LLM: ls_place_order(...)                → 거절 (preview_id 없음)
```

후자가 안 됨 — dry_run은 *validation echo*일 뿐 발주 권한 부여 아님. dry_run은 *시뮬레이션*, preview는 *진짜 의도 표명*.

### 3.4 cancel의 예외

§2.4에서 명시했듯 cancel은 *긴급 행위*가 종종 있음 (잘못 낸 주문 빨리 끄기). preview 강제는 unnecessary friction. cancel은 `confirm=true` 명시만 요구.

modify는 *의도 변경*이라 preview 거치는 게 정직 (가격 바뀐 줄 모르고 정정하는 위험).

---

## 4. 디자인 — Local trading policy (Sub-change 3 — 새 패턴)

### 4.1 왜 sanity warnings로는 부족한가

Sanity warnings ([SPEC v1.7 초기 draft] §3.4) 는 *block 안 함, 알림만*. 그런데 사용자가 *명시적으로 "한도 넘으면 절대 안 됨"* 원하는 케이스가 있음:
- "오늘은 100만원 이상 안 사겠다 다짐했어"
- "성장주 도박 그만하려고 005380 (현대차) blacklist에 넣어둠"
- "실투는 일단 보류, 모의만"

이런 의도는 *사용자가 명시적으로 설정*한 것이라 *LLM이 자체적으로 깰 수 없어야 함*. Hard block 필요.

### 4.2 Hard block vs soft warning

| | Sanity warnings | Trading policy |
|---|---|---|
| 출처 | 우리가 임계값 계산 | 사용자가 명시 설정 |
| 효과 | `_meta.warnings` 부착, 발주 진행 | 발주 *거절* (preview/place 모두) |
| LLM 우회 가능? | 가능 (warning 무시) | **불가능** (도구가 거절) |
| 예시 | "이 주문 35% 차지" | "max_order_amount 500만 초과" |

두 layer 공존 — sanity는 *우리 디폴트 보호망*, policy는 *사용자 의도 hard rule*.

### 4.3 Policy violation 시 흐름

LLM이 policy 위반 시도 → `PolicyViolation` envelope → LLM이 사용자에게 한국어로 *어떤 정책 위반인지 + 어떻게 변경하는지* 전달 → 사용자가 명시적으로 policy 변경 요청 → LLM이 `ls_trading_policy(action="set", ...)` 호출 → 재시도.

**중요**: LLM이 *자체 판단으로* policy 변경하지 않음. ServerInstructions에 명시.

### 4.4 Policy의 portfolio.db 저장

기존 portfolio.db에 `trading_policy` 테이블 추가 (schema migration v2의 일부 — §5):

```sql
CREATE TABLE trading_policy (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,             -- JSON encoded (number / array / bool 다 표현)
    value_type  TEXT NOT NULL,             -- "int" / "float" / "bool" / "array" / "string"
    set_at      TEXT NOT NULL DEFAULT (datetime('now')),
    set_by_session TEXT
);
```

---

## 5. 디자인 — Idempotency / paper-live / sanity / audit (Sub-change 4 — 기존 패턴 retained)

### 5.1 Idempotency dedup

[DESIGN-PRINCIPLES §4.2](./DESIGN-PRINCIPLES.md) 패턴 그대로:
- 자동 생성: `idempotency_key = SHA256(preview_id)` 만으로 충분 (preview_id 자체가 이미 시간 + intent 고유)
- 사용자 override: `idempotency_key` 명시 인자
- portfolio.db `order_audit.idempotency_key` UNIQUE 제약

### 5.2 Paper-live 이중 신호

[DESIGN-PRINCIPLES §4.3](./DESIGN-PRINCIPLES.md):
- env `LSOPENAPI_VIRTUAL=1` (default) → 모의 endpoint
- `LSOPENAPI_VIRTUAL=0` → 실투 endpoint
- per-call `live: true` 명시 + env가 일치해야 실투 가능
- `live_account_allowed=false` policy면 *env와 무관히* live 호출 거절

### 5.3 Sanity warnings

§4.1의 hard policy와 별개로 default sanity 보호망 — `_meta.warnings`에 부착:
- `LARGE_ORDER` — 주문 금액이 가용 예수금의 30% 이상 (env override: `LSOPENAPI_ORDER_LARGE_ORDER_PCT`)
- `EXTREME_PRICE` — limit 가격이 현재가 대비 ±5% 벗어남 (env: `LSOPENAPI_ORDER_EXTREME_PRICE_PCT`)
- `LARGE_QUANTITY` — 수량이 max orderable의 50% 이상 (env: `LSOPENAPI_ORDER_LARGE_QUANTITY_PCT`)

LLM이 사용자에게 *반드시* 전달 (ServerInstructions 강제).

### 5.4 Audit log

[DESIGN-PRINCIPLES §4.5](./DESIGN-PRINCIPLES.md) — portfolio.db `order_audit` 테이블에 *모든 actuator 호출 시도*를 영구 기록 (성공/실패/거절/에러).

---

## 6. 디자인 — Schema migration v2

기존 v1 위에 새 테이블 *3개* 추가:

```sql
-- migration v2: v1.7 trading actuators

-- (1) order audit log — 우리 자기 action 기록
CREATE TABLE order_audit (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    submitted_at    TEXT NOT NULL DEFAULT (datetime('now')),
    completed_at    TEXT,
    session_id      TEXT,
    idempotency_key TEXT NOT NULL UNIQUE,
    preview_id      TEXT,                       -- 연결된 preview (있을 때)
    tool            TEXT NOT NULL,              -- ls_place_order / ls_modify_order / ls_cancel_order
    tr_code         TEXT NOT NULL,              -- CSPAT00601 / 00701 / 00801
    mode            TEXT NOT NULL,              -- virtual / live
    dry_run         INTEGER NOT NULL DEFAULT 0,
    account_no      TEXT NOT NULL,
    symbol          TEXT,
    side            TEXT,                       -- buy / sell / modify / cancel
    quantity        INTEGER,
    price           INTEGER,
    order_type      TEXT,
    request_payload TEXT NOT NULL,
    ls_order_no     TEXT,
    ls_response     TEXT,
    warnings        TEXT,                       -- _meta.warnings JSON
    policy_check    TEXT,                       -- policy_check JSON
    status          TEXT NOT NULL,              -- submitted / acknowledged / rejected / error
    error_message   TEXT,
    notes           TEXT
);
CREATE INDEX ix_order_audit_submitted ON order_audit(submitted_at);
CREATE INDEX ix_order_audit_account   ON order_audit(account_no, submitted_at);
CREATE INDEX ix_order_audit_status    ON order_audit(status, submitted_at);

-- (2) order preview — 5분 valid preview ID 저장
CREATE TABLE order_preview (
    preview_id    TEXT PRIMARY KEY,
    issued_at     TEXT NOT NULL DEFAULT (datetime('now')),
    expires_at    TEXT NOT NULL,
    consumed_at   TEXT,                         -- 소비 시각 (NULL이면 unconsumed)
    consumed_by_audit_id INTEGER REFERENCES order_audit(id),
    session_id    TEXT,
    intent_json   TEXT NOT NULL,                -- 원래 의도 (account/symbol/side/qty/price/...)
    validation_json TEXT NOT NULL,              -- preview 응답의 validation block
    policy_check_json TEXT NOT NULL
);
CREATE INDEX ix_order_preview_expires ON order_preview(expires_at);

-- (3) trading policy — 사용자 자기 룰
CREATE TABLE trading_policy (
    key            TEXT PRIMARY KEY,
    value          TEXT NOT NULL,               -- JSON encoded
    value_type     TEXT NOT NULL,               -- int / float / bool / array / string
    set_at         TEXT NOT NULL DEFAULT (datetime('now')),
    set_by_session TEXT
);
```

Migration v2 entry `SqlitePortfolioRepository.Migrations`에 추가. 기존 v1 사용자가 v1.7 첫 launch 시 자동 migrate.

---

## 7. 디자인 — ServerInstructions 트레이딩 단락

v1.6 ServerInstructions에 *교체*되거나 추가:

```markdown
## 주문 발주 — 안전 패턴 (v1.7+)

LS-LsOpenApi v1.7부터 *주문 발주 도구*를 제공합니다:

- `ls_preview_order` — 주문 전 검증 + preview_id 발급 (부작용 없음)
- `ls_place_order` — preview_id 소비하여 실제 발주
- `ls_modify_order` — 주문 정정 (preview 필요)
- `ls_cancel_order` — 주문 취소 (preview 또는 confirm만)
- `ls_trading_policy` — 사용자 trading policy 관리

### 절대 지켜야 할 규칙

1. **Preview → Place 2-step**: `ls_place_order` 호출은 *반드시* `ls_preview_order` 먼저 호출 → 사용자에게 preview 결과 (intent / validation / advisories) 한국어로 보여주기 → 사용자가 명시적 동의 → `ls_place_order(preview_id=...)` 호출. 사용자가 "그냥 사" / "확인 안 해도 돼" 라고 해도 *절대* preview 건너뛰지 말 것.

2. **Cancel의 예외**: `ls_cancel_order`는 긴급 행위가 잦아 `confirm=true` 명시만으로 즉시 실행 가능 (preview 면제). 단 cancel 의도 확실히 사용자에게 확인 후.

3. **Policy 자체 변경 금지**: `ls_trading_policy(action="set"|"remove"|"reset")`은 *사용자가 명시적으로 요청*할 때만 호출. PolicyViolation 발생 시 LLM이 *자체 판단으로 policy 변경 안 함* — 위반 내용 사용자에게 전달 + 사용자가 policy 변경 의사 표명 후만 set 호출.

4. **모드 명시**: 응답 `_meta.mode` (virtual / live) 를 *항상 사용자에게 알리기*. "모의로 발주됐어요" vs "실제로 발주됐어요" 한 줄 명확히.

5. **Advisories 전달**: preview 응답의 `advisories` / `policy_check.warnings` 모두 사용자에게 전달. 큰 주문 / 비정상 가격 — 자동 발주 전 사용자가 알 권리.

6. **Idempotency 존중**: `IdempotencyViolation` envelope이 오면 사용자에게 "방금 같은 주문을 5분 안에 또 보내려고 합니다. 정말 다른 주문이면 새 preview를 만들어주세요" 안내. 자체 우회 금지.

7. **실투 신중**: `live: true` 호출은 *돈이 실제로 움직임*. 사용자가 "실제로", "진짜로", "실투로" 명시했을 때만. 모호하면 모의 (`live: false`).

8. **dry_run 활용**: 사용자가 "이렇게 사면 어떻게 돼?" 같은 *시뮬레이션* 요청이면 `ls_place_order(dry_run=true, ...)` 또는 `ls_preview_order(...)` 사용. 실 발주 의도 명확해질 때까지 dry_run 권장.

### Side-effect 도구 vs 조회 도구

- 동사 prefix (`ls_place_*`, `ls_modify_*`, `ls_cancel_*`) = **부작용 있음**
- 명사 prefix (`ls_get_*`, `ls_account_*`, `ls_holding*`, `ls_search_*`, `ls_preview_*`) = 조회/검증만, 부작용 없음

`ls_preview_order`는 *부작용 없는 검증 도구*. 자유롭게 호출 가능.
```

---

## 8. Tool surface 변화

| | Before (v1.6.0) | After (v1.7.0) |
|---|---|---|
| `standard` | 50 | **55** (+5: preview/place/modify/cancel/policy) |
| `all` | 53 | **58** (+5) |

기존 도구 무변경, response shape 무변경. 순수 additive.

---

## 9. 비범위

- **WebSocket order events (SC0-SC4)** — [DESIGN-PRINCIPLES §1.2](./DESIGN-PRINCIPLES.md)
- **Advanced order types** — 예약/조건부/IOC/FOK 등은 v1.8+ phase 2 후보
- **다중 LS 계정** — 한 LS 계정 (multiple sub-accounts) 만
- **자동 매매 / 알고 트레이딩** — [DESIGN-PRINCIPLES §5.1](./DESIGN-PRINCIPLES.md) (워크플로우 영역)
- **Stop-loss / take-profit *자동* 발주** — 사용자/LLM이 *대화 속에서* 조건 평가하여 명시적 발주. 우리가 background로 조건 monitoring 안 함
- **주문 만료 / 미체결 잔존 자동 처리** — 사용자 책임
- **Preview 자동 재발급** — 만료 시 사용자/LLM이 재호출. 자동 갱신 안 함

---

## 10. 테스트 계획

### 10.1 Preview-gate 패턴

- `ls_place_order()` bare invocation → `PreviewRequired` 거절
- `ls_place_order(dry_run=true)` → validation only, audit에 dry_run=1 기록, 실 발주 X
- `ls_place_order(preview_id=valid)` → 정상 발주, preview_id consumed
- 같은 preview_id로 두 번 호출 → 두 번째 `PreviewAlreadyConsumed`
- 만료된 preview_id (6분 후) → `PreviewExpired`
- 다른 LLM 세션의 preview_id → 거절 (session_id 검증)

### 10.2 Trading policy

- `ls_trading_policy(action="set", key="max_order_amount", value=5000000)` → DB 저장
- preview/place 호출 시 자동 평가 → 위반 시 `PolicyViolation`
- `symbol_deny_list`에 종목 추가 → 그 종목 거절
- `live_account_allowed=false` → env=real이어도 live=true 거절
- `dry_run_only=true` → 모든 actuator dry_run으로 강제 변환
- `ls_trading_policy(action="reset", confirm=true)` → factory default

### 10.3 Idempotency / paper-live / sanity / audit

- 자동 generated key (preview_id 기반) 정확성
- 명시 key override
- env=virtual + live=true → `ModeMismatch`
- 임계값 위반 시 `_meta.warnings` 부착 (block 안 함)
- 모든 actuator 호출 audit row 기록 (INSERT on entry, UPDATE on complete)

### 10.4 통합 (E2E)

**모의투자 + Claude Desktop**:
- "삼전 100주 75,300원 사줘"
  → LLM: `ls_preview_order(...)` → preview_id 받음
  → preview 결과 (예상 비용 7,530,000원, 잔고 영향, 등) 사용자에게 한국어로
  → 사용자 "응"
  → LLM: `ls_place_order(preview_id=...)` → 모의 발주 성공
  → audit log 확인
- "위 주문 취소해"
  → LLM: `ls_cancel_order(order_no=..., confirm=true)` → 즉시 취소 (preview 면제)
- "1억원치 사" + policy `max_order_amount=10000000`
  → `ls_preview_order` 호출 → `PolicyViolation` envelope
  → LLM이 사용자에게 한국어로 policy 위반 안내 + 변경 방법
- "정책 풀어줘 → 5천만으로"
  → 사용자 명시 → LLM: `ls_trading_policy(action="set", key="max_order_amount", value=50000000)`
  → 다시 preview → 통과
- "그냥 사. 확인 안 해도 돼"
  → LLM이 *그래도* preview 거침 (ServerInstructions 강제)
- "이렇게 사면 어떻게 돼?" (시뮬레이션)
  → LLM: `ls_place_order(dry_run=true, ...)` → validation echo, 발주 X

**실투 (사용자 명시 동의 + 최소 단위)**:
- env `LSOPENAPI_VIRTUAL=0` + `live_account_allowed=true` (policy) + per-call `live=true`
- 1주 limit 안전가 발주 → 즉시 cancel
- audit log mode=live 정확 기록

### 10.5 회귀

- v1.6 inquiry 도구 무영향
- 기존 portfolio 도구 무영향
- portfolio.db migration v1 → v2 데이터 무손실
- 차트/시세 도구 무영향

---

## 11. Release notes preview (Mcp)

```markdown
## v1.7.0 — 2026-XX-XX

**Preview-gated order placement (REST, daemon-less) with local trading policy.**

### Added — Trading actuator tools (5)

REST-based order placement with **preview-gate pattern**: `ls_place_order`
cannot execute without a valid `preview_id` (issued by `ls_preview_order`
within 5 minutes) or explicit `dry_run=true`. This makes the
two-call dance enforced *at the tool layer*, independent of LLM
behavior.

- `ls_preview_order(account?, symbol, side, quantity, order_type, price?, notes?)` —
  validates the intent against live balance, holdings, pending orders,
  market quote, max orderable quantity, and trading policy. Returns a
  `preview_id` valid for 5 minutes plus a rich `validation` + `advisories`
  block for the LLM to relay verbatim. Side-effect free.
- `ls_place_order(preview_id|dry_run, live?, idempotency_key?)` —
  consumes a preview_id to actually place the order via `CSPAT00601`.
  Bare invocation rejected. `dry_run=true` mode runs validation
  without placing.
- `ls_modify_order(order_no, new_quantity?, new_price?, preview_id|dry_run, live?)` —
  `CSPAT00701`. Requires preview (intent change is non-trivial).
- `ls_cancel_order(order_no, quantity?, preview_id|confirm, live?)` —
  `CSPAT00801`. Cancel often is *urgent*, so `confirm=true` alone
  suffices — preview not required.
- `ls_trading_policy(action="get"|"set"|"remove"|"reset", key?, value?, confirm?)` —
  user-defined hard rules stored in `portfolio.db`. Built-in keys
  include `max_order_amount`, `max_order_pct_of_balance`,
  `symbol_allow_list`, `symbol_deny_list`, `live_account_allowed`,
  `dry_run_only`, `max_pending_orders`. Policy violations *block*
  the actuator (vs sanity warnings which only warn).

### Safety machinery

- **Preview-gate.** Place / modify require a valid `preview_id`
  consumed within 5 minutes of issue. The preview itself runs a
  live multi-call validation (balance + holdings + pending + quote
  + policy). LLMs cannot bypass with a single call.
- **Local trading policy.** User-set hard rules in `portfolio.db`.
  `PolicyViolation` blocks the actuator; LLMs cannot self-modify
  policy (ServerInstructions enforced, user must explicitly request
  policy change).
- **Paper-trading default.** `live: false` default; live trading
  requires both `LSOPENAPI_VIRTUAL=0` env AND per-call `live: true`
  AND `live_account_allowed=true` policy. Triple gate.
- **Idempotency.** Auto-generated key from `preview_id` (or override
  via parameter) with 5-min UNIQUE constraint in `order_audit`.
- **Sanity warnings.** Default thresholds (`LARGE_ORDER`,
  `EXTREME_PRICE`, `LARGE_QUANTITY`) attach to `_meta.warnings`
  without blocking. Separate from `trading_policy` (which blocks).
- **Audit log.** Every actuator call (success/rejection/error/dry_run)
  writes to `portfolio.db.order_audit` (schema migration v2 — runs
  automatically on first v1.7 startup).

### Schema migration v2

`portfolio.db` gains three new tables: `order_audit`, `order_preview`,
`trading_policy`. Migration runs automatically; no user action.
Existing v1 data untouched.

### ServerInstructions

New section on trading safety covering the preview→place 2-step
flow, cancel's urgent exception, policy self-modification ban, mode
disclosure, advisories relay, idempotency respect, and dry_run usage.
Verb-vs-noun prefix convention extended (`ls_preview_*` is noun-side,
side-effect free).

### Surface

Standard: 50 → 55. All: 53 → 58. Pure additive — no existing tool
removed, renamed, or changed in shape.

### Not in v1.7

- **WebSocket order events (SC0-SC4)** — REST `ls_account_orders`
  polling covers the same information. See
  [docs/DESIGN-PRINCIPLES.md §1.2](docs/DESIGN-PRINCIPLES.md).
- **Advanced order types** (reserved, conditional, IOC, FOK) —
  phase 2 candidate.
- **Multi-LS-account support** — single LS account (with multiple
  sub-accounts) only.
- **Automated trading / algo bots / stop-loss daemons** — out of
  scope by paradigm. See [docs/DESIGN-PRINCIPLES.md §5](docs/DESIGN-PRINCIPLES.md).
```

---

## 12. 작업 분량 (추정)

- Tools 5개 (preview / place / modify / cancel / policy): ~250 LOC × 5 = ~1250 LOC
- Preview lifecycle (저장 / 소비 / 만료 / session 검증): ~300 LOC
- Trading policy 평가 엔진 (key별 평가, multi-policy AND): ~300 LOC
- Idempotency / paper-live / sanity (기존 [SPEC v1.7 초기 draft] §3 그대로): ~400 LOC
- Schema migration v2 (3 tables + repository): ~400 LOC
- ServerInstructions 트레이딩 단락: ~150 lines
- 테스트 (unit + integration): ~2000 LOC
- E2E (모의 full + 최소 실투): ~3-4일 manual
- 합계: ~4800 LOC code, **약 2-2.5주 작업**

---

## 13. 위험 / 미해결

### 13.1 LS 모의투자의 cover 범위

[[ls-virtual-returns-real-prices]] — 모의 = real *prices* 확정. 그러나 *주문 처리 로직* (부분 체결 / 사이드카 거절 / 시간외 거절) 도 실투와 동일한지는 v1.6 운영 중 + v1.7 진입 직후 검증.

### 13.2 LS 인증 token scope

v1.6에서 확인된 token scope가 actuator에도 적용되는지. 분리될 경우 `LsTokenCache` scope key 분리.

### 13.3 부분 체결 / 일부 취소

LS 응답에 부분 체결 표현 방식 확인 필요. v1.7 phase 1은 *단일 transition*만 (place → 즉시 응답). 부분 체결 후속은 사용자가 `ls_account_orders` 자체 모니터링.

### 13.4 ServerInstructions enforcement 강도

Claude Sonnet 4.6+ / Opus 4.7은 ServerInstructions 잘 따름. GPT-5 / Gemini 2.5는 약간 덜. **Tool-layer enforcement (preview 강제, policy hard block) 가 진짜 마지막 방어선** — ServerInstructions 약한 모델에서도 작동해야. 이미 도구 자체에 enforce.

### 13.5 Preview TTL 5분 적절성

장중 가격 변동 빠른 종목에선 5분 = stale 위험. Phase 2에서 *symbol-specific TTL* 검토 (변동성 큰 종목은 1-2분, 안정적이면 10분). v1.7은 5분 fixed (env override 가능).

---

## Appendix A — LLM 우회 시도 시나리오 (도구 단 거절 매트릭스)

| 시나리오 | LLM 행동 | 우리 반응 |
|---|---|---|
| 사용자 "그냥 사" | LLM이 `ls_place_order(qty=..., side=...)` 바로 호출 (preview 없이) | `PreviewRequired` 거절 |
| LLM 환각 — preview 위조 | `ls_place_order(preview_id="prv_fake_...")` | DB lookup 실패 → `InvalidPreviewId` |
| 같은 preview 두 번 사용 | `ls_place_order(preview_id=X)` 두 번 | 두 번째 `PreviewAlreadyConsumed` |
| 만료된 preview 사용 | 6분 후 `ls_place_order(preview_id=X)` | `PreviewExpired` |
| Cross-session preview 도용 | 다른 LLM 세션의 preview_id 사용 | session_id 미스매치 거절 |
| LLM이 policy 위반 회피 시도 | `ls_trading_policy(action="remove", ...)` 자체 호출 | ServerInstructions 위반 — 모델별. tool 단에선 *허용* (사용자가 동의했다고 가정). 검출 = audit log + 사용자 사후 review |
| Jailbreak — confirm/preview 무시 | preview_id 인자 누락 | tool schema validation 거절 (required 인자) |
| dry_run 모드로 실 발주 시도 | `ls_place_order(dry_run=true, live=true)` | dry_run 우선, 실 발주 X. `live` 무시 |
| live=true + env=virtual | env 가드 | `ModeMismatch` 거절 |
| live=true + live_account_allowed=false | policy 가드 | `PolicyViolation` 거절 (env 무관) |

핵심: **각 layer 독립 작동**. ServerInstructions가 약해도 tool layer 거절, tool layer 우회 시도되어도 schema validation 거절, schema 우회되어도 policy 거절.

---

## Appendix B — v1.7 후 follow-up backlog

- **Phase 2 order types** (v1.8?): 예약주문 / 조건부 / IOC / FOK / 신용 / 공매도
- **계좌 권한 확장**: 해외주식 거래 (v1.3 read-only를 actuator로) / 해외선물옵션 / ELW / 파생
- **Audit log read 도구**: `ls_audit_get(audit_id)` / `ls_audit_list(account, start, end)` — LLM 회수용
- **Symbol-specific preview TTL**: 변동성 기반 동적 TTL
- **Trading policy phase 2**: 일일 한도 (per-day cap), 시간대 제한 (장중만 발주), 카테고리 limit (섹터 비중)
- **Audit-based 학습**: LLM이 audit log 패턴 분석해 "당신이 보통 매수 후 1주일 안에 매도" 같은 인사이트 제공

이번 슬라이스에 포함 안 함.

---

## Appendix C — 자연 워크플로우 ↔ 우리 도구 매핑 (재인용)

워크플로우 엔진 진영이 사전 그려두는 매수/매도 DAG가 *LLM 대화 + 우리 도구* 조합으로 자연 표현됨:

**매수**:
| 워크플로우 단계 | LLM 호출 |
|---|---|
| 후보 종목 (관심종목 / screener) | `ls_search_stock`, `ls_run_screener`, `ls_watchlist` |
| 보유/제외 제거 | `ls_holdings_list` (filter) |
| 지표 평가 (RSI/MACD/MA 등) | `ls_get_chart`, `ls_add_indicator` (server-computed) |
| 후보 압축 (intersection/logic) | LLM 자체 reasoning |
| 현재가 | `ls_get_quote` |
| Position sizing | `ls_account_balance` + LLM 계산 |
| 주문 preview | `ls_preview_order` ★ |
| 확인 후 주문 | `ls_place_order(preview_id=...)` |

**매도**:
| 워크플로우 단계 | LLM 호출 |
|---|---|
| 보유 종목 | `ls_account_holdings` |
| 손절/익절/트레일링 조건 | `ls_get_quote` + LLM 평가 |
| Passed positions 추출 | LLM filter |
| 수량 계산 | LLM |
| 주문 preview | `ls_preview_order(side="sell", ...)` ★ |
| 확인 후 주문 | `ls_place_order(preview_id=...)` |

**핵심 차이**:
- 워크플로우 엔진: *사전에 그려진 DAG* — 변경하려면 다시 그림
- 우리: *LLM이 대화 속에서 매번 다시 reasoning* — 사용자 의도가 바뀌면 즉시 다른 흐름

두 패러다임이 같은 안전 패턴 (preview → confirm → execute) 을 공유하지만, *결정 주체*와 *유연성*이 다름 ([DESIGN-PRINCIPLES §5](./DESIGN-PRINCIPLES.md)).
