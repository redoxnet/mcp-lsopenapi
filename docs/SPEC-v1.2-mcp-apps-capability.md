# SPEC: v1.2.0 — MCP Apps Capability Negotiation

- **상태**: Draft — spike-gated. **Spike A·B 통과 전 본 구현 착수 금지** (§6).
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

## 7. 작업 항목

### W1. Capability 검사 인프라

`initialize` 시점에 호스트의 `io.modelcontextprotocol/ui` extension capability를 읽어 세션 컨텍스트에 캐싱한다.

```csharp
/// <summary>
/// Reads the MCP Apps UI capability advertised by the host during initialize.
/// </summary>
/// <param name="clientCapabilities">Capabilities object from the initialize request.</param>
/// <returns>The UI capability descriptor, or <c>null</c> when not advertised.</returns>
internal static McpAppsCapability? GetUiCapability(ClientCapabilities clientCapabilities)
{
    // Concrete implementation depends on Spike A:
    //   - typed surface : cast/read directly
    //   - dict surface  : lookup by extension id key
    //   - raw JSON      : deserialize the relevant subtree manually
}

/// <summary>Descriptor for the MCP Apps UI extension capability.</summary>
internal sealed record McpAppsCapability(IReadOnlyList<string> MimeTypes)
{
    /// <summary>Whether the host accepts the HTML MCP app profile.</summary>
    public bool SupportsHtmlApp =>
        MimeTypes.Contains("text/html;profile=mcp-app", StringComparer.Ordinal);
}
```

- **캐싱 위치**: per-connection state holder (`LsOpenApiSession` 또는 동등). `tools/list` 필터·`tools/call` 필터·응답 빌더가 동일 인스턴스 참조.
- **보장**: capability는 세션 단위 stable — `initialize` 시 1회 캐싱, 매 호출 재조회 안 함.

### W2. `chart_available` 마커 제거 — 5개 도구

차트-emitting 도구 응답 빌더 전체에서 `chart_available` 텍스트 마커를 제거한다. 차트는 `structuredContent.chart`에만 존재하고, 모델은 차트 존재를 인지하지 않는다 — 호스트가 알아서 렌더(또는 무시).

- **대상**: §4.1의 5개 도구 (`ls_get_chart`, `ls_add_indicator`, `ls_reframe_chart`, `ls_get_etf_holdings`, `ls_get_program_trading`)
- **갱신 항목**:
  - 응답 빌더 — `GetChartTool.cs`(7곳), `GetEtfHoldingsTool.cs`(1곳), `GetProgramTradingTool.cs`(5곳)
  - `chart_available`을 assert하는 테스트 fixture — `GetChartToolPlotlyTests`, `GetEtfHoldingsToolPlotlyTests`, `GetProgramTradingToolTests`
  - README의 응답 예제 JSON (`README.en.md` 등 — `chart_available` 노출 예시)
  - 도구 description 문구 — "chart will be embedded" / "ships … as structuredContent" 류 표현 정리

### W3. Capability 조건부 차트 표면 노출

`tools/list` 응답을 capability-aware 필터로 가공한다.

**UI 지원 호스트** (capability 보유):
- 차트 도구의 `include_chart` 파라미터를 inputSchema에 포함
- `_meta.ui.resourceUri = "ui://lsopenapi/plotly"` 부착 (nested 형식 — §4.4에서 이미 nested 확인)
- `ui://lsopenapi/plotly` 리소스를 `resources/list`에 advertise

**UI 미지원 호스트**:
- `include_chart` 파라미터를 inputSchema에서 제거
- `_meta.ui` 미부착
- `ui://lsopenapi/plotly` 리소스 advertise 안 함

**구현 접근**: SDK의 `AddListToolsFilter`(또는 동등 hook) 사용. 명령형 `RegisterTool` 분기보다 깔끔 — 등록은 한 번, 노출은 동적. 기존 `Program.cs`의 `AddListToolsFilter`(ToolProfile / SchemaNormalizer / `PatchToolMetaIfChartEmitting` 실행)를 확장. `SchemaNormalizer`가 이미 스키마를 재작성하므로 속성 제거는 실현 가능.

**ToolSurfaceFreezeTests 상호작용**: 동결 테스트는 C# 메서드 시그니처를 리플렉션으로 읽는다. `include_chart`는 **C# 시그니처에 그대로 남고**, capability 필터는 송출되는 `tools/list` JSON만 가공한다 → 동결 테스트는 변함없이 `include_chart`를 보고 통과한다. 동결 테스트 수정 불필요. 신규 검증은 §9의 integration 테스트로.

**W3a — `PlotlyEmittingToolNames` 정합화**: §4.3 갭 청산. `structuredContent.chart`를 실제로 방출하는 도구 전체(5개)가 `_meta.ui`를 받도록 `PlotlyEmittingToolNames`에 `ls_add_indicator` / `ls_reframe_chart`를 추가. (혹은 두 도구를 인라인 렌더 대상에서 제외하는 것이 의도였는지 확인 — §11 open question.)

**Corner case — 미지원 호스트가 우회로 `include_chart=true` 호출**: 필터는 `tools/list`만 가리고 도구 자체는 등록 상태로 남는다. 핸들러 처리 방안:

| 옵션 | 설명 | 평가 |
|---|---|---|
| **1 (선호)** | `AddCallToolFilter`로 capability 없으면 `include_chart=true` 호출을 reject | 명시적·안전. SDK가 지원하면 채택 |
| 2 | 핸들러 내부에서 capability 미보유 시 `include_chart`를 silently 무시 | 폴백으로만 — 동작은 일관되나 모델이 인지 못 함 |

`AddCallToolFilter` 가용성은 Spike A에서 확인.

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
| Claude.ai / Claude Code (지원) | 차트 정상 렌더, 텍스트에 마커 없음, narrative 자연스러움 |
| Codex CLI (미지원) | `tools/list`에 `include_chart` 파라미터 안 보임, 모델이 "차트 있음" 안내 안 함 |

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

- [ ] Spike A·B 결과 문서화 (PR 또는 commit message, 본 SPEC §6 갱신)
- [ ] W1~W4 (+ W3a) 구현 + 테스트
- [ ] README의 chart 관련 섹션 갱신 + §10 마이그레이션 가이드 추가
- [ ] 도구 description 검토 — 마커/임베드 관련 표현 제거
- [ ] E2E 수동 검증 (지원 / 미지원 호스트 양쪽 — §9)
- [ ] `RELEASENOTES.Mcp.md` / `RELEASENOTES.Core.md` v1.2.0 엔트리 — schema 표면 변경 명시
- [ ] csproj `<Version>` + `server.json` version 1.2.0 lockstep (csproj `VerifyServerJsonVersion` 타깃이 강제)
- [ ] 메모리 `next_mcp_apps_capability.md` — 본 SPEC 출시 후 Closed 처리

## 참조

- [SEP-1865 — MCP Apps: Interactive User Interfaces for MCP](https://modelcontextprotocol.io/seps/1865-mcp-apps-interactive-user-interfaces-for-mcp)
- [ext-apps 정식 draft spec](https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/draft/apps.mdx) — capability 협상 절: "Client⇔Server Capability Negotiation"
- 선행 SPEC: [SPEC-v0.10.md](./SPEC-v0.10.md) (마지막 0.x — 도구 표면 동결의 기준선)
- v1.1.0 선행 커밋: `982d4d0`(analyze `include_chart` 제거), `9121732`/`9e44713`/`933422f`(program≠institutional 용어 정정)
- 백로그 메모리: `next_mcp_apps_capability.md` (본 SPEC이 대체) · `next_program_trading_wrapper.md` (Analysis phase 2)
