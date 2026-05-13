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

## NuGet publishing scripts

Three publish scripts share `nuget-common.ps1` (the actual pack + push logic). Re-runs are safe — `--skip-duplicate` makes already-published versions a silent no-op.

| Script | Packages |
|---|---|
| `publish-core.ps1` | `RedoxNet.LsOpenApi.Core` only |
| `publish-mcp.ps1` | `RedoxNet.Mcp.LsOpenApi` only |
| `publish-nuget.ps1` | Both, in dependency order (Core first) |

### Setup

Set the API key once, in your user environment:

```powershell
[Environment]::SetEnvironmentVariable('NUGET_API_KEY_REDOXNET', 'oy2xxxx...', 'User')
```

Then open a fresh PowerShell session so `$env:NUGET_API_KEY_REDOXNET` is populated.

### Run

```powershell
# Combined release — most common
pwsh scripts/publish-nuget.ps1

# Per-package — when only one package version bumped
pwsh scripts/publish-core.ps1
pwsh scripts/publish-mcp.ps1

# Local pack only (inspect artifacts/, no push)
pwsh scripts/publish-nuget.ps1 -SkipPush

# One-off API key override (e.g. CI)
pwsh scripts/publish-nuget.ps1 -NuGetApiKey 'oy2xxxx...'
```

### Dependency order matters

`RedoxNet.Mcp.LsOpenApi` declares a NuGet dependency on `RedoxNet.LsOpenApi.Core`. If you bump both at the same time, **publish Core first** (or use `publish-nuget.ps1` which does it for you) so Core is indexed on nuget.org before consumers restore Mcp.

### Versioning

The package version comes from `<Version>` in each `.csproj`. The MSBuild `VerifyServerJsonVersion` target (defined in `RedoxNet.Mcp.LsOpenApi.csproj`) fails the pack if `.mcp/server.json` drifts from `<Version>`, so bump both atomically — typically also `TrCatalog.json`'s `version` field — before running these scripts.

### What success looks like

```
=== Pushing to nuget.org ===
  Using API key ****abcd
  Pushing RedoxNet.LsOpenApi.Core.0.1.0.nupkg ...
  Pushing RedoxNet.Mcp.LsOpenApi.0.1.0.nupkg ...
  All packages pushed.
```

### Notes

- The API key is read from `$env:NUGET_API_KEY_REDOXNET` (project-scoped). All three scripts mask it as `****<last-4>` in the log line.
- No code signing step — RedoxNet packages ship unsigned.
- `--skip-duplicate` means a repeated push of an already-published version is silently skipped (HTTP 409 is treated as success), so re-running after a partial failure is safe.

## Release commit conventions

`.github/workflows/release.yml` triggers on `git push` to `main` when the head commit message starts with one of:

| Commit message | Produces | Tag(s) |
|---|---|---|
| `Release v0.1.0` | one combined GitHub Release with both packages' notes joined | `v0.1.0` |
| `Core v0.1.1` | Core-only GitHub Release | `lsopenapi.core-v0.1.1` |
| `Mcp v0.2.0` | Mcp-only GitHub Release | `mcp.lsopenapi-v0.2.0` |
| `Core v0.1.1 Mcp v0.2.0` (same commit) | two separate Releases, one each | both of the above |

The release workflow does **not** push to NuGet — that stays manual via the publish scripts above. Push the package(s) to nuget.org AFTER the GitHub Release lands, so the tag and the published nupkg are consistent.
