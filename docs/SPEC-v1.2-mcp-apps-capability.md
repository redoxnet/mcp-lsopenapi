# SPEC: v1.2.0 — MCP Apps Capability Negotiation

- **상태**: 구현 진행 — Spike A·B 완료 (§6 Spike 결과). 구현 순서 W1 → W3c → W2 → W3a/W3b → W4. **크로스-레포 의존**: AssistStudio v1.1의 `io.modelcontextprotocol/ui` advertise (§6 결정 · §13).
- **작성일**: 2026-05-22 (`v1.2-mcp-apps-capability-slice.md` 초안 → 리포 실제 상태와 대조해 본 SPEC으로 정리)
- **대상 버전**: v1.2.0 — v1.1 안정 라인 위의 **첫 기능 슬라이스**. 단일 슬라이스 집중 릴리스.
- **작성자**: Jong Hyun + Claude
- **선행**: v1.1.0 release (program-trading 슬라이스 — 별도 SPEC 없음, `RELEASENOTES.Mcp.md` v1.1.0 참조)
- **스펙 근거**: [SEP-1865 — MCP Apps](https://modelcontextprotocol.io/seps/1865-mcp-apps-interactive-user-interfaces-for-mcp) (Final, Extensions Track) · extension id `io.modelcontextprotocol/ui`
- **범위**: 차트 노출·마커·스키마를 호스트의 MCP Apps capability에 따라 자동 분기. leaky `chart_available` 마커 제거 + capability 조건부 `tools/list` 표면.

---

## 1. 목표

차트 노출·마커·스키마를 호스트의 MCP Apps capability에 따라 자동 분기한다. UI 렌더 가능 호스트에는 Plotly 차트를 그대로, 미지원 호스트에는 **텍스트 전용 표면**을 제공해 — 모델이 "차트가 있다"고 거짓 안내하는 leaky marker 현상을 제거한다.

이 슬라이스는 기능 추가가 아니라 **정직성 교정**이다. v1.1.0의 program≠institutional 용어 정정(§2.1)과 같은 원칙 — *모델에게 부정확한 신호를 주지 않는다* — 을 UI 레이어로 확장한다.

## 2. v1.1.0에서 넘어온 상태

> 첨부 초안은 `ls_analyze_program_flow`의 `include_chart` 제거를 "v1.1.1 hotfix"로 표기했으나, 실제로는 **v1.1.0의 일부**다. 도구가 v1.1에서 처음 출시되므로 미출시 상태에서 파라미터를 빼는 것은 무비용 — 별도 hotfix 릴리스가 아니다. 본 SPEC은 이 점을 정정해 기술한다.

### 2.1 v1.1.0이 이미 처리한 것 (이 슬라이스의 선행 작업)

| 항목 | 커밋 | 내용 |
|---|---|---|
| `ls_analyze_program_flow` `include_chart` 제거 | `982d4d0` | 파라미터 + `chart_available` + `structuredContent.chart` 일괄 제거. analyze는 이제 **순수 텍스트-verdict 도구**. `PlotlyEmittingToolNames`에서도 제외 |
| program ≠ investor-class 용어 정정 | `9121732` | `ls_get_program_trading` / `ls_analyze_program_flow`의 description·XML 문서·`note`에서 "institutional" 제거 → "program-trading footprint"로 재포지셔닝 |
| description ↔ note 문구 통일 | `9e44713` | 채널 caveat를 두 곳에서 자구 동일하게 |
| scope note의 무수식 "accumulating" 제거 | `933422f` | `ls_get_program_trading` 5개 scope `note`에 `ls_get_investor_flow` 교차검증 steer 추가 |

→ **시사점**: v1.1.0은 "모델이 보는 런타임 텍스트가 거짓·과장을 담지 않도록" 하는 작업을 program-trading 도구군에 적용했다. v1.2는 **같은 원칙을 차트/UI 마커로 확장**한다. `chart_available:true`는 미지원 호스트에서 정확히 그런 거짓 신호다.

### 2.2 v1.2가 다루는 잔여 상태

차트-emitting 도구는 호스트 capability와 무관하게 **항상** `include_chart` 파라미터를 노출하고, `structuredContent.chart` + `_meta.ui`를 송출하며, 응답 텍스트에 `chart_available:true` 마커를 붙인다. MCP Apps 미지원 호스트(Codex CLI 등)는 `structuredContent`를 무시하므로 — 차트는 안 보이는데 모델은 "차트가 있다"고 안내한다.

이는 `ls_get_chart`/`ls_get_etf_holdings` 시절(v0.2/v0.4)부터의 **선존 이슈**이며 v1.1 회귀가 아니다. v1.0/v1.1에서 의도적으로 미뤘고, v1.2가 청산한다.

## 3. 배경 — 문제와 SEP-1865 근거

### 3.1 문제

1. **Leaky marker** — 모델 안내와 사용자가 보는 화면의 mismatch. 호스트 능력을 고려하지 않은 채 `chart_available:true`를 송출.
2. **낭비된 페이로드** — 매 응답마다 capability와 무관하게 Plotly spec / `_meta.ui` / UI 리소스를 advertise. 미지원 호스트엔 전부 무의미한 바이트.
3. **결정 주체 mismatch** — "모델이 `include_chart`로 차트 노출을 결정"하는 구조 자체가 SEP-1865의 권장 분기 방향(**host capability 기반**)과 어긋난다.

### 3.2 SEP-1865 근거

Capability 협상은 spec 필수다 — 호스트가 `initialize`에서 extension capability로 advertise한다. 권장 패턴(Server Behavior 절):

> "Servers SHOULD provide text-only fallback behavior for all UI-enabled tools"
> "Servers MAY register different tool variants based on host capabilities"

호스트가 `initialize`에서 advertise하는 형식:

```json
{
  "capabilities": {
    "extensions": {
      "io.modelcontextprotocol/ui": {
        "mimeTypes": ["text/html;profile=mcp-app"]
      }
    }
  }
}
```

현재 코드는 이 SHOULD를 따르지 않는다 — capability와 무관하게 항상 UI 표면을 advertise한다. 또한 `chart_available` 마커는 SEP-1865에 근거가 없는 자체 발명품이다 ("모델에게 UI 렌더 사실을 알려라"는 지침이 spec에 없음) → 제거 대상.

> ⚠️ **현실 점검 (§6 참조)**: 2026-05-22 기준 `io.modelcontextprotocol/ui`를 advertise하는 호스트는 0개다 — AssistStudio조차 capability 협상 없이 `structuredContent.chart`를 무조건 렌더한다. v1.2의 capability 게이팅이 의미를 가지려면 AssistStudio v1.1이 이 capability를 advertise해야 한다 (§6 결정).

## 4. 현재 리포 상태 — 사실관계 대조 (v1.1.0 기준)

> 첨부 초안의 도구 수("4개")는 부정확하다. 본 절이 정확한 수치를 고정한다.

### 4.1 `chart_available` 마커 — **5개 도구**가 방출

| 도구 | 파일 | 위치 |
|---|---|---|
| `ls_get_chart` | `Tools/GetChartTool.cs` | 단일(reference/display) 응답, export 빌더, multi 응답 |
| `ls_add_indicator` | `Tools/GetChartTool.cs` | follow-up 응답 |
| `ls_reframe_chart` | `Tools/GetChartTool.cs` | follow-up 응답 |
| `ls_get_etf_holdings` | `Tools/GetEtfHoldingsTool.cs` | 단일 응답 |
| `ls_get_program_trading` | `Tools/GetProgramTradingTool.cs` | 5개 scope payload (market intraday/daily, ranking, stock intraday/daily) |

`ls_analyze_program_flow`는 v1.1.0(`982d4d0`)에서 이미 제거됨.

### 4.2 `PlotlyEmittingToolNames` (`_meta.ui` 부착 대상) — **3개**

`Apps/UiResources.cs`의 `PlotlyEmittingToolNames` 상수 = `ls_get_chart`, `ls_get_etf_holdings`, `ls_get_program_trading`.

### 4.3 ⚠️ 불일치 발견 — `_meta.ui` 누락 도구 2개

`ls_add_indicator` / `ls_reframe_chart`는 `structuredContent.chart`(`CandlestickChartBuilder.Build`)를 방출하지만 `PlotlyEmittingToolNames`에 **없다** → `_meta.ui` 미부착 → MCP Apps 호스트가 이들의 차트를 인라인 렌더하지 못한다.

이는 v1.1.0부터 존재하는 **선존 갭**이다. 두 도구는 사용자의 follow-up("MA200 추가", "일봉으로 바꿔줘")에 대해 갱신된 차트를 내놓으므로 논리상 인라인 렌더되어야 한다. → v1.2 W3a에서 청산 (§7).

### 4.4 `_meta.ui` 형식 — **이미 nested**

`UiResources.cs`의 `PatchToolMetaIfChartEmitting` / `BuildToolUiMeta` / `BuildResourceUiMeta`는 모두 `meta["ui"]["resourceUri"]` 형태의 **nested**(SEP-1865 정식) 형식을 쓴다. 첨부 초안의 W4("flat이면 nested로 마이그레이션")는 **검증만 남는다** — 마이그레이션 불필요.

부수 관찰: `BuildToolUiMeta()`와 `PatchToolMetaIfChartEmitting`이 동일한 `{ui:{resourceUri,visibility}}`를 중복 생성한다. W3에서 capability-aware 필터로 일원화하며 통합 권장 (minor).

## 5. Non-goals (이 슬라이스 범위 외)

- `HostContext.theme` / `locale` / `timeZone` 활용 (Plotly 테마/현지화) — **v1.3+ 후보**
- `visibility: ["app"]` 도구 도입 (UI iframe 내부 인터랙션용) — 별도 슬라이스
- 신규 차트 도구 추가
- 비-차트 기능의 capability 게이팅
- **백로그의 다른 항목** — Analysis phase 2(VWAP-deviation/POV, market-regime analyzer), t1640/t1631 snapshot, accounts/trading/realtime TR surface는 v1.2 범위 밖 (§11 백로그 점검 참조)

## 6. Spike Gate

본 슬라이스의 구현 형상은 두 spike 결과에 좌우된다. **Spike 통과 전 본 구현 착수 금지.**

### Spike A — SDK extension capability 노출

**목표**: `ModelContextProtocol` C# SDK 1.3.0이 `initialize` 시 호스트가 advertise한 extension capability를 사용 가능한 형태로 노출하는가.

**확인 항목**:
- `ClientCapabilities`(또는 동등 표면)에 `Extensions` 필드 존재 여부 — string-keyed dict인지, strongly-typed 컬렉션인지
- 키 `io.modelcontextprotocol/ui` 접근 패턴
- `mimeTypes` 등 nested 필드 deserialize 처리
- `tools/list` 필터의 `RequestContext`가 연결된 클라이언트의 capability에 도달 가능한지
- `AddListToolsFilter` / `AddCallToolFilter` 가용성 + 시그니처 (W3에서 사용)

**결과별 분기**:

| 결과 | 대응 |
|---|---|
| Pass (typed/dict 노출) | 본 슬라이스 계획대로 진행 |
| Partial (raw JSON 파싱 필요) | 수동 deserializer 추가 후 진행 (커밋 1개 추가) |
| Fail (extension 정보 손실) | SDK 업그레이드 PR 또는 우회 패치 대기 — **슬라이스 보류** |

### Spike B — text + structuredContent 응답 라우팅 신뢰도

**목표**: v1.1 E2E(2026-05-22, Claude 호스트)에서 관측된 "verdict 텍스트 누락" 이상이 (a) `ls_analyze_program_flow` 특이 현상이었는지 (b) "text + structuredContent 공존" 일반 라우팅 이슈인지 판별.

**배경**: v1.1.0(`982d4d0`)이 analyze에서 `include_chart` 자체를 제거해 증상 경로는 없앴으나, "text + structuredContent 공존" 패턴 자체의 신뢰도는 미검증. 현재 `ls_get_chart` / `ls_get_etf_holdings` / `ls_get_program_trading`은 여전히 이 패턴을 쓴다 (메모리 `next_mcp_apps_capability.md`에 기록됨).

**방법**:
- MCP Apps 지원 호스트(Claude Code 등)에서 `ls_get_chart` / `ls_get_program_trading`을 `include_chart=true`/`false` 양쪽으로 10회 이상 호출
- 응답 `content` 배열을 캡처해 텍스트 block 누락 여부 확인 — `include_chart` true/false 간 모델이 받는 내용을 diff

**결과별 분기**:

| 결과 | 대응 |
|---|---|
| 누락 0건 | (a) 결론 — analyze 특이. 추가 조사 불필요 |
| 누락 발견 | (b) 결론 — 라우팅 픽스를 v1.2 앞 별도 슬라이스로 끼우거나 본 슬라이스 시작점에 흡수 |

### Spike 결과 (2026-05-22)

**Spike A — PASS.** `ModelContextProtocol.Core` 1.3.0 어셈블리 리플렉션 검사:
- `ClientCapabilities.Extensions` 존재 — `IDictionary<string, object>`, `[Experimental(MCPEXP001)]` 부착. 사용 시 `MCPEXP001` 진단 억제 필요 (`<NoWarn>` 또는 `#pragma`). 값은 per-extension settings object(런타임 `JsonElement`) → `io.modelcontextprotocol/ui` 키 lookup 후 `mimeTypes`만 수동 deserialize. SPEC §6 분류상 **"Pass (dict 노출)"**.
- `RequestContext<T>.Server`(`McpServer`) → `McpServer.ClientCapabilities` → `.Extensions` 경로 확인. `tools/list`·`tools/call` 필터에서 호스트 capability에 **직접 도달 가능** → W1의 per-connection state holder는 불필요, `ctx.Server.ClientCapabilities`가 정식 경로 (캐싱은 선택).
- `AddListToolsFilter` / `AddCallToolFilter` 둘 다 존재 — `(IMcpRequestFilterBuilder, McpRequestFilter<TParams,TResult>)` 시그니처, `Program.cs`가 이미 쓰는 형태.

**Spike B — (b) 결론: 일반 라우팅 이슈.** `ls_get_chart`를 `include_chart` true/false로 12+회 호출. `include_chart=false`(analyze)는 요약 텍스트 정상 수신, `include_chart=true`(text + structuredContent 공존)는 **모델이 Plotly JSON만 받고 요약 텍스트 `content`가 소실**. 6/6 결정적 재현 — analyze 특이 현상 아님. 코드상 `McpJson.OkResult`는 text + structuredContent를 둘 다 정상 송출 → **호스트가 `structuredContent`를 모델에 먹이고 텍스트 `content`를 가린다.** 핵심: `structuredContent`는 SEP-1865 전용이 아니라 MCP 2025-06 generic 구조화 출력 필드 — MCP Apps 미지원이라도 generic structuredContent를 지원하는 호스트는 이 필드를 모델 컨텍스트에 전달한다. → 비-UI 호스트에 `structuredContent.chart`를 송출하면 모델 컨텍스트가 거대한 Plotly JSON으로 오염되고 요약이 파괴된다.

### 호스트 사실관계 — SEP-1865 채택 0 (2026-05-22)

`fieldcure-assiststudio` 소스 + LS004 세션(`docs/case-studies/sessions/`) 확인:
- **AssistStudio** — `McpServerConnection.ConnectAsync`는 `Roots`/`Elicitation` capability만 advertise, `Extensions` 미설정 → `io.modelcontextprotocol/ui` **안 보냄**. 차트 렌더는 capability 협상 없이 모든 tool 결과의 `structuredContent.chart`(`type=="plotly"`)를 무조건 검사하고 자체 번들 `plotly.min.js`를 WebView2에 주입해 직접 렌더 — `ui://` 리소스 / `resources/read` / `_meta.ui` 일절 미사용. `structuredContent`는 모델에 안 먹이고 텍스트 `content`만 모델로(올바름).
- **Claude Code / Codex** — capability 미advertise, 차트 렌더 안 함.
- → SPEC §3.2가 가정한 `io.modelcontextprotocol/ui` 게이팅 신호를 advertise하는 호스트가 (현재) **존재하지 않는다.** SPEC을 그대로 구현하면 유일한 차트 렌더 호스트(AssistStudio)가 게이트에서 탈락해 차트가 전면 소실되는 회귀가 된다.

### 결정 (2026-05-22, Jong Hyun)

**듀얼 신호로 게이팅하고, AssistStudio는 capability를 advertise한다 — iframe 렌더는 미구현.**
- mcp-lsopenapi v1.2는 capability 단일이 아니라 **capability + `clientInfo` allowlist 듀얼 신호**로 게이팅한다 (W1, `ChartRenderingMode`).
- AssistStudio v1.1은 `initialize`에서 `io.modelcontextprotocol/ui`를 advertise한다. 단, **SEP-1865 iframe 앱 렌더는 구현하지 않는다** — `structuredContent.chart`의 Plotly spec을 자체 렌더러로 직접 그린다. 차트는 데이터(spec)/표현(렌더)이 이미 분리된 형태이고, 호스트가 일관된 테마로 그리는 편이 서버마다 다른 HTML 위젯보다 UX가 낫다. capability advertise는 **의도된·문서화된 절충**이다 — 서버가 호스트명 allowlist 대신 표준 단일 신호로 게이팅하게 해주고, 훗날 AssistStudio가 진짜 iframe 렌더를 추가하면 같은 신호로 자연 승격된다. 비용보다 이득이 크다.
- 서버 입장: capability를 advertise하는 호스트는 `Sep1865`, allowlist에만 잡히는 호스트(capability 이전 AssistStudio 빌드 등)는 `StructuredContent`, 둘 다 miss면 `TextOnly`. `Sep1865`·`StructuredContent` 모두 `structuredContent.chart`를 송출하므로 AssistStudio는 어느 경로든 차트를 받는다.

**크로스-레포 출시 독립성**: 듀얼 신호 덕분에 v1.2는 AssistStudio v1.1을 기다릴 필요가 없다 — 단독 출시해도 capability 미advertise AssistStudio는 allowlist의 `StructuredContent` 경로로 차트를 유지하고, Claude Code 등은 `TextOnly`로 떨어진다.

## 7. 작업 항목

### W1. Capability 검사 인프라 — 듀얼 신호

호스트가 차트를 어떻게 소비하는지를 **두 신호의 OR**로 판정한다. 게이팅의 단일 기준은 SEP-1865 capability 하나가 아니라 `ChartRenderingMode` 3-값이다.

| 모드 | 판정 | 서버 동작 |
|---|---|---|
| `Sep1865` | `io.modelcontextprotocol/ui` capability advertise | structuredContent + `_meta.ui` + `ui://` 리소스 |
| `StructuredContent` | capability 없음 + `clientInfo.Name`이 allowlist hit | structuredContent만 (SEP-1865 메타·리소스 생략) |
| `TextOnly` | 둘 다 miss | structuredContent strip, `include_chart` 숨김 |

**듀얼 신호의 이유**: §6에서 확인했듯 현재 `io.modelcontextprotocol/ui`를 advertise하는 호스트는 0개다. capability 단일 게이팅이면 유일한 차트 호스트(AssistStudio)가 탈락한다. `clientInfo` allowlist(`{"AssistStudio"}`)가 `StructuredContent` 경로로 잡아 차트 공백을 막고, AssistStudio v1.1이 capability를 붙이면 같은 연결이 자동으로 `Sep1865`로 승격된다 — 서버 변경 불필요.

```csharp
/// <summary>How the connected host consumes chart payloads.</summary>
internal enum ChartRenderingMode { TextOnly, StructuredContent, Sep1865 }

internal static class ChartHostSupport
{
    private static readonly HashSet<string> KnownChartRenderers =
        new(StringComparer.Ordinal) { "AssistStudio" };

    public static ChartRenderingMode Resolve(
        ClientCapabilities? capabilities, Implementation? clientInfo)
    {
        if (McpAppsCapability.Read(capabilities) is { SupportsHtmlApp: true })
            return ChartRenderingMode.Sep1865;
        if (clientInfo?.Name is { Length: > 0 } name && KnownChartRenderers.Contains(name))
            return ChartRenderingMode.StructuredContent;
        return ChartRenderingMode.TextOnly;
    }
}

/// <summary>SEP-1865 UI extension descriptor, read from ClientCapabilities.Extensions
/// (string-keyed dict; the io.modelcontextprotocol/ui value's mimeTypes are
/// deserialized manually — Spike A).</summary>
internal sealed record McpAppsCapability(IReadOnlyList<string> MimeTypes)
{
    public bool SupportsHtmlApp =>
        MimeTypes.Contains("text/html;profile=mcp-app", StringComparer.Ordinal);

    public static McpAppsCapability? Read(ClientCapabilities? capabilities) { /* … */ }
}
```

- **도달 경로**: Spike A 확인 — `RequestContext<T>.Server`(`McpServer`) → `.ClientCapabilities` / `.ClientInfo`. `tools/list`·`tools/call` 필터와 `resources/list` 핸들러가 매 요청 `Resolve(...)`를 호출한다. capability는 세션 stable이라 캐싱이 가능하나 dict+hashset lookup이라 무비용 — per-connection state holder는 **불필요**.
- **`MCPEXP001`**: `ClientCapabilities.Extensions`는 `[Experimental(MCPEXP001)]` — `McpAppsCapability.Read`의 해당 접근에 `#pragma warning disable MCPEXP001`을 국소 적용한다.

### W2. `chart_available` 마커 제거 — 5개 도구

차트-emitting 도구 응답 빌더 전체에서 `chart_available` 텍스트 마커를 제거한다. 차트는 `structuredContent.chart`에만 존재하고, 모델은 차트 존재를 인지하지 않는다 — 호스트가 알아서 렌더(또는 무시).

- **대상**: §4.1의 5개 도구 (`ls_get_chart`, `ls_add_indicator`, `ls_reframe_chart`, `ls_get_etf_holdings`, `ls_get_program_trading`)
- **갱신 항목**:
  - 응답 빌더 — `GetChartTool.cs`(7곳), `GetEtfHoldingsTool.cs`(1곳), `GetProgramTradingTool.cs`(5곳)
  - `chart_available`을 assert하는 테스트 fixture — `GetChartToolPlotlyTests`, `GetEtfHoldingsToolPlotlyTests`, `GetProgramTradingToolTests`
  - README의 응답 예제 JSON (`README.en.md` 등 — `chart_available` 노출 예시)
  - 도구 description 문구 — "chart will be embedded" / "ships … as structuredContent" 류 표현 정리

### W3. Capability 조건부 차트 노출 — W3a / W3b / W3c

Spike B 이후 W3는 세 갈래로 나뉜다. SPEC 원안의 무게중심은 `tools/list` 표면(W3b)이었으나, Spike B가 드러낸 **본 픽스는 `tools/call` 응답 게이팅(W3c)** 이다 — capability 미보유 호스트가 `structuredContent`를 모델 컨텍스트로 흘려 요약 텍스트를 파괴하기 때문. 구현 순서는 W1 → **W3c** → W2 → W3a/W3b.

#### W3c — `tools/call` 응답 게이팅 (본 픽스 · W1 다음 우선)

`Program.cs`의 `AddCallToolFilter`를 확장한다. `next()` 실행 후 `ChartHostSupport.Resolve(ctx.Server.ClientCapabilities, ctx.Server.ClientInfo)`로 모드를 판정해, **`TextOnly`일 때 `CallToolResult.StructuredContent`에서 `chart` / `panel` 키를 strip**한다(키 제거 후 비면 `StructuredContent = null`). 응답 후처리이므로 차트 송출의 두 입구 — `include_chart=true`와 `output_mode=display`(`GetChartTool.cs`의 `includeStructuredChart = include_chart || outputMode == "display"`) — 를 한 곳에서 모두 차단한다.

SPEC 원안 corner-case 표의 옵션 2(silently strip)를 **본 픽스로 승격**한다. Spike B가 보였듯 strip이 reject보다 안전하다 — capability 미보유 호스트에서 모델이 텍스트 요약을 그대로 유지하고, 차트만 사라진다. 도구 핸들러가 차트를 빌드하는 낭비는 남지만(W3b가 `include_chart`를 스키마에서 가려 호출 자체가 드물어짐) 정확성에는 무해.

#### W3b — `tools/list` 스키마 표면

`AddListToolsFilter`를 확장한다. 모드별 `tools/list` 표면:
- **`Sep1865`**: 차트 도구 inputSchema에 `include_chart` 유지 + `_meta.ui.resourceUri = "ui://lsopenapi/plotly"` 부착(nested — §4.4) + `resources/list`에 `ui://lsopenapi/plotly` advertise.
- **`StructuredContent`**: `include_chart` 유지 + `_meta.ui` 미부착 + 리소스 미advertise (호스트가 `structuredContent.chart`를 자체 렌더러로 직접 그리므로 SEP-1865 메타·리소스가 불필요).
- **`TextOnly`**: inputSchema에서 `include_chart` 제거 + `_meta.ui` 미부착 + 리소스 미advertise.

기존 `Program.cs`의 `AddListToolsFilter`(ToolProfile / `SchemaNormalizer` / 차트 표면 패치)를 확장 — `UiResources.ApplyChartSurface(tool, mode)`. `SchemaNormalizer`가 이미 스키마를 재작성하므로 `properties`에서 `include_chart` 제거는 실현 가능. `resources/list` 게이팅은 `UiResources.ListAsync`에서 `Resolve(...) == Sep1865`로 분기.

**ToolSurfaceFreezeTests 상호작용**: `include_chart`는 C# 시그니처에 그대로 남고 필터는 송출 `tools/list` JSON만 가공 → 동결 테스트 영향 없음. 신규 검증은 §9 integration 테스트로.

#### W3a — `PlotlyEmittingToolNames` 정합화

`UiResources.PlotlyEmittingToolNames`에 `ls_add_indicator` / `ls_reframe_chart`를 추가해 §4.3 갭을 청산한다 — `structuredContent.chart`를 실제로 방출하는 도구 전체(5개)가 `_meta.ui`를 받도록. LS004 세션(`docs/case-studies/sessions/`)이 두 도구가 실제로 차트를 방출하고 AssistStudio가 렌더함을 확인했으므로, "인라인 렌더 비대상" 가설은 기각 — 추가가 확정이다 (§12 open question 해소).

### W4. `_meta.ui` 형식 검증

§4.4에서 코드가 이미 nested(`_meta.ui.resourceUri`) 형식임을 확인했다. → W4는 **검증 + 회귀 테스트 1개**로 축소. flat `_meta["ui/resourceUri"]` 형식이 어디에도 남지 않았는지 grep 확인 후, nested 형식을 pin하는 테스트만 추가. 별도 슬라이스 분리 불필요 — W3에 흡수.

## 8. 구현 원칙

1. **모델은 호스트 capability를 모른다** — 응답 텍스트에 capability 분기 흔적 없음. 마커, "차트 있음" 안내, 어떤 신호도 노출 안 함.
2. **Capability는 세션 단위 stable** — `initialize` 시 1회 캐싱, 세션 종료까지 유효. 매 호출 재조회 안 함.
3. **차트 spec 위치는 spec 정의 그대로** — `structuredContent.chart`. `content` 배열로 옮기지 않음. SEP-1865 명시: *"Structured data optimized for UI rendering (not added to model context)"*.
4. **미advertise는 정상 동작** — capability 부재는 오류가 아니다. 텍스트 전용 표면이 1급 시민.

## 9. 테스트 계획

### 신규 테스트

- **(Unit)** `GetUiCapability` — typed/dict 양쪽 입력, `mimeTypes` 매칭, 미advertise 케이스, 잘못된 형식 fallback
- **(Integration, 지원 호스트)** `tools/list` 결과에 `include_chart` 파라미터 + `_meta.ui` 포함 확인
- **(Integration, 미지원 호스트)** `tools/list` 결과에 `include_chart` 파라미터 + `_meta.ui` 없음, `ui://lsopenapi/plotly` 리소스 미advertise 확인
- **(Integration)** `chart_available` 마커가 응답 텍스트에 어떤 케이스에도 등장 안 함 확인
- **(Integration, W3 옵션 1 채택 시)** 미지원 호스트가 `include_chart=true`로 호출 시 reject 확인
- **(Regression)** `_meta.ui`가 nested 형식임을 pin (W4)
- **(Regression)** `PlotlyEmittingToolNames`가 `structuredContent.chart` 방출 도구 전체와 일치 (W3a)

### 기존 테스트 영향

- `chart_available`을 assert하던 테스트 → 마커 부재 assert로 전환
- `include_chart=true` 가정 테스트 → capability mock 추가 필요
- `ToolSurfaceFreezeTests` → 영향 없음 (§7 W3 참조)
- README / 예제 코드 갱신

### 수동 검증 (E2E)

| 호스트 | 검증 항목 |
|---|---|
| AssistStudio — capability 미advertise (`StructuredContent`) | `tools/list`에 `include_chart` 노출(`_meta.ui` 없음), `tools/call`에 `structuredContent.chart` 수신·인라인 렌더, 텍스트에 마커 없음 |
| AssistStudio v1.1+ — capability advertise (`Sep1865`) | 위 + `_meta.ui` 부착 + `resources/list`에 `ui://lsopenapi/plotly` advertise (AssistStudio는 spec을 직접 렌더하므로 이 메타는 미사용 — 향후 iframe 렌더용) |
| Claude Code / Codex CLI (`TextOnly`) | `tools/list`에 `include_chart` 안 보임, `tools/call` 응답에 `structuredContent` 없음, 모델이 "차트 있음" 안내 안 함 |

## 10. 호환성 / 버전

| 채널 | 정책 |
|---|---|
| v1.1.x line | 차트 정책 변경 없음. 마커 + 무조건 spec 송출 유지 |
| **v1.2.0** | 본 슬라이스 일괄 적용. Core·Mcp NuGet + `server.json` 모두 1.2.0 lockstep |

**Breaking 표면**: `include_chart` 파라미터가 capability 미지원 호스트에선 inputSchema에서 사라진다. 다만 호출 측 영향은 미미 — 모델은 inputSchema 기반으로 호출하므로 자연 적응한다. 시스템 프롬프트에 `include_chart=true`를 명시적으로 박아둔 외부 통합이 있는 경우만 영향. 실질적으로 *schema-breaking이나 동작상 투명* → v1.2.0 minor로 충분.

**마이그레이션 가이드** (README 한 섹션):
> "v1.2.0부터 차트 파라미터는 호스트의 MCP Apps capability에 따라 자동 노출/숨김. 호스트가 capability를 advertise하지 않으면 `include_chart`는 사용 불가."

## 11. 백로그 점검 — v1.2 범위 확정

2026-05-22 백로그(메모리) 점검 결과, v1.2 = **MCP Apps capability negotiation 단일 슬라이스**. 나머지는 이후로 미룬다:

| 백로그 항목 | 출처 메모리 | 처리 |
|---|---|---|
| MCP Apps capability negotiation | `next_mcp_apps_capability.md` | **= v1.2 (본 SPEC)** |
| Analysis phase 2 — VWAP-deviation / POV, market-regime analyzer | `next_program_trading_wrapper.md` | v1.3+ 후보. t8410/t8412 price·volume 차트 페어링 필요 |
| t1640 / t1631 snapshot scope | `next_program_trading_wrapper.md` | low-value 판정 — 보류 |
| `BaselineComparison` chart_view | `next_program_trading_wrapper.md` | v1.5 (N-day intraday 히스토리 필요) |
| accounts / trading / realtime TR surface | `todo_tr_reference_files.md` | v2.0 |
| AssistStudio Plotly 렌더 | `next_assiststudio_plotly.md` | **cross-repo** (fieldcure-assiststudio). 서버 측 완료 — mcp-lsopenapi 작업 아님 |

단일 슬라이스로 묶는 이유: capability 협상은 W1~W4가 한 덩어리로 맞물린다(검사 인프라 → 마커 제거 → 조건부 노출). 부분 적용은 표면 불일치를 남긴다 — `chart_available`만 제거하면 모델은 여전히 `include_chart`를 본다(반쪽 픽스). 셋을 함께 출시해야 UX가 실제로 고쳐진다.

## 12. Open Questions / Follow-up

- [ ] `AddListToolsFilter` / `AddCallToolFilter`의 SDK 시그니처 — Spike A에서 확인
- [ ] §4.3 — `ls_add_indicator` / `ls_reframe_chart`를 `PlotlyEmittingToolNames`에 추가(W3a)할지, 인라인 렌더 비대상이 의도였는지 확인
- [ ] `BuildToolUiMeta()` vs `PatchToolMetaIfChartEmitting` 중복 — capability-aware 필터로 일원화 시 통합
- [ ] Spike B가 (b) 결론이면 — 라우팅 픽스 슬라이스를 v1.2 앞에 끼울지, 시작점에 흡수할지
- [ ] `HostContext.theme` 활용 (Plotly 라이트/다크 자동 분기) — v1.3 후보로 별도 ticket

## 13. Release 체크리스트 (구현 완료 후)

- [x] Spike A·B 결과 문서화 (§6 Spike 결과 — 2026-05-22)
- [ ] W1 → W3c → W2 → W3a/W3b → W4 구현 + 테스트
- [ ] README의 chart 관련 섹션 갱신 + §10 마이그레이션 가이드 추가
- [ ] 도구 description 검토 — 마커/임베드 관련 표현 제거
- [ ] E2E 수동 검증 (지원 / 미지원 호스트 양쪽 — §9)
- [ ] `RELEASENOTES.Mcp.md` / `RELEASENOTES.Core.md` v1.2.0 엔트리 — schema 표면 변경 명시
- [ ] csproj `<Version>` + `server.json` version 1.2.0 lockstep (csproj `VerifyServerJsonVersion` 타깃이 강제)
- [ ] **크로스-레포 후속** — AssistStudio v1.1은 `io.modelcontextprotocol/ui`를 advertise(`StructuredContent` → `Sep1865` 승격)하되 iframe 렌더는 미구현 — `structuredContent.chart` spec을 직접 렌더. 듀얼 신호(W1)라 v1.2 출시 순서 제약 없음 — v1.2 단독 출시해도 capability 미advertise AssistStudio는 `StructuredContent` 경로로 차트 유지.
- [ ] 메모리 `next_mcp_apps_capability.md` — 본 SPEC 출시 후 Closed 처리

## 참조

- [SEP-1865 — MCP Apps: Interactive User Interfaces for MCP](https://modelcontextprotocol.io/seps/1865-mcp-apps-interactive-user-interfaces-for-mcp)
- [ext-apps 정식 draft spec](https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/draft/apps.mdx) — capability 협상 절: "Client⇔Server Capability Negotiation"
- 선행 SPEC: [SPEC-v0.10.md](./SPEC-v0.10.md) (마지막 0.x — 도구 표면 동결의 기준선)
- v1.1.0 선행 커밋: `982d4d0`(analyze `include_chart` 제거), `9121732`/`9e44713`/`933422f`(program≠institutional 용어 정정)
- 백로그 메모리: `next_mcp_apps_capability.md` (본 SPEC이 대체) · `next_program_trading_wrapper.md` (Analysis phase 2)
