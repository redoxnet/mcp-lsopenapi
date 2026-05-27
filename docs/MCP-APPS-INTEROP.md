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
v1.5 plans to add `CallToolResult._meta.render_status` for narration
honesty — that's the *other* `_meta`.

**Implication**: a host that "supports SEP-1865" only commits to reading
the first. The second is private extension territory each server has to
sell to hosts individually (or eventually push for standardization via a
SEP companion proposal — see [[render_hints_standardization]] memory).

---

## 2. Empirical host matrix (snapshot as of 2026-05-27)

`clientInfo.name` as reported on `initialize`. Mode is what
`ChartHostSupport.Resolve` returns today (post-v1.5 will tighten — see
§3 Q6).

| Host | `clientInfo.name` | SEP-1865 capability advertised | iframe actually renders | Mode today | Verified by |
|---|---|---|---|---|---|
| **ext-apps `basic-host`** (reference) | `MCP Apps Host` | ❌ no | ✅ **yes** | TextOnly (would be Sep1865 once whitelist lands) | 2026-05-27 spike/sep1865-verify, end-to-end Plotly chart visible |
| **AssistStudio** (WinUI 3) | `AssistStudio` | ✅ yes (advertised, not consumed) | n/a — reads `structuredContent.chart` directly | StructuredContent | [[next_assiststudio_plotly]] |
| **Claude Desktop** | `claude-ai` | ✅ yes | ❌ silent fail (frame-ancestors CSP) | Sep1865 (will downgrade in v1.5) | [[next_mcp_apps_capability]] 2026-05-22 |
| **Claude Cowork 3P** (Bedrock/Vertex/Foundry inference) | `claude-ai` (same as Desktop) | ✅ yes | ❌ (`anthropics/claude-ai-mcp#236`) | Sep1865 (will downgrade in v1.5) | research agent 2026-05-26 |
| **Codex Desktop / TUI** | `codex-mcp-client` | ❌ no (`enable_mcp_apps=true` is a separate client flag) | ❌ — TUI structurally can't | TextOnly | 2026-05-26 empirical test, user self-report |
| **Claude Code CLI** | (TUI) | ❌ no | ❌ — TUI structurally can't | TextOnly | research agent + structural reasoning |
| **Cursor 2.6+, VS Code, ChatGPT, Goose, Postman, MCPJam** | unknown | ✅ per their changelogs | ⚠️ reported in changelogs, *not verified by us* | Sep1865 (v1.5 will downgrade until verified) | research agent (secondary sources) |

**Reading guide**: capability advertisement is a *claim*, not a *contract*.
Two of three hosts on our desk (Claude Desktop, Codex) that we tested
either advertise without rendering or don't advertise despite being able
in theory. v1.5's tightening (capability AND whitelist) is the response.

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
  client advertises) excluded basic-host. v1.5 introduces
  `KnownIframeRenderingHosts` — a `clientInfo.name` allowlist — to
  Sep1865-route hosts that don't advertise but are known to render.
- **First whitelist entry confirmed**: `"MCP Apps Host"`
  (basic-host's `clientInfo.name`).
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

### Q6. Capability advertisement is *not* a reliable signal of actual rendering

- **Pattern across hosts** (Q6 generalizes from Q3 + the host matrix):
  - Claude Desktop: advertises ✅, renders ❌ (frame-ancestors CSP)
  - Claude Cowork 3P: advertises ✅, renders ❌
  - basic-host: advertises ❌, renders ✅
- **Implication**: gating on capability advertisement *alone* is
  unsound in both directions. v1.5 `KnownIframeRenderingHosts`
  whitelist *plus* the existing capability check is the answer:
  - capability advertised AND host on whitelist → `Sep1865` (emit
    `_meta.ui`)
  - host on whitelist without capability → `Sep1865` (whitelist
    overrides)
  - capability advertised but not on whitelist → `TextOnly` (don't
    waste bandwidth on a payload that won't render)
- **Whitelist expansion procedure**: see [[SPEC-v1.5.md §Appendix D]]
  and §5 below.

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
  false narration and the self-synthesis fallback; response carries
  `_meta.render_status` so the model has an objective signal that no
  chart was delivered; `output_mode=export` response carries
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

1. **Spike branch** with HTTP transport + (currently) two
   PlotlyTemplate.html fixes from Q1/Q2 plus the `KnownIframeRenderingHosts`
   inline whitelist: `git checkout spike/sep1865-verify`.
   Once those fixes land on main and v1.5 ships the whitelist for real,
   this step becomes unnecessary on `main`.

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

### Whitelist-acceptance checklist (per `SPEC-v1.5.md` §Appendix D)

A host qualifies for `KnownIframeRenderingHosts` only after *all* of:

1. `tools/list` response includes `_meta.ui` on chart-emitting tools
   (server-side log confirms `mode=Sep1865`)
2. `tools/call` response includes `structuredContent.chart`
3. Host fetches `ui://lsopenapi/plotly` via `resources/read`
4. Host's iframe mounts the HTML successfully (no CSP / sandbox errors
   in console)
5. AppBridge handshake completes (`ui/initialize` → response →
   `ui/notifications/initialized`)
6. Plotly chart visually rendered with **server-computed** traces
7. Host does **not** attempt self-synthesis fallback when given chart
   spec (§4 anti-pattern)

basic-host satisfied 1-6 in the 2026-05-27 session. (#7 is unaffected
because basic-host receives the chart spec and renders it directly;
the anti-pattern only triggers when the model gets no chart and
improvises.)

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
  (Q3), postMessage stringify bug (Q1), missing appInfo bug (Q2).
  First end-to-end successful render of our Plotly chart in basic-host.
  SEP-1865 path empirically validated; `KnownIframeRenderingHosts`
  whitelist pattern justified.

---

## 7. Open follow-ups

- **Cherry-pick Q1 + Q2 fixes from `spike/sep1865-verify` to `main`**
  as a clean `fix(apps): ...` commit, independent of v1.5. These are
  real bugs that affect any future SEP-1865 host using the iframe
  path.
- **v1.5 implementation** (per `SPEC-v1.5.md`) lands the proper
  `KnownIframeRenderingHosts` whitelist with `"MCP Apps Host"` as the
  first verified entry.
- **HTTP transport** stays on the spike branch for now. If it gets
  promoted to a maintained feature (remote / web deployment use cases),
  the stdio vs HTTP code paths should be unified via a shared
  service-config extension method, and Q4 documented inline.
- **Cursor 2.6+ / VS Code / ChatGPT / Goose / Postman**: each needs an
  in-person run of the verification procedure (§5) before earning a
  `KnownIframeRenderingHosts` entry. Don't trust changelogs.
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
