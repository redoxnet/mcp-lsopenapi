#requires -Version 7.0
<#
.SYNOPSIS
    v0.7 A1 spike — live-verify t3320 (FNG_요약) input format and ETF behaviour
    before locking the krx_sector enrichment strategy.

.DESCRIPTION
    Runs four ls_call_tr invocations through the MCP server:
      1. t3320 / gicode="005930"   — KOSPI 일반 (samsung) with 6-char shcode
      2. t3320 / gicode="A005930"  — same stock, 7-char "A"+shcode
      3. t3320 / gicode="000660"   — KOSPI 일반 (SK하이닉스)
      4. t3320 / gicode="069500"   — ETF (KODEX 200) — likely fails or partial

    Each result prints whether t3320OutBlock was returned and which
    industry name (upgubunnm) the field carries. The output guides the
    SPEC-v0.7 §4.5 input-format pin (6 vs 7 char) and the ETF skip
    decision for the enrichment path.

    Reads credentials from environment variables:
        LS_APPKEY       (required)
        LS_APPSECRETKEY (required)
        LS_MARKET       (optional; default 'virtual')

.EXAMPLE
    $env:LS_APPKEY = 'PSxxxx...'
    $env:LS_APPSECRETKEY = 'PSxxxx...'
    $env:LS_MARKET = 'virtual'
    pwsh scripts/verify-t3320.ps1
#>

[CmdletBinding()]
param(
    [string]$Framework = 'net8.0'
)

$ErrorActionPreference = 'Stop'

if (-not $env:LS_APPKEY -or -not $env:LS_APPSECRETKEY) {
    Write-Error @'
LS_APPKEY and LS_APPSECRETKEY environment variables must be set.

In PowerShell:
    $env:LS_APPKEY = 'PSxxxx...'
    $env:LS_APPSECRETKEY = 'PSxxxx...'
    $env:LS_MARKET = 'virtual'   # or 'real'
'@
}

$market = if ($env:LS_MARKET) { $env:LS_MARKET } else { 'virtual' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src/RedoxNet.Mcp.LsOpenApi/RedoxNet.Mcp.LsOpenApi.csproj'

Write-Host ""
Write-Host "==== t3320 (FNG_요약) live verification ====" -ForegroundColor Cyan
Write-Host ("  market    : {0}" -f $market)
Write-Host ("  framework : {0}" -f $Framework)
Write-Host ""

Write-Host "[1/3] Building MCP server (Debug — Release Core.dll may be locked by running MCP clients)..." -ForegroundColor DarkGray
& dotnet build $projectPath -c Debug -f $Framework --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error @'
Build failed. Common cause: a running MCP client (Claude Desktop, AssistStudio, etc.) holds a lock on the build output. If you see MSB3027 above, stop the MCP server hosts and rerun, or rebuild manually:
    dotnet build src/RedoxNet.Mcp.LsOpenApi/RedoxNet.Mcp.LsOpenApi.csproj -c Debug
'@
}

Write-Host "[2/3] Launching MCP server over stdio..." -ForegroundColor DarkGray
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
$psi.Arguments = "run --no-build --project `"$projectPath`" --framework $Framework -c Debug"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.WorkingDirectory = $repoRoot
$psi.EnvironmentVariables['LS_APPKEY'] = $env:LS_APPKEY
$psi.EnvironmentVariables['LS_APPSECRETKEY'] = $env:LS_APPSECRETKEY
$psi.EnvironmentVariables['LS_MARKET'] = $market

$proc = [System.Diagnostics.Process]::Start($psi)
$nextId = 1

function SendJsonRpc($obj) {
    $line = ($obj | ConvertTo-Json -Depth 16 -Compress)
    $proc.StandardInput.WriteLine($line)
    $proc.StandardInput.Flush()
}

function ReceiveJsonRpc {
    while (-not $proc.HasExited) {
        $line = $proc.StandardOutput.ReadLine()
        if (-not $line) { Start-Sleep -Milliseconds 25; continue }
        try { return $line | ConvertFrom-Json -Depth 32 } catch { continue }
    }
    return $null
}

function CallTr($code, $body) {
    $script:nextId++
    # MCP C# SDK 1.2 generates ambiguous schema for JsonElement parameters — some
    # client/server combos require the object to be JSON-stringified. CallTrTool.cs
    # accepts both forms; we send the stringified form for maximum compatibility.
    $bodyJson = ($body | ConvertTo-Json -Depth 8 -Compress)
    SendJsonRpc @{
        jsonrpc = '2.0'
        id = $script:nextId
        method = 'tools/call'
        params = @{
            name = 'ls_call_tr'
            arguments = @{ tr_cd = $code; in_block = $bodyJson }
        }
    }
    return ReceiveJsonRpc
}

try {
    SendJsonRpc @{
        jsonrpc = '2.0'; id = $nextId; method = 'initialize'
        params = @{ protocolVersion = '2024-11-05'; capabilities = @{}; clientInfo = @{ name = 'verify-t3320'; version = '0.0' } }
    }
    $init = ReceiveJsonRpc
    if (-not $init.result) { throw ("initialize failed: {0}" -f ($init | ConvertTo-Json -Depth 4)) }
    SendJsonRpc @{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} }

    Write-Host "[3/3] Calling t3320 four times..." -ForegroundColor DarkGray
    Write-Host ""

    # ls_call_tr expects only the inner block — server wraps it as { "t3320InBlock": ... }.
    $cases = @(
        @{ Label = '1. KOSPI 6-char (005930 삼성전자)';   Body = @{ gicode = '005930' } },
        @{ Label = '2. KOSPI "A"+6 (A005930 삼성전자)';   Body = @{ gicode = 'A005930' } },
        @{ Label = '3. KOSPI 6-char (000660 SK하이닉스)'; Body = @{ gicode = '000660' } },
        @{ Label = '4. ETF 6-char (069500 KODEX 200)';   Body = @{ gicode = '069500' } }
    )

    foreach ($c in $cases) {
        Write-Host ("--- {0}" -f $c.Label) -ForegroundColor Yellow
        $rsp = CallTr 't3320' $c.Body
        if ($rsp.error) {
            Write-Host ("    JSON-RPC error: {0}" -f ($rsp.error | ConvertTo-Json -Depth 4 -Compress)) -ForegroundColor Red
            continue
        }
        $raw = $rsp.result.content[0].text
        try {
            $parsed = $raw | ConvertFrom-Json -Depth 32
            $rspCd = $parsed.rsp_cd
            $rspMsg = $parsed.rsp_msg
            Write-Host ("    rsp_cd  : {0}" -f $rspCd) -ForegroundColor $(if ($rspCd -eq '00000') { 'Green' } else { 'Red' })
            Write-Host ("    rsp_msg : {0}" -f $rspMsg)
            $body = $parsed.body
            if ($body -and $body.t3320OutBlock) {
                $ob = $body.t3320OutBlock
                Write-Host ("    company : {0}" -f $ob.company) -ForegroundColor Green
                Write-Host ("    upgubunnm: {0}" -f $ob.upgubunnm) -ForegroundColor Green
                Write-Host ("    sijangcd / marketnm: {0} / {1}" -f $ob.sijangcd, $ob.marketnm)
                Write-Host ("    sigavalue: {0} 억원, foreignratio: {1}%" -f $ob.sigavalue, $ob.foreignratio)
                if ($body.t3320OutBlock1) {
                    Write-Host ("    fundamentals: PER={0} PBR={1} ROE={2} BPS={3}" -f $body.t3320OutBlock1.per, $body.t3320OutBlock1.pbr, $body.t3320OutBlock1.roe, $body.t3320OutBlock1.bps)
                    Write-Host ("    OutBlock1.gicode echo: {0}" -f $body.t3320OutBlock1.gicode)
                }
            } else {
                Write-Host "    (no t3320OutBlock in response body — likely TR-level rejection or ETF)" -ForegroundColor DarkYellow
                $preview = if ($raw.Length -gt 300) { $raw.Substring(0, 300) + '...' } else { $raw }
                Write-Host ("    raw: {0}" -f $preview) -ForegroundColor DarkGray
            }
        }
        catch {
            Write-Host ("    (non-JSON response — preview):") -ForegroundColor DarkYellow
            $preview = if ($raw.Length -gt 300) { $raw.Substring(0, 300) + '...' } else { $raw }
            Write-Host $preview -ForegroundColor DarkGray
        }
        # Respect the 1 TPS limit between calls
        Start-Sleep -Milliseconds 1100
        Write-Host ""
    }

    Write-Host "==== verification done ====" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Decision points to pin in SPEC §4.5:" -ForegroundColor Cyan
    Write-Host "  - input format: 6-char vs 'A'+6 — does case 1 vs case 2 differ?"
    Write-Host "  - ETF behaviour (case 4): rsp_cd != 00000 → enrichment skips ETFs"
    Write-Host "  - upgubunnm spelling (e.g. '반도체 및 관련장비' vs '반도체') for filter UX"
}
finally {
    if (-not $proc.HasExited) {
        $proc.StandardInput.Close()
        if (-not $proc.WaitForExit(3000)) { $proc.Kill() }
    }
    $stderr = $proc.StandardError.ReadToEnd()
    if ($stderr) {
        Write-Host ""
        Write-Host "Server stderr (full):" -ForegroundColor DarkGray
        Write-Host $stderr -ForegroundColor DarkGray
    }
}
