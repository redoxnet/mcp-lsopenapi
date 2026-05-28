#requires -Version 7.0
<#
.SYNOPSIS
    One-off helper to upsert a virtual-mode account into portfolio.db.

.DESCRIPTION
    Spawns the MCP server with LS_MARKET=virtual (loaded from
    E:\MCP_E2E\.env.local by default) and invokes ls_account
    action="upsert" so the account lands under mode='virtual'. Used
    during v1.6 bring-up before the v1.6 ls_account_* tools have a
    real registered account to resolve against.

.PARAMETER AccountNumber
    Brokerage account number (LS issues 11 digits for virtual accounts).

.PARAMETER Nickname
    Human label for the account (e.g. "모의" or "paper").

.PARAMETER SetDefault
    Promote to virtual-mode default. The first virtual account auto-
    promotes regardless, so this only matters when 2+ are registered.

.PARAMETER EnvFile
    Path to .env.local. Default E:\MCP_E2E\.env.local.

.PARAMETER Framework
    Target framework. Default net8.0.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$AccountNumber,
    [Parameter(Mandatory)] [string]$Nickname,
    [switch]$SetDefault,
    [string]$EnvFile = 'E:\MCP_E2E\.env.local',
    [string]$Framework = 'net8.0'
)

$ErrorActionPreference = 'Stop'

if (Test-Path $EnvFile) {
    Get-Content $EnvFile | ForEach-Object {
        $line = $_.Trim()
        if (-not $line -or $line.StartsWith('#')) { return }
        $eq = $line.IndexOf('=')
        if ($eq -lt 1) { return }
        $name = $line.Substring(0, $eq).Trim()
        $value = $line.Substring($eq + 1).Trim().Trim('"', "'")
        Set-Item -Path "Env:$name" -Value $value
    } | Out-Null
}

# Force virtual mode for this run even if .env.local says otherwise.
$env:LS_MARKET = 'virtual'

if (-not $env:LS_APPKEY -or -not $env:LS_APPSECRETKEY) {
    Write-Error "LS_APPKEY / LS_APPSECRETKEY must be present in $EnvFile or the current session."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src/RedoxNet.Mcp.LsOpenApi/RedoxNet.Mcp.LsOpenApi.csproj'

Write-Host "[1/2] Building MCP server (release)..." -ForegroundColor DarkGray
& dotnet build $projectPath -c Release -f $Framework --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

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
$psi.EnvironmentVariables['LS_MARKET'] = 'virtual'

$proc = [System.Diagnostics.Process]::Start($psi)
$nextId = 1

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
    Write-Host "[2/2] Upserting virtual account $AccountNumber / $Nickname..." -ForegroundColor DarkGray

    SendJsonRpc @{
        jsonrpc = '2.0'
        id = $nextId++
        method = 'initialize'
        params = @{
            protocolVersion = '2024-11-05'
            capabilities = @{}
            clientInfo = @{ name = 'register-virtual-account'; version = '0.0' }
        }
    }
    $init = ReceiveJsonRpc
    if (-not $init -or -not $init.result) { throw "initialize failed." }

    SendJsonRpc @{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} }

    $upsertArgs = @{
        action = 'upsert'
        account_number = $AccountNumber
        nickname = $Nickname
        set_default = [bool]$SetDefault
    }

    SendJsonRpc @{
        jsonrpc = '2.0'
        id = $nextId++
        method = 'tools/call'
        params = @{ name = 'ls_account'; arguments = $upsertArgs }
    }
    $upsertResp = ReceiveJsonRpc
    $rawText = $upsertResp.result.content[0].text
    $parsed = $rawText | ConvertFrom-Json -Depth 32
    if ($parsed.error) {
        Write-Host ("    ❌ {0}" -f $parsed.error) -ForegroundColor Red
        if ($parsed.details) {
            Write-Host ("       details: {0}" -f ($parsed.details | ConvertTo-Json -Depth 5 -Compress)) -ForegroundColor DarkRed
        }
    } else {
        Write-Host ("    ✅ Registered: account_number={0}, nickname={1}, mode={2}, is_default={3}" -f `
            $parsed.account_number, $parsed.nickname, $parsed.mode, $parsed.is_default) -ForegroundColor Green
    }

    # Also list virtual-mode accounts so the user sees the registry state after the write.
    SendJsonRpc @{
        jsonrpc = '2.0'
        id = $nextId++
        method = 'tools/call'
        params = @{ name = 'ls_account'; arguments = @{ action = 'list' } }
    }
    $listResp = ReceiveJsonRpc
    $listText = $listResp.result.content[0].text
    Write-Host ""
    Write-Host "Virtual-mode account registry:" -ForegroundColor Cyan
    Write-Host $listText
}
finally {
    if (-not $proc.HasExited) {
        $proc.StandardInput.Close()
        if (-not $proc.WaitForExit(3000)) { $proc.Kill() }
    }
}
