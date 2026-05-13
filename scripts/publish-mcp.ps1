<#
.SYNOPSIS
    Pack and push RedoxNet.Mcp.LsOpenApi to nuget.org.
.DESCRIPTION
    Use for Mcp-only releases. Note: RedoxNet.Mcp.LsOpenApi has a NuGet
    dependency on RedoxNet.LsOpenApi.Core — if you're bumping Core in the
    same release cycle, publish Core FIRST so it indexes on nuget.org
    before consumers try to restore Mcp.
.EXAMPLE
    .\publish-mcp.ps1
    .\publish-mcp.ps1 -SkipPush
#>
param(
    [switch]$SkipPush,
    [string]$NuGetApiKey
)

. "$PSScriptRoot\nuget-common.ps1"

Invoke-NuGetPublish `
    -Projects @(
        'src\RedoxNet.Mcp.LsOpenApi\RedoxNet.Mcp.LsOpenApi.csproj'
    ) `
    -SkipPush:$SkipPush `
    -NuGetApiKey $NuGetApiKey
