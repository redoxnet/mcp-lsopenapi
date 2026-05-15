# SPEC: Portfolio Multi-Account, Buy/Sell Semantics, Corporate Actions

- **상태**: Draft
- **작성일**: 2026-05-15
- **대상 버전**: v0.5.0
- **작성자**: Jong Hyun
- **선행**: [AGENTS-PATCH-005-portfolio.md](./6.%20AGENTS-PATCH-005-portfolio.md) (v0.1 단일 계좌 기반)

## 1. 컨텍스트

v0.4까지 portfolio 모듈은 단일 계좌 가정으로 출시됐다. 실 사용 E2E에서 다음 한계가 드러남:

- 다중 계좌 보유(한투, KB, ISA 등 동시 사용)가 한국 개인 투자자 표준 형태인데 도구가 한 계좌만 노출.
- `ls_holdings_add` 의미가 "현재 상태 set"인지 "추가 매수"인지 모호. ON CONFLICT가 무조건 replace라 "5주 더 샀어"가 의도와 어긋남.
- 액면분할/무상증자 같은 corporate action 발생 시 cost basis가 자동 보정되지 않아 `current_value`, `pnl` 계산 결과가 가짜 손실로 표시됨.

이 패치는 위 3가지를 v0.5에서 한꺼번에 정리한다. 실 brokerage 동기화는 하지 않으므로 **로컬 노트패드 정확성** 수준에 맞춰 설계한다.

## 2. 결정

| # | 결정 | 비고 |
|---|------|------|
| 1 | placeholder seed 제거. 계좌 0개 상태 허용 | empty state는 도구 응답에서 `RequiresAccount` 에러 envelope로 안내 |
| 2 | account 식별은 nickname + account_number 양쪽 수용, 둘 다 오면 account_number 우선 | 사용자는 대부분 nickname 멘탈모델 ("주식1", "ISA") |
| 3 | broker는 free text, `ls_broker_rename(from, to)`로 일괄 갱신 | 같은 broker_name의 모든 계좌 nickname 머지 시 충돌 검사 |
| 4 | `is_default` 컬럼 유지, 정확히 0 또는 1개. 0개 상태도 허용 | 마지막 계좌 삭제 시 default도 자연스럽게 해제 |
| 5 | Ambiguity 정책: 읽기는 폴백, 쓰기는 명시 요구 | 표 2.2 참조 |
| 6 | `ls_holdings_list` 응답은 **항상 grouped** | `accounts:[…]` + `total_summary`. 단일 계좌는 길이 1 |
| 7 | `ls_account_remove`는 2단계 confirm | 보유종목 있으면 `RequiresConfirmation` 에러, `cascade=true`로 재호출 |
| 8 | holdings 도구를 3개로 분리 | `_set` (replace), `_buy` (incremental merge), `_sell` (incremental subtract) |
| 9 | 분할/증자 도구 3개 | `_split`, `_reverse_split`, `_bonus`. account 미지정 시 해당 종목 보유한 모든 계좌 일괄 적용 |
| 10 | 의심 가격 차이 경고 | list 응답에 `\|current_price/avg_price - 1\| > 5`면 분할 가능성 warning 부착 |

### 2.1 Ambiguity 매트릭스

| 도구 | 0개 계좌 | 1개 | 2개+ |
|---|---|---|---|
| `ls_holdings_list`, `ls_accounts_list` | 빈 응답 | auto | grouped (default 우선) |
| `ls_holdings_set` / `_buy` | error `RequiresAccount` | auto (1개로 폴백) | error `AmbiguousAccount` |
| `ls_holdings_sell` / `_update`* / `_remove` | error | 명시 권장, auto | error `AmbiguousAccount` |
| `ls_holdings_split` / `_reverse_split` / `_bonus` | n/a | auto | account 미지정 시 **해당 종목 보유한 모든 계좌**에 일괄 |

*v0.5에서는 `_set`/`_buy`/`_sell`이 `_update`를 대체.

### 2.2 에러 envelope

```json
{
  "error": "AmbiguousAccount",
  "message": "Holding '005930' exists in 2 accounts. Specify the account.",
  "candidates": [
    { "account_number": "12345-01", "nickname": "한투", "broker": "한국투자" },
    { "account_number": "67890-22", "nickname": "ISA",  "broker": "한국투자" }
  ]
}
```

`AccountNotFound`, `RequiresAccount`, `RequiresConfirmation`도 동일 envelope. `candidates`는 항상 현재 계좌 목록 동봉 → LLM이 정정 호출에 사용.

## 3. 데이터 모델

### 3.1 마이그레이션 v2

```sql
-- Seed 단일 placeholder 행 제거. 기존 'UNSET' 행이 있으면 보유종목 없을 때만 제거.
DELETE FROM accounts
WHERE account_no = 'UNSET'
  AND nickname = '기본 계좌'
  AND id NOT IN (SELECT account_id FROM holdings);

-- nickname UNIQUE 제약. 기존 데이터는 단일 placeholder뿐이라 충돌 없음.
CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_nickname ON accounts(nickname);
```

### 3.2 컬럼 추가 없음

기존 v1 스키마(`accounts.id/account_no/nickname/broker/is_default/created_at`, `holdings.account_id` FK)로 충분. `account_no`는 이미 UNIQUE.

### 3.3 무결성 제약

- 정확히 0 또는 1개 row가 `is_default = 1` (애플리케이션 레벨에서 보장)
- 마지막 계좌 삭제 시 default는 자동으로 0개가 됨
- 계좌 추가 시 (a) 기존 default가 없으면 자동 default (b) `set_default=true`로 명시하면 기존 default 해제

## 4. 도구 surface

### 4.1 계좌 관리 (5개)

| 도구 | 인자 | 동작 |
|---|---|---|
| `ls_accounts_list` | — | 모든 계좌 + 보유종목 카운트 + default 표시. 0개면 빈 배열 |
| `ls_account_upsert` | `account_number`, `nickname`, `broker`, `set_default?=false` | account_number 기준 upsert. nickname 충돌 시 에러 |
| `ls_account_set_default` | `account` (nickname 또는 account_number) | is_default 토글. 트랜잭션 |
| `ls_account_remove` | `account`, `confirm?=false` | 보유종목 0개면 즉시 삭제. 1+이면 `RequiresConfirmation` 에러 + 카운트/평가금액. confirm=true면 cascade |
| `ls_broker_rename` | `from`, `to` | `accounts.broker = to WHERE broker = from`. UNIQUE 위반(머지 대상 nickname 충돌) 시 에러 |

기존 `ls_account_get`은 유지(default 단건 조회). 기존 `ls_account_set`은 제거(개명 X — 의미가 바뀌어서 silent break보다 깨끗한 제거).

### 4.2 보유종목 (7개)

| 도구 | 인자 | 동작 |
|---|---|---|
| `ls_holdings_list` | `account?` | grouped 응답. account 필터 시 `accounts` 길이 1 |
| `ls_holdings_set` | `shcode`, `quantity`, `avg_price`, `note?`, `account?` | replace. 기존 row 있으면 덮어씀 |
| `ls_holdings_buy` | `shcode`, `quantity`, `price`, `account?` | merge. new_qty = old+qty, new_avg = (old.qty×old.avg + qty×price)/new_qty. 새 행이면 set과 동일 |
| `ls_holdings_sell` | `shcode`, `quantity`, `account?` | subtract. new_qty = old-qty. avg 유지. new_qty==0이면 row 삭제. old<qty면 error |
| `ls_holdings_remove` | `shcode`, `account?` | 행 완전 삭제 |
| `ls_holdings_split` | `shcode`, `ratio` (int ≥ 2), `account?` | qty ×= ratio, avg /= ratio. account 미지정 시 보유 모든 계좌 |
| `ls_holdings_reverse_split` | `shcode`, `ratio` (int ≥ 2), `account?` | qty /= ratio, avg ×= ratio. 나눠 떨어지지 않으면 error (정수 보유 가정) |
| `ls_holdings_bonus` | `shcode`, `ratio` (double > 0), `account?` | qty ×= (1 + ratio), avg /= (1 + ratio). 무상증자 0.1 → 10% 증가 |

기존 `ls_holdings_add` / `_update`는 제거. 의미 충돌 방지.

### 4.3 관심종목 / 섹터

- 신설: `ls_watchlist_group_rename(old_name, new_name)`
- 나머지 v0.4 도구 그대로 유지
- `ls_watchlist_add` 재호출 시 ON CONFLICT(group_id, symbol) DO UPDATE notes로 메모 갱신 가능 — v0.5에서 도구 추가 없이 검증으로 확인

### 4.4 응답 모양

`ls_holdings_list`:

```json
{
  "accounts": [
    {
      "account_number": "12345-01",
      "nickname": "한투",
      "broker": "한국투자",
      "is_default": true,
      "holdings": [
        {
          "shcode": "005930",
          "name": "삼성전자",
          "quantity": 12,
          "avg_price": 71000,
          "note": null,
          "quote": { "price": 75000, "change": 1000, "change_pct": 1.35, "...": "..." },
          "market_value": 900000,
          "cost_basis": 852000,
          "pnl": 48000,
          "pnl_pct": 5.63,
          "warning": null
        }
      ],
      "summary": {
        "cost_basis": 852000,
        "market_value": 900000,
        "pnl": 48000,
        "pnl_pct": 5.63
      }
    }
  ],
  "total_summary": {
    "cost_basis": 852000,
    "market_value": 900000,
    "pnl": 48000,
    "pnl_pct": 5.63
  },
  "quote_error": null
}
```

기존 `current_value` → `market_value`, `total_cost` → `cost_basis` 개명. summary 필드명 통일.

`warning` 예시: `"분할/무상증자 가능성 (현재가 / 평단 비율 5배 이상)"`. 분할 도구 안내 문자열은 응답 텍스트가 아닌 별도 필드로 분리해 LLM이 자동 라우팅 가능.

## 5. 마이그레이션 영향

- **호스트(MCP 클라이언트)**: 도구 이름이 다수 변경. v0.4→v0.5는 dev preview 구간이라 break 허용.
- **데이터**: placeholder seed 자동 제거. 기존 보유종목 데이터는 그대로 유지 (account_id FK 변경 없음).
- **공식 nuget**: v0.5.0 minor bump. README에 마이그레이션 가이드 추가.

## 6. v0.5 스코프

### IN

- A1~A7 전부 (계좌 CRUD + default + broker rename)
- H1/H2 add/buy → 신설 도구 분리
- H3 multi-account 같은 종목 별개 row
- H4/H5 ambiguity 정책
- H6 account 필터
- H7 grouped 응답
- W8 그룹 이름 변경
- 분할/증자 3종

### OUT (v0.6+)

- H8 계좌 간 이동 (`ls_holdings_transfer`)
- W2 그룹 순서 변경
- W4/S3 sector × 보유종목 교차 (krx_sector enrichment via t1102 선행)
- S2 KOSPI 업종 코드 추적
- 거래 이벤트 로그 (snapshot 외에 actions 테이블)
- 실 brokerage 계좌 동기화

## 7. 함정/리스크

- **nickname rename 후 LLM이 옛 이름 사용**: `AccountNotFound` 에러 envelope에 `candidates` 동봉으로 자동 정정 유도. 모든 not-found 에러 envelope이 동일 shape.
- **liability**: `ls_holdings_set`이 의도치 않게 호출되면 기존 매수 이력이 덮어써짐. tool description에 "현재 총량으로 교체 (추가매수는 _buy 사용)" 명시.
- **분할 시 정수 나누어떨어짐**: `_reverse_split` ratio가 quantity를 정수로 나누지 못하면 error. 예외 처리 필요.
- **default 0개 상태**: portfolio_list는 empty 응답, holdings_set은 RequiresAccount. 모든 진입점에서 일관 처리.
- **WAL 파일 동시성**: 기존 v0.4 패턴 유지. WAL은 init 시 1회만.

## 8. 테스트 계획

### 8.1 단위 테스트 (xunit)

- `SqlitePortfolioRepository`: 계좌 0개 상태, default 토글, nickname UNIQUE 충돌, ambiguity (같은 symbol 다중 계좌), cascade with/without holdings, broker rename + 머지 충돌, split/bonus 정확성 (정수 나누어떨어짐 포함)
- `PortfolioService`: ambiguity 에러 envelope, 1계좌 폴백, grouped 응답 shape, buy/sell 누적 정확성, split 전체 계좌 일괄

### 8.2 통합 (portfolio-smoke.py)

기존 24 케이스 + 신규:
1. 계좌 0개 상태 → holdings_list 빈 응답
2. ls_account_upsert × 2 → ls_accounts_list 2개
3. ls_account_set_default 전환
4. ls_holdings_buy → 1회: set과 동일, 2회: weighted avg 확인
5. ls_holdings_sell → quantity 감소, 0이면 자동 삭제
6. 같은 symbol 두 계좌 등록 → list grouped 응답 확인
7. _update/_remove on ambiguous symbol → AmbiguousAccount + candidates
8. ls_holdings_split(005930, 50) → quantity ×50, avg /50, 모든 계좌
9. ls_account_remove(보유종목 있음, confirm=false) → RequiresConfirmation
10. ls_account_remove(confirm=true) → cascade 완료
11. ls_broker_rename → 머지 충돌 시 에러
12. ls_watchlist_group_rename → 정상 동작 + 충돌 시 에러

### 8.3 E2E (수동)

`E:\MCP_E2E`에서 실 LS 자격으로 호스트 경유:
- 사용자 발화 "한투에 LG전자 64주 평단 115,801, KB ISA에도 LG전자 10주 평단 98,450" → 정확히 2계좌 분리 등록되는지
- "삼성전자 평단 갱신해줘" → 두 계좌에 있으면 AmbiguousAccount 에러, 모델이 "어느 계좌?" 되묻는지
- "삼성전자 10:1 분할했대" → ls_holdings_split(005930, 10) 라우팅 + 두 계좌 모두 갱신

## 9. 작업 순서

1. **스키마 v2 마이그레이션** (`SqlitePortfolioRepository`)
2. **에러 envelope 통일** — `PortfolioException` 도입 또는 `RequiresXxx`/`AmbiguousXxx` typed exceptions
3. **모델 재구조화** — `AccountInfo` += `IsDefault`, `AccountHoldings`, `HoldingListResult` v2
4. **레포지토리 확장** — `ListAccountsAsync`, `GetAccountByIdentifierAsync`, `UpsertAccountAsync`, `RemoveAccountAsync`, `SetDefaultByIdentifierAsync`, `RenameBrokerAsync`, `ListAllHoldingsAsync`, `BuyHoldingAsync`, `SellHoldingAsync`, `ApplyCorporateActionAsync`
5. **서비스** — ambiguity 정책 구현, grouped 응답 빌더, warning 부착
6. **도구** — 추가/개명/삭제. description에 USE WHEN / AVOID WHEN 유지
7. **단위 테스트 + smoke**
8. **README** 다중 계좌 안내 추가
9. **NuGet 버전 bump** (Mcp 0.5.0)

## 10. Open questions

- `_buy`의 가격이 현재가보다 비합리적으로 멀면 (예: 3배 이상) confirmation 요구? → v0.5는 단순 동작. v0.6에서 sanity check 검토.
- `ls_watchlist_group_rename` 호출 시 default 그룹 이름도 변경 가능? → 가능. 영향 없음.
- `ls_holdings_split`의 ratio가 `1`이면 noop? error? → noop + 안내 메시지.
- 마이그레이션 v2가 placeholder 삭제 후 기존 holdings.account_id FK 깨지지 않는지? → 보유종목 있으면 placeholder 보존하는 SQL이라 안전.
