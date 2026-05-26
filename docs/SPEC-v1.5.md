# SPEC: v1.5.0 — Fidelity-first chart narration

- **상태**: Draft (final 2026-05-26) — empirical Codex 테스트로 디자인 확정
- **대상 버전**: v1.5.0
- **선행**: [SPEC-v1.4.md](./SPEC-v1.4.md), [SPEC-v1.2-mcp-apps-capability.md](./SPEC-v1.2-mcp-apps-capability.md), v1.4 commit `6d7d3a0` (ServerInstructions chart-routing hint)
- **범위**: 단일 슬라이스 — chart-emitting 도구의 *모델 narration 정직성* 강제. (1) ServerInstructions narration honesty + self-synthesis 명시 금지, (2) `_meta.render_status` 응답 필드, (3) Sep1865 진입 엄격화(`KnownIframeRenderingHosts` whitelist), (4) `output_mode=export` 응답에 anti-synthesis 가드 메타.
- **이전 draft에서 묶었거나 본문이었던 항목들**: candle cache(§Appendix A), saved screener macros(§Appendix B), `_meta.render_hints.wrap_template`(§Appendix C — 본문에서 *부록으로 강등*), empirical host matrix(§Appendix D — 이번 세션 finding).
- **메시지**: v1.5는 *기능 추가*가 아니라 *모델 거짓말 차단*. 사용자가 받는 것: "차트 못 봤는데 그렸다고 한다"의 종료.

---

## 1. 컨텍스트

### 1.1 v1.4 → v1.5

v1.4는 두 슬라이스 ship(date envelope + Q-Click). v1.5는 *기능*이 아니라 *fidelity*에 좁게 집중 — v1.4-dev Cowork E2E 회귀(commit `0ce0507`)와 v1.5 디자인 세션 중 2026-05-26 Codex 실측에서 동일한 패턴이 *호스트를 바꿔도 재현*되며 *prompt 강도가 아니라 디자인 변경이 필요*함을 확인.

### 1.2 v1.5가 푸는 *세 가지 empirical 문제*

| # | 문제 | 출처 |
|---|---|---|
| P1 | 호스트가 SEP-1865 capability를 *광고*하지만 iframe을 *렌더하지 않음* → 우리 서버는 chart 페이로드 보내고 모델은 "그렸다"고 narration → 사용자는 차트 없음 | Claude Desktop frame-ancestors CSP 관찰(메모리 `next_mcp_apps_capability` 2026-05-22) |
| P2 | TextOnly 호스트에서 chart spec이 *전송조차 안 됐는데도* 모델이 `output_mode=display`를 호출한 사실만 보고 "그렸다" 거짓 narration | **2026-05-26 Codex 실측** — `include_chart` 파라미터 schema에 없었음(=우리 v1.2 TextOnly 분류 정확), spec 미전송에도 모델 "그려뒀습니다" |
| P3 | 호스트 분류가 *정확해도* 모델이 `output_mode=export`로 OHLCV 재요청 후 Python/JS로 *자체 차트 합성* → server-computed indicators(우리가 보낸 MA5/20/60 값) *완전 무시* → 시각적으로 그럴듯하지만 *우리가 보장하는 fidelity가 아닌* 차트가 사용자에게 표시됨 | **2026-05-26 Codex 실측** — `render_samsung_chart.py` +248 lines 생성, OHLCV에서 자체 MA 계산해 PNG inline 표시. 사용자가 chart에서 보는 MA값과 우리 summary의 MA값이 *다른* 상태 |

P1은 *광고와 능력의 갈림*. P2는 *모델 narration의 정직성*. P3는 *모델 행동의 자기-합성 충동*. 셋 다 *prompt 강도*로 해결 안 되며, *response shape과 instructions의 명시*로 차단해야 함.

### 1.3 Empirical host matrix (요약, 자세한 건 §Appendix D)

| 호스트 | capability 광고 | 실제 inline 렌더 | 우리 분류 | 모델 narration |
|---|---|---|---|---|
| AssistStudio (WinUI 3) | ✅ SEP-1865 | ✅ (자체 Plotly) | StructuredContent | OK |
| Claude Desktop | ✅ SEP-1865 | ❌ (frame-ancestors CSP) | Sep1865 (현 분기) → **v1.5에서 TextOnly로 강등** | 미확인 (광고만) |
| Codex Desktop/TUI | ❌ 미광고 (`enable_mcp_apps=true`여도 capability는 별개) | ❌ (TUI 구조적; Desktop도 플래그) | TextOnly ✅ | **거짓 "그렸다" 관찰** |
| Claude Code CLI | ❌ (TUI) | ❌ | TextOnly | 미확인 |
| Cowork 3P (Bedrock/Vertex/Foundry) | ✅ SEP-1865 | ❌ (광고 후 미렌더, [claude-ai-mcp#236](https://github.com/anthropics/claude-ai-mcp/issues/236)) | Sep1865 (현 분기) → **v1.5에서 TextOnly로 강등** | 미확인 |
| Cursor 2.6+, VS Code, ChatGPT, Goose, Postman, MCPJam | ✅ (변동) | 광고대로 작동한다는 *보고만* 있음, 우리는 *실측 안 함* | Sep1865 (현 분기) → **v1.5에서 TextOnly로 보수적 강등, empirical verify 후 whitelist 추가** | 미확인 |

총평: **광고만으로는 신뢰 못 함**. v1.5의 Sub-change 3(Sep1865 진입 엄격화)이 이 매트릭스에 직접 응답.

### 1.4 비범위

- **`_meta.render_hints.wrap_template` (이전 draft 본문)** → Appendix C로 강등. Codex empirical에서 *호스트 분류가 정확해도* 모델이 자기-합성 우회를 *능동적으로* 수행함을 확인 → wrap_template은 *또 다른 합성 표면을 열어줄* 위험이 있어 v1.5에서 *제거*. 진짜로 필요한 사용자가 발견되면 Appendix C에서 재오픈.
- **SQLite 일봉 캐시** → Appendix A 유지.
- **Saved screener macros** → Appendix B 유지.
- **PNG 폴백(server-side render)** → AssistStudio가 이미 client-side `Plotly.toImage` 사용 — server-side 불필요. v1.6+ 보류.
- **`output_mode` / `summary_only` 인자명 일관화** → v1.5.1 또는 v1.6.

---

## 2. 디자인

### 2.1 Sub-change 1 — `ChartRenderingMode.Sep1865` 진입 엄격화

**현 동작** ([ChartHostSupport.cs:63](../src/RedoxNet.Mcp.LsOpenApi/Apps/ChartRenderingMode.cs)):
```
if (HasUiCapability(capabilities)) return Sep1865;  // capability 광고만 보고
if (clientInfo?.Name in KnownChartRenderers) return StructuredContent;
return TextOnly;
```

**v1.5 변경**: capability 광고 + *empirical-verified 렌더 호스트 whitelist* 둘 다 충족해야 Sep1865:

```
if (HasUiCapability(capabilities) && clientInfo?.Name in KnownIframeRenderingHosts) return Sep1865;
if (clientInfo?.Name in KnownChartRenderers) return StructuredContent;
return TextOnly;  // capability 광고만 있는 호스트는 여기로 (보수)
```

**`KnownIframeRenderingHosts` 초기 상태**: *빈 set*. 정확히 한 시점에 한 호스트씩 empirical verify 후 추가. Cursor 2.6+, VS Code, ChatGPT 등은 *코드를 우리가 돌려 실제 iframe 렌더를 본 다음* 추가. 그 전까지는 TextOnly로 강등.

**효과**:
- Claude Desktop / Cowork 3P / Codex / Claude Code CLI → 모두 TextOnly → chart 페이로드 자체가 안 감 → 모델이 "그렸다" 할 spec이 처음부터 없음
- 호스트가 광고와 다르게 실제 렌더 못해도 *우리는 페이로드 낭비 없음*
- AssistStudio: 기존 `KnownChartRenderers` 화이트리스트 그대로 → StructuredContent 분류 유지

### 2.2 Sub-change 2 — `_meta.render_status` 응답 필드

chart-emitting 도구 응답에 모드 신호:

```jsonc
"_meta": {
  "render_status": "delivered" | "stripped_text_only"
}
```

- `delivered`: Sep1865(verified) 또는 StructuredContent 모드 — chart 페이로드가 응답에 *포함*되어 호스트에 전달됨
- `stripped_text_only`: TextOnly 모드 — chart 페이로드가 `UiResources.StripChartStructuredContent`로 *제거*됨. 호스트와 모델 모두에게 명시.

모델은 이 필드를 보고 narration 결정 (Sub-change 3에서 contract 명시).

### 2.3 Sub-change 3 — ServerInstructions narration honesty

현 ServerInstructions의 chart-routing 단락(commit `6d7d3a0`)을 *완전히 교체*. 새 문안:

> **Chart rendering honesty.** A chart-emitting tool (ls_get_chart / ls_reframe_chart / ls_add_indicator / ls_get_overseas_chart / ls_get_etf_holdings / ls_get_program_trading) returns `_meta.render_status` indicating how the host will receive the chart:
>
> - `delivered`: the server emitted `structuredContent.chart.spec` and the host can render it inline (AssistStudio class with own Plotly; or a verified SEP-1865 host with iframe app). Narrate normally — e.g. "삼성전자 일봉 차트입니다, 최근 흐름은…".
> - `stripped_text_only`: the chart spec was NOT delivered. The host has no native renderer and no verified iframe path. You received only the analytical summary (closes, MAs, key turns). Do NOT claim you "drew", "rendered", "표시", "그렸", or otherwise visualized the chart. The user sees no chart. State the limitation explicitly: e.g. "이 호스트에서는 inline 차트가 표시되지 않습니다. 데이터 요약: ..." or "I can't render inline here — here's the analytical summary: ...".

### 2.4 Sub-change 4 — ServerInstructions self-synthesis 금지 (P3 직접 응답)

같은 단락에 *강한 어조로* 이어 붙임:

> **Do not self-synthesize charts when render_status is `stripped_text_only`.** Specifically, do NOT:
>
> - Re-call the same tool with `output_mode=export` to fetch raw OHLCV, then render a chart yourself in Python (matplotlib / plotly / mplfinance), JavaScript (Plotly / Chart.js / D3), HTML/SVG, or any other path. A self-synthesized chart looks plausible but its indicators (MA / RSI / Bollinger / etc.) will NOT match the server's `summary.moving_averages` values — different adjustment mode (raw vs ADJ), different warm-up window, different formula (SMA vs EMA vs weighted), different bar count. The user sees a chart that LOOKS authoritative but disagrees with the analytical summary on the same screen.
> - Recompute any indicator yourself from raw bars. The server's `summary.moving_averages.MA{5,20,60,120,200}`, `summary.ma60_slope`, `summary.drawdown_from_peak_pct`, and `context.*` are the authoritative values; your re-computation will diverge.
> - Write a PNG / SVG / HTML chart file "to show the user something". A self-rendered chart with mismatched indicators is *worse* than no chart, because users trust what they see.
>
> Instead, when `render_status` is `stripped_text_only`:
> 1. Report the analytical summary verbatim from the tool response (closes, MA values, key turning points, drawdown).
> 2. Name the limitation: "this host does not render inline charts".
> 3. Offer the `dataset_id` so the user can ask for *data analysis* (export to pandas / etc.) without rendering, or switch to a chart-capable host (AssistStudio, verified iframe hosts) for the actual visualization.
> 4. `output_mode=export` is legitimate ONLY for handing OHLCV to a data analysis pipeline (statistical tests, custom backtesting, passing to pandas). It is NOT a workaround to render charts.

### 2.5 Sub-change 5 — `output_mode=export` 응답에 anti-synthesis 가드 메타

export 모드 응답의 `_meta`에 명시 신호(모델이 합성 충동을 느낄 때 한 번 더 보는 brake):

```jsonc
"_meta": {
  "data_purpose": "analysis_only",
  "do_not_render": "Server-computed indicators (MA / RSI / Bollinger / drawdown) are not included in this payload. Rendering a chart from this OHLCV will produce different indicator values than the server computes (different adjustment mode, warm-up window, formula choice). Use this data for analysis (pandas, numpy, statistical work, custom backtests), not for chart synthesis. For inline charts, switch to a chart-capable host (AssistStudio, verified iframe hosts)."
}
```

Sub-change 3·4의 ServerInstructions 텍스트와 함께 작용 — *export 사용처마다* 모델에 정직성 reminder.

---

## 3. 도구 표면 영향

- **신규 도구 0개**
- **standard / all 표면 변동 없음** — 40 / 43 그대로
- 응답 페이로드에 `_meta.render_status` (모든 chart-emitting 도구), `_meta.data_purpose` + `_meta.do_not_render` (export 모드 응답) 추가
- ServerInstructions chart-routing 단락 교체 + 확장 (현 ~250자 → ~600자)
- `ChartHostSupport.Resolve`에 whitelist 체크 1줄 추가
- `ToolSurfaceFreezeTests` 카운트 영향 없음
- ServerInstructions length budget 6000 → 7000자로 한 단계 증가 (현 commit `6d7d3a0`이 4800 → 6000 했음)

---

## 4. 테스트 전략

- **`ChartHostSupport.Resolve` 테스트**:
  - Claude Desktop 시뮬레이션 (capability 광고 + name "claude-ai") → **TextOnly** 강등 검증 (이전엔 Sep1865)
  - Codex 시뮬레이션 (no capability + name "codex-mcp-client") → **TextOnly** (기존 동작 회귀 보호)
  - AssistStudio 시뮬레이션 (capability 광고 + name "AssistStudio") → **StructuredContent** (whitelist 외이지만 `KnownChartRenderers`에 있어 StructuredContent로)
  - 가상 verified iframe host 시뮬레이션 (capability + name in `KnownIframeRenderingHosts`) → **Sep1865**
- **`RenderStatusBuilder` 단위 테스트**: mode → status 매핑 (`Sep1865`/`StructuredContent` → `delivered`, `TextOnly` → `stripped_text_only`)
- **chart-emitting 도구 응답 어셔션**: `_meta.render_status` 필드 존재 + 값 정확성
- **export 모드 응답 어셔션**: `_meta.data_purpose == "analysis_only"`, `_meta.do_not_render` 키워드 포함 (`"indicators"`, `"different"`, `"analysis"`, `"not for chart synthesis"`)
- **ServerInstructions 키워드 어셔션** (Sub-change 3·4 문안 검증):
  - 긍정 키워드: `"render_status"`, `"stripped_text_only"`, `"delivered"`
  - 부정 narration 금지 키워드: `"do not claim"`, `"do not render"`, `"do not self-synthesize"`, `"output_mode=export"` (금지 맥락)
  - self-synthesis 금지 구체화: `"Python"`, `"JavaScript"`, `"PNG"`, `"recompute"`
- **회귀**: 기존 chart-emitting 도구 테스트가 새 `_meta` 필드를 *허용*하도록 매칭 완화

추가 테스트 수 ≈ **12-15개**.

---

## 5. 일정 추정

| 항목 | 시간 |
|---|---|
| `ChartHostSupport.Resolve`에 `KnownIframeRenderingHosts` whitelist 추가 + Sep1865 분기 조건 강화 | 1h |
| `RenderStatusBuilder` + 6개 chart 도구 통합 | 1.5h |
| export 모드 응답 `_meta` 가드 추가 | 30분 |
| ServerInstructions chart-routing 단락 교체 + 확장 (Sub-change 3·4 문안) | 1h |
| 테스트 12-15개 (resolution / render_status / export meta / ServerInstructions keywords) | 2.5h |
| SPEC-v1.5.md 마무리 + README hero / RELEASENOTES | 1h |
| Release prep (사용자 commit) | 30분 |
| **소계** | **~8h ≈ 1 work day** |

이전 wrap-only(7.5h)와 거의 동일. wrap_template emission 작업이 빠지고 narration honesty / whitelist / export meta가 추가됨.

---

## 6. 작업 순서

1. **킥오프** (15분): `KnownIframeRenderingHosts` 초기 상태(빈 set) 확정, render_status enum 두 값 확정, ServerInstructions 신규 문안 확정.
2. **`ChartHostSupport` 변경** (1h): whitelist 도입 + 테스트.
3. **`RenderStatusBuilder` + 6 chart 도구 통합** (1.5h).
4. **export 모드 `_meta` 가드** (30분).
5. **ServerInstructions chart-routing 단락 교체** (1h): 현 commit `6d7d3a0` paragraph를 v1.5 새 paragraph로. 토큰 budget 6000 → 7000.
6. **테스트 통합 통과** (2h).
7. **수동 E2E** (30분): Codex에서 *같은 prompt* ("삼성전자 일봉 그려줘") 재실행 → `render_status=stripped_text_only` 응답 → 모델이 "그렸다" 안 하고 한계 명시 + self-synthesis 시도 안 함 검증.
8. **문서 + release prep** (1.5h).

---

## 7. 사용자 검증 — E2E 시나리오

### Codex (TextOnly, P2+P3 재현 차단 검증)

```
[v1.4 production — 회귀 발생]
사용자: "삼성전자 일봉 그려줘"
  → ls_get_chart(output_mode=display) 호출
  → 응답: summary + context (chart spec stripped)
  → 모델: "그려뒀습니다" (거짓)
사용자: "실제로는 안 그려졌네요"
  → 모델: 4분 9초 우회 작업
  → output_mode=export 재호출 → OHLCV
  → render_samsung_chart.py 248 lines 작성
  → PNG 생성 (server-computed MA와 다른 자체 합성 MA로)

[v1.5 — 회귀 차단]
사용자: "삼성전자 일봉 그려줘"
  → ls_get_chart(output_mode=display) 호출
  → 응답: summary + context + _meta.render_status="stripped_text_only"
  → 모델: render_status 확인 → "이 호스트에서는 inline 차트가 표시되지 않습니다.
        2026-05-26 종가 299,000원 (+2.22%), MA5 288,500 / MA20 264,475 / MA60 219,083
        정배열입니다. 차트가 필요하시면 AssistStudio 같은 호스트에서 같은 질문을
        주시거나, output_mode=export로 데이터를 받아 pandas로 분석하실 수 있습니다.
        dataset_id: ds_xxxx"
사용자: "차트 보고 싶은데 우회 안 돼?"
  → 모델: "OHLCV로 차트를 직접 그릴 수는 있지만, 그 경우 제가 계산한 MA가 서버
        측 MA와 달라져서 잘못된 그림이 됩니다. inline 차트는 chart-capable 호스트가
        필요합니다 — 그 외에는 데이터 분석만 가능합니다."
```

### AssistStudio (StructuredContent — 회귀 없음)

```
사용자: "삼성전자 일봉 그려줘"
  → ls_get_chart(output_mode=display)
  → 응답: structuredContent.chart.spec + summary + _meta.render_status="delivered"
  → AssistStudio가 spec 자체 Plotly로 inline 렌더
  → 모델: "삼성전자 일봉 차트입니다. ..." (정상 narration)
```

성공 기준: Codex 환경에서 *v1.4 회귀의 4분 9초 우회 작업이 발생하지 않음*. 모델이 즉시 한계를 명시하고 정확한 요약만 제공.

---

## 8. Resolved Decisions

Codex 2026-05-26 실측으로 모든 결정 확정.

| # | 항목 | 결정 | 근거 |
|---|---|---|---|
| Q1 | wrap_template 본문 포함 여부 | **제외, Appendix C로 강등** | Codex empirical: 호스트 분류가 정확해도 모델이 자기-합성 우회 → wrap_template은 *또 다른 합성 표면을 열어줄* 위험. 진짜 필요 사용자가 나타나면 Appendix C에서 재오픈. |
| Q2 | Sep1865 진입 조건 | **capability 광고 + `KnownIframeRenderingHosts` whitelist 둘 다** | Claude Desktop frame-ancestors CSP 관찰 + Codex 광고-실제렌더 갈림 패턴. 광고만으로는 신뢰 불가. |
| Q3 | TextOnly 모드 모델 narration | **chart 렌더 주장 명시 금지** | Codex 실측 "그려뒀습니다" 거짓 narration. ServerInstructions에 강한 어조로 명시. |
| Q4 | export 모드 self-synthesis | **명시 금지 + `_meta.do_not_render` 가드** | Codex 실측 render_samsung_chart.py 248 lines 자체 합성. server-computed indicators 무시. |
| Q5 | `render_status` enum 값 | **`delivered` / `stripped_text_only` 두 가지만** | 모델 narration 결정에 필요한 신호의 최소 집합. `iframe_advertised_unverified` 등 세분화는 Sub-change 1로 흡수(보수적 TextOnly 강등). |
| Q6 | Cursor 2.6+, VS Code 등 화이트리스트 등록 | **v1.5에는 등록 안 함** — empirical verify 후 별 패치(v1.5.1+) | 조사 보고서가 "production 작동" 보고했지만 우리 책상에서 직접 확인 안 됨. Codex 케이스에서 보고-실측 갈림이 한 번 드러난 이상 보수적으로. |
| Q7 | wrap_template 옵션을 env-opt-in으로라도 남기기 | **아니오, 완전 제외** | Sub-change 4(self-synthesis 금지)와 메시지 충돌. opt-in이라도 *합성 표면 제공*이라는 패턴은 동일. Appendix C로 보존하면 충분. |

---

## 9. 릴리스 노트 초안

```markdown
## v1.5.0

**Fidelity-first chart narration.** Chart-emitting tools (ls_get_chart,
ls_reframe_chart, ls_add_indicator, ls_get_overseas_chart,
ls_get_etf_holdings, ls_get_program_trading) now ship `_meta.render_status`
on every response — `delivered` when the host can render the chart spec,
`stripped_text_only` when the chart payload was withheld because the host
has no verified renderer. ServerInstructions tells the model to read this
signal: when `stripped_text_only`, the model must not claim to have drawn /
rendered / 표시 the chart — it states the limitation explicitly and
provides only the analytical summary.

The same paragraph also forbids self-synthesis fallbacks: the model must
NOT fetch raw OHLCV via `output_mode=export` and render the chart
yourself in Python / JavaScript / PNG. Self-rendered indicators (MA / RSI /
Bollinger) do not match the server's authoritative values — different
adjustment mode, warm-up window, formula choice. A self-rendered chart
that looks plausible but disagrees with the analytical summary on the
same screen is worse than no chart. `output_mode=export` responses now
carry `_meta.data_purpose: "analysis_only"` and `_meta.do_not_render`
guard text reinforcing this contract.

The `ChartRenderingMode.Sep1865` branch is tightened: capability
advertisement alone is no longer enough — the host must also be on the
`KnownIframeRenderingHosts` whitelist (initially empty; entries added
only after we empirically verify a host actually renders the iframe).
Hosts that advertise SEP-1865 but don't actually render (Claude Desktop
frame-ancestors CSP, Claude Cowork 3P inference, others) cleanly fall
back to TextOnly — the chart payload is stripped, the model gets honest
narration signal, no wasted bandwidth.

Tool surface unchanged (40 standard / 43 all). All additions are
non-breaking response metadata + ServerInstructions text.
```

---

## 10. 참고

- v1.4 ServerInstructions chart-routing hint (v1.5가 *교체*): commit `6d7d3a0`
- v1.4-dev artifact-fidelity 회귀(Cowork E2E): commit `0ce0507`
- slice C가 wrap-only로 collapse된 결정: commit `532d61d`
- chart-emitting tool 집합: [UiResources.cs:62](../src/RedoxNet.Mcp.LsOpenApi/Apps/UiResources.cs) `PlotlyEmittingToolNames`
- 호스트 렌더링 모드 분기: [ChartRenderingMode.cs](../src/RedoxNet.Mcp.LsOpenApi/Apps/ChartRenderingMode.cs)
- v1.2 MCP Apps capability negotiation: [SPEC-v1.2-mcp-apps-capability.md](./SPEC-v1.2-mcp-apps-capability.md)
- v1.5 디자인 세션 핵심 finding(Codex self-synthesis 우회): [[chart_self_synthesis_antipattern]] 메모리
- 호스트 reality 보정 기록: [[render_hints_standardization]] 메모리
- AssistStudio reference implementation: [next_assiststudio_plotly](../../../Users/diluc/.claude/projects/D--Codes-mcp-lsopenapi/memory/next_assiststudio_plotly.md)

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
- 그리고 동시에 — Sub-change 4 self-synthesis 금지가 *작동하고 있어서* wrap_template을 *제어된 표면*으로 도입할 수 있다는 신뢰.

### 보존 자료 (이전 본문 draft)

- 응답에 `_meta.render_hints.{preferred, wrap_template, spec_token}` 추가
- `wrap_template` HTML 모양: `<div id='c' style='height:440px;...'></div><script src='cdn.plot.ly/plotly-2.35.2.min.js'></script><script>const spec = __SPEC__; Plotly.newPlot('c', spec.data, spec.layout, {responsive: true, displaylogo: false, displayModeBar: 'hover'});</script>` (AssistStudio baseline과 동기)
- spec_token = `__SPEC__`, 모델이 `JSON.stringify(structuredContent.chart.spec)`로 substitute
- TextOnly 모드에서만 송신 (Sep1865/StructuredContent 호스트는 무시)

### 보류 이유 요약

- 실측 사용자(Codex)가 *호스트가 wrap_template을 받았어도* 자기 Python 합성으로 우회할 가능성 — wrap이 fidelity를 *구조적으로* 보장하지 않음 (이전엔 보장한다고 봤지만 self-synthesis 충동은 wrap 우회까지 능동적)
- 직접 수혜자는 Anthropic Cowork 3P + visualize MCP 셋업 한 가지 niche
- 메인 메시지(fidelity-first, self-synthesis 금지)와 *반대 방향* 신호

### 관련 메모리

[[render_hints_standardization]] — wrap_template 표준화 추적 + edge case 보존

---

## Appendix D — Empirical host render matrix (2026-05-26 기준)

향후 host 추가/변경 시 *empirical verify*해서 업데이트할 reference.

| 호스트 | 버전 / 식별 | capability 광고 | 실제 inline 렌더 | 우리 분류 (v1.5) | render_status (v1.5) | 관찰 출처 |
|---|---|---|---|---|---|---|
| AssistStudio | WinUI 3, `clientInfo.name="AssistStudio"` | ✅ SEP-1865 (advertise, 자체 미사용) | ✅ (자체 Plotly v2.35.2 bundle, `chat.html:2233`) | StructuredContent | `delivered` | [[next_assiststudio_plotly]] + 직접 코드 검증 |
| Claude Desktop | `clientInfo.name="claude-ai"` | ✅ SEP-1865 | ❌ frame-ancestors CSP block (2026-05-14 관찰) | **TextOnly** (Sep1865에서 강등) | `stripped_text_only` | [[next_mcp_apps_capability]] |
| Cowork 3P (Bedrock/Vertex/Foundry inference) | `clientInfo.name="claude-ai"` (same) | ✅ SEP-1865 | ❌ ([claude-ai-mcp#236](https://github.com/anthropics/claude-ai-mcp/issues/236)) | **TextOnly** (Sep1865에서 강등) | `stripped_text_only` | 조사 보고서 |
| Codex (Desktop / TUI) | `clientInfo.name="codex-mcp-client"` (TUI 확인) | ❌ capability 미광고 (`enable_mcp_apps=true`여도 별개) | ❌ (TUI 구조적; Desktop 플래그 뒤) | TextOnly (기존 동작 유지) | `stripped_text_only` | **2026-05-26 직접 실측** — `include_chart` 파라미터 부재 + spec 미전송 + self-synthesis 우회 시도 |
| Claude Code CLI (this session) | `clientInfo.name="claude-code"` (추정) | ❌ (TUI) | ❌ (구조적) | TextOnly | `stripped_text_only` | TUI structural, 조사 보고서 |
| Cursor 2.6+ | `clientInfo.name` 미확인 | ✅ MCP Apps 지원 보고 ([changelog](https://cursor.com/changelog/2-6)) | 보고만 있음 *우리 실측 없음* | **TextOnly (보수)** until verify | `stripped_text_only` | 조사 보고서 (간접) |
| VS Code, ChatGPT, Goose, Postman, MCPJam | 미확인 | ✅ (변동) | 보고만 있음 *우리 실측 없음* | **TextOnly (보수)** | `stripped_text_only` | 조사 보고서 (간접) |

### 확장 절차 (`KnownIframeRenderingHosts` whitelist에 호스트 추가)

1. 해당 호스트로 `ls_get_chart` 호출
2. 응답에 `structuredContent.chart.spec`이 포함된 상태(`Sep1865`로 임시 분기 후) chart가 *실제로* inline 렌더되는지 시각 확인
3. 모델 narration이 정확한지 확인 (실 렌더면 "차트 표시" narration, 미렌더면 거짓말)
4. 둘 다 통과 → `clientInfo.name`을 `KnownIframeRenderingHosts`에 추가 + commit + Appendix D 행 갱신
5. 통과 못하면 TextOnly로 유지

이 절차를 README나 CONTRIBUTING에 한 문단 추가 권장 (호스트 등록 contract을 외부에 노출).
