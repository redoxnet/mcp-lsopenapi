<#
.SYNOPSIS
    Pack and push both RedoxNet packages (Core then Mcp) to nuget.org.
.DESCRIPTION
    Convenience wrapper that publishes the SDK (RedoxNet.LsOpenApi.Core) and
    the MCP server (RedoxNet.Mcp.LsOpenApi) in dependency order — Core first
    so it is indexed before consumers restore the Mcp package.

    For single-package releases use publish-core.ps1 / publish-mcp.ps1.
.EXAMPLE
    .\publish-nuget.ps1
    .\publish-nuget.ps1 -SkipPush
#>
param(
    [switch]$SkipPush,
    [string]$NuGetApiKey
)

. "$PSScriptRoot\nuget-common.ps1"

Invoke-NuGetPublish `
    -Projects @(
        'src\RedoxNet.LsOpenApi.Core\RedoxNet.LsOpenApi.Core.csproj',
        'src\RedoxNet.Mcp.LsOpenApi\RedoxNet.Mcp.LsOpenApi.csproj'
    ) `
    -SkipPush:$SkipPush `
    -NuGetApiKey $NuGetApiKey
