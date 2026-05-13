<#
.SYNOPSIS
    Pack and push the RedoxNet.LsOpenApi NuGet packages.
.DESCRIPTION
    1. dotnet clean + dotnet pack (Release) for both projects.
    2. dotnet nuget push to nuget.org (skips duplicates so re-runs are safe).

    No code signing step — the project ships unsigned.
.PARAMETER SkipPush
    Pack only; do not push to nuget.org. Useful for local verification.
.PARAMETER NuGetApiKey
    NuGet.org API key. If omitted, reads from $env:NUGET_API_KEY_REDOXNET.
.EXAMPLE
    .\publish-nuget.ps1             # pack + push (uses env var)
    .\publish-nuget.ps1 -SkipPush   # pack only, inspect artifacts/ folder
    .\publish-nuget.ps1 -NuGetApiKey 'oy2xxxx...'
#>
param(
    [switch]$SkipPush,
    [string]$NuGetApiKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Config ---
$RepoRoot = Split-Path $PSScriptRoot -Parent
$OutputDir = Join-Path $RepoRoot 'artifacts'
$NuGetSource = 'https://api.nuget.org/v3/index.json'

# Order matters: Core must pack before Mcp (Mcp depends on Core via ProjectReference,
# and `dotnet pack` on Mcp will rebuild Core too — but listing both here keeps the
# artifacts/ output explicit and lets a Core-only patch ship without a Mcp re-pack
# if the user ever runs them individually.)
$Projects = @(
    'src\RedoxNet.LsOpenApi.Core\RedoxNet.LsOpenApi.Core.csproj',
    'src\RedoxNet.Mcp.LsOpenApi\RedoxNet.Mcp.LsOpenApi.csproj'
)

# --- Clean artifacts ---
if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item $OutputDir -ItemType Directory | Out-Null

# --- 1. Clean & Pack ---
Write-Host "`n=== Cleaning ===" -ForegroundColor Cyan
foreach ($proj in $Projects) {
    $projPath = Join-Path $RepoRoot $proj
    dotnet clean $projPath -c Release --nologo -v q
}

Write-Host "`n=== Packing (clean build, Release) ===" -ForegroundColor Cyan
foreach ($proj in $Projects) {
    $projPath = Join-Path $RepoRoot $proj
    Write-Host "  Packing $proj ..."
    dotnet pack $projPath -c Release -o $OutputDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Pack failed for $proj"
    }
}

$packages = Get-ChildItem $OutputDir -Filter '*.nupkg' | Sort-Object Name
if (-not $packages) {
    Write-Error "No .nupkg files produced under $OutputDir"
}

Write-Host "`n  Packages created:" -ForegroundColor Green
$packages | ForEach-Object {
    Write-Host "    $($_.Name)  ($([math]::Round($_.Length / 1KB, 1)) KB)"
}

# --- 2. Push ---
if ($SkipPush) {
    Write-Host "`n  -SkipPush set — stopping after pack." -ForegroundColor Yellow
    return
}

Write-Host "`n=== Pushing to nuget.org ===" -ForegroundColor Cyan

if (-not $NuGetApiKey) {
    $NuGetApiKey = $env:NUGET_API_KEY_REDOXNET
}
if (-not $NuGetApiKey) {
    Write-Error "NuGet API key required. Pass -NuGetApiKey or set NUGET_API_KEY_REDOXNET env var."
}

# Quick fingerprint so the user can spot mismatches without leaking the key.
$keyTail = if ($NuGetApiKey.Length -ge 4) { $NuGetApiKey.Substring($NuGetApiKey.Length - 4) } else { '****' }
Write-Host "  Using API key ****$keyTail"

foreach ($pkg in $packages) {
    Write-Host "  Pushing $($pkg.Name) ..."
    dotnet nuget push $pkg.FullName `
        --api-key $NuGetApiKey `
        --source $NuGetSource `
        --skip-duplicate
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Push failed for $($pkg.Name)"
    }
}
Write-Host "  All packages pushed." -ForegroundColor Green

Write-Host "`n=== Done ===" -ForegroundColor Cyan
