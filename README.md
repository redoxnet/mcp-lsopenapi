<p align="right">
  <strong>한국어</strong> · <a href="README.en.md">English</a>
</p>

# mcp-lsopenapi

[![NuGet Mcp](https://img.shields.io/nuget/v/RedoxNet.Mcp.LsOpenApi?label=Mcp)](https://www.nuget.org/packages/RedoxNet.Mcp.LsOpenApi/)
[![NuGet Core](https://img.shields.io/nuget/v/RedoxNet.LsOpenApi.Core?label=Core)](https://www.nuget.org/packages/RedoxNet.LsOpenApi.Core/)
[![CI](https://github.com/redoxnet/mcp-lsopenapi/actions/workflows/ci.yml/badge.svg)](https://github.com/redoxnet/mcp-lsopenapi/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## AI 에이전트에게 내가 가진 종목을 물어보세요.

Claude·ChatGPT·Copilot 같은 AI 비서에 **LS증권 OpenAPI**를 붙입니다. 시세·차트·기업정보·ETF 구성·시장 스크리너를 평소 쓰던 대화창에서 자연어로 묻고 받습니다. **v0.5부터는 내 보유 종목·관심종목·관심테마**를, **v0.6부터는 코스피/업종/테마 시장 컨텍스트와 포트폴리오 백업/복원까지** 대화로 처리합니다.

> *"오늘 코스피 어땠어? 강한 업종은?"*
> *"한투에서 삼성전자 64주 평단 21.5만에 샀어"*
> *"내 보유 중 2차전지 테마만 모아봐"*
> *"포트폴리오 백업해줘"*

설정 한 번이면 됩니다. 종목 코드를 외울 필요도, HTS를 따로 띄울 필요도 없습니다.

> 개발자용 기술 디테일(환경 변수, 자격증명 정책, 도구 시그니처, SDK 사용법 등)은 [영문 README](README.en.md)에 있습니다.

---

## 이런 활용이 가능합니다

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

## v0.6 — 시장 컨텍스트 + 백업/복원

### 시장 한 통에 정리

> *"오늘 코스피 어땠어?"* → 종합·대형주·중형주·소형주 한 화면에
> *"오늘 강한 업종은?"* → 등락률 상위 N개 업종 한 번에 (KOSPI ~25개, 2.5초)
> *"전기전자 업종 종목 비교"* / *"2차전지 테마 종목 비교"* → 업종/테마 안의 종목 일괄 시세

업종(KRX 산업분류)과 테마(LS 큐레이션 그룹)를 명확히 분리합니다. *"2차전지"* 같이 후보가 여러 개인 키워드는 모델이 *"2차전지 셀? 소재? 장비?"* 후보를 보여주고 되묻습니다.

### 내 보유 × 테마 교차

> *"내 보유종목 중 2차전지 테마만"* → `ls_holdings_list(theme_keyword="2차전지")`
> *"내 한투 계좌의 AI 테마"* → `account` + `theme_keyword` AND 결합

종목을 등록·매수할 때 백그라운드로 t1532를 호출해 *"이 종목이 어떤 테마인지"* 캐싱합니다. 다음 list 호출에서 자동으로 필터링이 됩니다. (KRX 업종 필터는 v0.7로 — LS API에서 종목별 KRX 산업분류 매핑을 제공하지 않는 게 v0.6 구현 중 확인됨)

### 포트폴리오 백업·이관

> *"포트폴리오 백업해줘"* → `exports/portfolio-2026-05-16T….json` 자동 생성
> *"다른 PC로 옮길게"* → export → 파일 복사 → `ls_portfolio_import`

`schema_version` 명시된 단일 JSON. 머지 모드(기본, 중복 skip + 사유 기록) / 교체 모드(`confirm=true` 필요, 덮어쓰기 직전 자동 백업). 캐시(`stocks`, `stock_themes`)는 export 대상 아님 — 시세 enrichment에서 자동 재구축.

## v0.5 — 내 포트폴리오, 노트패드처럼 기록

여러 증권사 계좌, 매수/매도 기록, 액면분할 대응까지 — 별도 화면 없이 대화로 관리합니다.

> *"LS에서 삼성전자 64주 평단 21.5만에 샀어"* → 자동 등록
> *"5주 더 28만에 추가 매수"* → 가중평균 평단 자동 계산
> *"LS일렉트릭 5:1 분할됐대"* → 모든 계좌에 일괄 반영 (v0.6에서 `ls_holdings_corporate_action`로 통합)
> *"키움 보유분만 보여줘"* → 계좌 필터링
> *"미래에셋 삼성전자 24주 익절"* → 부분 매도, 평단 유지

흩어진 보유분이 자동으로 **계좌별 + 통합 평가손익**으로 정리됩니다. 잘못된 평단을 넣으면 현재가 대비 5배 이상 차이 날 때 *"분할/무상증자 가능성"* 경고가 따라붙어 모델이 되묻습니다.

내 데이터는 **로컬 디스크에만** (`%LOCALAPPDATA%\RedoxNet\LsOpenApi\portfolio.db`, token.db 옆) 저장됩니다. 브로커 동기화·외부 송신 없음.

---

## ⚡ v0.4 — 같은 질문, 16× 적은 컨텍스트

![v0.3 vs v0.4 token efficiency](docs/case-studies/assets/v0.4.0-token-efficiency-hero.png)

동일한 모델(`claude-sonnet-4-6`)에 *"2024년 1~6월 삼성전자 일봉 + MA60 추세"* 를 던졌을 때, v0.3은 좁은 창에 MA60을 채우기 위해 두 번의 도구 호출 (3개월 padding 시도 → 부족 인지 → `count=190`으로 재시도) 이 필요했습니다.

v0.4는 모델이 첫 시도에 `with_warmup=true`를 선택해 한 번에 끝냅니다. 표시 60바, 분석 300바로 분리되어 long-period 지표가 모두 채워지고, 응답은 summary-first 구조라서 raw OHLCV 60개를 컨텍스트에 쏟지 않습니다.

전체 7-턴 세션 분석 → [docs/case-studies/v0.4.0-token-efficiency.md](docs/case-studies/v0.4.0-token-efficiency.md)

---

## 설치 — 1분 컷

**사전 준비.** `dnx`는 **.NET SDK 10 이상**에 들어 있는 dotnet 도구 실행기입니다. 아직 없으면 [.NET 다운로드](https://dotnet.microsoft.com/download/dotnet/10.0)에서 SDK를 먼저 설치하세요 (Windows/macOS/Linux 모두 지원). 터미널에서 `dnx --help`가 도움말을 출력하면 준비 완료. — 처음 `dnx RedoxNet.Mcp.LsOpenApi`를 실행하는 시점에 패키지 자동 다운로드 + 캐싱, 다음 실행부터는 즉시 기동.

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

## 이렇게 질문해 보세요!

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

### 내 포트폴리오 / 매수·매도 기록 (v0.5)
> *"한투에 삼성전자 10주 6.8만 샀어"* / *"5주 더 7.5만에 추가 매수"* / *"내 종목 평가손익 보여줘"* / *"민테크 10:1 분할 반영"*

여러 증권사 계좌, 종목별 수량·평단·메모, 매수 시 가중평균 평단 자동 계산, 부분/전량 매도(0주 자동 정리), 액면분할/무상증자 일괄 반영, 계좌별 + 통합 평가손익. 같은 종목이 여러 계좌에 있을 때 매도/삭제는 모델이 어느 계좌인지 되묻습니다.

### 관심종목 / 관심 테마 (v0.5 + v0.6)
> *"관심종목에 NAVER 추가"* / *"AI 그룹 만들어서 거기에"* / *"반도체 장비 테마 추적해줘"* / *"내가 보고 있는 테마들 등락률"*

그룹별 책갈피(`반도체-AI` / `2차전지수혜주` 같은 사용자 분류), 테마 코드(t1531 tmcode)로 평균등락률 추적. 시세 enrich는 LS 자격증명 있을 때 자동.

> **v0.6 명명 정정.** v0.5에서 `ls_watched_sectors_*`로 부르던 도구는 사실 LS 테마(tmcode)였습니다. v0.6에서 `ls_watched_themes_*`로 정정 — 기존 등록 데이터는 schema v3 마이그레이션으로 자동 보존됩니다.

### 시장 컨텍스트 (v0.6+)
> *"오늘 코스피"* / *"강한 업종"* / *"2차전지 테마 종목"* / *"삼성전자가 속한 테마들"*

지수 단건 조회(`ls_get_index_quote`), 해외 지수·환율·선물 단건 조회(`ls_get_global_market_quote`), 업종 등락률 랭킹(`ls_get_industry_indices`), 업종/테마 내 종목 비교(`ls_get_industry_stocks`, `ls_get_theme_stocks`), 종목별 테마 역조회(`ls_get_stock_themes`). 키워드가 모호하면 후보를 보여주고 되묻습니다.

### 스크리너 (v0.7)
> *"PER 낮은 종목 30개"* / *"코스닥 ROE 상위"* / *"오늘 외인 매수 상위"* / *"삼성전자 외인·기관 최근 한 달 흐름"* / *"카카오 다음 주주총회 언제"* / *"내 보유 중 관리종목 있어?"*

펀더멘털 랭킹(PER/PBR/PEG/EPS/BPS/ROE + 성장률/부채비율/유보율), 시간대별 매매주체 종합 + 종목별 일별 외인·기관 수급, 단일 종목 코퍼레이트 액션·주주총회 일정, KRX 관리·매매정지·정리매매·단기과열 등 13가지 지정 종목 — 보유 종목 한정 필터링도 한 번에.

### 종목 분석 + 시장 수급 (v0.8)
> *"삼성전자 투자의견"* / *"SK하이닉스 공매도 추이"* / *"오늘 52주 신고가 종목"* / *"요즘 고객예탁금·신용잔고 추이"*

증권사 투자의견·목표주가 변경 이력(`ls_get_analyst_opinions`), 종목별 공매도 일별 추이(`ls_get_short_selling_trend`), 신고/신저가 스크리너(`ls_get_high_low_stocks` — 돌파유지/일시돌파 선택, ETF·ETN 제외 기본), 증시 주변 자금 추이(`ls_get_market_funds_trend` — 고객예탁금·신용잔고·미수금·펀드 자금).

### 지수 시계열 (v0.7)
> *"코스피 최근 한 달 추이"* / *"KOSDAQ 주봉으로 60개"* / *"KRX100 월봉"*

지수의 일/주/월봉 + 시고저종가 + 거래량·거래대금 + 시장 폭(상승/하락/보합/상한/하한) + 외인·기관 순매수.

### 업종 필터 + 메타데이터 새로고침 (v0.7)
> *"내 보유 중 반도체 종목"* / *"증권업 종목만"* / *"지금 새로 가져와"*

FICS 산업 분류 기반 보유종목 필터(`ls_holdings_list(industry?)`), 테마·산업 캐시 동기 새로고침(`ls_stocks_refresh_metadata`).

### 백업/복원 (v0.6)
> *"포트폴리오 백업"* / *"이 파일 복원해줘"*

단일 JSON(schema v1) 백업·복원. `mode=merge` 기본 (중복 skip), `mode=replace` 시 `confirm=true` 필수 + 자동 백업 생성.

전체 도구의 정확한 인자·반환 스키마는 [영문 README의 Tools 섹션](README.en.md#tools) 참고.

---

## 진행 상황

- [x] **v0.1.0** — MCP 서버 초기 공개. LS증권 OpenAPI 인증 + 10개 시세 도구.
- [x] **v0.2.0** — MCP 호스트 호환성 정비 (도구 surface 변경 없음). 스키마·UI 리소스 메타데이터 정리.
- [x] **v0.3.0** — 시장 스크리너 도구 + 검색 파라미터 정리 + 차트 표시 다듬기.
- [x] **v0.4.0** — 토큰 효율 차트 페이로드, dataset handle 후속 도구 2종, ZigZag 기반 swing 감지, indicator coverage 블록.
- [x] **v0.5.0** — 로컬 포트폴리오 모듈. 멀티 계좌·매수/매도/액면조정·관심종목·관심 테마. 13 → 37 도구. 로컬 SQLite 저장(브로커 동기화 없음).
- [x] **v0.6.0** — 시장 컨텍스트(지수/업종/테마) + 포트폴리오 JSON 백업/복원 + 테마 자동 enrich + Tier 1 도구 압축. 37 → 39 도구.
- [x] **v0.7.0** — 스크리너 4종 (펀더멘털/투자자 수급/이벤트 일정/관리·매매정지) + 지수 시계열 + 동기 메타데이터 refresh + FICS 업종 enrichment(`ls_holdings_list(industry?)`) + holdings.avg_price 정수 저장 + ETF themes_pending 수정 + Tier 2 도구 압축. 40 → 43 도구.
- [x] **v0.8.0** — 해외 지수·환율·선물 단건 조회 + 투자의견·공매도·신고저가·증시 주변자금 래퍼 5종 + TR 카탈로그 12종 추가. 43 → 48 도구.
- [x] **v0.9.0** — 응답 shape·토큰 이코노미 리팩터 (SPEC v0.9). `ls_get_index_history` / `ls_get_stock_info` / `ls_holdings_list` 3종 재구성 — 기본 호출 토큰 66~97% 절감. MCP SDK 1.3.0 + net10.0.
- [ ] **v0.10.0** — 도구 표면 압축 (SPEC v0.10). `LS_TOOL_PROFILE` 프로파일 + 도메인 dispatcher 5종으로 48 → 약 32 도구.
- [ ] **v2.0.0** — 실시간 시세 + 실시간 뉴스 헤더(NWS) → 본문(t3102) 페어 wrapper, 실 계좌 조회·잔고, 주문 발주 (WebSocket 전반).

상세 변경 내역은 [RELEASENOTES.Mcp.md](RELEASENOTES.Mcp.md) · [RELEASENOTES.Core.md](RELEASENOTES.Core.md) 참고.

---

## 면책 조항

이 프로젝트는 **비공식 third-party MCP 서버**입니다. LS증권(LS Securities Co., Ltd.)과 공식적인 제휴·후원·승인 관계가 없으며, "LS증권" 및 관련 상표는 해당 권리자의 소유입니다.

본 도구는 **정보 제공 목적의 시세·차트 데이터 조회용**입니다. 투자 자문이나 매매 권유가 아니며, 주식 거래에는 원금 손실을 포함한 위험이 따릅니다. 모든 투자 결정과 그에 따른 손익은 전적으로 사용자 본인의 책임입니다.

API 사용 시 [LS증권 OpenAPI 이용 안내](https://openapi.ls-sec.co.kr/howto-use)를 참조하시고, 사이트 하단의 "이용약관" 링크로 표시되는 정식 약관을 확인 후 준수하시기 바랍니다.

v0.x.x는 **국내주식 read-only 시세 데이터 + 로컬 포트폴리오 노트** 범위입니다. 포트폴리오 도구(v0.5+)는 수동 입력 기반의 로컬 저장만 지원하며 브로커 계좌 동기화·실주문은 하지 않습니다. v0.6에서 시장 컨텍스트(지수·업종·테마)와 포트폴리오 JSON 백업/복원이 추가됐지만, 모든 데이터는 여전히 로컬 디스크에만 보관됩니다. 실시간 시세(WebSocket), 실 계좌 조회/주문은 후속 릴리스 예정입니다.

---

## 라이선스 · 관련 자료

- License — [MIT](LICENSE)
- 개발자용 기술 문서 — [README.en.md](README.en.md)
- 릴리스 노트 — [Mcp](RELEASENOTES.Mcp.md) · [Core](RELEASENOTES.Core.md)
- 사례 분석 — [v0.4 token efficiency](docs/case-studies/v0.4.0-token-efficiency.md)
