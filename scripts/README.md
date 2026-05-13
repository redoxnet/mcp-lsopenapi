# Scripts

Local helper scripts. These run on the developer's machine; they are **not** part of the shipping package.

## `live-smoke.ps1`

End-to-end verification against the live LS증권 OpenAPI. Use this when you want to confirm that real credentials reach LS and a TR comes back successfully — no mocks, no stubs.

### Setup

In the PowerShell session you will run the script from, set the LS credentials:

```powershell
$env:LS_APPKEY       = 'PSxxxx...'
$env:LS_APPSECRETKEY = 'PSxxxx...'
$env:LS_MARKET       = 'virtual'   # or 'real'
```

The values stay in your shell session. The script reads them via `$env:` and never prints them in plaintext — only `****XXXX` (last 4 chars).

### Run

```powershell
pwsh scripts/live-smoke.ps1
# default: ls_get_quote 005930 (Samsung Electronics)

pwsh scripts/live-smoke.ps1 -Shcode 000660 -ToolName ls_get_stock_info
# fuller verification: hits t1102 and parses fundamentals
```

### What it does

1. Builds the MCP server in Release.
2. Launches it as a child process over stdio.
3. Runs `initialize` + `tools/call <ToolName>`.
4. Pretty-prints a masked summary of the tool output. The raw appkey / secret / access token are never surfaced.
5. On failure, shows the last 8 lines of the server's stderr for diagnostics.

### What success looks like

- Build succeeds.
- `initialize` returns `serverInfo`.
- `tools/call ls_get_quote` returns a parsed JSON payload with `price`, `name`, `order_book`, etc.
- Token cache file is created at `%LOCALAPPDATA%\RedoxNet\LsOpenApi\token.db` (subsequent runs should reuse it until the 5-min pre-expiry window).

### What failure usually means

| Symptom | Likely cause |
| --- | --- |
| `LsAuthException` mentioning 401/403 | Wrong appkey/secret, or `LS_MARKET` doesn't match the account type |
| `LsTrException` with 404 | Endpoint path mismatch — re-check `TrCatalog.json` against the testbed |
| `LsTrException` with 429 | Rate limit hit; very rare in smoke tests |
| Tool returns `{"error": "..."}` with `rsp_cd != "00000"` | LS-side business error (e.g. invalid shcode, no permission for this TR) |
