<p align="right">
  <strong>한국어</strong> · <a href="README.en.md">English</a>
</p>

# mcp-lsopenapi

**LS증권 OpenAPI**용 MCP 서버 — AI 도우미가 한국 주식 시세, 차트, 지표를 자연어로 조회할 수 있게 합니다.

> v1.0은 **국내주식 read-only 시세 데이터** 범위입니다. 실시간 시세(WebSocket), 계좌/잔고, 주문은 후속 릴리스에 포함될 예정입니다.

## 면책 조항

이 프로젝트는 **비공식 third-party MCP 서버**입니다. LS증권(LS Securities Co., Ltd.)과 공식적인 제휴·후원·승인 관계가 없으며, "LS증권" 및 관련 상표는 해당 권리자의 소유입니다.

본 도구는 **정보 제공 목적의 시세·차트 데이터 조회용**입니다. 투자 자문이나 매매 권유가 아니며, 주식 거래에는 원금 손실을 포함한 위험이 따릅니다. 모든 투자 결정과 그에 따른 손익은 전적으로 사용자 본인의 책임입니다.

API 사용 시 LS증권 OpenAPI [이용약관](https://openapi.ls-sec.co.kr/howto-use)을 준수하시기 바랍니다.

## 패키지 구성

| 패키지 | 종류 | 용도 |
| --- | --- | --- |
| `RedoxNet.LsOpenApi.Core` | 라이브러리 | SDK: 인증(OAuth2 client_credentials), HTTP 클라이언트, TR 카탈로그, 지표. |
| `RedoxNet.Mcp.LsOpenApi` | dotnet tool | stdio 기반 MCP 서버. |
| `RedoxNet.LsOpenApi.Core.Catalog.Builder` | 개발 도구 | LS 문서 사이트를 스크래핑하여 내장 TR 카탈로그를 재생성. 배포되지 않습니다. |

## 빠른 시작 (NuGet 공개 후)

```jsonc
{
  "mcpServers": {
    "lsopenapi": {
      "command": "dnx",
      "args": ["RedoxNet.Mcp.LsOpenApi", "--yes"],
      "env": {
        "LS_APPKEY": "...",
        "LS_APPSECRETKEY": "...",
        "LS_MARKET": "virtual"
      }
    }
  }
}
```

### 환경 변수

| 이름 | 필수 | 설명 |
| --- | --- | --- |
| `LS_APPKEY` | 예 | LS OpenAPI 앱 키. |
| `LS_APPSECRETKEY` | 예 | LS OpenAPI 앱 시크릿 키. |
| `LS_MARKET` | 아니오 | `real` 또는 `virtual` (기본 `virtual`). |
| `LS_BASEURL` | 아니오 | REST 베이스 URL 재정의(거의 사용되지 않습니다). |

토큰 캐시 위치:
- Windows: `%LOCALAPPDATA%\RedoxNet\LsOpenApi\token.db`
- Linux/macOS: `~/.local/share/redoxnet/lsopenapi/token.db`

캐시는 SQLite 데이터베이스(WAL 모드)이며, 캐시 키는 `SHA256(appkey):market` 형태로 생성됩니다. 원시 앱 키는 디스크에 저장되지 않습니다. 토큰은 만료 5분 전에 자동으로 갱신됩니다.

### 자격 증명 처리 정책

이 서버는 **환경 변수로만** `LS_APPKEY` / `LS_APPSECRETKEY`를 받습니다. 의도적인 설계로, 채팅창·툴 인자·MCP elicitation 등 **모델 컨텍스트가 닿을 수 있는 어떤 경로로도 키를 입력받지 않습니다.** 호스트(Claude Desktop, AssistStudio 등)가 OS 환경 또는 자격 저장소에서 읽어 자식 프로세스에 주입하는 구조를 전제합니다.

- **디스크 평문 저장 없음.** 위 토큰 캐시는 `SHA256(appkey):market`만 보관하며, 원시 앱 키/시크릿은 메모리 외부로 나가지 않습니다.
- **로그·에러·툴 응답에 노출 안 됨.** 진단 출력은 `****` + 마지막 4자리만 표시하며(`AppKey`), 시크릿 키는 어떤 형태로도 로그에 찍지 않습니다.
- **잘못된 키 입력 시 LS의 `IGW00121` 등 인증 오류**는 [`LsAuthException`](src/RedoxNet.LsOpenApi.Core/Auth/LsAuthException.cs)으로 변환되어 툴 응답의 `error` 필드에만 표시되며, 입력한 키 값은 응답에 포함되지 않습니다.

이는 [MCP 스펙이 "Servers MUST NOT use elicitation to request sensitive information" 으로 권고](https://modelcontextprotocol.io/specification/2025-06-18/server/elicitation#security-considerations)하는 사항을 가장 보수적으로 해석한 결과입니다.

## 사용 시나리오 예시

#### 1. 일일 신호 리포트 자동화

매일 장 마감 후 관심종목의 매매 신호를 자동 분석하여 메신저로 받기:

```
스케줄러(예: Mcp.Runner) → LLM 호출
  → ls_get_chart(shcode, period_type="day,week,month", indicators=["ma:5","ma:20","ma:60"])
  → context.bullish_alignment, divergence_from_ma 등을 LLM이 종합 판단
  → 메신저 MCP(예: Mcp.Outbox)로 카톡/슬랙 리포트 전송
```

#### 2. 자연어 시세 조회

```
사용자: "삼성전자 현재가랑 호가창 보여줘"
  → ls_get_quote(shcode="005930")
  → 현재가 + 10단계 호가 즉시 응답
```

#### 3. 지표 기반 분석 대화

```
사용자: "코덱스 AI전력핵심설비, 12이평선 기준으로 매도 신호가 왔는지 봐줘"
  → ls_get_chart(shcode="490090", period_type="day,week,month",
                 indicators=["ma:12","ma:60"])
  → LLM이 일/주/월 다중 시간프레임 context를 종합 해석
  → "월봉으로는 매수 영역 유지 중이나, 일봉 12이평 근접 + 거래량 폭증으로 단기 경계 신호"
```

#### 4. 인터랙티브 차트

`include_chart: true`로 호출하면 Plotly v5 JSON spec이 응답에 포함됩니다. AssistStudio처럼 Plotly.js를 임베드한 클라이언트는 인라인 차트로, 그 외 클라이언트는 구조화된 데이터(`candles`/`indicators`/`context`)로 활용 가능합니다.

## 도구

### 메타 도구

| 도구 | 용도 |
| --- | --- |
| `ls_search_tr` | 한국어/영어 키워드로 내장 TR 카탈로그를 검색. |
| `ls_describe_tr` | 특정 TR 코드의 전체 입력/출력 스키마. |
| `ls_call_tr` | 호출자가 제공한 `in_block` JSON으로 임의의 TR 호출. |

### 의미 도구 (시세 데이터)

| 도구 | TR | 용도 |
| --- | --- | --- |
| `ls_get_quote` | `t1101` | 단일 종목의 현재가 + 10단계 호가창. |
| `ls_get_multi_quote` | `t8407` | **최대 50종목을 한 번에** 비교 — 가격/OHLC/거래량/최우선 매도·매수호가/총잔량/체결강도. 워치리스트 또는 종목 비교용. |
| `ls_get_stock_info` | `t1102` | 기업 프로필 + 펀더멘털: PER/PBR/EPS, 분기별 재무·성장률, 52주 + YTD 범위, 매도·매수 상위 5개 거래원, 외국인 동향, 상태 플래그(SPAC/관리종목). |
| `ls_get_chart` | `t8410` / `t8412` / `t1301` | OHLCV 캔들(일/주/월/년/분/틱), 선택적 지표(SMA, EMA, RSI, MACD, Bollinger), 사전 계산된 분석 context, **다중 시간프레임 한 번에 조회**(`period_type: "day,week,month"`). |
| `ls_search_stock` | `t8436` | 종목명 일부로 KOSPI/KOSDAQ 종목코드 검색; SPAC + 관리종목 플래그 + `instrument` 필터(`all` / `stock` / `etf`). |
| `ls_get_etf_info` | `t1901` | ETF/ETN 전용 스냅샷 — NAV, 추적기준지수, 괴리율, AUM, LP 5개, 52주/연중 가격 범위, 관련 선물. |
| `ls_get_etf_holdings` | `t1904` | ETF PDF(구성종목) — 종목별 비중·평가금액·시가총액 + ETF 요약(NAV/AUM/현금). 채권/현금 구성종목도 그대로 통과. |

### `ls_get_chart` 컨텍스트 메타데이터

모든 응답에는 사전 계산된 분석 결과를 담은 `context` 블록이 포함되어, LLM이 원시 OHLCV에서 평균·드로다운·이격을 다시 계산할 필요가 없습니다. 필드:

- `divergence_from_ma` — 마지막 종가 대비 각 `ma:N` / `ema:N` 지표 이격률(%).
- `volume.{latest,avg_20,ratio_20,avg_60,ratio_60}` — 거래량 대비 이동평균.
- `drawdown.{period_high,period_high_date,current,pct}` — 기간 최고가 대비 하락폭.
- `ma_trend` — 최근 5봉 기준 각 이동평균(MA) 방향성(`"up"` / `"down"` / `"flat"`).
- `bullish_alignment` — 짧은 기간 MA가 긴 기간 MA보다 위에 있을 때 `true`.

### 다중 시간프레임 한 번에 조회

`period_type`에 쉼표로 구분된 값을 전달하면 응답이 `frames[]` 배열로 감싸지며, 각 시간프레임마다 자체적인 candles / indicators / context를 갖습니다:

```jsonc
// ls_get_chart shcode=005930 period_type="day,week,month" indicators=["ma:5","ma:20","ma:60"]
{
  "shcode": "005930",
  "period_types": ["day", "week", "month"],
  "frames": [
    { "period_type": "day",   "tr_cd": "t8410", "count": 60, "candles": [...], "indicators": {...}, "context": {...} },
    { "period_type": "week",  "tr_cd": "t8410", "count": 60, "candles": [...], "indicators": {...}, "context": {...} },
    { "period_type": "month", "tr_cd": "t8410", "count": 60, "candles": [...], "indicators": {...}, "context": {...} }
  ]
}
```

단일 `period_type`은 호환성을 위해 평탄한 구조(`candles`, `indicators`, `context`가 최상위에 위치)를 유지합니다.

### 차트 렌더링 (`include_chart: true`)

`include_chart: true`로 호출하면 응답의 `chart` 필드에 Plotly v5 JSON spec이 함께 옵니다. 서버는 spec만 생성하며, 서버 측 이미지 렌더링이나 차트 라이브러리 의존성이 없습니다. 클라이언트는 spec을 그대로 Plotly.js에 전달합니다.

```jsonc
// ls_get_chart shcode=005930 period_type=day count=60 indicators=["ma:5","ma:20"] include_chart=true
{
  "shcode": "005930",
  "period_type": "day",
  "candles": [...],
  "indicators": {...},
  "context": {...},
  "chart": {
    "type": "plotly",
    "version": "5",
    "spec": {
      "data": [
        { "type": "candlestick", "name": "OHLC",   "x": [...], "open": [...], "high": [...], "low": [...], "close": [...], "increasing": { "line": { "color": "#E74C3C" } }, "decreasing": { "line": { "color": "#3498DB" } }, "yaxis": "y" },
        { "type": "scatter",     "name": "MA:5",   "x": [...], "y": [...], "mode": "lines", "line": { "color": "#F39C12" }, "yaxis": "y" },
        { "type": "scatter",     "name": "MA:20",  "x": [...], "y": [...], "mode": "lines", "line": { "color": "#27AE60" }, "yaxis": "y" },
        { "type": "bar",         "name": "Volume", "x": [...], "y": [...], "marker": { "color": ["#E74C3C", "#3498DB", ...] }, "yaxis": "y2" }
      ],
      "layout": {
        "title": { "text": "005930 — 일봉" },
        "xaxis": { "type": "category", "rangeslider": { "visible": false } },
        "yaxis":  { "title": { "text": "Price"  }, "domain": [0.3, 1.0] },
        "yaxis2": { "title": { "text": "Volume" }, "domain": [0.0, 0.25] },
        "hovermode": "x unified",
        "showlegend": true
      }
    }
  }
}
```

한국 증권사 관례: 양봉/양봉 거래량은 빨강(`#E74C3C`), 음봉/음봉 거래량은 파랑(`#3498DB`)입니다.

차트의 지표 처리:
- `ma:N`, `ema:N`, `bb:N,SD` → 가격 서브플롯에 오버레이로 그려집니다.
- `rsi:N`, `macd:F,S,Sig` → `indicators`에는 포함되지만 **그려지지는 않습니다** (별도 스케일의 서브플롯이 필요하며, 향후 개선 예정).

Plotly.js로 spec을 렌더링하는 최소 HTML 스니펫:

```html
<!DOCTYPE html>
<html>
<head>
  <script src="https://cdn.plot.ly/plotly-2.35.2.min.js"></script>
</head>
<body>
  <div id="chart" style="width: 900px; height: 500px;"></div>
  <script>
    // 여기서 `response`는 ls_get_chart가 반환한 JSON입니다.
    const { data, layout } = response.chart.spec;
    Plotly.newPlot("chart", data, layout, { responsive: true });
  </script>
</body>
</html>
```

Plotly.js를 임베드하지 않는 클라이언트는 `chart` 필드를 무시할 수 있으며, 구조화된 `candles` / `indicators` / `context` 페이로드가 단일 정보 출처(source of truth) 역할을 합니다.

### 지표 명세 (`ls_get_chart` 용)

| Spec | 의미 |
| --- | --- |
| `ma:N` | 단순이동평균(SMA), 기간 N. |
| `ema:N` | 지수이동평균(EMA). |
| `rsi:N` | 상대강도지수(RSI). |
| `macd:F,S,Sig` | MACD (fast/slow/signal). `.macd`, `.signal`, `.histogram` 반환. |
| `bb:N,SD` | 볼린저 밴드. `.lower`, `.middle`, `.upper` 반환. |

## 소스에서 빌드

```bash
dotnet restore mcp-lsopenapi.slnx
dotnet build mcp-lsopenapi.slnx -c Release
dotnet test mcp-lsopenapi.slnx -c Release
```

로컬 실행:
```bash
dotnet run --project src/RedoxNet.Mcp.LsOpenApi --framework net8.0
```

## 진행 상황

- ✅ M1 — Core 스캐폴드 + 인증(OAuth2 client_credentials, SQLite WAL 토큰 캐시, 시크릿 마스킹).
- ✅ M2 — testbed로 검증된 11개 TR이 포함된 내장 TR 카탈로그(`t1101`, `t1102`, `t8407`, `t8410`, `t8412`, `t1301`, `t8430`, `t8436`, `t9945`, `t1901`, `t1904`).
- ✅ M3 — TR 실행(`LsApiClient.CallTrAsync`): Polly 재시도, TR별 레이트 리미터, 헤더 + body 두 가지 페이징 모드.
- ✅ M4 — 메타 도구 3개(`ls_search_tr`, `ls_describe_tr`, `ls_call_tr`)를 노출하는 stdio MCP 서버.
- ✅ M5 — 의미 도구 7개: `ls_get_quote`, `ls_get_multi_quote`, `ls_get_stock_info`, `ls_get_chart` (+ 지표, 컨텍스트 메타데이터, 다중 시간프레임, `include_chart`로 Plotly v5 spec), `ls_search_stock`, **`ls_get_etf_info`, `ls_get_etf_holdings`**.
- ✅ LS 모의투자 서버에서 라이브 검증 완료 (v0.1.0-alpha.3).
- ⏳ 다음 릴리스 — 실시간(WebSocket), 계좌/잔고, 주문.

## License

MIT.
