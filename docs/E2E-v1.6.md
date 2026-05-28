# v1.6 E2E test prompts

MCP host (Claude Desktop / Cowork / Claude Code / Codex / VS Code Chat 등) 에서 한국어로 자연스럽게 던질 프롬프트 세트. 각 프롬프트의 *기대 동작*은 모델이 어떤 도구를 호출해야 하는지 + 응답 envelope에서 무엇이 보여야 하는지를 명시한다.

**사전 준비**:
1. `scripts/deploy-dev.ps1` 실행 (./deploy 최신화)
2. MCP host 재시작 (server는 `D:\Codes\mcp-lsopenapi\deploy\redoxnet-mcp-lsopenapi.exe`)
3. host config의 `LS_APPKEY` / `LS_APPSECRETKEY` 는 실투 키 페어, `LS_MARKET=real`
4. portfolio.db는 그대로 둠 (auto-discovery 동작 확인 = `LS-virtual-20856195501` nickname 존재 — 이번 세션에서 자동 등록된 것; 새 세션도 이 row를 그대로 쓰면 됨)

응답을 paste할 때는 모델이 *어떤 도구를 골랐는지* + *최종 답변에 어떤 숫자/메타가 등장했는지* 둘 다 알려주세요.

---

## A. 기본 흐름 (happy path × 10 tools)

각 프롬프트는 한 도구가 호출되어야 함. 모델이 다른 도구를 부르거나 "내가 모른다"고 답하면 routing 문제.

### A1. ls_account_holdings (t0424)
```
LS 실계좌에 지금 뭐 들어있어?
```
**기대**: `ls_account_holdings` 호출 → 삼성전자 1주 (qty=1, avg≈301,000, cur=현재가) 표시. `_meta.account_used.account_number`에 `20856195501` 등장, `tr_code: "t0424"`, `source: "live"`.

### A2. ls_account_orders (t0425)
```
오늘 주문 현황 보여줘
```
**기대**: `ls_account_orders` 호출. 어제(2026-05-28) 매수 주문은 이미 체결됐으니 *오늘* 새 주문이 없으면 빈 결과. 모델은 "오늘 들어간 주문은 없습니다" 정직하게 narration.

### A3. ls_account_balance (CSPAQ12200)
```
내 LS 계좌 잔고 알려줘
```
**기대**: `ls_account_balance` 호출 → 예수금 500,000 / D2 198,990 / 평가금액 ≈300,000 / 매입금액 301,000 / 평가손익 ≈-1,000. `_meta.tr_code: "CSPAQ12200"` (CSPAQ22200 *아님* — 정정 확인).

### A4. ls_account_bep (CSPAQ12300)
```
보유 종목 BEP 단가 알려줘
```
**기대**: `ls_account_bep` 호출. 삼성전자 평균단가 ≈301,000~301,695 표시. SellPrc (BEP) 필드는 현재 0으로 옴 — minor follow-up (메모리 §"Minor follow-ups" 참조).

### A5. ls_account_order_history (CSPAQ13700)
```
어제 주문 내역 좀 보여줘
```
**기대**: `ls_account_order_history(order_date="2026-05-28")` 또는 모델이 그제 날짜 변환. 005930 매수 1주 #11028 체결 row 표시.

### A6. ls_account_transactions (CDPCQ04700)
```
최근 일주일 거래 내역 정리해줘
```
**기대**: `ls_account_transactions(start_date, end_date)`. 입금/매수 row 표시. 모델이 "입금 1건 + 매수 1건" 형식으로 narration.

### A7. ls_account_performance (FOCCQ33600)
```
이번 달 수익률 어때?
```
**기대**: `ls_account_performance(start_date, end_date, term="monthly" 또는 "daily")` 호출. 평가금액 변동 + 수익률 표시. 거래 1건만 있어 short period라 의미 있는 trend는 안 나올 수 있음 — 모델이 "데이터가 짧다"고 narration하면 좋음.

### A8. ls_account_daily_pnl (t0150)
```
오늘 매매일지 보여줘
```
**기대**: `ls_account_daily_pnl` 호출 (today → t0150). 오늘 매매가 없으면 `count=0, total=0` 빈 응답. `_meta.tr_code: "t0150"`.

### A9. ls_account_daily_pnl (t0151 — 어제)
```
어제 매매일지 보여줘
```
**기대**: `ls_account_daily_pnl(date="2026-05-28")` → t0151. 어제 005930 매수 1주 (수수료 10원, 매수금액 301,000) row 표시. `_meta.tr_code: "t0151"`.

### A10. ls_account_credit_limit (CSPAQ00600)
```
신용한도 얼마 남았어?
```
**기대**: `ls_account_credit_limit` 호출 → `rsp_cd: "02062"` "신용계좌가 아닙니다" error envelope. 모델이 "이 계좌는 신용 거래 약정이 없어서 한도 조회가 막혀있다"고 정직하게 narration.

### A11. ls_account_max_order_qty (CSPBQ00200)
```
지금 가격으로 삼성전자 최대 몇 주 살 수 있어?
```
**기대**: `ls_account_max_order_qty(symbol="005930", side="buy")` 호출. 현재가 기준 1주 가능 (D2 ≈199k / 300k = 0주이지만 100% 증거금률 계좌면 1주). `margin_tiers` 응답 확인.

---

## B. 분리 검증 (local portfolio vs LS broker live)

### B1. 두 출처 명확히 구분
```
내가 가진 모든 주식 보여줘. 종이 포트폴리오랑 실계좌 둘 다.
```
**기대**: 모델이 *두 도구를 모두 호출* — `ls_holdings_list` (local) + `ls_account_holdings` (LS live). 둘 결과를 *별도로 narration*. ServerInstructions §"TWO distinct sources" 적용 확인.

### B2. 종이 포트폴리오만 (LS 호출 안 함)
```
내가 등록한 paper portfolio만 보여줘. 실계좌 말고.
```
**기대**: `ls_holdings_list` 만 호출 (혹은 빈 결과면 `ls_watchlist` / `ls_holding`). `ls_account_*` 호출 *안 함*.

### B3. LS 실계좌만
```
LS 실계좌에 있는 거 정확히 알려줘. 내가 따로 적어둔 거 말고.
```
**기대**: `ls_account_holdings` 호출. `ls_holdings_list` 호출 *안 함*.

---

## C. 모드 + 자동 발견

### C1. account_used echo 검증
```
LS 계좌 잔고 보여주고, 어떤 계좌인지도 같이 알려줘.
```
**기대**: `ls_account_balance` 호출 → response의 `_meta.account_used`에서 `account_number: "20856195501"`, `nickname: "LS-virtual-20856195501"`, `mode: "virtual"` 등장. 모델이 nickname을 그대로 사용자에게 보여줘야 함. **이 nickname의 mode 라벨이 'virtual'인 건 LS_MARKET이 'virtual'로 주입됐던 흔적 — 실은 real 키 + real 데이터**. 모델이 헷갈리지 않게 nickname을 정정하려면 다음 프롬프트:

### C2. nickname 재라벨
```
방금 LS-virtual-20856195501로 잡혔는데, 사실 실투 계좌야. 닉네임 "실투-주식"으로 바꿔주고 default로 잡아줘.
```
**기대**: 모델이 `ls_account(action="upsert", account_number="20856195501", nickname="실투-주식", set_default=true)` 호출. 응답에 nickname 변경 확인. *주의*: 이 시점 `LS_MARKET=virtual`이면 portfolio.db의 mode='virtual' 그대로. 환경을 `LS_MARKET=real`로 바꾸고 재시작하면 새로운 real-mode 등록이 필요 (cross-mode account_no collision → InvalidOperationException 발생; 검증할 의향이 있다면 D2 시나리오 참조).

### C3. 실투/모의 분리 확인 (앱키 교체 시나리오)
```
LS_MARKET=virtual 그대로 두고, 다음 명령으로 결과를 확인해줘:
- 내 LS 계좌 잔고
```
이미 다음 단계를 위해 호스트 config의 `LS_MARKET`만 `real`로 바꾸고 서버 재시작 후:
```
이제 다시 내 LS 계좌 잔고 보여줘.
```
**기대**: mode 변경 후 portfolio.db에 real-mode row가 없으면 `ls_account_balance`가 *다시* auto-discovery → `LS-real-20856195501` 닉네임 새로 등록. account_no가 동일하지만 mode가 다르므로 column-level UNIQUE 위반... 잠시, 우리는 cross-mode를 `InvalidOperationException`으로 차단했음. 이 시나리오에서는:
- 만약 `UpsertAccountAsync`가 호출되면 → 이미 virtual-mode로 등록된 같은 account_no를 real로 upsert 시도 → `InvalidOperationException` raise
- `RecordDiscoveredAsync`는 catch + Debug log + null 반환 (fire-and-forget) → account_used echo가 synthetic shape으로 떨어짐 (`account_number=20856195501, nickname=null, mode=real, discovered=true`)

**검증 포인트**: 이 시나리오 발생 시 모델이 어떻게 narration하는지. discovered=true 라벨을 사용자에게 보여주면 idea — 사용자가 ls_account(action="remove")로 stale virtual row 정리 후 재등록 안내.

---

## D. 에러 envelope 검증

### D1. AmbiguousAccount (2+ 계좌, default 없음)
*전제*: portfolio.db에 LS 계좌 두 개 이상이 같은 mode에 있고 둘 다 `is_default=0`이어야 함. 현재 상태에선 발생 안 함 — 검증하려면 의도적으로 두 번째 계좌 등록 후 `is_default` 직접 SQL로 풀어야 함. 우선 skip 가능.

### D2. AccountNotFound
```
LS 계좌 "없는닉네임"의 잔고 보여줘.
```
**기대**: `ls_account_balance(account="없는닉네임")` 호출 → error envelope: `error_code: "AccountNotFound"`, `identifier: "없는닉네임"`, `candidates: [...]`. 모델이 candidates를 사용자에게 노출.

### D3. LS business error pass-through
A10에서 이미 확인 (`CSPAQ00600` → 02062).

---

## E. 후속 follow-up 라우팅

### E1. holdings → 종목 정보
```
내 LS 계좌 보유 종목 중 첫 번째 종목 정보 자세히 알려줘.
```
**기대**: `ls_account_holdings` → 005930 식별 → `ls_get_stock_info(shcode="005930")` 호출. 시가총액 / PER 등 추가 정보.

### E2. holdings → 차트
```
내 보유 종목 일봉 차트 보여줘.
```
**기대**: `ls_account_holdings` → 005930 → `ls_get_chart(shcode="005930", period_type="day")`. 차트가 인라인 (host가 SEP-1865 지원하면) 또는 분석 요약만.

### E3. 거래 + 시세 결합
```
내가 어제 산 종목, 지금 가격이랑 비교해서 손익이 어떻게 됐는지 알려줘.
```
**기대**: `ls_account_order_history(order_date="2026-05-28")` 또는 `ls_account_daily_pnl(date="2026-05-28")` → 005930 매수가 301,000 추출 → `ls_get_quote("005930")` 호출 → 현재가와 비교 narration.

---

## F. 핵심 검증 시그널 체크리스트

각 호출에서 다음을 확인:

- [ ] **도구 라우팅**: 모델이 의도된 `ls_account_*` 도구를 골랐는지 (`ls_holdings_list` 같은 local 도구로 잘못 가지 않았는지)
- [ ] **`_meta.account_used`**: `account_number`, `nickname`, `mode`, `is_default` 4개 필드가 모두 present
- [ ] **`_meta.data_as_of`**: ISO 8601 timestamp 또는 yyyyMMdd (도구마다)
- [ ] **`_meta.tr_code`**: 정확한 TR 코드 (CSPAQ12200 ✓, CSPAQ22200 ✗)
- [ ] **`_meta.source: "live"`**: 모든 ls_account_* 호출에 등장
- [ ] **에러 envelope**: `error_code` + `details` 구조; `account_used` echo도 포함 (resolver가 nicer label)
- [ ] **CSPAQ rsp_cd**: stderr 로그에서 `00136 / 00133 / 00200 / 00707` 등 non-zero success codes도 통과되어 나옴 — `LS_MCP_STDERR_LOG` 환경 변수로 stderr 캡처 가능
- [ ] **ServerInstructions 효과**: 모델이 *narration*에서 "두 출처를 구분", "live REST snapshot", "v1.6은 read-only inquiry, 발주는 v1.7" 같은 문구를 자연스럽게 표현하는지

---

## 결과 보고 양식

새 세션에 다음 형식으로 paste해주시면 됩니다:

```
[A1] LS 실계좌에 지금 뭐 들어있어?
도구: ls_account_holdings
결과: 삼성전자 1주, avg 301,000 ... 
이상한 점: 없음 / 있다면 어떤
```

이 정도면 새 세션이 곧장 release prep으로 진입 가능합니다.
