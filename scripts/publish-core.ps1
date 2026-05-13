<#
.SYNOPSIS
    Pack and push RedoxNet.LsOpenApi.Core to nuget.org.
.DESCRIPTION
    Use for Core-only releases (e.g. SDK-side patch without an MCP-server
    release). For a combined Core + Mcp release, run publish-nuget.ps1.
.EXAMPLE
    .\publish-core.ps1
    .\publish-core.ps1 -SkipPush
    .\publish-core.ps1 -NuGetApiKey 'oy2xxxx...'
#>
param(
    [switch]$SkipPush,
    [string]$NuGetApiKey
)

. "$PSScriptRoot\nuget-common.ps1"

Invoke-NuGetPublish `
    -Projects @(
        'src\RedoxNet.LsOpenApi.Core\RedoxNet.LsOpenApi.Core.csproj'
    ) `
    -SkipPush:$SkipPush `
    -NuGetApiKey $NuGetApiKey
