# MCP Apps (SEP-1865) Interop Notes

> **Purpose**: capture empirical interop knowledge from testing our SEP-1865
> implementation against real hosts. Pairs with
> [`docs/LS-API-QUIRKS.md`](./LS-API-QUIRKS.md) on the upstream-API side.
> Updated as new hosts are tested or new quirks are surfaced.

This document is the practical companion to
[`docs/SPEC-v1.2-mcp-apps-capability.md`](./SPEC-v1.2-mcp-apps-capability.md)
(original design) and
[`docs/SPEC-v1.5.md`](./SPEC-v1.5.md) (fidelity-first narration + empirical
host matrix). The spec docs describe intent; this doc records *what
actually happened* when we ran it against real hosts.

---

## 1. Architecture: two `_meta` locations

SEP-1865 puts `_meta` in *two different places*, and they mean different
things. Conflating them is the most common source of confusion in this
codebase.

| Location | Place in protocol | What goes in it | Standardized? |
|---|---|---|---|
| `Tool._meta.ui` | `tools/list` response — *tool descriptor* | `resourceUri` (the `ui://…` HTML template), `visibility`, `csp.resourceDomains` | **Yes** — SEP-1865 |
| `CallToolResult._meta` | `tools/call` response — *result* | Server-private hints (e.g. v1.5 `render_status`, `data_purpose`) | No — server-defined |

Our v1.2 attaches `_meta.ui` to chart-emitting tool descriptors in
[`UiResources.ApplyChartSurface`](../src/RedoxNet.Mcp.LsOpenApi/Apps/UiResources.cs).
v1.5 adds `CallToolResult._meta.render_status` for narration honesty
and `CallToolResult._meta.do_not_render` on `output_mode=export`
responses — both go in the *other* `_meta` (the result-level one).

**Implication**: a host that "supports SEP-1865" only commits to reading
the first. The second is private extension territory each server has to
sell to hosts individually (or eventually push for standardization via a
SEP companion proposal — see [[render_hints_standardization]] memory).

---

## 2. Empirical host matrix (snapshot as of 2026-05-27, post PlotlyTemplate 3-fix)

`clientInfo.name` as reported on `initialize`. Mode is what
`ChartHostSupport.Resolve` returns — v1.5 keeps v1.2's capability-based
gating; no whitelist tightening was needed (see §3 Q6).

| Host | `clientInfo.name` | SEP-1865 capability advertised | iframe actually renders | Mode | Verified by |
|---|---|---|---|---|---|
| **AssistStudio** (WinUI 3) | `AssistStudio` | ✅ yes (advertised, not consumed) | n/a — reads `structuredContent.chart` directly | StructuredContent | [[next_assiststudio_plotly]] |
| **Claude Desktop Chat** | `claude-ai` | ✅ yes | ✅ **yes** (PlotlyTemplate 3-fix) | Sep1865 | 2026-05-27 spike/sep1865-verify, `docs/claude_desktop_chat_chart_lgelectronics.png` |
| **Claude Cowork** (chat panel) | `claude-ai` | ✅ yes | ✅ **yes** (PlotlyTemplate 3-fix) | Sep1865 | 2026-05-27 spike/sep1865-verify, `docs/claude_desktop_cowork_chart_lgelectronics.png` |
| **ext-apps `basic-host`** (reference) | `MCP Apps Host` | ❌ no (intentional — see Q3) | ✅ yes | TextOnly (capability absent) | 2026-05-27 spike/sep1865-verify, end-to-end Plotly chart visible |
| **VS Code Chat** (GitHub Copilot) | (varies) | ✅ yes | ✅ **yes** (PlotlyTemplate 3-fix) | Sep1865 | 2026-05-27 spike/sep1865-verify, `docs/vscode_copilot_chat_chart_nvidia.png` (NVDA daily — also exercises the v1.3 overseas chart path) |
| **Codex Desktop / TUI** | `codex-mcp-client` | ❌ no (`enable_mcp_apps=true` is a separate client flag, Q7) | ❌ — TUI structurally can't | TextOnly | 2026-05-26 empirical test, user self-report |
| **Claude Code CLI** | (TUI) | ❌ no | ❌ — TUI structurally can't | TextOnly | research agent + structural reasoning |
| **Google Antigravity** (Gemini 3.5 Flash) | (unknown — TBD inspect) | ❌ no (inferred from observed TextOnly mode) | n/a — GUI app, *not structurally blocked*, simply no SEP-1865 path today | TextOnly | 2026-05-27 empirical, `docs/antigravity_chart_nvidia.png` — model narrates limitation honestly per v1.5 ServerInstructions, server-computed MAs (MA20/60/120/200) + `bullish_alignment` + `ma60_slope` + `drawdown_from_peak_pct` used verbatim, no self-synthesis attempt. **First non-TUI empirical evidence of v1.5 narration honesty firing on a TextOnly host.** |
| **Cursor 2.6+, ChatGPT, Goose, Postman, MCPJam** | unknown | ✅ per their changelogs | ⚠️ reported in changelogs, *not directly empirically verified by us* — but the v1.2 capability gate should hold given the Claude / VS Code / basic-host class verifications | Sep1865 | research agent (secondary sources) |

**Reading guide**: after the PlotlyTemplate handshake 3-fix
(`38d4dc2` / `46e20f0` / `01d74e3`, all cherry-picked to main before
v1.5), capability advertisement *is* a reliable signal of actual
rendering for the hosts we've tested directly. The "advertise-but-don't-render"
pattern that motivated an earlier draft's `KnownIframeRenderingHosts`
whitelist turned out to be *our bug*, not a host shortcoming.

The remaining advertise/render mismatch direction (basic-host class:
*don't advertise but do render*) is acknowledged but left untreated in
v1.5 — TextOnly for those hosts means the user sees the honest
analytical summary instead of an iframe, which is acceptable. A future
release can add a small whitelist if a real user actually runs into it.

---

## 3. Quirks catalog

### Q1. PostMessage payload must be an object, not a JSON string

- **Discovered**: 2026-05-27, `spike/sep1865-verify`
- **Symptom**: basic-host hangs forever at
  `[HOST] Waiting for MCP App to initialize...`. Our iframe console
  shows `script loaded` + `ui/initialize sent`, no error.
- **Root cause**: ext-apps `PostMessageTransport.messageListener`
  (`D:/Codes/ext-apps/src/message-transport.ts:80`) parses
  `event.data` with `JSONRPCMessageSchema.safeParse(event.data)` directly.
  A string payload fails schema, and `event.data?.jsonrpc` is `undefined`
  on a string, so the listener takes the
  *"Not a JSON-RPC message at all… Ignore silently"* branch — no error
  surfaced anywhere.
- **Fix**: send the object literal, not a JSON string:
  ```js
  // Wrong (silently dropped by basic-host transport):
  window.parent.postMessage(JSON.stringify(msg), "*");
  // Right:
  window.parent.postMessage(msg, "*");
  ```
- **Why hidden so long**: this bug shipped from v1.2 through v1.4
  undetected. The only verified SEP-1865 host (AssistStudio) consumes
  `structuredContent.chart` directly and never exercises the iframe
  path ([[next_assiststudio_plotly]] memory). Claude Desktop's iframe
  mount fails earlier at the CSP level, never reaching our HTML.
  `ext-apps/basic-host` is the first host to actually run our iframe
  code on the wire.
- **Fix location**: `src/RedoxNet.Mcp.LsOpenApi/Apps/PlotlyTemplate.html`,
  `send()` function.

### Q2. `ui/initialize.params` requires `appInfo`

- **Discovered**: 2026-05-27, same session, immediately after Q1 fix.
- **Symptom**: same as Q1 — basic-host hangs at
  `Waiting for MCP App to initialize...`. Iframe console confirms
  `ui/initialize sent` with our params payload.
- **Root cause**: `McpUiInitializeRequest`'s zod schema requires three
  `params` fields: `protocolVersion`, `appCapabilities`, **`appInfo`**.
  The reference `App.connect()` (`D:/Codes/ext-apps/src/app.ts:1961`)
  always sends all three. Our hand-rolled handshake omitted `appInfo`,
  so basic-host's schema validation silently dropped the message.
- **Fix**: add `appInfo: { name, version }` to `params`:
  ```js
  params: {
    protocolVersion: PROTOCOL_VERSION,
    appCapabilities: { availableDisplayModes: ["inline"] },
    appInfo: { name: "mcp-lsopenapi/plotly", version: "1.0.0" },
  }
  ```
- **Why hidden so long**: same as Q1 — AssistStudio doesn't exercise
  iframe path.
- **Fix location**: same file, inline `<script>` block at end of body.

### Q3. SEP-1865 capability advertisement is *optional* from the host side

- **Observation**: 2026-05-26.
- **basic-host** (the SEP-1865 *reference* host) does NOT advertise the
  `io.modelcontextprotocol/ui` capability on `initialize`. Its iframe
  mount is triggered purely by reading `_meta.ui.resourceUri` on the
  tool descriptor (`ext-apps/examples/basic-host/src/implementation.ts:13`,
  `IMPLEMENTATION = { name: "MCP Apps Host", version: "1.0.0" }` — no
  capability option).
- **Implication**: our v1.2 capability-only gating (Sep1865 mode iff
  client advertises) classifies basic-host as TextOnly. v1.5 accepts
  this: an honest analytical summary is the correct fallback for a
  host whose capability we can't observe. A `KnownIframeRenderingHosts`
  allowlist was prototyped during the spike but *not shipped* in v1.5
  — see Q6.
- **Counter-pattern**: AssistStudio *does* advertise the capability but
  doesn't consume the iframe — it's an intentional trade-off so servers
  can gate on a single standard signal ([[next_assiststudio_plotly]]).
  These are opposite stances on the same ambiguity in the spec.

### Q4. Streamable HTTP `Stateless = true` breaks capability gating

- **Discovered**: 2026-05-27, `spike/sep1865-verify`.
- **Symptom**: `ChartHostSupport.Resolve` sees
  `ctx.Server.ClientInfo == null` on every `tools/list` and
  `tools/call`, even though `initialize` clearly carried the clientInfo.
  Server falls through to TextOnly always.
- **Root cause**: `ModelContextProtocol.AspNetCore` with
  `WithHttpTransport(o => o.Stateless = true)` creates a fresh
  `McpServer` per HTTP request. `initialize` establishes clientInfo for
  *that* request; the next request gets a brand-new server with no
  memory.
- **Fix**: `WithHttpTransport(o => o.Stateless = false)`. Session-based
  mode uses the `Mcp-Session-Id` header to thread requests to the same
  `McpServer` instance — clientInfo and capabilities survive across
  `tools/list` / `tools/call`.
- **Scope**: HTTP transport only. stdio is always single-stream so
  clientInfo is implicitly retained.

### Q5. ext-apps basic-host's sandbox URL is hardcoded

- **Discovered**: 2026-05-27 during port-conflict workaround.
- **Symptom**: setting `SANDBOX_PORT` env to anything other than `8081`
  → iframe area shows
  `localhost에서 연결을 거부했습니다 (connection refused)`.
- **Root cause**: `examples/basic-host/src/implementation.ts:13` has
  `const SANDBOX_PROXY_BASE_URL = "http://localhost:8081/sandbox.html";`
  hardcoded. `SANDBOX_PORT` env affects only the server-side listen
  port in `serve.ts`, not the client-side iframe `src`.
- **Workaround**: keep `SANDBOX_PORT` at default 8081. If 8081 is
  occupied by another process (we hit `ApplicationWebServer.exe` from
  some Korean vendor app), use only `HOST_PORT` override. If 8081 truly
  cannot be freed, patch basic-host's `implementation.ts` and rebuild.

### Q6. Capability advertisement *is* a reliable signal once our iframe handshake is correct

- **2026-05-27 update**: an earlier draft of this doc claimed Claude
  Desktop "advertises but doesn't render" (frame-ancestors CSP block)
  and the same of Claude Cowork (`anthropics/claude-ai-mcp#236`).
  `spike/sep1865-verify` ran our iframe against ext-apps `basic-host`
  and surfaced three real bugs in our own `PlotlyTemplate.html` (Q1,
  Q2, and the `ui/notifications/size-changed` omission). Once those
  three were fixed (`38d4dc2` / `46e20f0` / `01d74e3`, cherry-picked
  to main before v1.5), the same hosts that "didn't render" *did*
  render Plotly correctly:
  - Claude Desktop Chat: advertises ✅, renders ✅
  - Claude Cowork: advertises ✅, renders ✅
  - VS Code Chat: advertises ✅, renders ✅
  - basic-host: advertises ❌ (intentional), renders ✅
- **Implication**: the v1.2 capability-based gate is sound *for the
  advertise/render direction*. v1.5 keeps it unchanged — no
  `KnownIframeRenderingHosts` whitelist is added.
- **Remaining mismatch (basic-host class — don't advertise but do
  render)**: acknowledged but left untreated in v1.5. Falls through to
  TextOnly, which produces the honest analytical summary instead of
  an iframe. If a real user ends up running an unadvertised
  iframe-capable host, a small whitelist can be added in v1.5.1+; the
  hook point is still `ChartHostSupport.Resolve`.
- **Lesson learned**: an "advertise-but-don't-render" diagnosis is
  cheaper to attribute to a real host CSP / behavior bug than it is
  to verify against our own iframe code. Always run against ext-apps
  `basic-host` (the reference) before concluding the host is broken.

### Q7. Codex's `enable_mcp_apps` flag is internal, not protocol

- **Observation**: 2026-05-26.
- Even with `[features] enable_mcp_apps = true` set in
  `~/.codex/config.toml`, Codex does NOT advertise
  `io.modelcontextprotocol/ui` on MCP `initialize`. The flag and the
  capability advertisement are independent.
- Codex CLI/TUI is structurally incapable of iframe rendering anyway,
  so TextOnly is the correct classification regardless. Codex Desktop
  is gated behind a further `renderMcpApps` flag and is not yet a
  verified Sep1865 host.
- Documented in (`openai/codex#21019`).

### Q8. `hostContext.theme` propagation is host-by-host and partial

- **Discovered**: 2026-05-27 (v1.5 post-ship), VS Code Copilot Chat
  empirical: dark host, white chart card. Same pattern likely on other
  dark-mode hosts.
- **Symptom**: even when the host is in dark mode, the chart card
  inside our iframe renders with a light palette (white paper, dark
  text, light grid lines on dark surroundings) — visually breaks
  identification.
- **Root cause** (two-part):
  1. The host doesn't send `hostContext.theme` on `ui/initialize` and
     doesn't emit `ui/notifications/host-context-changed` when the
     theme flips. The SEP-1865 schema (`spec.types.ts:351`) marks
     `theme` as optional, so no host is *required* to send it.
  2. The iframe's own `prefers-color-scheme` does *not* reliably
     follow the parent. A `:root { color-scheme: light dark; }`
     declaration alone is not enough — sandboxed iframes in
     `basic-host`-class proxies, and several production hosts, see
     `prefers-color-scheme: light` regardless of the parent's actual
     scheme.
- **Reference behavior**: ext-apps `basic-host` *does* send
  `hostContext.theme = getTheme()` from its `prefers-color-scheme`
  detection on the initial `hostContext`, and emits
  `sendHostContextChange({ theme })` on toggle
  (`examples/basic-host/src/implementation.ts:292, 306`). So a
  reference-faithful host should work.
- **v1.5.1 mitigation** (three additive fixes):
  1. **Transparent layout backgrounds.** Every builder now sets
     `paper_bgcolor: "rgba(0,0,0,0)"` / `plot_bgcolor:
     "rgba(0,0,0,0)"`; `PlotlyTemplate.applyTheme()` also defaults
     transparent; the iframe `<body>` background is `transparent`.
     The host card's background shows through regardless of theme
     signal, so even on a host that omits `hostContext.theme` the
     chart visually integrates with the panel.
  2. **Tool-side `theme` param.** `ls_get_chart` /
     `ls_get_overseas_chart` / `ls_add_indicator` / `ls_reframe_chart`
     accept `theme = "auto" | "light" | "dark"`. When set, the value
     is embedded as `layout._themeHint` and overrides
     `hostContext.theme` / `prefers-color-scheme` in
     `applyTheme()`. Persists on the dataset, so follow-up calls
     inherit it.
  3. **`[theme]` diagnostic log.** `applyTheme()` prints the
     resolved theme + raw signals on every render, enabling
     per-host Track A inspection in dev tools without recompiling.
- **Host empirical matrix** (Track A, partial — 2026-05-27 v1.5.1 dev build):

  | Host | Sends `hostContext.theme`? | iframe `prefers-color-scheme` follows parent? | Evidence |
  |---|---|---|---|
  | ext-apps `basic-host` | ✅ (verified in source) | n/a (basic-host classifies as TextOnly on our gate; if forced, host's own `getTheme()` covers it) | source read |
  | **VS Code Copilot Chat** | **✅ yes** — sends `theme=dark` on dark host | **❌ no** — `matchMedia("(prefers-color-scheme: dark)")` returns `false` even on dark parent | 2026-05-27 dev tools `[theme]` log: `resolved=dark hostContext.theme=dark matchMedia dark=false spec hint=(none)` |
  | Claude Desktop Chat | unknown | unknown | TBD |
  | Claude Cowork | unknown | unknown | TBD |
  | Cursor 2.6+ / ChatGPT / Goose / Postman / MCPJam | unknown | unknown | TBD |

  **Surprise**: the original v1.5.0 "white chart card on VS Code Copilot
  Chat dark" diagnosis assumed the host *wasn't* sending
  `hostContext.theme`. The 2026-05-27 empirical *refutes* that —
  VS Code Copilot Chat does send `theme=dark` correctly. The actual root
  cause of the original symptom remains uncertain (likely host update
  added the signal between v1.5.0 ship and v1.5.1 measurement, or a
  `host-context-changed` timing window we missed). Either way, the
  v1.5.1 transparent default works as a safety net for *both*
  signal-absent and signal-present cases.

  **Confirmed** for iframe `prefers-color-scheme` unreliability: even
  when the parent is dark, the iframe's `matchMedia` query returns
  `false`. So the SEP-1865 `hostContext.theme` signal is the *only*
  reliable path; relying on the iframe's own media query is broken.
- **Future work**: 3-state UI toggle button (auto / light / dark) on
  the chart card with best-effort `localStorage` persistence remains a
  candidate if tool-side `theme` proves insufficient. Sandbox iframes
  with `origin: null` will silently fail `localStorage`, which is
  expected (`try { ... } catch {}` — non-fatal).

---

## 4. The "self-synthesis" anti-pattern (model behavior, not protocol)

Separate from the protocol quirks above, the v1.5 design session
surfaced a *model-behavior* gotcha worth recording here for posterity.

- **Discovered**: 2026-05-26 Codex empirical test.
- **Symptom**: even when the server correctly classifies a host as
  TextOnly and strips `structuredContent.chart`, the model can:
  1. Narrate "차트를 그렸습니다" / "chart rendered" as if a chart
     was visible (it wasn't).
  2. Re-fetch with `output_mode=export` to get raw OHLCV, then
     write Python (`render_*.py`) to synthesize a PNG chart with
     model-computed MA values that disagree with the server's
     authoritative `summary.moving_averages`.
- **Why dangerous**: the resulting PNG looks plausible but its
  indicators (MA / RSI / Bollinger) differ from the server's values
  shown in the same response. User sees a chart and trusts it.
- **v1.5 mitigation**: ServerInstructions explicitly forbids both the
  false narration and the self-synthesis fallback — *regardless of
  `render_status`*, since the same pattern recurs on Cowork even when
  the iframe renders fine (e.g. height-customization requests routed
  through `mcp__visualize__show_widget` instead of being identified as
  a host panel constraint). The response carries `_meta.render_status`
  so the model has an objective signal of what the host did; an
  `output_mode=export` response additionally carries
  `_meta.do_not_render` guard.
- **Captured in**: [[chart_self_synthesis_antipattern]] memory.

This is not a SEP-1865 issue per se but interacts: even a perfect
SEP-1865 implementation can be undermined by model behavior, which is
why the v1.5 spec body lands on *narration honesty* rather than *render
path expansion*.

---

## 5. Verification procedure — ext-apps basic-host

Reference setup for empirically validating SEP-1865 host integration.
Use this whenever adding a host to `KnownIframeRenderingHosts` or
investigating an iframe rendering regression.

### Prerequisites

- Node.js + npm for basic-host
- .NET 8 SDK + LS API keys (`LS_APPKEY`, `LS_APPSECRETKEY`) — see
  `E:\MCP_E2E\.env.local`
- Clone `modelcontextprotocol/ext-apps` somewhere local
  (e.g. `D:\Codes\ext-apps`)

### Stand up the test environment

1. **`main` after v1.5**: the three PlotlyTemplate.html fixes from
   Q1 / Q2 / size-changed (`38d4dc2` / `46e20f0` / `01d74e3`) are on
   `main`. The `spike/sep1865-verify` branch additionally carries an
   HTTP transport scaffold and inline diagnostics that haven't been
   promoted; check that branch out only if you need HTTP-mode testing
   against `basic-host`.

2. **Start our MCP server in HTTP mode**:
   ```powershell
   # Load LS creds
   Get-Content E:\MCP_E2E\.env.local | ForEach-Object {
     $line = $_.Trim()
     if (-not $line -or $line.StartsWith('#')) { return }
     $eq = $line.IndexOf('=')
     if ($eq -lt 1) { return }
     Set-Item -Path "Env:$($line.Substring(0,$eq).Trim())" -Value `
       $line.Substring($eq+1).Trim().Trim('"',"'")
   }

   dotnet run --project src\RedoxNet.Mcp.LsOpenApi --no-build -- --http
   # → "MCP HTTP server listening on http://localhost:3001/mcp"
   ```

3. **Start basic-host** in a separate terminal:
   ```powershell
   cd D:\Codes\ext-apps\examples\basic-host
   # If 8080 is squatted, override only HOST_PORT (Q5):
   $env:HOST_PORT = "8090"
   $env:SERVERS = '["http://localhost:3001/mcp"]'
   npm run start
   # → "Host server: http://localhost:8090"
   # → "Sandbox server: http://localhost:8081"
   ```

4. **Open browser** to `http://localhost:8090`.

### Run the smoke test

1. Server dropdown → `mcp-lsopenapi` (auto-selected if only one)
2. Tool dropdown → `ls_get_chart`
3. Input JSON:
   ```json
   {
     "shcode": "005930",
     "period_type": "day",
     "count": 120,
     "output_mode": "display"
   }
   ```
   (`output_mode=display` is currently required — the default `analyze`
   doesn't emit `structuredContent.chart`. v1.5 may revisit defaults
   for hosts that resolve to Sep1865.)
4. Click **Call Tool**.

### Expected results

- Inline iframe renders a Samsung Electronics daily candlestick chart
  with volume subplot and high/low markers.
- Browser console shows the full handshake:
  ```
  [HOST] Calling tool ls_get_chart with input { ... output_mode: 'display' }
  [HOST] Reading UI resource: ui://lsopenapi/plotly
  [HOST] Loading sandbox proxy... (CSP: {resourceDomains:["https://cdn.plot.ly"]})
  [HOST] Sandbox proxy loaded
  [HOST] Sending UI resource HTML to MCP App
  [HOST] Waiting for MCP App to initialize...
  [HOST] MCP App initialized                       ← handshake OK
  [HOST] Sending tool call input to MCP App: ...
  [HOST] Sending tool call result to MCP App: ...
  ```
- Server-side diagnostics (spike branch only):
  ```
  [spike] tools/list: clientInfo=MCP Apps Host/1.0.0 → mode=Sep1865
  [spike] tools/call ls_get_chart: ... structuredContent=object(keys=chart)
  ```

### Host integration acceptance checklist

When validating a new host (or re-validating an existing one after a
PlotlyTemplate change), the full chain that should succeed:

1. `tools/list` response includes `_meta.ui` on chart-emitting tools
   (server-side log confirms `mode=Sep1865`)
2. `tools/call` response includes `structuredContent.chart` and
   `_meta.render_status="delivered"`
3. Host fetches `ui://lsopenapi/plotly` via `resources/read`
4. Host's iframe mounts the HTML successfully (no CSP / sandbox errors
   in console)
5. AppBridge handshake completes (`ui/initialize` → response →
   `ui/notifications/initialized` → `ui/notifications/size-changed`)
6. Plotly chart visually rendered with **server-computed** traces, at
   a sensible height (not 0px or default 240px)
7. Host does **not** attempt self-synthesis fallback when asked to
   customize the rendered chart (§4 anti-pattern)

basic-host satisfied 1-6 in the 2026-05-27 session; Claude Desktop
Chat / Claude Cowork / VS Code Chat satisfied 1-6 after the
PlotlyTemplate 3-fix landed on main. (#7 is exercised by v1.5
ServerInstructions when the model is asked to customize a delivered
chart — see `SPEC-v1.5.md` §7 for the E2E scenarios.)

---

## 6. Session history

- **2026-05-22**: v1.2 ships MCP Apps capability negotiation
  (`SPEC-v1.2-mcp-apps-capability.md`). Verified against AssistStudio
  (StructuredContent path) and Claude Desktop (Sep1865 protocol
  verified line-by-line in `mcp.log`, but inline iframe doesn't render
  — frame-ancestors CSP, Q6).
- **2026-05-26 morning**: v1.5 spec drafted with three slices (candle
  cache, saved screener, chart payload host adaptation).
- **2026-05-26 afternoon**: research agent investigation pushed back on
  the "many hosts render SEP-1865 today" optimism. Cursor 2.6+ / VS Code
  / ChatGPT reported as Sep1865 in changelogs but *unverified by us*.
  Anthropic `visualize` MCP discovered to be 1P-only.
- **2026-05-26 evening**: Codex empirical test exposes the
  self-synthesis anti-pattern (§4). v1.5 design pivots to Option F+
  fidelity-first (`SPEC-v1.5.md`).
- **2026-05-26 night**: spike `spike/sep1865-verify` opened. HTTP
  transport added (`WithHttpTransport`, Q4 hadn't surfaced yet).
  ext-apps `basic-host` stood up locally.
- **2026-05-27 morning**: diagnostic instrumentation in
  `ChartHostSupport.Resolve` and `PlotlyTemplate.html` peels back the
  layers: Stateless=true caveat (Q4), capability-not-advertised pattern
  (Q3), postMessage stringify bug (Q1), missing appInfo bug (Q2),
  missing `ui/notifications/size-changed` notification (iframe
  collapses to 0px). First end-to-end successful render of our Plotly
  chart in `basic-host`.
- **2026-05-27 afternoon**: PlotlyTemplate 3-fix cherry-picked to
  `main` (`38d4dc2` / `46e20f0` / `01d74e3`). Re-tested against Claude
  Desktop Chat, Claude Cowork, VS Code Chat — *all three render
  correctly*. The earlier "advertise-but-don't-render" diagnosis was
  attributed to host CSP / behavior bugs; it was actually our own
  PlotlyTemplate. `KnownIframeRenderingHosts` whitelist plan
  (originally Sub-change 1 of `SPEC-v1.5.md` draft) is *dropped* —
  capability advertisement is reliable. v1.5 narrows to the four
  narration-honesty sub-changes (Q6).

---

## 7. Open follow-ups

- **PlotlyTemplate 3-fix on main**: Q1 + Q2 + size-changed fixes
  shipped as `38d4dc2` / `46e20f0` / `01d74e3` before v1.5 (the chat
  panel height bump in `01d74e3` keeps the default render legible
  inside the smaller Cowork chat panel). Nothing pending here.
- **v1.5 implementation** (per `SPEC-v1.5.md`) ships `_meta.render_status`
  + `_meta.do_not_render` + updated ServerInstructions. The earlier
  draft's `KnownIframeRenderingHosts` whitelist is *not* shipped —
  the PlotlyTemplate 3-fix made it unnecessary (Q6).
- **HTTP transport** stays on the spike branch for now. If it gets
  promoted to a maintained feature (remote / web deployment use cases),
  the stdio vs HTTP code paths should be unified via a shared
  service-config extension method, and Q4 documented inline.
- **Cursor 2.6+ / ChatGPT / Goose / Postman**: each still needs a
  direct empirical run of the verification procedure (§5) — the v1.2
  capability gate should hold given the Claude / VS Code / basic-host
  class verifications, but secondary-source changelogs are not
  authoritative.
- **basic-host class — capability-not-advertised but iframe-capable**:
  v1.5 leaves these as TextOnly (honest analytical summary). If a
  real user runs such a host, a small `KnownIframeRenderingHosts`
  override in `ChartHostSupport.Resolve` can be added without
  reshaping the v1.2 contract.
- **render_hints standardization watch**: if multiple servers end up
  needing result-side `_meta` patterns, propose a small SEP companion
  ([[render_hints_standardization]] memory).

## 8. References

- Spec: [`SPEC-v1.2-mcp-apps-capability.md`](./SPEC-v1.2-mcp-apps-capability.md),
  [`SPEC-v1.5.md`](./SPEC-v1.5.md)
- Code: [`src/RedoxNet.Mcp.LsOpenApi/Apps/`](../src/RedoxNet.Mcp.LsOpenApi/Apps/) —
  `UiResources.cs`, `ChartRenderingMode.cs`,
  `McpAppsCapability.cs`, `PlotlyTemplate.html`
- Upstream: [modelcontextprotocol/ext-apps](https://github.com/modelcontextprotocol/ext-apps) —
  SEP-1865 reference spec, types, basic-host, basic-server-*
- SEP: <https://modelcontextprotocol.io/community/seps/1865-mcp-apps-interactive-user-interfaces-for-mcp>
- Memory: `next_mcp_apps_capability`, `next_assiststudio_plotly`,
  `chart_self_synthesis_antipattern`, `render_hints_standardization`
- Spike branch: `spike/sep1865-verify` (current head at session end:
  `a1918bc`)
