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

## 3. 슬라이스 B — 조건식(Q-클릭) 검색 노출

### 3.1 무엇인가

LS xingTrader는 **씽큐스마트 / Q-클릭검색**이라는 *시각적 조건 빌더*를 제공한다. 사용자가 HTS UI 안에서 "거래량 5일 평균의 200% 이상 + RSI 30 미만" 같은 조건을 만들어 *이름 붙여 저장*하고, 그 조건을 클릭 한 번에 매일 돌릴 수 있다. **저장된 조건은 LS 서버에 사용자별로 보관**된다.

v1.4의 슬라이스 B는 그 *저장된 조건들*을 AI 비서가 사용할 수 있도록 노출한다 — 사용자가 평소 자기 HTS에서 만들어둔 전문 지식을 모델이 활용하도록.

**v1.4는 조회 / 실행만 노출한다. 조건 *작성*은 v1.5+.**

### 3.2 LS TR

| TR | 이름 | 현재 카탈로그? | v1.4 |
|---|---|---|---|
| `t1825` | 종목Q클릭검색 (검색 실행) | ❌ | ✅ 추가 |
| `t1826` | 종목Q클릭검색 리스트조회 | ❌ | ✅ 추가 |
| `t1809` | 신호조회 | ❌ | ⚪ 후속 (신호 = 조건과 결이 다름) |

[k-ebest-im의 `stock_search` 네임스페이스](file:///d/Codes/k-ebest-im/ebest.js#L102) 참조.

t1825 / t1826의 in/out block은 v1.4 킥오프 시 LS 자료실 또는 testbed-console로 확정. 일반적 모양:
- `t1826OutBlock1[]` — 저장된 조건 목록: `name`, `id`(또는 코드), `created_date`
- `t1825InBlock` — `condition_id` (또는 `name`) + 옵션
- `t1825OutBlock1[]` — 매칭 종목 리스트: `shcode`, `hname`, `price`, `change_pct`, `volume`

### 3.3 도구 설계

#### `ls_list_screeners`
사용자의 저장된 Q-클릭 조건 목록을 돌려준다.

```
out: {
  count: int,
  results: [
    { id: string, name: string, created_at: string }
  ],
  source_tr: "t1826"
}
```

USE WHEN: "내가 만들어둔 조건들 뭐 있어?" / "Q-클릭 조건 목록"
AVOID WHEN: 조건을 실행하려는 경우는 `ls_run_screener`.

#### `ls_run_screener`
조건을 실행하고 매칭 종목을 돌려준다.

```
in: {
  name_or_id: string,    // 정확한 ID 또는 이름 (퍼지 매칭 v1.5+)
  limit: int = 20        // 매칭 상위 N개
}
out: {
  screener: { id, name },
  count: int,
  results: [
    { shcode, hname, current_price, change_pct, volume, ... }
  ],
  data_as_of: string,    // 슬라이스 A envelope
  source_tr: "t1825"
}
```

USE WHEN: "내 '눌림목 매수' 조건으로 종목 찾아줘"

#### 통합 효과

`ls_run_screener` 결과의 `shcode`를 후속으로:
- `ls_get_quote(shcode)` — 상세 시세
- `ls_get_chart(shcode)` — 차트
- `ls_get_stock_info(shcode)` — 펀더멘털

즉 "내 조건으로 → 결과 종목 중 N번째 차트 / 펀더멘털" 흐름이 자연어로 가능해진다. **v0.10 dataset-cache, v1.1 program-trading, v1.3 follow-up routing과 같은 "출력→입력 체이닝" 패턴**의 연장.

### 3.4 비범위 (v1.4)

- 신규 조건 *작성* — v1.5+. 작성은 LS HTS의 시각 빌더가 더 적합.
- t1809 신호조회 — 별개 컨셉(신호 = LS 측 분석 시그널). v1.5+.
- 미장 조건검색 — v1.4 범위는 국내(t1825/t1826). LS가 미장용 별도 TR을 제공한다면 v1.5+에서 검토.

### 3.5 테스트

- 모킹: t1826 응답에 2-3개 가상 조건, t1825 응답에 5-10개 가상 매칭.
- 케이스:
  1. `ls_list_screeners` 정상 — count, 첫 entry name 매칭
  2. `ls_run_screener` 이름으로 매칭
  3. `ls_run_screener` 알 수 없는 이름 → ErrorResult
  4. `ls_run_screener` 사용자가 아직 조건 안 만들었음(empty) — 친절한 안내

---

## 4. 도구 표면 영향

| 프로파일 | v1.3 | v1.4 (제안) |
|---|---|---|
| `standard` | 37 | **39** (+2 from 슬라이스 B) |
| `all` | 40 | **42** (+2) |

슬라이스 A는 도구 *수* 변동 없음 (param만 추가). `ToolSurfaceFreezeTests`는 39 / 42로 갱신.

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
"내가 LS에 저장해둔 조건들 뭐 있어?"
  → ls_list_screeners
  → 목록 출력

"눌림목 매수 조건으로 매칭된 종목 보여줘"
  → ls_run_screener(name_or_id="눌림목 매수")
  → 매칭 종목 표시
  
"위 결과에서 1번째 종목 일봉 차트 보여줘"
  → 모델이 shcode 추출 → ls_get_chart → 슬라이스 A의 data_as_of도 동시에 보여줌
```

체이닝 흐름이 자연어로 끊김 없이 이어지는지가 성공 기준.

---

## 8. Open Questions

1. **`query_date` 필드명** — `query_date` vs `as_of` vs `data_date` vs `target_date`. 의도된 의미는 "사용자가 *기준일자를 지정하는* 입력". `query_date`가 가장 명확하지만 v1.4 킥오프 시 최종 결정.
2. **`market_close_date`(quote류)** — quote는 스냅샷이라 `data_as_of`가 어색. 별도 필드명? 또는 `data_as_of`로 통일?
3. **`pre_market` 임계 시각** — KRX 장 개시 09:00 → 그 직전(08:55? 08:00?) 호출은 pre_market인가 used인가. 일단 단순화: 09:00 이전 = pre_market.
4. **B의 `name_or_id` 매칭 강도** — exact match만 (v1.4)? 또는 case-insensitive substring (v1.4)? 또는 모델이 list_screeners → 정확한 name으로 호출하도록 강제(v1.4 권장)?
5. **B의 빈 결과 처리** — 사용자가 LS HTS에서 조건을 안 만들었으면 t1826이 empty. 도구는 `count: 0` + 친절한 안내 메시지? 또는 어떤 가이드 텍스트도 안 넣고 모델이 알아서?
6. **A의 응답 envelope가 *모든* 도구에 동일한 위치**에 들어가야 하는가? — 예: 항상 최상위 `data_as_of` 필드. 일부 도구는 nested 구조라 위치 결정 필요.

킥오프 30분이 이 질문들 답하는 데 충분하다.

---

## 9. 릴리스 노트 초안 (참고)

```markdown
## v1.4.0

**Date envelope across date-bearing tools.** Every daily-snapshot tool now
takes an optional `query_date` (yyyyMMdd) and echoes back `data_as_of` +
`query_date_resolution` so non-trading-day fallbacks are explicit to the
model. Weekend fallback applies on both KR and US markets; holiday
calendars land in v1.5.

**Saved-screener access (Q-클릭).** Two new tools — `ls_list_screeners`
and `ls_run_screener` — surface the user's LS xingTrader saved-condition
list (t1826) and execute a saved condition (t1825). Results chain into
`ls_get_quote` / `ls_get_chart` / `ls_get_stock_info` via the standard
shcode field. Condition *authoring* stays in LS HTS for v1.4.

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
