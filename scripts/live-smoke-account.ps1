#requires -Version 7.0
<#
.SYNOPSIS
    Live smoke test for the v1.6 ls_account_* read-only inquiry tools
    against the real LS증권 OpenAPI (default: virtual / 모의투자).

.DESCRIPTION
    Spawns the MCP server as a child process over stdio, runs the
    initialize handshake, then calls each of the three currently-built
    inquiry tools and prints a compact summary. Surfaces real LS rsp_cd
    values so we can confirm the CSPAQ "00136" quirk (see
    docs/LS-API-QUIRKS.md §4.2b) holds against live traffic, and that
    t0424 / t0425 succeed under the standard token scope.

    Reads credentials from environment variables:
        LS_APPKEY       (required)
        LS_APPSECRETKEY (required)
        LS_MARKET       (optional; default 'virtual')

    Never logs the raw secrets. Last 4 characters only.

.PARAMETER Account
    Optional account identifier (account_number or nickname). Omit to
    let the tool pick the default account from portfolio.db.

.PARAMETER Framework
    Target framework for dotnet run. Default net8.0.

.EXAMPLE
    # In a session where LS_APPKEY / LS_APPSECRETKEY are set:
    pwsh scripts/live-smoke-account.ps1

.EXAMPLE
    pwsh scripts/live-smoke-account.ps1 -Account "모의계좌"
#>

[CmdletBinding()]
param(
    [string]$Account,
    [string]$Framework = 'net8.0'
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
'@
}

$market = if ($env:LS_MARKET) { $env:LS_MARKET } else { 'virtual' }
Write-Host ""
Write-Host "==== RedoxNet.Mcp.LsOpenApi v1.6 account-inquiry smoke ====" -ForegroundColor Cyan
Write-Host ("  appkey       : {0}" -f (Mask $env:LS_APPKEY))
Write-Host ("  appsecretkey : {0}" -f (Mask $env:LS_APPSECRETKEY))
Write-Host ("  market       : {0}" -f $market)
Write-Host ("  account      : {0}" -f $(if ($Account) { $Account } else { '(default — auto from portfolio.db)' }))
Write-Host ("  framework    : {0}" -f $Framework)
Write-Host ""

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src/RedoxNet.Mcp.LsOpenApi/RedoxNet.Mcp.LsOpenApi.csproj'

if (-not (Test-Path $projectPath)) {
    Write-Error "Project not found at $projectPath. Run from the repo root."
}

Write-Host "[1/3] Building MCP server (release)..." -ForegroundColor DarkGray
& dotnet build $projectPath -c Release -f $Framework --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed." }

Write-Host "[2/3] Launching MCP server over stdio..." -ForegroundColor DarkGray

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

function CallTool([string]$Name, [hashtable]$ArgsTable) {
    $script:nextId++
    SendJsonRpc @{
        jsonrpc = '2.0'
        id = $script:nextId
        method = 'tools/call'
        params = @{ name = $Name; arguments = $ArgsTable }
    }
    return ReceiveJsonRpc
}

try {
    SendJsonRpc @{
        jsonrpc = '2.0'
        id = $nextId
        method = 'initialize'
        params = @{
            protocolVersion = '2024-11-05'
            capabilities = @{}
            clientInfo = @{ name = 'live-smoke-account'; version = '0.0' }
        }
    }
    $init = ReceiveJsonRpc
    if (-not $init -or -not $init.result) { throw "initialize failed." }
    Write-Host ("    server: {0} v{1}" -f $init.result.serverInfo.name, $init.result.serverInfo.version) -ForegroundColor Green

    SendJsonRpc @{ jsonrpc = '2.0'; method = 'notifications/initialized'; params = @{} }

    $accountArgs = if ($Account) { @{ account = $Account } } else { @{} }

    Write-Host ""
    Write-Host "[3/3] Calling tools..." -ForegroundColor DarkGray

    # --- ls_account_holdings ---
    Write-Host ""
    Write-Host "== ls_account_holdings (t0424) ==" -ForegroundColor Yellow
    $resp = CallTool 'ls_account_holdings' $accountArgs
    $rawText = $resp.result.content[0].text
    try {
        $parsed = $rawText | ConvertFrom-Json -Depth 32
        if ($parsed.error) {
            Write-Host ("    ❌ {0}" -f $parsed.error) -ForegroundColor Red
            if ($parsed.details) {
                Write-Host ("       details: {0}" -f ($parsed.details | ConvertTo-Json -Depth 5 -Compress)) -ForegroundColor DarkRed
            }
        } else {
            $used = $parsed._meta.account_used
            Write-Host ("    ✅ account {0} ({1}, mode={2})" -f $used.account_number, $used.nickname, $used.mode) -ForegroundColor Green
            Write-Host ("       data_as_of   : {0}" -f $parsed._meta.data_as_of)
            Write-Host ("       summary      : net_assets={0:N0}  evaluation={1:N0}  pnl={2:N0}  d2_deposit={3:N0}" -f `
                $parsed.summary.estimated_net_assets, $parsed.summary.total_evaluation, $parsed.summary.total_evaluation_pnl, $parsed.summary.estimated_d2_deposit)
            Write-Host ("       holdings     : {0}" -f $parsed.count)
            foreach ($row in @($parsed.holdings) | Select-Object -First 5) {
                Write-Host ("         - {0,-8} {1,-12} qty={2,4} avg={3,9:N0} cur={4,9:N0} pnl%={5,7:N2}" -f `
                    $row.symbol, $row.name, $row.quantity, $row.average_price, $row.current_price, $row.evaluation_pnl_pct)
            }
            if ($parsed.count -gt 5) {
                Write-Host ("         ... ({0} more)" -f ($parsed.count - 5))
            }
        }
    } catch {
        Write-Host "    (non-JSON output):" -ForegroundColor Yellow
        Write-Host $rawText
    }

    # --- ls_account_orders ---
    Write-Host ""
    Write-Host "== ls_account_orders (t0425) ==" -ForegroundColor Yellow
    $resp = CallTool 'ls_account_orders' $accountArgs
    $rawText = $resp.result.content[0].text
    try {
        $parsed = $rawText | ConvertFrom-Json -Depth 32
        if ($parsed.error) {
            Write-Host ("    ❌ {0}" -f $parsed.error) -ForegroundColor Red
            if ($parsed.details) {
                Write-Host ("       details: {0}" -f ($parsed.details | ConvertTo-Json -Depth 5 -Compress)) -ForegroundColor DarkRed
            }
        } else {
            $used = $parsed._meta.account_used
            Write-Host ("    ✅ account {0} ({1}, mode={2})" -f $used.account_number, $used.nickname, $used.mode) -ForegroundColor Green
            Write-Host ("       filter       : status={0} side={1} sort={2}" -f $parsed.filter.status, $parsed.filter.side, $parsed.filter.sort)
            Write-Host ("       summary      : tqty={0}  filled={1}  pending={2}" -f $parsed.summary.total_order_quantity, $parsed.summary.total_filled_quantity, $parsed.summary.total_pending_quantity)
            Write-Host ("       orders       : {0}" -f $parsed.count)
            foreach ($row in @($parsed.orders) | Select-Object -First 5) {
                Write-Host ("         - #{0,-6} {1,-8} {2,-6} qty={3,5} price={4,9:N0} status={5} time={6}" -f `
                    $row.order_no, $row.symbol, $row.side, $row.order_quantity, $row.order_price, $row.status, $row.order_time)
            }
            if ($parsed.count -gt 5) {
                Write-Host ("         ... ({0} more)" -f ($parsed.count - 5))
            }
        }
    } catch {
        Write-Host "    (non-JSON output):" -ForegroundColor Yellow
        Write-Host $rawText
    }

    # --- ls_account_balance ---
    Write-Host ""
    Write-Host "== ls_account_balance (CSPAQ12200 / CSPAQ22200) ==" -ForegroundColor Yellow
    $resp = CallTool 'ls_account_balance' $accountArgs
    $rawText = $resp.result.content[0].text
    try {
        $parsed = $rawText | ConvertFrom-Json -Depth 32
        if ($parsed.error) {
            Write-Host ("    ❌ {0}" -f $parsed.error) -ForegroundColor Red
            if ($parsed.details) {
                Write-Host ("       details: {0}" -f ($parsed.details | ConvertTo-Json -Depth 5 -Compress)) -ForegroundColor DarkRed
            }
        } else {
            $used = $parsed._meta.account_used
            Write-Host ("    ✅ tr_code={0}  account {1} ({2}, mode={3})" -f $parsed._meta.tr_code, $used.account_number, $used.nickname, $used.mode) -ForegroundColor Green
            Write-Host ("       deposit      : Dps={0:N0}  D1={1:N0}  D2={2:N0}" -f $parsed.balance.deposit, $parsed.balance.d1_deposit, $parsed.balance.d2_deposit)
            Write-Host ("       orderable    : cash={0:N0}  kospi={1:N0}  kosdaq={2:N0}  substitute={3:N0}" -f `
                $parsed.balance.cash_orderable_amount, $parsed.balance.kospi_orderable_amount, $parsed.balance.kosdaq_orderable_amount, $parsed.balance.substitute_orderable_amount)
            if ($parsed.balance.evaluation_amount -ne $null) {
                Write-Host ("       valuation    : evaluation={0:N0}  deposited_total={1:N0}  pnl_pct={2:N2}" -f $parsed.balance.evaluation_amount, $parsed.balance.deposited_asset_total, $parsed.balance.pnl_pct)
            } else {
                Write-Host "       valuation    : (virtual — LS does not return investment/eval fields)"
            }
        }
    } catch {
        Write-Host "    (non-JSON output):" -ForegroundColor Yellow
        Write-Host $rawText
    }

    # --- ls_account_bep ---
    Write-Host ""
    Write-Host "== ls_account_bep (CSPAQ12300) ==" -ForegroundColor Yellow
    $resp = CallTool 'ls_account_bep' $accountArgs
    $rawText = $resp.result.content[0].text
    try {
        $parsed = $rawText | ConvertFrom-Json -Depth 32
        if ($parsed.error) {
            Write-Host ("    ❌ {0}" -f $parsed.error) -ForegroundColor Red
            if ($parsed.details) {
                Write-Host ("       details: {0}" -f ($parsed.details | ConvertTo-Json -Depth 5 -Compress)) -ForegroundColor DarkRed
            }
        } else {
            $used = $parsed._meta.account_used
            Write-Host ("    ✅ account {0} ({1}, mode={2})" -f $used.account_number, $used.nickname, $used.mode) -ForegroundColor Green
            if ($parsed.summary) {
                Write-Host ("       summary      : eval={0:N0}  pnl={1:N0}  pnl_pct={2:N2}  invst_pnl={3:N0}" -f `
                    $parsed.summary.evaluation_amount, $parsed.summary.evaluation_pnl_sum, $parsed.summary.pnl_pct, $parsed.summary.investment_pnl)
            }
            Write-Host ("       holdings     : {0}" -f $parsed.count)
            foreach ($row in @($parsed.holdings) | Select-Object -First 5) {
                Write-Host ("         - {0,-8} {1,-12} qty={2,4} avg={3,9:N0} BEP_sell={4,9:N0} cur={5,9:N0} pnl%={6,7:N2}" -f `
                    $row.symbol, $row.name, $row.quantity, $row.average_price, $row.bep_sell_price, $row.current_price, $row.evaluation_pnl_pct)
            }
        }
    } catch {
        Write-Host "    (non-JSON output):" -ForegroundColor Yellow
        Write-Host $rawText
    }

    # --- ls_account_credit_limit ---
    Write-Host ""
    Write-Host "== ls_account_credit_limit (CSPAQ00600) ==" -ForegroundColor Yellow
    $resp = CallTool 'ls_account_credit_limit' $accountArgs
    $rawText = $resp.result.content[0].text
    try {
        $parsed = $rawText | ConvertFrom-Json -Depth 32
        if ($parsed.error) {
            Write-Host ("    ❌ {0}" -f $parsed.error) -ForegroundColor Red
            if ($parsed.details) {
                Write-Host ("       details: {0}" -f ($parsed.details | ConvertTo-Json -Depth 5 -Compress)) -ForegroundColor DarkRed
            }
        } else {
            $used = $parsed._meta.account_used
            Write-Host ("    ✅ account {0} ({1}, mode={2})  loan_type={3}" -f `
                $used.account_number, $used.nickname, $used.mode, $parsed.filter.loan_type) -ForegroundColor Green
            $l = $parsed.limits
            Write-Host ("       limits       : dist={0:N0}/{1:N0}  self={2:N0}/{3:N0}  short={4:N0}/{5:N0}" -f `
                $l.distribution_margin_used, $l.distribution_margin_limit, `
                $l.self_margin_used, $l.self_margin_limit, `
                $l.short_loan_used, $l.short_loan_limit)
            Write-Host ("       pledge       : ratio={0:N4}%  maint={1:N4}%  dpsast_sum={2:N0}  orderable={3:N0}" -f `
                $l.pledge_ratio_pct, $l.pledge_maintenance_ratio_pct, $l.deposited_asset_sum, $l.orderable_amount)
        }
    } catch {
        Write-Host "    (non-JSON output):" -ForegroundColor Yellow
        Write-Host $rawText
    }

    # --- ls_account_max_order_qty ---
    Write-Host ""
    Write-Host "== ls_account_max_order_qty (CSPBQ00200, sym=005930 buy) ==" -ForegroundColor Yellow
    $maxOrderArgs = $accountArgs.Clone()
    $maxOrderArgs['symbol'] = '005930'
    $maxOrderArgs['side'] = 'buy'
    $resp = CallTool 'ls_account_max_order_qty' $maxOrderArgs
    $rawText = $resp.result.content[0].text
    try {
        $parsed = $rawText | ConvertFrom-Json -Depth 32
        if ($parsed.error) {
            Write-Host ("    ❌ {0}" -f $parsed.error) -ForegroundColor Red
            if ($parsed.details) {
                Write-Host ("       details: {0}" -f ($parsed.details | ConvertTo-Json -Depth 5 -Compress)) -ForegroundColor DarkRed
            }
        } else {
            $used = $parsed._meta.account_used
            $c = $parsed.capacity
            $t = $parsed.margin_tiers
            Write-Host ("    ✅ account {0} ({1}, mode={2})  symbol={3} side={4}" -f `
                $used.account_number, $used.nickname, $used.mode, $parsed.filter.symbol, $parsed.filter.side) -ForegroundColor Green
            Write-Host ("       capacity     : orderable_qty={0:N0}  orderable_amt={1:N0}  cash_orderable={2:N0}  margin_acct={3:N2}%" -f `
                $c.orderable_quantity, $c.orderable_amount, $c.cash_orderable_amount, $c.margin_rate_account_pct)
            Write-Host ("       tiers (qty)  : 20%={0,8:N0}  30%={1,8:N0}  40%={2,8:N0}  100%={3,8:N0}  100%cash={4,8:N0}" -f `
                $t.pct20_orderable_quantity, $t.pct30_orderable_quantity, $t.pct40_orderable_quantity, $t.pct100_orderable_quantity, $t.pct100_cash_only_quantity)
        }
    } catch {
        Write-Host "    (non-JSON output):" -ForegroundColor Yellow
        Write-Host $rawText
    }

    Write-Host ""
    Write-Host "==== account-inquiry smoke complete ====" -ForegroundColor Green
}
finally {
    if (-not $proc.HasExited) {
        $proc.StandardInput.Close()
        if (-not $proc.WaitForExit(3000)) { $proc.Kill() }
    }
    $stderr = $proc.StandardError.ReadToEnd()
    if ($stderr) {
        $lines = $stderr -split "`n"
        $tail = if ($lines.Length -gt 8) { $lines[-8..-1] } else { $lines }
        Write-Host ""
        Write-Host "Server stderr (last 8 lines):" -ForegroundColor DarkGray
        foreach ($l in $tail) { Write-Host "  $l" -ForegroundColor DarkGray }
    }
}
