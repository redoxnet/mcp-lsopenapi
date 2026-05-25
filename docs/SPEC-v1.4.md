# SPEC: v1.4.0 — date envelope + 조건식(Q-클릭) 검색

- **상태**: Draft — v1.3.0 출시 직후 작성, v1.4 작업 킥오프용
- **작성일**: 2026-05-24
- **대상 버전**: v1.4.0
- **선행**: [SPEC-v0.10.md](./SPEC-v0.10.md), [LS-TR-INVENTORY.md](./LS-TR-INVENTORY.md), 메모리 [`next_date_envelope`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/next_date_envelope.md)
- **범위**: 독립적인 두 슬라이스를 한 마이너에 묶는다 —
  ① **date-envelope 표준화**(횡단, ≈12개 도구) ② **조건식(Q-클릭) 검색 노출**(추가 2 도구). 둘 다 *사용자-체감 정확도* 개선이며 신규 TR 커버리지는 ②에만 적용.
- **선행 메모리 권장**: [release_prep_convention](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/release_prep_convention.md), [release_publish_order](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/release_publish_order.md), [readme_hero_portfolio_coupling](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/readme_hero_portfolio_coupling.md)

---

## 1. 컨텍스트 & 범위

### 1.1 출발점 — v1.3.0 완료 상태

v1.3.0(2026-05-24 prep 완료, 사용자 release commit 대기)은 **미장 first-class** 슬라이스를 닫는다. 카탈로그 53→62 TR, 도구 표면 34→37 standard / 37→40 all, `OverseasStockTools`, follow-up 라우팅(`ls_add_indicator` / `ls_reframe_chart`가 KR+US 공통), `CandlestickChartBuilder.Build`에 옵셔널 `currency`, `bar_timezone` 힌트, ServerInstructions 미장 routing 보강. 자세한 슬라이스 정의는 [`next_overseas_stocks`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/next_overseas_stocks.md).

### 1.2 v1.4가 청산하는 백로그

| 슬라이스 | 출처 | 성격 |
|---|---|---|
| **A. Date-envelope 표준화** | v1.3 prep 중 deferred ([`next_date_envelope`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/next_date_envelope.md)) | additive (param + response field) |
| **B. 조건식(Q-클릭) 검색 노출** | v1.3 prep 중 README/INVENTORY에 v1.4 후보로 슬립한 "Q-Click style screeners" | additive (TR + tool) |

### 1.3 왜 한 릴리스로 묶는가

둘 다 **순수 additive**라 사용자 마이그레이션 비용이 없다. 묶어도 release-note 한 줄, 분리해도 한 줄. 그러나 두 슬라이스는 **서로 독립적**이므로 작업 순서는 자유 — A를 먼저(횡단 변경이라 회귀 surface 크고 테스트 부담 큼), B를 나중에(콤팩트 슬라이스) 권장.

대안: A만 v1.4.0, B를 v1.5.0으로 분리. **선택 기준**은 §5의 일정 추정에 달려있다.

### 1.4 비범위

- 실시간 WebSocket / 실계좌 / 주문 — v2.x.
- KRX·NYSE 공휴일 캘린더 자료 — A에서는 **주말만** 처리(§2.5). 공휴일 테이블은 v1.5+ 후속.
- 조건식 **작성**(LS HTS 안에서 신규 조건 만들기) — v1.5+. v1.4의 B는 **이미 저장된** 조건의 조회 / 실행만.
- 응답 payload shape 추가 reshape — v1.3까지의 shape를 그대로 유지.

---

## 2. 슬라이스 A — Date-envelope 표준화

### 2.1 문제

LS의 daily-snapshot 류 TR은 대부분 "직전 마감" 시점을 암시적으로 사용한다. 모델/사용자 입장에서 *그게 정확히 며칠인지가 응답에 적혀있지 않다*. 결과:

- 토요일에 "오늘 거래대금 상위"를 묻고 → 금요일 데이터를 받지만 → 그게 "오늘 = 토요일"인 줄로 응답이 작성됨.
- 미국 공휴일(예: Memorial Day)에 "NVDA 일봉" → 직전 금요일 데이터로 fallback되지만 → 그게 fallback인지 알 수 없음.
- `query_date=20260524`(미래)를 줘도 LS는 그냥 가장 최근으로 답함 → 사용자는 자기 입력이 무시된 줄도 모름.

### 2.2 필드 설계 (제안)

모든 date-bearing 도구가 **동일한 envelope**를 공유해 모델이 한번 학습하면 어디든 쓸 수 있게 한다.

#### 입력 (param)

```
query_date: string?     // "yyyyMMdd", 누락 시 "오늘 또는 직전 영업일"
```

규칙:
- 누락 → 호스트 시점 기준 *가장 최근 영업일*.
- yyyyMMdd 외 포맷 → ErrorResult, 정정 안내.
- 미래 → fallback resolution `future_date`(아래).

#### 출력 (response 필드)

```
data_as_of: string             // "yyyyMMdd", 실제로 사용된 거래일
query_date_resolution: enum    // "used" | "weekend" | "holiday" | "future_date" | "pre_market"
```

규칙:
- `used`: 요청 = 사용. 가장 흔한 케이스.
- `weekend`: 토/일 → 직전 금요일.
- `holiday`: KRX/NYSE 휴장 → 직전 영업일. (v1.4 범위에서는 캘린더 부재, §2.5)
- `future_date`: 입력이 미래 → 오늘(또는 직전 영업일)로 clamping.
- `pre_market`: 오늘이 영업일이지만 장 개시 전 → 직전 영업일.

### 2.3 영향 받는 도구 (≈12개)

| 도구 | 현재 상태 | 변경 |
|---|---|---|
| `ls_get_top_stocks` | 암시적 직전 마감 | `query_date` 옵션 + envelope |
| `ls_get_high_low_stocks` | 동일 | 동일 |
| `ls_get_market_funds_trend` | 동일 | 동일 |
| `ls_get_investor_flow` | 동일 | 동일 |
| `ls_get_short_selling_trend` | 동일 | 동일 |
| `ls_get_market_warnings` | 동일 | 동일 |
| `ls_get_industry_indices` | 동일 | 동일 |
| `ls_get_fundamentals_rank` | 동일 | 동일 |
| `ls_get_program_trading` | 부분 (날짜 param 일부) | envelope 통일 |
| `ls_get_index_history` | from/to 있음 | `data_as_of`만 echo, query_date는 무관 |
| `ls_get_chart` | from/to 있음 | 동일 — `data_as_of`만 추가 |
| `ls_get_overseas_chart` | from/to 있음 | 동일 (단, NYSE 캘린더 분리) |
| Quote류 (3개) | timestamp 있음 | `market_close_date` 컴패니언 필드 (옵션) |

13~15개 도구가 변경되며 각자 테스트 2-3개씩 (정상 + weekend fallback + invalid). 분량 예측은 §5.

### 2.4 행동 규칙 — edge cases

| 케이스 | 동작 |
|---|---|
| `query_date` 없음, 오늘이 영업일 + 장 후 | `data_as_of=today`, `resolution=used` |
| `query_date` 없음, 오늘이 영업일 + 장 전 | `data_as_of=last_trading_day`, `resolution=pre_market` |
| `query_date` 없음, 오늘이 주말 | `data_as_of=last_friday`, `resolution=weekend` |
| `query_date=오늘` | `used` |
| `query_date=과거 영업일` | `used` |
| `query_date=과거 주말` | `data_as_of=직전 금요일`, `resolution=weekend` |
| `query_date=미래` | `data_as_of=오늘 또는 직전 영업일`, `resolution=future_date` |
| `query_date=잘못된 포맷` | ErrorResult, 정정 안내 |

장 개시/마감 시각은 한국 시간 09:00/15:30(KRX), 미국 동부 09:30/16:00(NYSE) 고정으로 시작. DST 처리는 `TimeZoneInfo`로 위임.

### 2.5 거래일 캘린더 — v1.4 범위는 **주말만**

```csharp
internal interface ITradingCalendar
{
    bool IsTradingDay(DateOnly date);
    DateOnly LastTradingDayOnOrBefore(DateOnly date);
    DateOnly NextTradingDayOnOrAfter(DateOnly date);
}
```

구현:
- `WeekendOnlyCalendar` — 토/일만 제외. KRX/NYSE 공통. v1.4의 기본.
- `KrxCalendar` — v1.5+ candidate. 공휴일 표 + 임시휴장.
- `NyseCalendar` — v1.5+ candidate. 미국 공휴일 + early-close 이벤트.

서비스 등록:
```csharp
services.AddSingleton<ITradingCalendar, WeekendOnlyCalendar>();
// v1.5+: scope별 캘린더 분리, TR prefix로 라우팅
```

도구는 `ITradingCalendar`만 의존 — v1.5에서 캘린더만 교체하면 됨.

### 2.6 구현 노트

- **Param 추가는 non-breaking** — `ToolSurfaceFreezeTests`는 도구 이름만 pin하며 param schema를 pin하지 않음. 확인은 freeze 테스트 통과만으로 OK.
- **응답 필드 추가도 non-breaking** — 기존 필드 제거/리네임 없음.
- 헬퍼는 `RedoxNet.LsOpenApi.Core.Time` 새 네임스페이스에 두기. `LsTrResponse`나 도구 본체는 캘린더를 직접 알 필요 없음 — 도구 진입에서 `ITradingCalendar.Resolve(query_date)`로 정규화 후 LS 호출.
- 응답 필드를 *모든* 도구가 일관되게 갖도록 — 공통 helper `DateEnvelope.From(resolved, original)` 같은 형태.

### 2.7 테스트 전략

도구당 minimum 3개:
1. `query_date` 미지정 + 정상 응답 — envelope `resolution=used`(또는 `weekend`/`pre_market`) 확인
2. `query_date` 주말 입력 → `resolution=weekend`, `data_as_of=직전 금요일`
3. `query_date` 미래 → `resolution=future_date`

크로스 커팅:
- `WeekendOnlyCalendarTests` — 12-15개 케이스
- `DateEnvelopeTests` — 응답 필드 직렬화 / null 처리

총 추가 테스트 수 ≈ 40-50개. 기존 558 Mcp + 240 Core = 798 → 약 850 통과 예상.

---

## 3. 슬라이스 B — Q-클릭 시그널 카탈로그 노출

> **2026-05-24 정정**: 킥오프 단계에서는 "사용자가 HTS 시각 빌더에서 *만든* 조건을 노출"이라고 가정했으나, E2E 검증으로 확인된 실상은 다르다. t1825/t1826이 노출하는 것은 **사용자 저장 조건이 아니라 LS가 큐레이션해 둔 표준 시그널 카탈로그**다. 아래 §3.1~§3.5는 그 실상 기준으로 재서술됨. 이전 가정은 §3.6 "프레임 정정 메모"에 기록해 둔다.

### 3.1 무엇인가

LS는 **씽큐스마트 / Q-클릭검색**이라는 이름으로 *LS가 직접 큐레이션한* 표준 시그널 카탈로그를 제공한다. v1.4-dev E2E(2026-05-25) 시점 기준 **5개 그룹 / 총 99개 시그널**:

| 그룹 | search_gb | id 대역 | 개수 | 성격 |
|---|---|---|---|---|
| 핵심검색 (core) | 0 | 6001–6023 | 23 | 봉/패턴 위주 매매 시그널 ("쌍바닥형", "스윙트레이딩매수") |
| 지표검색 (indicator) | 1 | 6101–6133 | 33 | 이평/매물대/MACD/스토캐스틱 ("20일 이평 상향돌파", "이평 골든크로스(5,20)") |
| 시세동향 (market_trend) | 2 | 6201–6216 | 16 | 상한가/시가/장중 패턴 ("상한가직전", "오전고점14시이후돌파") |
| 투자자동향 (investor_trend) | 3 | 6301–6315 | 15 | 외인/프로그램/거래원 ("외인 3일연속 순매수", "프로그램 순매도 100") |
| **급변종목 (rapid_change)** | **4** ⚠️ | **6401–6412** | **12** | **분봉 급변 ("가격급등/급락 1·3·5·10분봉", "거래량급증 1·3·5·10분봉")** |

각 시그널은 4-character 코드(`6xxx`) + 한글 이름으로 식별된다. 카탈로그는 **사용자 계정과 무관하게 항상 동일** — 처음 가입한 사용자도 첫 호출부터 99개 모두 사용 가능. 사용자가 HTS에서 만든 자유조건(예: [1892] (KRX)조건검색의 "API보내기" 산출물)은 **이 surface로 노출되지 않는다** — 별개 시스템.

⚠️ **search_gb=4 quirk**: LS 공식 spec doc (`todo/[주식] 종목검색_t1826.txt:26`)은 `search_gb` 0~3만 명시하지만, **실제로 search_gb=4도 정상 동작**해서 12개 분봉 급변 시그널을 반환한다. v1.4-dev E2E에서 확정. spec doc 갱신 누락으로 추정 — [`LS-API-QUIRKS.md §4.4`](./LS-API-QUIRKS.md) 참조. 우리 도구는 search_gb=4가 거절되더라도 silent-fail 안전망으로 다른 그룹은 정상 fetch.

v1.4 슬라이스 B는 그 87개 시그널을 자연어로 선택·실행할 수 있게 한다. 사용자가 "오늘 골든크로스 뜬 종목" 같이 물으면 모델이 카탈로그에서 적절한 시그널(예: "이평 골든크로스(5,20)")을 골라 t1825로 실행하고, shcode를 다른 도구로 체이닝한다.

**v1.4는 조회 / 실행만 노출한다.** 사용자 자유조건 작성은 시각 빌더(HTS)의 영역으로 두며, LS API가 자유조건 표현식 전송을 지원하는 별도 TR을 제공한다는 자료가 발견되면 v1.5+에서 재검토.

### 3.2 LS TR

| TR | 이름 | v1.4 | 비고 |
|---|---|---|---|
| `t1826` | 종목Q클릭검색 리스트조회 (씽큐스마트) | ✅ 추가 | search_gb=0~3 그룹별 호출 |
| `t1825` | 종목Q클릭검색 실행 (씽큐스마트) | ✅ 추가 | search_cd + gubun(시장) |
| `t1809` | 신호조회 | ⚪ 후속 (v1.5+) | LS 측 별도 분석 시그널 — 카탈로그와 결이 다름 |

[k-ebest-im의 `stock_search` 네임스페이스](file:///d/Codes/k-ebest-im/ebest.js#L102) 참조. in/out 블록 모양은 LS 공식 spec 파일 [`todo/[주식] 종목검색_t1825.txt`](../todo/[주식]%20종목검색_t1825.txt), [`todo/[주식] 종목검색_t1826.txt`](../todo/[주식]%20종목검색_t1826.txt)로 확정.

t1826의 example 응답이 그대로 v1.4-dev 실호출 결과와 일치 — id 6001 "이평밀집정배열" 등.

### 3.3 도구 설계

세 도구가 공유 카탈로그(프로세스 lifetime 캐시)를 통해 동작한다. ls_list_screeners가 카탈로그를 enumeration, ls_run_screener가 단일 시그널 실행, **ls_combine_screeners가 복합(AND/OR) 시그널 실행 — 이 슬라이스의 시그니처 가치**.

#### `ls_list_screeners`
LS의 Q-Click 시그널 카탈로그를 그룹별로 돌려준다.

```
in: {
  search_group: "all" | "core" | "indicator" | "market_trend" | "investor_trend" | "rapid_change" | "0"|"1"|"2"|"3"|"4"
}
out: {
  search_group: string,
  count: int,
  results: [
    { id: string, name: string, group: string, group_label: string }
  ],
  source_tr: "t1826"
}
```

USE WHEN: "Q-클릭 조건 목록", "LS가 제공하는 시그널 뭐 있어?"
AVOID WHEN: 시그널을 실행 / 조합하려는 경우는 ls_run_screener / ls_combine_screeners (둘 다 키워드 매칭 + ambiguity 후보 반환을 지원하므로 호출 전에 list가 필수는 아니다).

빈 결과는 사실상 발생하지 않는다. 만약 비어 있다면 OpenAPI 권한/응답 이상이므로 에러 안내.

#### `ls_run_screener`
시그널을 실행하고 매칭 종목을 돌려준다. **키워드 매칭 + β policy ambiguity** — 입력이 카탈로그의 정확한 이름/ID가 아니라 키워드("골든크로스")라도 우리 도구가 캐시에서 매칭 시도.

```
in: {
  name_or_id: string,    // exact name | 4-character search_cd | Korean keyword
  market: "all" | "kospi" | "kosdaq",   // default "all"
  limit: int = 20
}
out (success): {
  screener: { id, name, group, group_label },
  market, count, total_available,
  data_as_of, query_date_resolution,    // 슬라이스 A envelope
  results: [ { rank, shcode, name, price, sign, consecutive_bars, change, change_pct, volume, volume_rate_pct } ],
  source_tr: "t1825"
}
out (ambiguity, error envelope):
{
  error: "Some Q-Click signals could not be resolved unambiguously.",
  details: {
    tool: "ls_run_screener",
    original: [string],
    ambiguous: { "<keyword>": [{ id, name, group, group_label }, ...] },
    not_found: [string],
    group_catalogs: { "<group>": [{ id, name }, ...] },   // β: 후보가 속한 그룹의 *전체* 미니 카탈로그
    hint: "Re-call with the exact name or 4-character id from candidates / group_catalogs above."
  }
}
```

USE WHEN: 단일 시그널 — "골든크로스 뜬 종목" / "외인 3일 연속 순매수"
AVOID WHEN: 복합 조건 — ls_combine_screeners.

매칭 우선순위:
1. exact id (Ordinal)
2. exact name (case-insensitive)
3. normalized substring (whitespace/구두점 제거 후) — 후보 1개면 즉시 실행, 2개 이상이면 ambiguity 응답
4. 4-digit numeric id가 카탈로그에 없을 때 passthrough (LS가 카탈로그 갱신 시 robust)

#### `ls_combine_screeners` (신규, v1.4의 시그니처)

2~8개 시그널을 동시에 실행하고 **shcode 기준 AND(교집합) 또는 OR(합집합)으로 결합**. HTS 화면이 표현 못하는 복합 조건을 자연어 한 줄로 표현.

```
in: {
  signals: [string],     // 2~8 entries, exact name | search_cd | keyword 혼용 가능
  mode: "and" | "or",    // default "and". "intersection"/"union"/"교집합"/"합집합" alias 허용
  market: "all" | "kospi" | "kosdaq",
  limit: int = 20        // 결합 후 row cap
}
out (success): {
  signals_resolved: [{ id, name, group, group_label, matched_count }],
  mode, market,
  count, total_in_combination,    // total_in_combination = limit 적용 전 결합 집합 크기
  data_as_of, query_date_resolution,
  results: [ {
    rank, shcode, name, price, sign, consecutive_bars,
    change, change_pct, volume, volume_rate_pct,
    signals_matched: [id, ...]    // 이 종목에서 fired된 시그널 id들 (or 모드 디버깅에 핵심)
  } ],
  source_tr: "t1825 x N"
}
out (ambiguity): ls_run_screener와 동일한 error 형태 — single entry가 아닌 *여러 입력*에 대한 후보들이 ambiguous map에 들어가고, *결정된* 입력은 resolved 배열에. group_catalogs는 ambiguous 그룹들의 합집합.
```

USE WHEN: "A 이면서 B" / "A + B + C 모두" / "A 또는 B" 같은 복합 패턴. *결과 종목에 `signals_matched`*가 붙어 있어 모델이 "이 종목은 골든크로스 + 외인 순매수가 동시에 떴습니다" 같은 자연어 설명을 정확히 생성 가능.
AVOID WHEN: 단일 시그널 → ls_run_screener. 단순 metric 랭킹 → ls_get_top_stocks / ls_get_fundamentals_rank.

#### 통합 효과

세 도구의 결과 `shcode`를 후속으로:
- `ls_get_quote(shcode)` — 상세 시세
- `ls_get_chart(shcode)` — 차트
- `ls_get_stock_info(shcode)` — 펀더멘털

자연어 흐름:
1. (선택) "Q-클릭 조건 뭐 있어?" → ls_list_screeners
2. "골든크로스(5,20)이면서 외인이 3일 연속 순매수한 종목" → ls_combine_screeners(["이평 골든크로스(5,20)", "외인 3일연속 순매수"], mode="and") → 결과 종목
3. "1번째 종목 일봉 차트" → ls_get_chart(shcode)

키워드 매칭 + β ambiguity 정책 덕에 *모델이 카탈로그를 미리 외울 필요 없이* 자연어 키워드만 던지면 됨. ambiguous 시 우리 응답에 후보 + 그룹 미니 카탈로그가 같이 와서 다음 턴에 *정확한 id*로 재호출하면 됨 — list 추가 round trip 없이.

### 3.3.1 카탈로그 인지 메커니즘 (구현 디테일)

원래 SPEC은 모델이 카탈로그 87+ 시그널을 어떻게 알지에 대해 모호했다. 결정:

- **결과 순서 정책 (ls_combine_screeners)**:
  - **AND**: 첫 시그널의 LS rank 순서 유지 — limit으로 잘려도 *가장 매칭 강한* 종목들이 먼저.
  - **OR**: `signals_matched.count DESC, best_rank ASC` — N개 시그널 모두 충족한 종목이 1개만 충족한 종목보다 위. 비대칭 OR(예: 300 vs 2)에서 limit이 작은 시그널 매칭을 누락하는 v1.4-dev 관찰 fix.
- **서버 사이드 캐싱**: ScreenerTools가 첫 t1826 호출 결과를 프로세스 lifetime 동안 캐싱. LS-curated라 안정적이라 TTL 무한 OK.
- **키워드 매칭**: ls_run_screener / ls_combine_screeners 입력을 캐시에서 매칭. exact id → exact name → normalized substring 순.
- **β policy ambiguity payload**: 후보가 2개 이상이면 *그 후보들*과 *그 후보가 속한 그룹의 미니 카탈로그 전체*를 응답에 박아 반환. 모델은 한 응답 안에서 *결정 컨텍스트*를 받음 → 다음 턴에 정확한 id로 재호출, list 추가 round trip 없음.
- **ServerInstructions에 안내 ~80 토큰**: "Q-Click signals are LS-curated; discover with ls_list_screeners; run with ls_run_screener; combine with ls_combine_screeners (AND/OR); ambiguous keywords return candidates + group catalog". 매 prompt에 한 번 들어감.

### 3.4 비범위 (v1.4)

- **사용자 자유조건 노출** — HTS [1892] 조건검색의 "API보내기"는 xingTrader 내 다른 화면(관심종목/주문창 등)으로 종목을 *전송*하는 기능이며, t1825/t1826 surface로 흘러오지 않는다. LS가 자유조건 표현식 전송 TR을 제공한다는 자료가 발견되면 v1.5+에서 재검토.
- **"급변종목" 그룹** — HTS [1801] 화면에 5번째 그룹으로 보이지만 t1826의 search_gb 0~3에는 포함되지 않는다. LS가 별도 TR(t1442 등)로 노출 중일 가능성이 높고, 우리는 이미 `ls_get_top_stocks(kind="volume_surge")` 등으로 비슷한 surface를 제공 중.
- **t1809 신호조회** — 별개 컨셉(LS 측 분석 시그널). v1.5+.
- **미장 조건검색** — v1.4 범위는 국내(t1825/t1826). LS가 미장용 별도 TR을 제공한다면 v1.5+에서 검토.

### 3.5 테스트

- **단위(mock)**: t1826 응답에 가상 시그널, t1825 응답에 가상 매칭. 케이스:
  1. `ls_list_screeners` 정상 — count, 첫 entry name/id 매칭
  2. `ls_run_screener` 이름으로 매칭 + envelope 필드 노출
  3. `ls_run_screener` 알 수 없는 이름 → ErrorResult + hint
  4. `ls_list_screeners` 빈 응답(드물게 권한 이슈 등) — note 안내
  5. **rsp_cd quirk**: 빈 `rsp_cd`도 성공으로 인정 (§4.2 of LS-API-QUIRKS)
  6. **방어**: 미래에 LS가 `"00000"`을 보내도 통과, 진짜 에러코드(`"00040"` 등)는 surface

- **실호출 E2E (v1.4-dev 2026-05-24 검증 완료)**:
  - `ls_list_screeners(all)` → 87개 (23/33/16/15)
  - `ls_list_screeners(indicator)` → 33개, 6101–6133
  - `ls_run_screener("6101")` 결과 = HTS [1801] "지표검색 > 20일 매물대 상향돌파" 화면과 자릿수까지 일치
  - `ls_run_screener("존재하지 않는 이름")` → 친절한 not-found 에러

### 3.6 프레임 정정 메모 (히스토리)

킥오프 시점에는 "사용자가 HTS 시각 빌더에서 만든 조건을 노출"이라는 프레임이었고, 사용자 E2E 중 직접 [1892] 조건검색에서 `Break_Above_MA20` 조건을 만들어 "API보내기"한 뒤 `ls_list_screeners`로 조회했으나 카탈로그에 없는 것을 확인했다. 동시에 t1826 응답이 `6001..6315` 코드의 일반화된 시그널 이름들로만 채워져 있어 → "이건 사용자 카탈로그가 아니라 **LS-curated 시그널 라이브러리**다"라는 결론.

배운 점:
- *명명이 오해를 부른다*: "씽큐스마트 / Q-Click"이라는 이름이 "사용자 정의 조건"을 암시하지만, 실은 LS가 미리 만든 시그널들.
- *spec example을 빨리 신뢰하자*: `todo/[주식] 종목검색_t1826.txt`의 example 응답이 처음부터 일반화된 이름들이었는데, "user-saved일 수도 있다"는 가정으로 흘렸음. 다음부터는 example의 *내용*까지 첫 단계에서 검사.
- *quirk 발견 부산물*: 같은 검증 과정에서 t1825/t1826의 빈 `rsp_cd` 응답 quirk도 같이 발견 — `ScreenerTools.IsScreenerSuccess`로 우회([LS-API-QUIRKS.md §4.2](./LS-API-QUIRKS.md#42-t1825--t1826-return-rsp_cd-on-success-)).

---

## 4. 도구 표면 영향

| 프로파일 | v1.3 | v1.4 |
|---|---|---|
| `standard` | 37 | **40** (+3 from 슬라이스 B: ls_list_screeners, ls_run_screener, ls_combine_screeners) |
| `all` | 40 | **43** (+3) |

슬라이스 A는 도구 *수* 변동 없음 (param만 추가). `ToolSurfaceFreezeTests`는 40 / 43으로 갱신됨. `FrozenRowCapTools`에 `ls_combine_screeners`도 포함 (limit 사용).

---

## 5. 일정 추정

| 슬라이스 | 항목 | 시간 |
|---|---|---|
| **A** | Spec 합의 (필드명, fallback enum) | 30분 |
| | `ITradingCalendar` + `WeekendOnlyCalendar` + `DateEnvelope` helper | 1h |
| | per-tool 변경 (param + 응답 field) — 12 도구 × 15분 | 3h |
| | per-tool 테스트 — 12 도구 × 25분 | 5h |
| | 크로스 커팅 테스트 (캘린더, envelope 직렬화) | 1h |
| | 문서 (README env 표 갱신, RELEASENOTES) | 30분 |
| | **소계** | **~11h** |
| **B** | t1825 / t1826 testbed-console 확인 + 카탈로그 추가 | 1h |
| | `ls_list_screeners` 도구 | 1h |
| | `ls_run_screener` 도구 | 1.5h |
| | 테스트 (4개 케이스) | 1.5h |
| | 문서 갱신 | 30분 |
| | **소계** | **~5.5h** |
| **공통** | RELEASENOTES Mcp+Core, README hero, csproj/server.json | 30분 |

**총 ≈ 17h 집중 작업 = 약 2.5 work days.**

대안 시퀀스 — 분량이 부담스러우면 v1.4.0 = A only (~11h), v1.5.0 = B (~5.5h)로 분리. 단, B가 작아 묶는 게 자연스럽다.

---

## 6. 작업 순서 권장

1. **킥오프 (30분)**: 본 SPEC 재확인 + 필드명 / fallback enum 최종 합의. 이름이 12 도구에 박히므로 첫 결정 비용이 가장 비싸다 — 변경하기 쉬울 때 확정.
2. **A 인프라 (1h)**: `WeekendOnlyCalendar` + `DateEnvelope` helper + 테스트.
3. **A 도구 — 1개 시범 (40분)**: 가장 단순한 daily-snapshot 하나 (예: `ls_get_top_stocks`)에 envelope 적용. 패턴 확정.
4. **A 도구 — 나머지 11개 (~6h)**: 시범 코드 복붙. 이 단계가 가장 길지만 패턴이 단순해 회귀 위험 낮음.
5. **A 통합 + 회귀 테스트 (1h)**: 798 → 850 통과 확인.
6. **B 카탈로그 (1h)**: t1825 / t1826 추가 + freeze 테스트 통과.
7. **B 도구 (2.5h)**: list + run.
8. **B 테스트 (1.5h)**.
9. **Release prep (30분)**: README / RELEASENOTES / csproj / server.json. 사용자가 release commit.

각 단계가 1-2시간 단위라 컨텍스트 끊겨도 재개 비용이 낮다. 메모리 [`next_date_envelope`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/next_date_envelope.md)에 진행 위치 기록.

---

## 7. 사용자 검증 — E2E 시나리오

릴리스 직전 호스트에서:

```
[Slice A]
"방금 토요일인데, 거래량 상위 보여줘"
  → ls_get_top_stocks 호출, response에 data_as_of=금요일 + resolution=weekend
  → 모델 응답: "토요일은 비영업일이라 마지막 거래일인 5/22(금) 데이터입니다"

"2026-01-01 거래대금 상위는?"
  → query_date=20260101 입력 → resolution=holiday(또는 weekend)
  → fallback된 거래일 명시

"내일 거래대금 상위 보여줘"
  → 미래 → resolution=future_date
  → 모델: "내일은 아직 시장이 안 열려서 가장 최근 영업일 기준으로 보여드립니다"

[Slice B]
"LS Q-클릭 시그널 뭐 있어?"
  → ls_list_screeners
  → 87개 시그널 목록 (핵심/지표/시세동향/투자자동향 4그룹)

"오늘 골든크로스 뜬 종목 보여줘"
  → 모델이 카탈로그에서 "이평 골든크로스(5,20)" (id 6116) 매칭
  → ls_run_screener(name_or_id="이평 골든크로스(5,20)")
  → 매칭 종목 표시 + data_as_of 동시 노출

"위 결과에서 1번째 종목 일봉 차트 보여줘"
  → 모델이 shcode 추출 → ls_get_chart → 슬라이스 A의 data_as_of도 동시에 보여줌

"외인 3일 연속 순매수 종목"
  → 모델이 "외인 3일연속 순매수" (id 6310) 직접 매칭 → ls_run_screener 실행

[Slice B — compound]
"골든크로스 떴으면서 외인이 3일연속 순매수한 종목"
  → 모델: ls_combine_screeners(["골든크로스", "외인 3일연속 순매수"], mode="and")
  → 첫 키워드 ambiguous → ambiguity payload (후보: 6115, 6116 + indicator 그룹 미니 카탈로그)
  → 모델: 사용자에게 "5일/20일이신가요 20일/60일이신가요?" 또는 더 자주 쓰이는 (5,20) 자동 선택
  → ls_combine_screeners(["이평 골든크로스(5,20)", "외인 3일연속 순매수"], mode="and") 재호출
  → 교집합 종목 + signals_matched 표시

"이평 정배열 또는 시가상한가 종목"
  → ls_combine_screeners(["이평 정배열(5,20,60)", "시가상한가"], mode="or")
  → 합집합 + 각 종목의 signals_matched
```

체이닝 흐름이 자연어로 끊김 없이 이어지는지가 성공 기준. 사용자가 카탈로그 이름을 외울 필요가 없도록 모델이 자연어 질문 → 시그널 매칭을 자연스럽게 수행해야 함. 복합 조건은 ls_combine_screeners 한 번의 호출로 N개 t1825를 묶음 + shcode 집합 연산. *signals_matched* 배열이 모델 응답에 정확한 컨텍스트("이 종목은 골든크로스와 외인 순매수가 동시에 떴습니다")를 제공.

---

## 8. Open Questions — 결정 기록

1. **`query_date` 필드명** ✅ `query_date`로 확정 (2026-05-24 결정). 의도된 의미 "사용자가 기준일자를 지정하는 입력"이 명확하다는 판단.
2. **`market_close_date`(quote류)** ⚪ v1.4 범위 밖으로 미룸 — quote/range tool들에 `data_as_of`를 일괄 적용할지 별도 필드를 둘지는 v1.5+에서 재검토.
3. **`pre_market` 임계 시각** ✅ KRX 09:00 KST 이전 = pre_market으로 확정. 단, **명시적 `query_date=오늘`**은 `used`로 처리(사용자가 의도적으로 오늘을 지정한 경우 모델이 "장 전 데이터"라고 헷갈리지 않도록). 생략했을 때만 장 전이면 `pre_market` resolution. 구현은 [DateEnvelope.cs:94](../src/RedoxNet.LsOpenApi.Core/Time/DateEnvelope.cs:94).
4. **B의 `name_or_id` 매칭 강도** ✅ **(2026-05-25 갱신)** 키워드 매칭 + β policy ambiguity 응답으로 진화. 매칭 우선순위: exact ID(Ordinal) → exact name(case-insensitive) → normalized substring. 후보 1개면 즉시 실행, 2개 이상이면 candidates + group_catalogs payload. Ambiguity 해결 정책은 모델 재량 — (a) 사용자에게 명확화 질문, 또는 (b) 합리적 default 선택(예: OR 합집합, 가장 흔한 변형) + 선택 이유 명시. ServerInstructions가 두 패턴 모두 권장하되 *silent pick 금지*. v1.4-dev E2E에서 모델이 "골든크로스에는 (5,20)과 (20,60) 둘 다 있어서 합집합" / "거래량급증은 5분봉 기준" 식의 자발적 명시가 자연스럽게 발현됨.
5. **B의 빈 결과 처리** ✅ `count: 0` + 친절한 note. 다만 실상이 LS-curated 87개 카탈로그라 빈 결과는 사실상 발생하지 않으며, 발생 시는 권한/응답 이상 의미. 현재 note 메시지는 §3.6 정정에 맞춰 v1.4 prep에서 갱신 예정.
6. **A의 응답 envelope 위치** ✅ 최상위 `data_as_of` + `query_date_resolution`으로 통일. ls_run_screener / ls_get_market_funds_trend / ls_get_short_selling_trend에 모두 동일 위치.

---

## 9. 릴리스 노트 초안 (참고)

```markdown
## v1.4.0

**Date envelope across date-bearing tools.** Every daily-snapshot tool now
takes an optional `query_date` (yyyyMMdd) and echoes back `data_as_of` +
`query_date_resolution` so non-trading-day fallbacks are explicit to the
model. Weekend fallback applies on both KR and US markets; holiday
calendars land in v1.5.

**LS Q-Click signal catalog + compound screening.** Three new tools —
`ls_list_screeners` (catalog enumeration), `ls_run_screener` (single
signal), `ls_combine_screeners` (multiple signals combined via
AND-intersection or OR-union on shcode) — surface LS's curated Q-Click
signal catalog (t1826) and execution (t1825). Signals are LS-maintained
across four groups (핵심검색 / 지표검색 / 시세동향 / 투자자동향, plus
급변종목 if exposed via search_gb=4); every account sees the same catalog
from the first call. Natural-language keywords ("골든크로스",
"외인 순매수") match against a process-cached catalog; ambiguous keywords
return candidates plus the matching group's full mini-catalog so the next
call can target an exact id without an extra discovery round trip.
Compound conditions ("골든크로스 + 외인 3일 연속 순매수", "이평 정배열 또는
시가상한가") that no single HTS screen can express are now expressible
in one tool call. User-authored conditions from HTS [1892] remain a
separate system and are not exposed in v1.4.

Tool surface 37 → 39 standard (40 → 42 all). All additions are
non-breaking; existing tool signatures unchanged.

Catalog 62 → 64 TRs (t1825, t1826).
```

---

## 10. 참고

- 미장 슬라이스 retrospective: [`next_overseas_stocks`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/next_overseas_stocks.md)
- Date-envelope 동기/이유: [`next_date_envelope`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/next_date_envelope.md)
- 메모리 — release 워크플로 / publish 순서: [`release_prep_convention`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/release_prep_convention.md), [`release_publish_order`](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/release_publish_order.md)
- 도구 표면 freeze 규칙: [`SPEC-v0.10.md`](./SPEC-v0.10.md) §10
- k-ebest-im의 stock_search 노출 패턴(t1825/t1826 사용 예): `D:\Codes\k-ebest-im\ebest.js`
