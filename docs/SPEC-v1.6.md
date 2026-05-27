# SPEC: v1.6.0 — Account inquiry + envelope cleanup

- **상태**: Draft (2026-05-27).
- **대상 버전**: v1.6.0
- **선행**: [SPEC-v1.5.md](./SPEC-v1.5.md), [SPEC-v1.4.md](./SPEC-v1.4.md), [LS-API-QUIRKS.md §7](./LS-API-QUIRKS.md) (특히 §7.11 — daemon-less 원칙).
- **범위**: 두 슬라이스를 한 릴리스에 묶음.
  1. **Account inquiry** — LS 브로커 계좌 *조회 전용* 10개 도구. 부작용 없음 (read-only REST). 주문 발주는 v1.7로 분리.
  2. **Date envelope cleanup** — v1.4의 `query_date_resolution` + `WeekendOnlyCalendar` + `ITradingCalendar` + `DateEnvelope.Resolve` *추상화 전체 삭제*. 잔여 ~10개 도구에 단순 `data_as_of` echo 추가.
- **메시지**: v1.6은 *기능 추가 + 정직화*. LS가 source of truth로 들고 있는 계좌 상태를 신선하게 노출 (캐싱 없음). v1.4가 만들었던 *redundant date classification* 추상화는 LLM의 캘린더 지식과 중복이라 폐기.
- **비범위**: 주문 발주(v1.7), WebSocket/daemon/sidecar(영구 거부, [LS-API-QUIRKS §7.11](./LS-API-QUIRKS.md)), 별도 `ls_account.db` (단일 portfolio.db 유지).

---

## 1. 컨텍스트

### 1.1 v1.5 → v1.6

v1.5가 chart narration 정직화에 좁게 집중한 single-slice 릴리스였다면, v1.6은 *기능 추가 + 추상화 정리* 두 슬라이스를 묶음. 둘 다 단일 테마: **"LS가 가진 것은 fresh REST로 노출, 우리가 잘못 추상화한 것은 삭제"**.

### 1.2 v1.6 직전 — 2026-05-27 design session 결정

긴 design session에서 v1.6 daemon 슬라이스 (NWS/JIF/SCx WebSocket) 후보를 차례로 검토한 결과 *전부 reject*. 핵심 결론은 [LS-API-QUIRKS.md §7.11](./LS-API-QUIRKS.md) — *MCP는 본질적으로 request-response이고 LLM은 episodic이라 push/streaming/daemon 인프라가 맞지 않음*. 영구 원칙으로 [[mcp-realtime-skeptic]] 메모리에 박힘.

이 결정의 *positive* 귀결: 트레이딩 슬라이스가 *순수 REST로* 가능하다는 사실. 그것을 v1.6 (inquiry) + v1.7 (actuator) 두 단계로 분리.

### 1.3 v1.4 envelope 추상화의 진단

v1.4 슬라이스 A가 만든 것:
- `RedoxNet.LsOpenApi.Core.Time.ITradingCalendar` 인터페이스
- `RedoxNet.LsOpenApi.Core.Time.WeekendOnlyCalendar` 구현
- `RedoxNet.LsOpenApi.Core.Time.DateEnvelope.Resolve` 메서드
- 응답 필드 `query_date_resolution` (`used` / `weekend` / `holiday` / `future_date` / `pre_market`)
- 2개 도구에 wire (`ls_get_market_funds_trend`, `ls_get_short_selling_trend`)
- v1.5+에 KRX/NYSE 휴장 캘린더 테이블 + 잔여 10개 도구 wire가 backlog

문제:
1. `query_date_resolution` 분류는 *LLM이 이미 자기 캘린더 지식 + 빈 데이터 신호로 더 정확히 추정*. 한글날/추석/임시휴장을 우리 캘린더가 알기보다 LLM이 더 잘 앎.
2. `WeekendOnlyCalendar`는 *주말만 알고 휴장은 모름* — LLM의 KR 공휴일 지식보다 strictly 부정확. 잘못된 신호 송출.
3. KRX/NYSE 휴장 캘린더 테이블 backlog는 ongoing 유지보수 부담 — 매년 KRX 공휴일 발표마다 우리 캘린더 업데이트. LLM은 이미 reliable.
4. 동일 anti-pattern을 design session 다른 곳에서도 발견 ([mcp-realtime-skeptic]] 원칙) — *LLM이 이미 아는 것은 우리가 데이터화하지 않음*.

`data_as_of` (응답 데이터 시점 timestamp) + `query_date_echo` (사용자 입력 반향) 두 필드는 *분류가 아니라 사실*이라 유지.

---

## 2. 디자인 — Sub-change 1: Account inquiry tools (10)

### 2.1 도구 일람

LS REST `/stock/accno` + `/stock/order` 의 조회용 TR을 wrapping. **출처 표시 prefix `ls_account_*`** — `ls_holding*` (portfolio.db 사용자 수동 입력) 와 명확히 구분.

| 도구 | TR | 역할 |
|---|---|---|
| `ls_account_holdings(account?)` | `t0424` | 현재 LS 잔고 (실시간) |
| `ls_account_orders(date?, status?)` | `t0425` | 오늘 체결/미체결 |
| `ls_account_balance(account?)` | `CSPAQ12200` / `CSPAQ22200` | 예수금 / 주문가능금액 / 총평가 |
| `ls_account_order_history(account, start, end)` | `CSPAQ13700` | 기간별 주문체결내역 |
| `ls_account_transactions(account, start, end)` | `CDPCQ04700` | 거래내역 (입출금 + 체결 통합) |
| `ls_account_performance(account, start, end)` | `FOCCQ33600` | 기간별 수익률 |
| `ls_account_daily_pnl(date?)` | `t0150` / `t0151` | 당일/전일 매매일지 + 수수료 |
| `ls_account_bep(account, symbol?)` | `CSPAQ12300` | BEP 단가 (보유 종목 평균) |
| `ls_account_credit_limit(account)` | `CSPAQ00600` | 신용한도 |
| `ls_account_max_order_qty(account, symbol, margin_rate?)` | `CSPBQ00200` | 종목별 최대 주문 가능 수량 (조회만, 발주 X) |

### 2.2 출처 disambiguation

ServerInstructions에 명시:

> `ls_holding*` = portfolio.db에 사용자가 *수동* 기록 (paper portfolio / 멀티 브로커). `ls_account_*` = LS 실제 계좌 *실시간* (LS REST). 두 출처는 일치하지 않을 수 있음 — 의도적.

### 2.3 Account 인자 처리

기존 portfolio.db `accounts` 테이블의 `is_default` 활용:
- `account` 인자 미지정 → portfolio.db에서 default account 조회 후 그 `account_number` 사용
- default 없고 등록 계좌 0개 → `RequiresAccount` envelope 반환 (`ls_account(action="upsert")` 안내)
- default 없고 등록 계좌 2개+ → `AmbiguousAccount` envelope + candidates 배열
- 위 패턴은 v0.7 portfolio 도구의 account 처리 규약과 동일 — 새 컨벤션 안 만듦.

### 2.4 응답 envelope

모든 `ls_account_*` 응답에:
```jsonc
{
  "data": { /* TR 응답 정규화 */ },
  "_meta": {
    "account_used": { "account_number": "...", "nickname": "...", "broker": "LS", "is_default": true },
    "data_as_of": "2026-05-27T15:30:00+09:00",   // 데이터 시점 (조회 시각)
    "tr_code": "t0424",
    "source": "live"                              // 항상 live (캐시 없음)
  }
}
```

`account_used`는 *어떤 계좌가 조회됐는지* 명시 — 멀티 계좌 환경에서 LLM이 헷갈리지 않도록.

### 2.5 캐싱 정책

**캐싱 안 함**. 모든 호출이 LS REST로 fresh. 이유는 [[mcp-realtime-skeptic]] 원칙 — 계좌 상태는 *돈*과 직결이라 staleness 위험이 캐싱 이득보다 큼.

성능 우려 (반복 호출): LS rate limit가 보통 1-2/sec 수준이라 LLM 채팅 흐름에 충분.

### 2.6 권한 / 인증

`/stock/order` + `/stock/accno`는 *계좌 권한 token*이 필요할 수 있음 — 시세 token과 다를 가능성. 기존 `LsTokenCache` ([RedoxNet.LsOpenApi.Core.Auth.LsTokenCache.cs](../src/RedoxNet.LsOpenApi.Core/Auth/LsTokenCache.cs))의 token scope 확인 필요. 한 가지 token으로 모두 커버되면 그대로, 분리되면 cache key에 scope 추가.

실측: 첫 도구 (`ls_account_holdings`) 구현 시 `LSOPENAPI_VIRTUAL=1`로 모의투자 계좌에서 호출하여 token scope 확인.

---

## 3. 디자인 — Sub-change 2: Date envelope cleanup

### 3.1 삭제 (코드)

**Core**:
- `src/RedoxNet.LsOpenApi.Core/Time/ITradingCalendar.cs` (인터페이스)
- `src/RedoxNet.LsOpenApi.Core/Time/WeekendOnlyCalendar.cs` (구현)
- `src/RedoxNet.LsOpenApi.Core/Time/DateEnvelope.cs`의 `Resolve(...)` 메서드 — 클래스 자체가 `Resolve` 단일 entry면 클래스 통째로

**DI**:
- `ITradingCalendar` / `WeekendOnlyCalendar` 등록 라인 제거 (`Program.cs` 또는 `ServiceCollectionExtensions`)

**Tests**:
- `WeekendOnlyCalendar` unit tests
- `DateEnvelope.Resolve` resolution 분류 tests
- `holiday` placeholder tests (실제로 never fires)

순 LOC 삭제 ~300.

### 3.2 응답 shape 축소

`query_date_resolution` 필드 제거 — v1.4에서 wire된 2개 도구:
- `ls_get_market_funds_trend` (t8428)
- `ls_get_short_selling_trend` (t1927)

`data_as_of` + `query_date_echo`는 유지 (사실, not 분류).

**ToolSurfaceFreezeTests 영향**: 필드 *삭제*는 freeze 정책상 *breaking response shape change* — pin 파일 업데이트 필요. release notes에 "v1.6 response shape consolidation"으로 명시.

### 3.3 추가 — `data_as_of` echo (10개 도구)

기존 2개 + 추가 10개 = total 12개 date-bearing 도구에 `data_as_of` 균일 부착.

**Stage 1 (snapshot 조회 — 기존 2개와 동일 패턴, ~30 LOC each)**:
- `ls_get_top_stocks` (t1441/t1444/t1452/t1463/t1466)
- `ls_get_high_low_stocks` (t1305/t1308)
- `ls_get_investor_flow` (t1716/t1717)
- `ls_get_industry_indices` (t8424/t1511)
- `ls_get_fundamentals_rank` (t1601)
- `ls_get_market_warnings` (t1404/t1405)

**Stage 2 (range 조회 — 마지막 row 시점 echo)**:
- `ls_get_program_trading` (t1662/t1633/t1636/t1637)
- `ls_get_index_history` (t8425)
- `ls_get_chart` (t8410/t8411/...)
- `ls_get_overseas_chart` (g3103/g3202/g3203/g3204)

**Quote 도구 (optional, `market_close_date` 또는 `data_as_of` 동등 필드)**:
- `ls_get_quote`
- `ls_get_overseas_quote`
- `ls_get_index_quote`

순 LOC 추가 ~300.

### 3.4 ServerInstructions 변경

v1.5 ServerInstructions에 다음 단락 *추가*:

> **장 운영 상태**는 우리가 알려주지 않습니다. 휴장 / 동시호가 / 사이드카 / 서킷브레이크 / 시간외 단일가 등 시장 세션 상태는 LLM의 시계 + 캘린더 지식으로 판단하세요. 비정상 가격 패턴 (장중인데 거래 정지된 듯한 호가 등) 발견 시 사용자에게 *WTS/MTS에서 확인*을 권유하세요. 우리 도구는 실시간 시장 상태 push를 제공하지 않습니다.

v1.4가 추가한 *date envelope 해석 가이드* 단락은 *교체* — `query_date_resolution` 사라졌으니 해석 안내도 사라짐.

---

## 4. 디자인 — Sub-change 3: ServerInstructions 통합

### 4.1 출처 disambiguation 단락 (신규)

§2.2의 한 줄 + 더 상세히:

> LS-LsOpenApi는 *사용자 수동 기록*과 *LS 실계좌 실시간* 두 출처를 분리해서 노출합니다:
>
> - `ls_holding*`, `ls_holdings_list`, `ls_portfolio_io`, `ls_account(action=...)` — **portfolio.db** (사용자가 직접 입력/관리). paper portfolio, 멀티브로커 (LS 외 키움/미래에셋 등) 트래킹용. LS와 자동 동기화되지 않음.
> - `ls_account_holdings`, `ls_account_balance`, `ls_account_orders`, `ls_account_*` — **LS 실제 계좌 실시간**. LS REST 호출 직전 시점의 진실.
>
> 두 출처가 *의도적으로 다를 수 있음* — 사용자가 paper portfolio로 가설 테스트하면서 실계좌는 별도 운영하는 패턴이 정상.

### 4.2 비범위 명시 단락 (신규)

> v1.6은 *주문 발주 도구를 제공하지 않습니다*. `ls_place_order` / `ls_cancel_order` / `ls_amend_order`는 v1.7에서 안전장치 (paper/실투 toggle, idempotency, confirmation pattern, 발주 audit log) 와 함께 도입 예정.

---

## 5. Tool surface 변화

| | Before (v1.5.1) | After (v1.6.0) |
|---|---|---|
| `standard` | 40 | **50** (+10 inquiry) |
| `all` | 43 | **53** (+10 inquiry) |

응답 shape 변화 (`query_date_resolution` 제거)는 *2개 도구*에 영향 — `additive` 변화가 아니라 `breaking response shape` (response field 삭제) — release notes에 명시.

`ToolSurfaceFreezeTests` 갱신:
- 도구 카운트 pin: 40/43 → 50/53
- 2개 도구 response shape pin: `query_date_resolution` 제거

---

## 6. Schema 변경

**없음**. portfolio.db 스키마 v1 그대로 유지. v1.7에서 `order_audit` 테이블 추가 (migration v2).

---

## 7. 비범위

- **주문 발주 도구** — v1.7
- **WebSocket / daemon / sidecar / 명명 파이프 IPC / schtasks 인스톨러** — 영구 거부 ([LS-API-QUIRKS §7.11](./LS-API-QUIRKS.md))
- **별도 `ls_account.db`** — 단일 portfolio.db 유지. LS 데이터는 캐싱하지 않음.
- **KRX/NYSE 휴장 캘린더 테이블** — 영구 취소 (LLM 지식 활용)
- **계좌 실시간 push (잔고 변동 알림 등)** — daemon 거부와 동일 논리
- **다중 LS API 키 / 다중 LS 계정 지원** — 한 LS 계정 (multiple sub-accounts)만. 진짜 multi-account는 future.

---

## 8. 테스트 계획

### 8.1 새 inquiry 도구 (각 도구당)

- **모의투자 (`LSOPENAPI_VIRTUAL=1`)** 에서 happy path — 응답 정규화 + `_meta.account_used` + `_meta.data_as_of` 확인
- account 인자 미지정 + default 있음 → default 자동 사용
- account 인자 미지정 + default 없음 + 등록 0개 → `RequiresAccount`
- account 인자 미지정 + default 없음 + 등록 2개+ → `AmbiguousAccount` + candidates
- 잘못된 account_no → LS REST의 `rsp_cd` 에러 → 우리 wrapper의 친화적 변환
- date 인자 (해당 도구만) — past / today / future / weekend 케이스

### 8.2 Envelope cleanup

- `WeekendOnlyCalendar` 클래스 삭제 후 빌드 성공
- `query_date_resolution` 필드 제거 후 v1.4 wire된 2 도구 응답 shape 검증
- 새 10개 도구의 `data_as_of` echo 정확성 (마지막 row의 date와 일치)
- ToolSurfaceFreezeTests 갱신 후 green

### 8.3 통합 (E2E)

- 모의투자 환경에서 Claude Desktop 또는 Codex로:
  - "내 LS 잔고 보여줘" → `ls_account_holdings` 호출 + portfolio.db `holdings`와 다른 결과 (의도된 분리)
  - "어제 거래 내역" → `ls_account_daily_pnl(date=어제)` 호출
  - "이번 달 수익률" → `ls_account_performance(start, end)` 호출
  - "주말 (토요일) 시장자금 흐름" → `ls_get_market_funds_trend(query_date=토요일)` → `data_as_of`로 최근 거래일 echo, **`query_date_resolution` 필드 *없음* 확인**
  - LLM이 빈 응답 + 자기 캘린더 지식으로 "한글날이라 휴장" 자체 추정 — *우리 라벨 없이도 잘 동작* 확인

### 8.4 회귀

- 기존 portfolio 도구 (`ls_holding*`, `ls_account(action=...)`) 무영향 검증
- v1.5 chart-emitting 도구 `_meta.render_status` 무영향 검증
- README hero 예시 (`"오늘 거래대금 상위"`, `"엔비디아 일봉"`) 작동 — 추가 `data_as_of` 필드는 hero text와 무관

---

## 9. Release notes preview (Mcp)

```markdown
## v1.6.0 — 2026-XX-XX

**LS broker account inquiry (read-only) + date envelope correction.**

### Added — Account inquiry tools (10)

Real-time read-only access to your LS Securities brokerage account.
All tools query LS REST live — no caching, no daemon, no installed
service. Source prefix `ls_account_*` distinguishes these from the
user-managed `ls_holding*` portfolio tools (which remain unchanged).

- `ls_account_holdings(account?)` — current LS broker positions
- `ls_account_orders(date?, status?)` — today's fills and pending orders
- `ls_account_balance(account?)` — cash + buying power + total valuation
- `ls_account_order_history(account, start, end)` — order/fill history
- `ls_account_transactions(account, start, end)` — full transaction log
- `ls_account_performance(account, start, end)` — period P&L
- `ls_account_daily_pnl(date?)` — today / yesterday trade log + fees
- `ls_account_bep(account, symbol?)` — break-even price per holding
- `ls_account_credit_limit(account)` — credit margin limit
- `ls_account_max_order_qty(account, symbol, margin_rate?)` — max orderable quantity (inquiry only — does NOT place an order)

Account argument follows the existing portfolio convention: omitted →
default account; ambiguous → `AmbiguousAccount` with candidates.

### Changed — Date envelope simplification (breaking response shape)

The v1.4 `query_date_resolution` field (`used` / `weekend` / `holiday`
/ `future_date` / `pre_market`) is **removed** from the two tools that
emitted it (`ls_get_market_funds_trend`, `ls_get_short_selling_trend`).
Rationale: the field's classification duplicated information that
modern LLMs derive more accurately from their own clock + Korean
holiday knowledge + the empty-data signal. The internal abstractions
(`ITradingCalendar`, `WeekendOnlyCalendar`, `DateEnvelope.Resolve`)
are deleted.

The `data_as_of` and `query_date_echo` fields remain — they carry
*facts*, not classifications, and are useful for the LLM to anchor
analysis to a concrete date.

The planned KRX/NYSE holiday calendar tables (v1.5+ backlog) are
**cancelled** for the same reason. See [docs/SPEC-v1.6.md §1.3](docs/SPEC-v1.6.md)
for the full reasoning.

### Added — `data_as_of` echo across remaining date-bearing tools

The 10 date-bearing tools that didn't yet emit `data_as_of` now do:
`ls_get_top_stocks`, `ls_get_high_low_stocks`, `ls_get_investor_flow`,
`ls_get_industry_indices`, `ls_get_fundamentals_rank`,
`ls_get_market_warnings`, `ls_get_program_trading`,
`ls_get_index_history`, `ls_get_chart`, `ls_get_overseas_chart`. Quote
tools (`ls_get_quote`, `ls_get_overseas_quote`, `ls_get_index_quote`)
gain an analogous `market_close_date` companion to `timestamp`.

### Surface

Standard: 40 → 50. All: 43 → 53. No existing tool removed or renamed.

### ServerInstructions

- New paragraph distinguishing `ls_holding*` (user-managed) vs
  `ls_account_*` (LS live) sources
- New paragraph clarifying that we do not push market session state —
  LLM should use its own clock + calendar knowledge
- v1.4's date-envelope interpretation paragraph removed (superseded)
- Explicit non-goal: order placement deferred to v1.7

### Not in v1.6

- **Order placement** — v1.7 ships `ls_place_order` / `ls_amend_order`
  / `ls_cancel_order` along with the safety machinery (paper-trading
  default, idempotency tokens, confirmation patterns, audit log).
- **WebSocket / daemon / sidecar** — see [docs/LS-API-QUIRKS.md §7.11](docs/LS-API-QUIRKS.md)
  for the design rejection.
- **Separate `ls_account.db`** — LS REST is the source of truth;
  no local cache of broker state.
```

---

## 10. 작업 분량 (추정)

- Account inquiry 도구 10개: ~150 LOC × 10 = ~1500 LOC + 테스트 ~1000 LOC = **~2500 LOC**
- Envelope cleanup: 삭제 ~300 LOC + 추가 ~300 LOC + 테스트 ~200 LOC = **~800 LOC**
- ServerInstructions: ~50 lines
- 합계: ~3500 LOC, **약 1.5-2주 작업**

E2E 검증 (모의투자 계좌 + Claude Desktop) 별도 ~1-2일.

---

## Appendix A — Account 인자 default 패턴 (기존 v0.7 컨벤션 인용)

[v0.7 portfolio multi-account spec](./SPEC-portfolio-multi-account.md)에서 확립된 패턴 그대로 채택:

| 호출 패턴 | 0 accounts | 1 account | 2+ accounts |
|---|---|---|---|
| `ls_account_holdings()` (account 미지정) | `RequiresAccount` | auto + echo in `_meta.account_used` | `AmbiguousAccount` + candidates |
| `ls_account_holdings(account="...")` | LS가 거부 (잘못된 계좌) | OK | OK |
| `ls_account_holdings()` + default 설정 있음 | n/a | auto | **default 자동** + echo |

`AmbiguousAccount` envelope:
```jsonc
{
  "error": "AmbiguousAccount",
  "message": "2개 계좌가 등록되어 있습니다. account 인자를 명시하거나 ls_account(action=\"upsert\", set_default=true)로 default를 설정하세요.",
  "candidates": [
    { "account_number": "123-456789-01", "nickname": "주식", "broker": "LS", "is_default": false },
    { "account_number": "123-456789-02", "nickname": "ISA", "broker": "LS", "is_default": false }
  ]
}
```

LLM이 candidates를 보고 *user prompt 없이* 재호출 가능.

---

## Appendix B — TR 응답 정규화 노트

LS TR 응답은 한국어 필드명 + 압축된 numeric type이 많음. 우리 도구는 응답을 *영어 snake_case + 명시적 type*으로 정규화. 예 `t0424` (주식잔고2):

| LS 원본 필드 | 정규화 후 |
|---|---|
| `expcode` | `symbol` |
| `hname` | `name` (단, [LS-API-QUIRKS §1.1](./LS-API-QUIRKS.md) padding 처리 — 기존 `CompactName` 헬퍼 재사용) |
| `appamt` | `evaluation_amount` (long, KRW) |
| `pamt` | `purchase_amount` (long, KRW) |
| `dtsunik` | `unrealized_pnl` (long, KRW) |
| `sunikrt` | `unrealized_pnl_rate` (decimal, % — *100배 안 곱해진 raw* — [LS-API-QUIRKS §3](./LS-API-QUIRKS.md) 확인 필요) |
| `mdposqt` | `quantity` (int) |
| `janqty` | `available_quantity` (int) |

도구 구현 시 각 TR의 정확한 필드 mapping을 wrapper 안에 captured + unit test로 pin.
