<#
.SYNOPSIS
    Builds the LS OpenAPI MCP server and mirrors it into the in-repo ./deploy folder.
.DESCRIPTION
    A running .NET process locks its own assemblies, so an MCP server running
    straight out of bin/ blocks every subsequent `dotnet build`. Running the
    server from a copied ./deploy folder (gitignored) decouples the two: repo
    builds never collide with a running server — only an explicit redeploy
    needs the server stopped.

    Flow: build into bin/ (never locked) -> stop any server already running
    from ./deploy -> mirror the build output into ./deploy.

    Point the MCP client (this project's .mcp.json and/or the E2E harness)
    at ./deploy/redoxnet-mcp-lsopenapi.exe, then restart the client.
.PARAMETER Configuration
    Build configuration. Default: Release (matches the E2E harness).
.PARAMETER Framework
    Target framework to deploy (the csproj multi-targets net8.0;net10.0).
    Default: net8.0.
.EXAMPLE
    .\deploy-dev.ps1
    .\deploy-dev.ps1 -Configuration Debug -Framework net10.0
#>
param(
    [string]$Configuration = 'Release',
    [string]$Framework = 'net8.0'
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$project   = Join-Path $repoRoot 'src/RedoxNet.Mcp.LsOpenApi/RedoxNet.Mcp.LsOpenApi.csproj'
$deployDir = Join-Path $repoRoot 'deploy'
$buildOut  = Join-Path $repoRoot "src/RedoxNet.Mcp.LsOpenApi/bin/$Configuration/$Framework"

Write-Host "[deploy-dev] dotnet build $Configuration/$Framework ..." -ForegroundColor Cyan
dotnet build $project -c $Configuration -f $Framework --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)." }
if (-not (Test-Path $buildOut)) { throw "Build output not found: $buildOut" }

# Stop anything already running from ./deploy so the mirror copy is not blocked.
$resolved = [System.IO.Path]::GetFullPath($deployDir)
$running = @(Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -and $_.CommandLine -like "*$resolved*" })
foreach ($p in $running) {
    Write-Host "[deploy-dev] Stopping PID $($p.ProcessId) (running from ./deploy)." -ForegroundColor Yellow
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
}
if ($running.Count -gt 0) { Start-Sleep -Milliseconds 600 }

New-Item -ItemType Directory -Force -Path $deployDir | Out-Null

Write-Host "[deploy-dev] Mirroring build output -> ./deploy ..." -ForegroundColor Cyan
robocopy $buildOut $deployDir /MIR /NJH /NJS /NDL /NFL /NP /R:2 /W:1 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)." }
$global:LASTEXITCODE = 0

$exe = Join-Path $deployDir 'redoxnet-mcp-lsopenapi.exe'
Write-Host ''
Write-Host '[deploy-dev] Done.' -ForegroundColor Green
Write-Host "  MCP server entry point: $exe"
Write-Host '  Point .mcp.json / the E2E harness at that path, then restart the MCP client.'
