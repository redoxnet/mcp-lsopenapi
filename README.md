<p align="right">
  <strong>한국어</strong> · <a href="README.en.md">English</a>
</p>

# mcp-lsopenapi

[![NuGet Mcp](https://img.shields.io/nuget/v/RedoxNet.Mcp.LsOpenApi?label=Mcp)](https://www.nuget.org/packages/RedoxNet.Mcp.LsOpenApi/)
[![NuGet Core](https://img.shields.io/nuget/v/RedoxNet.LsOpenApi.Core?label=Core)](https://www.nuget.org/packages/RedoxNet.LsOpenApi.Core/)
[![CI](https://github.com/redoxnet/mcp-lsopenapi/actions/workflows/ci.yml/badge.svg)](https://github.com/redoxnet/mcp-lsopenapi/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## 한국 주식을 AI에게 그냥 물어보세요.

Claude·ChatGPT·Copilot 같은 AI 비서에 **LS증권 OpenAPI**를 붙입니다. 시세·차트·기업정보·ETF 구성·시장 스크리너를 평소 쓰던 대화창에서 자연어로 묻고 받습니다.

> *"내가 들고 있는 삼성전자, 지금 흐름이 어때?"*
> *"오늘 많이 오른 종목 중에 이유가 있어 보이는 것만 골라줘"*
> *"KODEX 200은 실제로 어떤 주식들을 담고 있어?"*

설정 한 번이면 됩니다. 종목 코드를 외울 필요도, HTS를 따로 띄울 필요도 없습니다.

> 개발자용 기술 디테일(환경 변수, 자격증명 정책, 도구 시그니처, SDK 사용법 등)은 [영문 README](README.en.md)에 있습니다.

---

## 이런 게 됩니다

### 차트 + 추세 설명을 한 번에

> *"SK하이닉스 일봉 차트 보여주고 추세 정렬 봐줘"*

AI가 일봉을 불러와 인라인 차트로 띄우고, 이동평균선 배열·거래량·고점 대비 낙폭을 종합해 *"단기 추세는 살아있지만 60일선 근처 매물대 부담"* 같은 한 문장 진단을 자연어로 풉니다.

![SK하이닉스 일봉 차트 — AssistStudio 인라인 렌더링](docs/assiststudio-chart-skhynix.png)

### 시장 스크리닝 → 후보 종목 분석까지 한 대화 안에서

> *"거래대금 상위 종목을 기술적으로 분석해줘"*

AI가 거래대금 상위 리스트를 받고, 그 중 관심 종목 한두 개를 골라 일봉·주봉 지표로 후속 분석을 이어갑니다. 검색 결과에서 분석으로 넘어가는 데 별도 화면 전환이 없습니다.

![LG이노텍 다중 시간프레임 분석 — AssistStudio](docs/assiststudio-screener-analysis.png)

### 변곡점·진행 중인 swing 짚기

> *"카카오 5년 월봉 보여주고 주요 변곡점들 짚어줘. 지금 진행 중인 흐름도 같이 설명해줘."*

AI가 사전 계산된 ZigZag 변곡점 목록을 받아 *"2022-10 저점에서 2024-07 고점까지 +X%, 이후 조정 진입 중"* 처럼 시간순으로 풀어 설명합니다. 마지막 항목은 "아직 진행 중인 swing"으로 별도 표기됩니다.

### 좁은 구간 + 장기 지표도 한 번에

> *"2024년 1~6월 삼성전자 일봉만 따로 보여줘. 그 기간 안에서 MA60 추세도 같이."*

좁은 기간을 명시하면서 그 안에서 60일 이동평균 추세를 묻는, 일반 도구로는 두세 번 왔다 갔다 해야 풀리는 케이스. AI가 첫 시도에 정확히 처리합니다 — 자세한 비교는 아래 v0.4 case study.

---

## ⚡ v0.4 — 같은 질문, 16× 적은 컨텍스트

![v0.3 vs v0.4 token efficiency](docs/case-studies/assets/v0.4.0-token-efficiency-hero.png)

동일한 모델(`claude-sonnet-4-6`)에 *"2024년 1~6월 삼성전자 일봉 + MA60 추세"* 를 던졌을 때, v0.3은 좁은 창에 MA60을 채우기 위해 두 번의 도구 호출 (3개월 padding 시도 → 부족 인지 → `count=190`으로 재시도) 이 필요했습니다.

v0.4는 모델이 첫 시도에 `with_warmup=true`를 선택해 한 번에 끝냅니다. 표시 60바, 분석 300바로 분리되어 long-period 지표가 모두 채워지고, 응답은 summary-first 구조라서 raw OHLCV 60개를 컨텍스트에 쏟지 않습니다.

전체 7-턴 세션 분석 → [docs/case-studies/v0.4.0-token-efficiency.md](docs/case-studies/v0.4.0-token-efficiency.md)

---

## 설치 — 1분 컷

LS증권 OpenAPI 키 한 쌍(`AppKey` + `AppSecretKey`) 이 필요합니다 — [LS증권 OpenAPI 포털](https://openapi.ls-sec.co.kr/)에서 발급(모의투자도 동일 절차, 자세한 단계는 [영문 README](README.en.md#getting-an-api-key)). 키를 받았으면 사용하는 AI 호스트의 MCP 설정에 아래 한 덩어리를 붙여 넣고 재시작합니다.

### Claude Desktop / Claude Code

`claude_desktop_config.json` (Claude Desktop) 또는 워크스페이스 루트의 `.mcp.json` (Claude Code):

```jsonc
{
  "mcpServers": {
    "lsopenapi": {
      "command": "dnx",
      "args": ["RedoxNet.Mcp.LsOpenApi", "--yes"],
      "env": {
        "LS_APPKEY": "...",
        "LS_APPSECRETKEY": "...",
        "LS_MARKET": "virtual"  // "virtual" 모의투자 / "real" 실거래
      }
    }
  }
}
```

> 키는 호스트가 자식 프로세스에 환경변수로 넘기는 것 외의 경로(채팅, 도구 인자, MCP 엘리시테이션)로는 받지 않습니다 — 보안상 의도된 설계입니다. 자세한 정책은 [영문 README](README.en.md#credential-handling-policy) 참고.

다른 호스트(Codex CLI / VS Code / AssistStudio)의 설정 예시는 [영문 README](README.en.md#quick-start)에 있습니다.

### AssistStudio (인라인 차트)

Microsoft Store에서 *AssistStudio* 설치(Product ID `9N09D0QGSTZD`) → Settings → Connect → Add MCP Server. Command `dnx`, Arguments `RedoxNet.Mcp.LsOpenApi --yes`, 환경 변수 한 줄씩. 인라인 차트 렌더링은 AssistStudio v1.1 이상이 필요합니다.

---

## 무엇을 물어볼 수 있나

도구 시그니처가 아니라 *"어떤 질문에 답할 수 있는가"* 로 묶었습니다.

### 현재 시세 / 호가
> *"삼성전자 지금 얼마야?"* / *"카카오 호가창 보여줘"* / *"내 관심종목 10개 가격 한번에 비교해줘"*

단일 종목의 현재가와 10단계 호가, 또는 최대 50종목 일괄 비교.

### 차트 + 기술적 분석
> *"SK하이닉스 일·주·월봉 같이 보여줘"* / *"이동평균선이랑 RSI 같이 그려줘"* / *"여기에 MA200도 추가해줘"*

일봉/주봉/월봉/년봉/분봉/틱 차트, 이동평균·RSI·MACD·볼린저밴드 같은 기술 지표, 변곡점·MA 정렬·고점 대비 낙폭 같은 사전 계산된 분석. 추가 지표나 기간 변경은 후속 대화에서 그대로.

### 종목 찾기
> *"카카오 종목코드 뭐야?"* / *"바이오 ETF 좀 알려줘"* / *"이름에 '에너지' 들어가는 종목 찾아줘"*

KOSPI/KOSDAQ 종목명 부분 검색, 일반주식/ETF 필터링, SPAC·관리종목 플래그.

### 기업 정보 / 재무
> *"삼성전자 PER이랑 분기별 매출 추이 알려줘"* / *"외국인 보유 추이는?"*

PER/PBR/EPS, 분기별 재무·성장률, 52주·연중 가격 범위, 상위 매수·매도 거래원, 외국인 동향, SPAC·관리종목 상태.

### ETF 분석
> *"KODEX 200 NAV랑 괴리율 보여줘"* / *"TIGER 미국나스닥100 구성종목 비중 상위 10개"*

ETF/ETN 전용 정보(NAV, 추적오차율, 괴리율, AUM, LP), 구성종목(PDF) — 비중·평가금액·시가총액 순 정렬과 상위 N개 제한 옵션.

### 시장 스크리닝
> *"오늘 상승률 상위 10개 종목"* / *"거래대금 상위 + 시가총액 1조 이상으로 필터"* / *"오늘 거래량 급증 종목"*

상승·하락·보합 상위, 시가총액·거래량·거래대금 상위, 전일 동시간 대비 거래 급증 — 가격·거래량 필터링, KOSPI/KOSDAQ 분리·통합 옵션.

전체 도구의 정확한 인자·반환 스키마는 [영문 README의 Tools 섹션](README.en.md#tools) 참고.

---

## 면책 조항

이 프로젝트는 **비공식 third-party MCP 서버**입니다. LS증권(LS Securities Co., Ltd.)과 공식적인 제휴·후원·승인 관계가 없으며, "LS증권" 및 관련 상표는 해당 권리자의 소유입니다.

본 도구는 **정보 제공 목적의 시세·차트 데이터 조회용**입니다. 투자 자문이나 매매 권유가 아니며, 주식 거래에는 원금 손실을 포함한 위험이 따릅니다. 모든 투자 결정과 그에 따른 손익은 전적으로 사용자 본인의 책임입니다.

API 사용 시 [LS증권 OpenAPI 이용 안내](https://openapi.ls-sec.co.kr/howto-use)를 참조하시고, 사이트 하단의 "이용약관" 링크로 표시되는 정식 약관을 확인 후 준수하시기 바랍니다.

v0.x.x는 **국내주식 read-only 시세 데이터** 범위입니다. 실시간 시세(WebSocket), 계좌/잔고, 주문은 후속 릴리스에 포함될 예정입니다.

---

## 라이선스 · 관련 자료

- License — [MIT](LICENSE)
- 개발자용 기술 문서 — [README.en.md](README.en.md)
- 릴리스 노트 — [Mcp](RELEASENOTES.Mcp.md) · [Core](RELEASENOTES.Core.md)
- 사례 분석 — [v0.4 token efficiency](docs/case-studies/v0.4.0-token-efficiency.md)
