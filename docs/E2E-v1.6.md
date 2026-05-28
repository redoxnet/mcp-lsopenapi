# v1.6 E2E test prompts

MCP host (Claude Desktop / Cowork / Claude Code / Codex / VS Code Chat 등) 에서 한국어로 자연스럽게 던질 프롬프트 세트. 각 프롬프트의 *기대 동작*은 모델이 어떤 도구를 호출해야 하는지 + 응답 envelope에서 무엇이 보여야 하는지를 명시한다.

**사전 준비**:
1. `scripts/deploy-dev.ps1` 실행 (./deploy 최신화 — migration v7 + ls_accounts 테이블 신규 적용)
2. MCP host 재시작 (server는 `D:\Codes\mcp-lsopenapi\deploy\redoxnet-mcp-lsopenapi.exe`)
3. host config의 `LS_APPKEY` / `LS_APPSECRETKEY` 는 실투 키 페어, `LS_MARKET=real`
4. portfolio.db는 그대로 둠. v1.6-dev에서 broker='LS' 로 잘못 들어간 row가 있다면 migration v7이 자동으로 `ls_accounts` 테이블로 이주시킨다 (paper `accounts` 테이블에서는 사라짐).

응답을 paste할 때는 모델이 *어떤 도구를 골랐는지* + *최종 답변에 어떤 숫자/메타가 등장했는지* 둘 다 알려주세요.

> **v1.6 release-blocking fix**: dev 이터레이션에서는 paper-portfolio의 default account가 `_meta.account_used`를 가려서 모델이 유안타 paper portfolio를 LS 실계좌로 착각하던 문제가 있었음. release는 paper(accounts) ↔ live(ls_accounts) 테이블을 **물리적으로 분리**해서 이 클래스 버그를 구조적으로 차단. 자세한 내력은 `docs/LS-API-QUIRKS.md` §4.2e + `RELEASENOTES.Mcp.md` v1.6.0.

---

## A. 기본 흐름 (happy path × 10 tools)

각 프롬프트는 한 도구가 호출되어야 함. 모델이 다른 도구를 부르거나 "내가 모른다"고 답하면 routing 문제. **`ls_account_*` 10개 도구 모두 `account` 파라미터 없음** — appkey가 자체로 계좌를 결정.

### A1. ls_account_holdings (t0424)
```
LS 실계좌에 지금 뭐 들어있어?
```
**기대**: `ls_account_holdings()` 호출 → 보유 종목 표시. `_meta.account_used` shape = `{account_number, nickname?, mode, discovered, branch_name?, account_name?}` (구 shape의 `broker`/`is_default` 없음). `account_number`는 LS가 응답에 echo한 실제 AcntNo (예: `20856195501`). 첫 호출이면 `discovered=false` 가능 — t0424는 응답에 AcntNo가 없어서 cold-start에서는 synthetic이 됨.

### A2. ls_account_orders (t0425)
```
오늘 주문 현황 보여줘
```
**기대**: `ls_account_orders()` 호출. t0425 응답에 AcntNo 없음 → A1과 동일한 echo 동작.

### A3. ls_account_balance (CSPAQ12200)
```
내 LS 계좌 잔고 알려줘
```
**기대**: `ls_account_balance()` 호출 → 예수금 / D2 / 평가금액 / 매입금액. `_meta.tr_code: "CSPAQ12200"` (CSPAQ22200 아님). **이 호출이 핵심**: CSPAQ12200 응답의 OutBlock1.AcntNo + OutBlock2.BrnNm / AcntNm를 `ls_accounts`에 upsert → 이후 모든 `ls_account_*` 호출의 echo가 `discovered=true` + nickname/branch_name/account_name 채워서 나옴.

### A4. ls_account_bep (CSPAQ12300)
```
보유 종목 BEP 단가 알려줘
```
**기대**: `ls_account_bep()` 호출. 평균단가 등 표시. CSPAQ12300도 응답에 AcntNo + AcntNm가 있어서 A3 안 거쳤어도 첫 호출이면 여기서 auto-discovery 발화. SellPrc (BEP 매도가) 필드가 0으로 옴 — minor follow-up (별도 메모).

### A5. ls_account_order_history (CSPAQ13700)
```
어제 주문 내역 좀 보여줘
```
**기대**: `ls_account_order_history(order_date="...")` 호출. CSPAQ13700도 OutBlock1.AcntNo echo — auto-discovery 발화.

### A6. ls_account_transactions (CDPCQ04700)
```
최근 일주일 거래 내역 정리해줘
```
**기대**: `ls_account_transactions(start_date, end_date)` 호출. CDPCQ04700 OutBlock1.AcntNo echo. 입금/매수 row 표시.

### A7. ls_account_performance (FOCCQ33600)
```
이번 달 수익률 어때?
```
**기대**: `ls_account_performance(start_date, end_date, term="monthly"/"daily")` 호출. FOCCQ33600은 OutBlock2에 AcntNm까지 있어 wrapper가 캐시한다.

### A8. ls_account_daily_pnl (t0150)
```
오늘 매매일지 보여줘
```
**기대**: `ls_account_daily_pnl()` 호출 (today → t0150). t0150은 AcntNo 미echo — synthetic echo 가능.

### A9. ls_account_daily_pnl (t0151 — 어제)
```
어제 매매일지 보여줘
```
**기대**: `ls_account_daily_pnl(date="<어제>")` → t0151. t0151도 AcntNo 미echo.

### A10. ls_account_credit_limit (CSPAQ00600)
```
신용한도 얼마 남았어?
```
**기대**: `ls_account_credit_limit()` 호출 → `rsp_cd: "02062"` "신용계좌가 아닙니다" error envelope. 모델이 "이 계좌는 신용 거래 약정이 없어서 한도 조회가 막혀있다"고 narration.

### A11. ls_account_max_order_qty (CSPBQ00200)
```
지금 가격으로 삼성전자 최대 몇 주 살 수 있어?
```
**기대**: `ls_account_max_order_qty(symbol="005930", side="buy")` 호출. `margin_tiers` 응답 확인. CSPBQ00200도 OutBlock1.AcntNo + OutBlock2.AcntNm echo.

---

## B. 분리 검증 (paper portfolio vs LS broker live)

### B1. 두 출처 명확히 구분
```
내가 가진 모든 주식 보여줘. 종이 포트폴리오랑 실계좌 둘 다.
```
**기대**: 모델이 *두 도구를 모두 호출* — `ls_holdings_list` (paper) + `ls_account_holdings` (LS live). 둘 결과를 *별도로 narration*. ServerInstructions §"TWO distinct sources" 적용 확인.

### B2. 종이 포트폴리오만 (LS 호출 안 함)
```
내가 등록한 paper portfolio만 보여줘. 실계좌 말고.
```
**기대**: `ls_holdings_list` 만 호출. `ls_account_*` 호출 *안 함*.

### B3. LS 실계좌만
```
LS 실계좌에 있는 거 정확히 알려줘. 내가 따로 적어둔 거 말고.
```
**기대**: `ls_account_holdings` 호출. `ls_holdings_list` 호출 *안 함*.

### B4. paper 라벨 "LS증권" 자유 사용 (v1.6 schema-split 확인)
```
내 LS증권 종이 포트폴리오에 삼성전자 10주를 3만원에 매수한 걸로 기록해줘.
```
**기대**: 모델이 `ls_account(action="upsert", broker="LS증권", ...)`로 paper 계좌 등록 → `ls_holding(action="buy", account="...")` 호출. **여기 핵심**: paper의 `broker` 라벨이 "LS증권"이어도 live registry(`ls_accounts`)에 영향 없음. 다음 `ls_account_balance` 호출 결과의 `_meta.account_used`는 여전히 실제 LS appkey-bound 계좌. v1.6 release fix의 골든 시그널.

---

## C. 두 registry 라벨링

### C1. account_used echo 검증 (auto-discovery 직후)
```
LS 계좌 잔고 보여주고, 어떤 계좌인지도 같이 알려줘.
```
**기대**: `ls_account_balance` 호출 → response의 `_meta.account_used` shape:
```json
{
  "account_number": "20856195501",
  "nickname": null,
  "mode": "real",
  "discovered": true,
  "branch_name": "다이렉트206",
  "account_name": "김종현"
}
```
첫 호출이면 nickname은 null (자동 발견 시 부여 안 함). 모델이 branch_name + account_name으로 사용자에게 친절하게 설명.

### C2. nickname 부여
```
방금 LS 계좌 닉네임을 "실투-주식"으로 붙여줘.
```
**기대**: 모델이 `ls_account(action="set_live_nickname", account_number="20856195501", nickname="실투-주식")` 호출. 응답에 `updated.nickname: "실투-주식"`. 다음 `ls_account_*` 호출의 echo에 nickname 채워서 나옴.

### C3. 두 registry 같이 보기
```
등록된 계좌 다 보여줘.
```
**기대**: 모델이 `ls_account(action="list")` 호출. 응답 shape:
```json
{
  "paper_accounts": [/* 유안타-001, 카카오페이-001 등 */],
  "live_accounts":  [{ "account_number": "20856195501", "nickname": "실투-주식", "mode": "real", ... }]
}
```
모델이 두 그룹을 *분리해서* narration해야 함. 한 덩어리로 묶어 보이면 ServerInstructions 부족.

---

## D. 에러 envelope 검증

### D1. nickname 부여 실패 (존재 안 하는 account_no)
```
LS 계좌 번호 99999999999에 "테스트"라는 닉네임을 붙여줘.
```
**기대**: `ls_account(action="set_live_nickname", account_number="99999999999", nickname="테스트")` 호출 → response에 `{"error": "Not found.", "error_code": "LiveAccountNotFound", "account_number": "99999999999"}`. 모델이 "그런 LS 계좌 없습니다"로 narration.

### D2. LS business error pass-through
A10에서 이미 확인 (`CSPAQ00600` → `rsp_cd: "02062"`).

---

## E. 후속 follow-up 라우팅

### E1. holdings → 종목 정보
```
내 LS 계좌 보유 종목 중 첫 번째 종목 정보 자세히 알려줘.
```
**기대**: `ls_account_holdings` → 005930 식별 → `ls_get_stock_info(shcode="005930")` 호출.

### E2. holdings → 차트
```
내 보유 종목 일봉 차트 보여줘.
```
**기대**: `ls_account_holdings` → 005930 → `ls_get_chart(shcode="005930", period_type="day")`.

### E3. 거래 + 시세 결합
```
내가 어제 산 종목, 지금 가격이랑 비교해서 손익이 어떻게 됐는지 알려줘.
```
**기대**: `ls_account_order_history(order_date="<어제>")` 또는 `ls_account_daily_pnl(date="<어제>")` → 매수가 추출 → `ls_get_quote(...)` 호출 → 현재가와 비교 narration.

---

## F. 핵심 검증 시그널 체크리스트

각 호출에서 다음을 확인:

- [ ] **도구 라우팅**: 모델이 의도된 `ls_account_*` 도구를 골랐는지 (`ls_holdings_list` 같은 paper 도구로 잘못 가지 않았는지)
- [ ] **`_meta.account_used`**: shape = `{account_number, nickname, mode, discovered, branch_name?, account_name?}` — 구 shape의 `broker`/`is_default`가 **없어야** 함
- [ ] **`_meta.data_as_of`**: ISO 8601 timestamp 또는 yyyyMMdd (도구마다)
- [ ] **`_meta.tr_code`**: 정확한 TR 코드
- [ ] **`_meta.source: "live"`**: 모든 ls_account_* 호출에 등장
- [ ] **CSPAQ rsp_cd**: stderr 로그에서 `00136 / 00133 / 00200 / 00707` 등 non-zero success codes도 통과되어 나옴 — `LS_MCP_STDERR_LOG` 환경 변수로 stderr 캡처 가능
- [ ] **schema-split shape**: `ls_account(action="list")` 응답이 `{paper_accounts, live_accounts}` 형태 (구 shape의 flat array가 **아님**)
- [ ] **paper "LS증권" 라벨이 live를 가리지 않음** (B4 시나리오의 핵심)
- [ ] **ServerInstructions 효과**: 모델이 *narration*에서 "두 출처를 구분", "live REST snapshot", "v1.6은 read-only inquiry, 발주는 v1.7" 같은 문구를 자연스럽게 표현하는지

---

## 결과 보고 양식

새 세션에 다음 형식으로 paste해주시면 됩니다:

```
[A1] LS 실계좌에 지금 뭐 들어있어?
도구: ls_account_holdings
결과: 삼성전자 1주, avg 301,000 ...
_meta.account_used: {...}
이상한 점: 없음 / 있다면 어떤
```

이 정도면 새 세션이 곧장 release prep으로 진입 가능합니다.
