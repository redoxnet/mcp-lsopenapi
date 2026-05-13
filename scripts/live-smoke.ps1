#requires -Version 7.0
<#
.SYNOPSIS
    End-to-end live smoke test for RedoxNet.Mcp.LsOpenApi against the
    real LS증권 OpenAPI.

.DESCRIPTION
    Launches the MCP server as a child process (stdio), runs the standard
    initialize handshake, then calls a single read-only tool against LS.
    Prints a masked summary so you can verify token issuance, header
    plumbing, and at least one TR end-to-end against live credentials —
    without ever surfacing the appkey or access token in plaintext.

    Reads credentials from environment variables:
        LS_APPKEY       (required)
        LS_APPSECRETKEY (required)
        LS_MARKET       (optional; default 'virtual')

    The script never logs the raw secrets. Last 4 characters only.

.PARAMETER Shcode
    6-digit Korean stock code to quote. Default 005930 (Samsung Electronics).

.PARAMETER Framework
    Target framework for dotnet run. Default net8.0.

.PARAMETER ToolName
    Tool to invoke. Default ls_get_quote. Supported tools:
    - ls_get_quote        (uses -Shcode)
    - ls_get_stock_info   (uses -Shcode)
    - ls_get_multi_quote  (uses -Shcodes; default '005930,000660,078020')
    - ls_get_chart        (uses -Shcode + -PeriodType + -Count)

.PARAMETER Shcodes
    Comma-separated list of 6-digit codes for ls_get_multi_quote.
    Ignored by single-stock tools.

.PARAMETER PeriodType
    Period type for ls_get_chart. Ignored by other tools.

.PARAMETER Count
    Candle count for ls_get_chart. Ignored by other tools.

.EXAMPLE
    # In a PowerShell session where LS_APPKEY / LS_APPSECRETKEY are set:
    pwsh scripts/live-smoke.ps1

.EXAMPLE
    pwsh scripts/live-smoke.ps1 -Shcode 000660 -ToolName ls_get_stock_info

.EXAMPLE
    pwsh scripts/live-smoke.ps1 -ToolName ls_get_multi_quote -Shcodes '005930,000660,078020'

.EXAMPLE
    pwsh scripts/live-smoke.ps1 -ToolName ls_get_chart -PeriodType day -Count 5
#>

[CmdletBinding()]
param(
    [string]$Shcode = '005930',
    [string]$Shcodes = '005930,000660,078020',
    [string]$PeriodType = 'day',
    [int]$Count = 5,
    [string]$Framework = 'net8.0',
    [string]$ToolName = 'ls_get_quote'
)

$ErrorActionPreference = 'Stop'

function Mask([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return '(empty)' }
    if ($s.Length -le 4) { return '****' }
    return '****' + $s.Substring($s.Length - 4)
}

if (-not $env:LS_APPKEY -or -not $env:LS_APPSECRETKEY) {
    Write-Error @'
LS_APPKEY and LS_APPSECRETKEY environment variables must be set in this session.

In PowerShell:
    $env:LS_APPKEY = 'PSxxxx...'
    $env:LS_APPSECRETKEY = 'PSxxxx...'
    $env:LS_MARKET = 'virtual'   # or 'real'

Then re-run this script. The values stay in your session only.
'@
}

$market = if ($env:LS_MARKET) { $env:LS_MARKET } else { 'virtual' }
Write-Host ""
Write-Host "==== RedoxNet.Mcp.LsOpenApi live smoke ====" -ForegroundColor Cyan
Write-Host ("  appkey       : {0}" -f (Mask $env:LS_APPKEY))
Write-Host ("  appsecretkey : {0}" -f (Mask $env:LS_APPSECRETKEY))
Write-Host ("  market       : {0}" -f $market)
Write-Host ("  tool         : {0}" -f $ToolName)
switch ($ToolName) {
    'ls_get_multi_quote' { Write-Host ("  shcodes      : {0}" -f $Shcodes) }
    'ls_get_chart'       { Write-Host ("  shcode       : {0}" -f $Shcode); Write-Host ("  period_type  : {0} (count={1})" -f $PeriodType, $Count) }
    default              { Write-Host ("  shcode       : {0}" -f $Shcode) }
}
Write-Host ("  framework    : {0}" -f $Framework)
Write-Host ""

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src/RedoxNet.Mcp.LsOpenApi/RedoxNet.Mcp.LsOpenApi.csproj'

if (-not (Test-Path $projectPath)) {
    Write-Error "Project not found at $projectPath. Run from the repo root."
}

# Pre-build so the dotnet run startup doesn't muddle our stdio handshake.
Write-Host "[1/4] Building MCP server (release)..." -ForegroundColor DarkGray
& dotnet build $projectPath -c Release -f $Framework --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed."
}

Write-Host "[2/4] Launching MCP server over stdio..." -ForegroundColor DarkGray

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = 'dotnet'
$psi.Arguments = "run --no-build --project `"$projectPath`" --framework $Framework -c Release"
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.WorkingDirectory = $repoRoot
$psi.EnvironmentVariables['LS_APPKEY'] = $env:LS_APPKEY
$psi.EnvironmentVariables['LS_APPSECRETKEY'] = $env:LS_APPSECRETKEY
$psi.EnvironmentVariables['LS_MARKET'] = $market

$proc = [System.Diagnostics.Process]::Start($psi)

function SendJsonRpc($obj) {
    $line = ($obj | ConvertTo-Json -Depth 12 -Compress)
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

try {
    Write-Host "[3/4] initialize + tools/call $ToolName..." -ForegroundColor DarkGray

    SendJsonRpc @{
        jsonrpc = '2.0'
        id = 1
        method = 'initialize'
        params = @{
            protocolVersion = '2024-11-05'
            capabilities = @{}
            clientInfo = @{ name = 'live-smoke'; version = '0.0' }
        }
    }
    $init = ReceiveJsonRpc
    if (-not $init) { throw "No response to initialize." }
    if (-not $init.result) { throw ("initialize failed: {0}" -f ($init | ConvertTo-Json -Depth 4)) }
    Write-Host ("    server: {0} v{1}" -f $init.result.serverInfo.name, $init.result.serverInfo.version) -ForegroundColor Green

    SendJsonRpc @{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} }

    $toolArgs = switch ($ToolName) {
        'ls_get_multi_quote' {
            $codes = $Shcodes.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
            @{ shcodes = @($codes) }
        }
        'ls_get_chart' {
            @{ shcode = $Shcode; period_type = $PeriodType; count = $Count }
        }
        default {
            @{ shcode = $Shcode }
        }
    }

    SendJsonRpc @{
        jsonrpc = '2.0'
        id = 2
        method = 'tools/call'
        params = @{
            name = $ToolName
            arguments = $toolArgs
        }
    }
    $callResponse = ReceiveJsonRpc
    if (-not $callResponse) { throw "No response to tools/call." }
    if ($callResponse.error) {
        throw ("tools/call error: {0}" -f ($callResponse.error | ConvertTo-Json -Depth 4))
    }

    Write-Host ""
    Write-Host "[4/4] Tool output:" -ForegroundColor DarkGray
    Write-Host ""

    $rawText = $callResponse.result.content[0].text
    try {
        $parsed = $rawText | ConvertFrom-Json -Depth 32
        if ($parsed.error) {
            Write-Host ("    ❌ Tool returned error: {0}" -f $parsed.error) -ForegroundColor Red
            if ($parsed.details) {
                Write-Host ("       details: {0}" -f ($parsed.details | ConvertTo-Json -Depth 4 -Compress)) -ForegroundColor DarkRed
            }
            exit 2
        }

        switch ($ToolName) {
            'ls_get_quote' {
                Write-Host ("    ✅ {0} ({1})" -f $parsed.name, $parsed.shcode) -ForegroundColor Green
                Write-Host ("       price          : {0:N0}" -f $parsed.price)
                Write-Host ("       change         : {0} ({1:N2}%)" -f $parsed.change, $parsed.change_percent)
                Write-Host ("       open/high/low  : {0:N0} / {1:N0} / {2:N0}" -f $parsed.open, $parsed.high, $parsed.low)
                Write-Host ("       volume         : {0:N0}" -f $parsed.volume)
                Write-Host ("       best ask / bid : {0:N0} / {1:N0}" -f $parsed.order_book[0].ask, $parsed.order_book[0].bid)
            }
            'ls_get_stock_info' {
                Write-Host ("    ✅ {0} ({1}, {2})" -f $parsed.name, $parsed.shcode, $parsed.market) -ForegroundColor Green
                Write-Host ("       PER / PBR     : {0:N2} / {1:N2}" -f $parsed.fundamentals.per, $parsed.fundamentals.pbr)
                Write-Host ("       52w range     : {0:N0} ~ {1:N0}" -f $parsed.range.week52.low, $parsed.range.week52.high)
                Write-Host ("       market cap    : {0:N0} 억원" -f $parsed.listing.market_cap_in_100m_won)
            }
            'ls_get_multi_quote' {
                Write-Host ("    ✅ {0} quote(s) returned" -f $parsed.count) -ForegroundColor Green
                foreach ($q in $parsed.quotes) {
                    $pct = '{0,7:+0.00;-0.00; 0.00}' -f [double]$q.change_percent
                    Write-Host ("       {0,-20} {1,12:N0}  ({2}%)  best ask/bid {3,10:N0} / {4,10:N0}" -f $q.name, $q.price, $pct, $q.best_ask, $q.best_bid)
                }
            }
            'ls_get_chart' {
                Write-Host ("    ✅ {0} {1} candle(s) for {2}" -f $parsed.count, $parsed.period_type, $parsed.shcode) -ForegroundColor Green
                foreach ($c in $parsed.candles) {
                    Write-Host ("       {0,-12} O:{1,8:N0}  H:{2,8:N0}  L:{3,8:N0}  C:{4,8:N0}  V:{5,12:N0}" -f $c.date, $c.open, $c.high, $c.low, $c.close, $c.volume)
                }
                if ($parsed.context) {
                    $highDateRaw = $parsed.context.drawdown.period_high_date
                    $highDateDisplay = if ($highDateRaw -is [DateTime]) {
                        $highDateRaw.ToString('yyyy-MM-dd')
                    } else {
                        ([string]$highDateRaw).Split('T')[0]
                    }
                    Write-Host ("       drawdown      : {0:N2}% from {1:N0} on {2}" -f $parsed.context.drawdown.pct, $parsed.context.drawdown.period_high, $highDateDisplay)
                }
            }
            default {
                Write-Host ("    ✅ Tool returned (first 400 chars):") -ForegroundColor Green
                $preview = if ($rawText.Length -gt 400) { $rawText.Substring(0, 400) + '...' } else { $rawText }
                Write-Host $preview
            }
        }
    }
    catch {
        Write-Host "    (non-JSON tool output, preview only):" -ForegroundColor Yellow
        $preview = if ($rawText.Length -gt 400) { $rawText.Substring(0, 400) + '...' } else { $rawText }
        Write-Host $preview
    }

    Write-Host ""
    Write-Host "==== smoke passed ====" -ForegroundColor Green
}
finally {
    if (-not $proc.HasExited) {
        $proc.StandardInput.Close()
        if (-not $proc.WaitForExit(3000)) {
            $proc.Kill()
        }
    }
    # Surface server stderr tail for diagnostics
    $stderr = $proc.StandardError.ReadToEnd()
    if ($stderr) {
        $lines = $stderr -split "`n"
        $tail = if ($lines.Length -gt 8) { $lines[-8..-1] } else { $lines }
        Write-Host ""
        Write-Host "Server stderr (last 8 lines):" -ForegroundColor DarkGray
        foreach ($l in $tail) { Write-Host "  $l" -ForegroundColor DarkGray }
    }
}
