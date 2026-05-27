# SPEC: v1.5.0 — Fidelity-first chart narration

- **상태**: Final (2026-05-27) — `spike/sep1865-verify` empirical evidence와 main에 cherry-pick된 PlotlyTemplate fix 3 commits (`38d4dc2` / `46e20f0` / `01d74e3`)로 디자인 확정.
- **대상 버전**: v1.5.0
- **선행**: [SPEC-v1.4.md](./SPEC-v1.4.md), [SPEC-v1.2-mcp-apps-capability.md](./SPEC-v1.2-mcp-apps-capability.md), [MCP-APPS-INTEROP.md](./MCP-APPS-INTEROP.md), v1.4 commit `6d7d3a0` (ServerInstructions chart-routing hint, v1.5에서 교체).
- **범위**: 단일 슬라이스 — chart-emitting 도구의 *모델 narration 정직성* 강제. 네 sub-change:
  1. `_meta.render_status` 응답 필드 (`delivered` | `stripped_text_only`) — 모델에 명시적 신호.
  2. ServerInstructions chart-routing 단락 *교체* (commit `6d7d3a0` 대체) — TextOnly 호스트에서 "그렸/rendered/표시" narration 금지.
  3. **호스트 분류 무관 self-synthesis 금지** — Cowork 케이스(iframe 정상 렌더에서도 height 등 customization 요청 시 show_widget / create_artifact / output_mode=export 우회 발화)로 확장. 차트 변경은 `ls_reframe_chart` / `ls_add_indicator`만, height 같은 호스트 패널 제약은 정직 안내.
  4. `output_mode=export` 응답에 `_meta.do_not_render` 가드 (self-synthesis 충동을 한 번 더 받는 brake).
- **이전 draft에서 묶었거나 본문이었던 항목들**: candle cache(§Appendix A), saved screener macros(§Appendix B), `_meta.render_hints.wrap_template`(§Appendix C — 본문에서 *부록으로 강등*).
- **메시지**: v1.5는 *기능 추가*가 아니라 *모델 거짓말 차단*. 사용자가 받는 것: "차트 못 봤는데 그렸다고 한다" + "차트 정상 렌더돼도 customization 요청에 자체 합성 우회" 두 종료.

---

## 1. 컨텍스트

### 1.1 v1.4 → v1.5

v1.4는 두 슬라이스 ship(date envelope + Q-Click). v1.5는 *기능*이 아니라 *fidelity*에 좁게 집중 — v1.4-dev Cowork E2E 회귀(commit `0ce0507`)와 v1.5 디자인 세션 중 2026-05-26 Codex 실측에서 동일한 패턴이 *호스트를 바꿔도 재현*되며 *prompt 강도가 아니라 디자인 변경이 필요*함을 확인.

### 1.2 v1.5 직전: empirical evidence가 디자인을 바꾼 두 사건

**(a) 2026-05-26 Codex 실측 — TextOnly 호스트에서의 거짓 narration + self-synthesis**

`include_chart` 파라미터가 schema에서 스트립된 상태(=우리 v1.2 TextOnly 분류 정확)에서 모델이 `output_mode=display`로 호출만 한 사실로 "그려뒀습니다" narration. 후속 "실제로 안 그려졌네요" 발화에 4분 9초 우회 작업 — `output_mode=export` 재호출 → OHLCV → `render_samsung_chart.py` (+248 lines, 자체 MA 합성) → PNG. 서버 indicator와 *불일치하는* 자체 합성 차트가 chat에 등장.

**(b) 2026-05-27 `spike/sep1865-verify` — Claude Desktop + Cowork도 실제로 iframe을 렌더한다**

이전 디자인의 Sub-change 1 (whitelist) 근거였던 "Claude Desktop frame-ancestors CSP block / Cowork advertise 후 미렌더" 진단이 *우리 측 버그*였음 입증. PlotlyTemplate.html에 잠재해 있던 세 핸드셰이크 버그를 ext-apps basic-host 환경에서 발견하고 fix:

- Q1: `postMessage(JSON.stringify(msg), "*")` — basic-host 트랜스포트가 string 페이로드를 silently drop. 객체 리터럴이어야 함. (commit `38d4dc2`)
- Q2: `ui/initialize.params`에 `appInfo` 누락 — schema 검증 실패로 silently drop. (commit `38d4dc2`)
- Q3: `ui/notifications/size-changed` 누락으로 iframe height가 0/default로 잡힘 → "렌더 안 됨"처럼 보임. (commit `46e20f0`)
- 추가로 chat panel용 default frame height bump. (commit `01d74e3`)

세 fix가 main에 cherry-pick된 후 *동일 실측에서* Claude Desktop Chat / Cowork / basic-host / VS Code Chat 모두 ✅ iframe 렌더 확인. **원 진단이 *우리 버그*였고, 호스트 capability 광고는 신뢰 가능했음**.

증거 보존: `docs/claude_desktop_chat_chart_lgelectronics.png`, `docs/claude_desktop_cowork_chart_lgelectronics.png` (둘 다 LG전자 일봉, 같은 PlotlyTemplate fix 후).

### 1.3 v1.5가 푸는 *두 empirical 문제*

| # | 문제 | 출처 |
|---|---|---|
| P1 | 호스트가 TextOnly로 분류된 상태에서 chart spec이 *전송조차 안 됐는데도* 모델이 `output_mode=display`를 호출한 사실만 보고 "그렸다" 거짓 narration | 2026-05-26 Codex 실측 |
| P2 | 호스트가 iframe 차트를 *정상 렌더*하는 상태(Claude Desktop / Cowork / basic-host)에서도 height customization 요청 등 차트 변경 발화에 모델이 `show_widget` / `create_artifact` / `output_mode=export`로 우회해 자체 합성. server-computed indicators(우리가 보낸 MA5/20/60 값) 무시 → 시각적으로 그럴듯하지만 *우리가 보장하는 fidelity가 아닌* 차트가 사용자에게 표시됨 | 2026-05-27 Cowork 세션 관찰 (cherry-pick 직전 회귀) + 2026-05-26 Codex 실측 |

P1은 *모델 narration의 정직성*. P2는 *모델 행동의 자기-합성 충동 — 호스트 분류 무관*. 둘 다 *prompt 강도*로 해결 안 되며, *response shape과 instructions의 명시*로 차단해야 함.

원래 디자인 draft가 가정한 P3 ("Claude Desktop / Cowork가 capability를 *광고*하지만 iframe을 *렌더하지 않음*") 는 §1.2 (b)의 PlotlyTemplate 3-fix로 *해소* — empirical 증거가 들어오면 디자인이 바뀐다.

### 1.4 Empirical host matrix (요약, 자세한 건 [MCP-APPS-INTEROP.md §2](./MCP-APPS-INTEROP.md))

| 호스트 | capability 광고 | 실제 inline 렌더 | 우리 분류 | render_status |
|---|---|---|---|---|
| AssistStudio (WinUI 3) | ✅ SEP-1865 | ✅ (자체 Plotly) | StructuredContent | `delivered` |
| Claude Desktop Chat | ✅ SEP-1865 | ✅ (PlotlyTemplate 3-fix 후 — `docs/claude_desktop_chat_chart_lgelectronics.png`) | Sep1865 | `delivered` |
| Cowork (iframe + chat panel) | ✅ SEP-1865 | ✅ (PlotlyTemplate 3-fix 후 — `docs/claude_desktop_cowork_chart_lgelectronics.png`) | Sep1865 | `delivered` |
| VS Code Chat (GitHub Copilot) | ✅ SEP-1865 | ✅ (PlotlyTemplate 3-fix 후 — `docs/vscode_copilot_chat_chart_nvidia.png`, NVDA 미장 차트로 v1.3 overseas 경로도 동시 확인) | Sep1865 | `delivered` |
| ext-apps basic-host (reference) | ❌ 미광고 (구현 선택) | ✅ | TextOnly (capability 부재) | `stripped_text_only` |
| Codex (Desktop / TUI) | ❌ 미광고 | ❌ (TUI 구조적) | TextOnly | `stripped_text_only` |
| Claude Code CLI | ❌ (TUI) | ❌ | TextOnly | `stripped_text_only` |
| Cursor 2.6+, ChatGPT, Goose, Postman, MCPJam | ✅ (변동) | 광고대로 작동한다는 보고 (우리 실측 없음) | Sep1865 (capability 신뢰) | `delivered` |

총평: PlotlyTemplate fix 후 **capability 광고 = 실제 렌더가 거의 일치**. v1.2의 capability-based 분류가 신뢰 가능하다는 것이 empirical 결론이라, **`KnownIframeRenderingHosts` whitelist 도입은 보류**. basic-host 같은 "capability 미광고 + 실제 렌더" 케이스는 향후 발견되는 호스트별로 case-by-case로 보고 결정.

### 1.5 비범위

- **`KnownIframeRenderingHosts` whitelist 도입** → 2026-05-27 empirical evidence(§1.2 b)로 *필요 없음*이 입증. capability advertisement가 신뢰 가능. ext-apps basic-host 같은 capability 미광고+렌더 사례는 v1.5.1+에서 case-by-case로 결정.
- **`_meta.render_hints.wrap_template`** → Appendix C로 강등. 또 다른 합성 표면을 열어줄 위험이 v1.5의 self-synthesis 금지 메시지와 충돌.
- **SQLite 일봉 캐시** → Appendix A 유지.
- **Saved screener macros** → Appendix B 유지.
- **PNG 폴백(server-side render)** → AssistStudio가 이미 client-side `Plotly.toImage` 사용 — server-side 불필요. v1.6+ 보류.
- **`output_mode` / `summary_only` 인자명 일관화** → v1.5.1 또는 v1.6.

---

## 2. 디자인

### 2.1 Sub-change 1 — `_meta.render_status` 응답 필드

chart-emitting 도구 응답의 `CallToolResult.Meta`에 모드 신호:

```jsonc
"_meta": {
  "render_status": "delivered" | "stripped_text_only"
}
```

- `delivered`: `Sep1865` 또는 `StructuredContent` 모드 — chart 페이로드가 응답에 *포함*되어 호스트에 전달됨. 호스트가 inline 차트를 사용자에게 표시한다.
- `stripped_text_only`: `TextOnly` 모드 — chart 페이로드가 `UiResources.StripChartStructuredContent`로 *제거*됨. 호스트와 모델 모두에게 명시.

`Program.cs`의 `AddCallToolFilter`에서 strip 단계 *직후* 부착. `PlotlyEmittingToolNames`에 속한 도구의 응답만 대상. 그 외 도구 응답은 영향 없음.

모델은 이 필드를 보고 narration 결정 (Sub-change 2에서 contract 명시).

### 2.2 Sub-change 2 — ServerInstructions chart-routing 단락 *교체* (narration honesty)

현 ServerInstructions의 chart-routing 단락(commit `6d7d3a0` — "wrap-and-route to visualize MCP" 가이드)을 *완전히 교체*. 새 단락은 두 contract을 명시:

> **Chart rendering honesty.** A chart-emitting tool (ls_get_chart / ls_reframe_chart / ls_add_indicator / ls_get_overseas_chart / ls_get_etf_holdings / ls_get_program_trading) returns `_meta.render_status` indicating how the host received the chart:
>
> - `delivered`: the server emitted `structuredContent.chart.spec` and the host shows it inline (AssistStudio class with own Plotly; or a SEP-1865 host with iframe app — Claude Desktop, Cowork, Cursor 2.6+, VS Code, ChatGPT, ext-apps basic-host class). Narrate normally — "삼성전자 일봉 차트입니다, 최근 흐름은…".
> - `stripped_text_only`: the chart spec was NOT delivered. The host has no SEP-1865 / structured-chart path. You received only the analytical summary (closes, MAs, key turns). Do NOT claim you "drew", "rendered", "표시", "그렸", or otherwise visualized the chart. The user sees no chart. State the limitation explicitly: e.g. "이 호스트에서는 inline 차트가 표시되지 않습니다. 데이터 요약: ..." or "I can't render inline here — here's the analytical summary: ...".

### 2.3 Sub-change 3 — *호스트 분류 무관* self-synthesis 금지

§2.2 단락에 *강한 어조로* 이어 붙임. **P2 직접 응답: render_status가 `delivered`인 경우에도 self-synthesis 금지.**

> **Do not self-synthesize charts — regardless of `render_status`.** Specifically, do NOT:
>
> - Re-call the chart tool with `output_mode=export` to fetch raw OHLCV, then render a chart yourself in Python (matplotlib / plotly / mplfinance), JavaScript (Plotly / Chart.js / D3), HTML/SVG, or any other path. A self-synthesized chart looks plausible but its indicators (MA / RSI / Bollinger / etc.) will NOT match the server's `summary.moving_averages` values — different adjustment mode (raw vs ADJ), different warm-up window, different formula (SMA vs EMA vs weighted), different bar count. The user sees a chart that LOOKS authoritative but disagrees with the analytical summary on the same screen.
> - Forward the chart spec or the raw OHLCV to a generic visualization MCP (`mcp__visualize__show_widget`, `create_artifact`, `mcp__chart__render`, or any other rendering helper). The server's chart is already the authoritative render path for hosts that can show it; on hosts that can't, the analytical summary is the honest answer.
> - Recompute any indicator yourself from raw bars. The server's `summary.moving_averages.MA{5,20,60,120,200}`, `summary.ma60_slope`, `summary.drawdown_from_peak_pct`, and `context.*` are the authoritative values; your re-computation will diverge.
> - Write a PNG / SVG / HTML chart file "to show the user something". A self-rendered chart with mismatched indicators is *worse* than no chart, because users trust what they see.
>
> **Chart customization is tool-mediated.** When the user asks to change something about an already-rendered chart:
>
> - Indicator add / remove / change → call `ls_add_indicator` against the `dataset_id`.
> - Time range / period / count adjust → call `ls_reframe_chart` against the `dataset_id`.
> - **Panel height, sizing, colors, font, layout tweaks** → these are *host panel constraints*, not chart parameters. State the limitation honestly — e.g. "차트 패널 높이는 이 호스트(Cowork chat panel)가 결정합니다, 서버 쪽에서 키울 수 없어요. 더 큰 차트가 필요하시면 Claude Desktop 일반 채팅이나 AssistStudio처럼 패널이 더 큰 호스트에서 같은 질문을 주세요." Do NOT route around the constraint by synthesizing your own chart or calling a visualization MCP.
>
> `output_mode=export` is legitimate ONLY for handing OHLCV to a data analysis pipeline (statistical tests, custom backtesting, passing to pandas). It is NOT a workaround to render charts.

> Instead, when `render_status` is `stripped_text_only` *or* when a `delivered` chart can't be customized the way the user wants:
>
> 1. Report the analytical summary verbatim from the tool response (closes, MA values, key turning points, drawdown).
> 2. Name the limitation: "this host does not render inline charts" / "this panel can't be resized from the server".
> 3. Offer the `dataset_id` so the user can ask for *follow-up indicators* (`ls_add_indicator`) or *reframing* (`ls_reframe_chart`) without re-fetching, or switch to a more capable host for the visualization itself.

### 2.4 Sub-change 4 — `output_mode=export` 응답에 anti-synthesis 가드 메타

export 모드 응답의 `_meta`에 명시 신호(모델이 합성 충동을 느낄 때 한 번 더 보는 brake):

```jsonc
"_meta": {
  "data_purpose": "analysis_only",
  "do_not_render": "Server-computed indicators (MA / RSI / Bollinger / drawdown) are not included in this payload. Rendering a chart from this OHLCV will produce different indicator values than the server computes (different adjustment mode, warm-up window, formula choice). Use this data for analysis (pandas, numpy, statistical work, custom backtests), not for chart synthesis. For inline charts, the original tool call already delivered (or stripped) the chart; do not synthesize a replacement."
}
```

Sub-change 2·3의 ServerInstructions 텍스트와 함께 작용 — *export 사용처마다* 모델에 정직성 reminder.

대상 도구: `ls_get_chart`, `ls_get_overseas_chart` (둘 다 `output_mode` 파라미터를 지원하는 차트 도구). 다른 chart-emitting 도구(`ls_add_indicator` / `ls_reframe_chart` / `ls_get_etf_holdings` / `ls_get_program_trading`)는 `output_mode=export`를 지원하지 않으므로 영향 없음.

---

## 3. 도구 표면 영향

- **신규 도구 0개**
- **standard / all 표면 변동 없음** — 40 / 43 그대로
- 응답 페이로드에 `_meta.render_status` (모든 chart-emitting 도구 — `Program.cs` filter), `_meta.data_purpose` + `_meta.do_not_render` (export 모드 응답 — chart 도구 두 개)
- ServerInstructions chart-routing 단락 교체 + 확장 (현 ~1100자 → ~2200자)
- `ChartHostSupport.Resolve` 변경 없음 (v1.2 capability-based 분류 그대로 신뢰)
- `ToolSurfaceFreezeTests` 카운트 영향 없음
- ServerInstructions length budget 6000 → 7000자로 한 단계 증가

---

## 4. 테스트 전략

- **`RenderStatusBuilder`(또는 `UiResources.AttachRenderStatus`) 단위 테스트**: mode → status 매핑 (`Sep1865` / `StructuredContent` → `delivered`, `TextOnly` → `stripped_text_only`)
- **chart-emitting 도구 응답 어셔션**: `_meta.render_status` 필드 존재 + 값 정확성. non-chart 도구에는 부착 안 됨.
- **export 모드 가드 어셔션** (`McpJson.AttachExportGuard`): `_meta.data_purpose == "analysis_only"`, `_meta.do_not_render` 키워드 포함 (`indicators`, `different`, `analysis`, `not for chart synthesis` 등).
- **ServerInstructions 키워드 어셔션** (Sub-change 2·3 문안 검증):
  - 긍정 키워드: `render_status`, `stripped_text_only`, `delivered`
  - 부정 narration 금지 키워드: `Do not self-synthesize`, `Do NOT`, `output_mode=export`(금지 맥락), `host panel constraint`(또는 동등 표현)
  - self-synthesis 금지 구체화: `Python`, `JavaScript`, `PNG`, `recompute`
  - tool-mediated 변경: `ls_add_indicator`, `ls_reframe_chart`
- **회귀**: 기존 chart-emitting 도구 테스트가 새 `_meta` 필드를 *허용*하도록 매칭 완화. `Plotly.newPlot` 키워드 어셔션 제거 (v1.4에서 추가했던 wrap-and-route 문안과 함께 v1.5에서 사라짐).

추가 테스트 수 ≈ **10-12개**.

---

## 5. 일정 추정

| 항목 | 시간 |
|---|---|
| `AttachRenderStatus` + `AttachExportGuard` 헬퍼 + `Program.cs` filter 통합 | 1h |
| GetChartTool / OverseasStockTools에 export 가드 부착 | 30분 |
| ServerInstructions 단락 교체 + budget bump | 1h |
| 테스트 10-12개 | 1.5h |
| SPEC-v1.5.md + INTEROP doc 정리 | 1h |
| README hero / RELEASENOTES.Mcp.md + Core release notes 갱신 | 30분 |
| Release prep (csproj / server.json — 사용자 commit) | 30분 |
| **소계** | **~6h ≈ 0.75 work day** |

이전 draft(~8h) 대비 whitelist 코드 + ChartHostSupport 변경 작업이 빠지면서 2h 단축.

---

## 6. 작업 순서

1. **킥오프 (15분)**: render_status enum 두 값 확정, ServerInstructions 신규 문안 확정, export 가드 문구 확정.
2. **`UiResources.AttachRenderStatus` + `McpJson.AttachExportGuard` 추가 (45분)**.
3. **`Program.cs` AddCallToolFilter 통합 (15분)**: strip 단계 직후 render_status 부착.
4. **GetChartTool / OverseasStockTools export 가드 부착 (30분)**.
5. **ServerInstructions chart-routing 단락 교체 + budget 7000 bump (1h)**.
6. **테스트 통합 통과 (1.5h)**.
7. **수동 E2E (30분)**:
   - basic-host(또는 Codex/Claude Code) TextOnly 호스트에서 `render_status=stripped_text_only` 응답 → 모델이 "그렸다" 안 함 검증.
   - Claude Desktop Chat / Cowork에서 `render_status=delivered` 응답 → 차트 렌더 + height customization 요청 시 모델이 "호스트 패널 제약" 안내 + show_widget/export 우회 안 함 검증.
   - export 응답에서 `_meta.do_not_render`가 model context에 잡히는지 확인.
8. **문서 + release prep (1.5h)**.

---

## 7. 사용자 검증 — E2E 시나리오

### TextOnly 호스트 (Codex / Claude Code CLI) — P1 차단 검증

```
[v1.4 production — 회귀 발생]
사용자: "삼성전자 일봉 그려줘"
  → ls_get_chart(output_mode=display) 호출
  → 응답: summary + context (chart spec stripped)
  → 모델: "그려뒀습니다" (거짓)
사용자: "실제로는 안 그려졌네요"
  → 모델: 4분 9초 우회 작업
  → output_mode=export 재호출 → OHLCV
  → render_samsung_chart.py 248 lines 작성 + PNG (서버 indicator 불일치)

[v1.5 — 회귀 차단]
사용자: "삼성전자 일봉 그려줘"
  → ls_get_chart(output_mode=display) 호출
  → 응답: summary + context + _meta.render_status="stripped_text_only"
  → 모델: render_status 확인 → "이 호스트에서는 inline 차트가 표시되지 않습니다.
        2026-05-26 종가 299,000원 (+2.22%), MA5 288,500 / MA20 264,475 / MA60 219,083
        정배열입니다. dataset_id: ds_xxxx — 다른 지표를 추가하려면 ls_add_indicator,
        기간을 바꾸려면 ls_reframe_chart로 호출하실 수 있습니다. 차트 자체가
        필요하시면 AssistStudio / Claude Desktop Chat / Cowork 같은 호스트에서
        같은 질문을 주세요."
사용자: "차트 보고 싶은데 우회 안 돼?"
  → 모델: "OHLCV로 차트를 직접 그릴 수는 있지만, 그 경우 제가 계산한 MA가 서버
        측 MA와 달라져서 잘못된 그림이 됩니다. inline 차트는 chart-capable
        호스트가 필요합니다."
```

### Sep1865 호스트 (Claude Desktop Chat / Cowork) — P2 차단 검증

```
[v1.4 production — 회귀 발생]
사용자: "삼성전자 일봉 그려줘" → 차트 정상 렌더
사용자: "차트 좀 더 크게 보고 싶어"
  → 모델: 호스트 panel 제약을 인식 못 함
  → output_mode=export로 OHLCV 재요청
  → mcp__visualize__show_widget으로 자체 합성 차트 push
  → 사용자: server-rendered 차트 + self-synthesized 차트 두 개가 동시에 보임,
            indicator 값 불일치

[v1.5 — 회귀 차단]
사용자: "삼성전자 일봉 그려줘" → 차트 정상 렌더 (_meta.render_status="delivered")
사용자: "차트 좀 더 크게 보고 싶어"
  → 모델: render_status가 delivered, height는 host panel constraint
  → "차트 패널 높이는 이 호스트(Cowork chat panel)가 결정합니다, 서버
       쪽에서 키울 수 없어요. 더 큰 차트가 필요하시면 Claude Desktop
       일반 채팅 / AssistStudio처럼 패널이 더 큰 호스트에서 같은
       질문을 주세요."
사용자: "MA200도 추가해줘"
  → 모델: ls_add_indicator(dataset_id, "ma:200") — 정상 customization 경로
  → 추가된 차트 inline 갱신
```

### AssistStudio (StructuredContent — 회귀 없음)

```
사용자: "삼성전자 일봉 그려줘"
  → ls_get_chart(output_mode=display)
  → 응답: structuredContent.chart.spec + summary + _meta.render_status="delivered"
  → AssistStudio가 spec 자체 Plotly로 inline 렌더
  → 모델: "삼성전자 일봉 차트입니다. ..." (정상 narration)
```

성공 기준:
- Codex 환경에서 *v1.4 회귀의 4분 9초 우회 작업이 발생하지 않음*. 모델이 즉시 한계를 명시하고 정확한 요약만 제공.
- Cowork 환경에서 *height customization 요청에 모델이 호스트 패널 제약 안내*. show_widget / create_artifact / export 우회 발화 없음.

---

## 8. Resolved Decisions

| # | 항목 | 결정 | 근거 |
|---|---|---|---|
| Q1 | wrap_template 본문 포함 여부 | **제외, Appendix C로 강등** | 또 다른 합성 표면을 열어줄 위험. Sub-change 3(self-synthesis 금지)과 메시지 충돌. 진짜 필요 사용자가 나타나면 Appendix C에서 재오픈. |
| Q2 | Sep1865 진입 조건 | **v1.2 capability-based 분류 그대로 신뢰 (whitelist 도입 안 함)** | 2026-05-27 `spike/sep1865-verify` empirical: Claude Desktop / Cowork / basic-host / VS Code Chat 모두 ✅ iframe 렌더 (PlotlyTemplate 3-fix 후). capability 광고와 실제 렌더가 거의 일치 — whitelist는 over-engineering. |
| Q3 | TextOnly 모드 모델 narration | **chart 렌더 주장 명시 금지** | Codex 실측 "그려뒀습니다" 거짓 narration. ServerInstructions에 강한 어조로 명시. |
| Q4 | self-synthesis 금지 적용 범위 | **호스트 분류 무관** | render_status=delivered에서도 height customization 요청 시 모델이 show_widget / export로 우회 — Sub-change 3가 양쪽 모드를 다 커버. |
| Q5 | export 모드 self-synthesis | **명시 금지 + `_meta.do_not_render` 가드** | Codex 실측 render_samsung_chart.py 248 lines 자체 합성. server-computed indicators 무시. |
| Q6 | `render_status` enum 값 | **`delivered` / `stripped_text_only` 두 가지만** | 모델 narration 결정에 필요한 신호의 최소 집합. 호스트 분류 세분화는 INTEROP doc의 reference matrix에 충분. |
| Q7 | basic-host 같은 capability 미광고 + 실제 렌더 케이스 | **v1.5에는 자동 처리 없음** | TextOnly로 분류되어 `stripped_text_only` 응답 — 사용자 입장에서는 honest narration. 사용자가 basic-host 같은 호스트를 직접 운영하면 향후 explicit opt-in 가능. |

---

## 9. 릴리스 노트 초안

```markdown
## v1.5.0

**Fidelity-first chart narration.** Chart-emitting tools (ls_get_chart,
ls_reframe_chart, ls_add_indicator, ls_get_overseas_chart,
ls_get_etf_holdings, ls_get_program_trading) now ship `_meta.render_status`
on every response — `delivered` when the host receives the chart spec,
`stripped_text_only` when the chart payload was withheld because the host
has no SEP-1865 / structured-chart path. ServerInstructions tells the
model to read this signal: when `stripped_text_only`, the model must not
claim to have drawn / rendered / 표시 the chart — it states the limitation
explicitly and provides only the analytical summary.

The same paragraph forbids self-synthesis fallbacks *regardless of
`render_status`*: the model must NOT route around the server's render
path by fetching raw OHLCV via `output_mode=export` and rendering the
chart in Python / JavaScript / PNG, or by forwarding the chart spec to a
generic visualize MCP tool. Chart customization requests (indicator add,
reframe) go through `ls_add_indicator` / `ls_reframe_chart`; layout-level
requests (panel height, sizing) are honestly identified as host panel
constraints rather than routed around. `output_mode=export` responses
now carry `_meta.data_purpose: "analysis_only"` and `_meta.do_not_render`
guard text reinforcing this contract.

v1.2 capability-based host classification is preserved — no
`KnownIframeRenderingHosts` allowlist is needed. The 2026-05-27
`spike/sep1865-verify` session empirically verified that Claude Desktop
Chat, Claude Cowork, ext-apps basic-host, and VS Code Chat all render
the SEP-1865 iframe correctly once three PlotlyTemplate.html handshake
bugs were fixed (`38d4dc2`, `46e20f0`, `01d74e3`, cherry-picked to main
before v1.5).

Tool surface unchanged (40 standard / 43 all). All additions are
non-breaking response metadata + ServerInstructions text.
```

---

## 10. 참고

- v1.4 ServerInstructions chart-routing hint (v1.5가 *교체*): commit `6d7d3a0`
- v1.4-dev artifact-fidelity 회귀(Cowork E2E): commit `0ce0507`
- slice C가 wrap-only로 collapse된 결정: commit `532d61d`
- PlotlyTemplate handshake 3-fix (v1.5 직전 main cherry-pick): `38d4dc2`, `46e20f0`, `01d74e3`
- chart-emitting tool 집합: [UiResources.cs:62](../src/RedoxNet.Mcp.LsOpenApi/Apps/UiResources.cs) `PlotlyEmittingToolNames`
- 호스트 렌더링 모드 분기: [ChartRenderingMode.cs](../src/RedoxNet.Mcp.LsOpenApi/Apps/ChartRenderingMode.cs)
- v1.2 MCP Apps capability negotiation: [SPEC-v1.2-mcp-apps-capability.md](./SPEC-v1.2-mcp-apps-capability.md)
- empirical host matrix + interop quirks: [MCP-APPS-INTEROP.md](./MCP-APPS-INTEROP.md)
- v1.5 디자인 세션 핵심 finding(Codex self-synthesis 우회): [[chart_self_synthesis_antipattern]] 메모리
- 호스트 reality 보정 기록: [[render_hints_standardization]] 메모리
- AssistStudio reference implementation: [[next_assiststudio_plotly]] 메모리
- empirical 렌더 evidence: `docs/claude_desktop_chat_chart_lgelectronics.png` (Claude Desktop Chat), `docs/claude_desktop_cowork_chart_lgelectronics.png` (Cowork), `docs/vscode_copilot_chat_chart_nvidia.png` (VS Code Copilot Chat — NVDA 미장 일봉으로 SEP-1865 iframe + v1.3 overseas chart 경로 동시 확인)

---

## Appendix A — Deferred: Daily candle SQLite cache

> 원래 v1.5 slice A로 묶었으나 2026-05-26 deferred. 사용자 가치 가설이 *추측 기반*이라 보류. 디자인은 보존해 트리거 충족 시 재도출 비용 없이 재오픈 가능.

### 트리거 조건 (셋 중 하나라도 충족 시 재오픈)

- LS rate limit (HTTP 401 / TR-level rate error) 실제 관찰
- "MA200 워밍업이 느리다" / "같은 차트 다시 그리는데 느리다" 사용자 불평
- 사용량 분석에서 동일 (종목·timeframe) 반복 호출 분포가 *기대보다 큼* 확인

### 보존 자료

디자인 자체는 [`todo/4. AGENTS-PATCH-003-daily-candle-cache.md`](../todo/4.%20AGENTS-PATCH-003-daily-candle-cache.md) 그대로 채택 가능. 이전 spec draft가 답해둔 PATCH-003 §Open Questions 6개 항목의 v1.5 시점 합의:

1. **raw vs ADJ t8410 파라미터**: 구현 시 testbed 호출 1회로 확정.
2. **market 식별**: `stocks_metadata` 캐시(v0.5+) 재사용.
3. **ETF/ETN/미장**: 같은 `candles_daily` 테이블 사용 (`market` 컬럼이 KOSPI/KOSDAQ/NASDAQ/NYSE/AMEX 등 자연 partitioning). ELW 제외.
4. **Retention**: 기본 20년, `CandleCacheOptions.RetentionYears`로 조정.
5. **클리어/리빌드 도구**: `ls_candle_cache_admin(action="clear"|"rebuild"|"status", shcode?)` (all profile only).
6. **Opt-in vs default-on**: 결정 미정 — default-on은 첫 호출 latency 회귀 위험(5000봉 backfill). 재오픈 시 *opt-in 첫 릴리스 → default-on 한 단계 후*가 안전.

### 보류 이유 요약

- 통점 4가지(rate limit / 워밍업 / 월·주봉 일관성 / 분석 품질) 중 *실증된* 것 없음
- v0.10 `output_mode='reference'` + `dataset_id`가 *같은 세션 내 반복*은 이미 줄였음 — 캐시는 *세션 간* 가치이며 그 가치는 사용량 분포에 의존
- 영구 상태(스키마·sync policy·migration) 도입 비용 크고 제거 비용도 큼
- 미장(g3204) 통합 여부 결정 미정 — 함께 통합 시 일정 +3-4h, KR만 vs 전체 결정 필요

---

## Appendix B — Deferred: Saved screener macros

> 원래 v1.5 slice B로 묶었으나 2026-05-26 deferred. 사용자 가치 가설이 챗봇 인터페이스에서 약함.

### 트리거 조건

- "이 조합 자주 쓰는데 매번 재구성 귀찮다" 사용자 불평
- 호스트 측 메모리 alias(예: Claude Code `memory/`, Cursor `.cursorrules` 등)로 푸는 *0-코스트 대안*이 *충분하지 않다*는 피드백

### 가벼운 대안 (당장 가능, 도구 추가 없음)

사용자에게 *호스트 메모리에 매크로를 적어두면 모델이 자연어로 인용하면서 `ls_combine_screeners`를 호출한다*는 한 문단 가이드를 README에 추가. SQLite 도구 5개 없이 동일 효과.

예:
```
# Claude Code memory 예시
- "내 매수1" = MACD 0선 돌파 + 정배열 + 외인 3일연속 순매수 (AND)
- "내 관찰" = 거래량 회전율 100%↑ + 5일선 위 (AND)
```
모델이 "내 매수1 돌려봐"를 듣고 메모리에서 풀이 후 `ls_combine_screeners(signals=[6130, 6120, 6310], mode="and")` 호출.

### 보존 자료

이전 spec draft의 §3 디자인(SQLite 스키마, drift detection 표, 5-6개 도구 시그니처, 자연어 흐름 예시)을 git history에 보존. 트리거 발화 시 그대로 재오픈 가능. 미해결 결정 사항:

- export/import를 단일 `ls_screener_io`로 묶을지 vs 별개 도구로 둘지 (portfolio_io 패턴 → 묶는 게 자연)
- drift 시 부분 실행 vs 거부 정책 (첫 구현 거부 권장)

### 보류 이유 요약

- LLM 챗봇 인터페이스에서 자연어 재구성 비용이 거의 0 — 매크로 이름 기억 부담이 자연어 입력 부담보다 가볍다고 단정 어려움
- "매일 같은 매크로" 패턴은 알람·대시보드 use case(HTS [0150])에 더 가깝지 챗봇에 맞지 않음
- portfolio_io와 달리 매크로는 *외부 source of truth 없는 사용자 머릿속 라벨* — MCP 서버보다 호스트 메모리에 두는 게 자연스러움
- catalog drift detection 5개 도구가 *드물게 발생하는* LS rename 이벤트만을 위해 영구 상태를 떠받침

---

## Appendix C — Deferred: `_meta.render_hints` / wrap_template

> v1.5 디자인 중간 단계(2026-05-26)에 본문으로 검토했으나 **Codex empirical에서 자기-합성 우회 패턴을 직접 관찰**한 뒤 제외. 모델에 *또 다른 합성 표면*을 열어주는 위험이 v1.5의 fidelity-first 메시지와 충돌.

### 트리거 조건 (재오픈)

- *Cowork 3P 사용자가 "wrap_template이 있으면 visualize MCP로 차트가 표시될 텐데 없어서 불편하다"는 구체 불평*. 추측이 아닌 실 사용자 발화.
- 그리고 동시에 — Sub-change 3 self-synthesis 금지가 *작동하고 있어서* wrap_template을 *제어된 표면*으로 도입할 수 있다는 신뢰.

### 보존 자료 (이전 본문 draft)

- 응답에 `_meta.render_hints.{preferred, wrap_template, spec_token}` 추가
- `wrap_template` HTML 모양: `<div id='c' style='height:440px;...'></div><script src='cdn.plot.ly/plotly-2.35.2.min.js'></script><script>const spec = __SPEC__; Plotly.newPlot('c', spec.data, spec.layout, {responsive: true, displaylogo: false, displayModeBar: 'hover'});</script>` (AssistStudio baseline과 동기)
- spec_token = `__SPEC__`, 모델이 `JSON.stringify(structuredContent.chart.spec)`로 substitute
- TextOnly 모드에서만 송신 (Sep1865/StructuredContent 호스트는 무시)

### 보류 이유 요약

- 실측 사용자(Codex)가 *호스트가 wrap_template을 받았어도* 자기 Python 합성으로 우회할 가능성 — wrap이 fidelity를 *구조적으로* 보장하지 않음
- 직접 수혜자는 Anthropic Cowork 3P + visualize MCP 셋업 한 가지 niche
- 메인 메시지(fidelity-first, self-synthesis 금지)와 *반대 방향* 신호

### 관련 메모리

[[render_hints_standardization]] — wrap_template 표준화 추적 + edge case 보존
