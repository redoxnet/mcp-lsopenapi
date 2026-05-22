using FluentAssertions;
using ModelContextProtocol.Protocol;
using RedoxNet.Mcp.LsOpenApi.Apps;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Apps;

/// <summary>
/// Pins which tools advertise the SEP-1865 <c>_meta.ui</c> envelope so an MCP
/// Apps host pairs their <c>structuredContent.chart</c> with the inline Plotly
/// renderer. A chart-emitting tool missing from the set silently renders no
/// chart — exactly the regression this guards.
/// </summary>
public class UiResourcesTests
{
    [Theory]
    [InlineData("ls_get_chart")]
    [InlineData("ls_get_etf_holdings")]
    [InlineData("ls_get_program_trading")]
    public void PatchToolMeta_PairsChartEmittingToolsWithThePlotlyResource(string toolName)
    {
        var tool = new Tool { Name = toolName };

        UiResources.PatchToolMetaIfChartEmitting(tool);

        tool.Meta.Should().NotBeNull($"{toolName} ships structuredContent.chart");
        tool.Meta!["ui"]!["resourceUri"]!.GetValue<string>()
            .Should().Be("ui://lsopenapi/plotly");
    }

    [Fact]
    public void PatchToolMeta_LeavesNonChartToolsUntouched()
    {
        var tool = new Tool { Name = "ls_get_quote" };

        UiResources.PatchToolMetaIfChartEmitting(tool);

        tool.Meta.Should().BeNull();
    }
}
