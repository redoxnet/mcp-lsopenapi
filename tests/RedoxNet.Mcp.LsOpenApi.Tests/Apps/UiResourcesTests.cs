using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using RedoxNet.Mcp.LsOpenApi.Apps;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Apps;

/// <summary>
/// Covers the v1.2 capability-gated chart surface:
/// <see cref="UiResources.ApplyChartSurface"/> shaping a <c>tools/list</c>
/// entry per <see cref="ChartRenderingMode"/>, and
/// <see cref="UiResources.StripChartStructuredContent"/> removing the UI-only
/// payload from a <c>tools/call</c> result.
/// </summary>
public class UiResourcesTests
{
    const string PlotlyResourceUri = "ui://lsopenapi/plotly";

    static Tool ChartTool(string name = "ls_get_chart") => new()
    {
        Name = name,
        InputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                shcode = new { type = "string" },
                include_chart = new { type = "boolean" },
            },
        }),
    };

    static bool SchemaHasProperty(Tool tool, string property) =>
        tool.InputSchema.GetProperty("properties").TryGetProperty(property, out _);

    // ── ApplyChartSurface: Sep1865 ──────────────────────────────────────────

    [Theory]
    [InlineData("ls_get_chart")]
    [InlineData("ls_add_indicator")]
    [InlineData("ls_reframe_chart")]
    [InlineData("ls_get_overseas_chart")]
    [InlineData("ls_get_etf_holdings")]
    [InlineData("ls_get_program_trading")]
    public void ApplyChartSurface_Sep1865_AttachesNestedMetaUi(string toolName)
    {
        var tool = ChartTool(toolName);

        UiResources.ApplyChartSurface(tool, ChartRenderingMode.Sep1865);

        // W4 regression: pin the nested _meta.ui shape (resourceUri under a
        // "ui" object), never the flat "ui/resourceUri" key.
        tool.Meta.Should().NotBeNull($"{toolName} ships structuredContent.chart");
        tool.Meta!["ui"].Should().BeOfType<JsonObject>();
        tool.Meta["ui"]!["resourceUri"]!.GetValue<string>().Should().Be(PlotlyResourceUri);
        tool.Meta.ContainsKey("ui/resourceUri").Should().BeFalse("the flat form is not used");

        SchemaHasProperty(tool, "include_chart")
            .Should().BeTrue("include_chart stays callable on a SEP-1865 host");
    }

    // ── ApplyChartSurface: StructuredContent ────────────────────────────────

    [Fact]
    public void ApplyChartSurface_StructuredContent_KeepsIncludeChart_NoMetaUi()
    {
        var tool = ChartTool();

        UiResources.ApplyChartSurface(tool, ChartRenderingMode.StructuredContent);

        tool.Meta.Should().BeNull("a host that renders the spec directly ignores _meta.ui");
        SchemaHasProperty(tool, "include_chart").Should().BeTrue(
            "a structured-content host renders the chart, so include_chart stays callable");
    }

    // ── ApplyChartSurface: TextOnly ─────────────────────────────────────────

    [Fact]
    public void ApplyChartSurface_TextOnly_StripsIncludeChart_NoMetaUi()
    {
        var tool = ChartTool();

        UiResources.ApplyChartSurface(tool, ChartRenderingMode.TextOnly);

        tool.Meta.Should().BeNull();
        SchemaHasProperty(tool, "include_chart").Should().BeFalse(
            "a text-only host must not see include_chart");
        SchemaHasProperty(tool, "shcode").Should().BeTrue("other parameters are untouched");
    }

    // ── ApplyChartSurface: non-chart tool ───────────────────────────────────

    [Fact]
    public void ApplyChartSurface_NonChartTool_UntouchedInEveryMode()
    {
        foreach (var mode in new[]
                 {
                     ChartRenderingMode.Sep1865,
                     ChartRenderingMode.StructuredContent,
                     ChartRenderingMode.TextOnly,
                 })
        {
            var tool = ChartTool("ls_get_quote");

            UiResources.ApplyChartSurface(tool, mode);

            tool.Meta.Should().BeNull($"non-chart tool keeps no _meta.ui ({mode})");
            SchemaHasProperty(tool, "include_chart").Should().BeTrue(
                $"a non-chart tool's schema is never reshaped ({mode})");
        }
    }

    // ── StripChartStructuredContent ─────────────────────────────────────────

    static CallToolResult ResultWith(object structured) => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = "{}" } },
        StructuredContent = JsonSerializer.SerializeToElement(structured),
    };

    [Fact]
    public void Strip_RemovesChartAndPanel_NullsEmptyStructuredContent()
    {
        var result = ResultWith(new
        {
            chart = new { type = "plotly" },
            panel = new { kind = "etf_holdings" },
        });

        UiResources.StripChartStructuredContent(result);

        result.StructuredContent.Should().BeNull(
            "structuredContent held only UI keys, so it is dropped entirely");
    }

    [Fact]
    public void Strip_KeepsNonChartKeys()
    {
        var result = ResultWith(new { chart = new { type = "plotly" }, meta = "keep-me" });

        UiResources.StripChartStructuredContent(result);

        result.StructuredContent.Should().NotBeNull();
        result.StructuredContent!.Value.TryGetProperty("chart", out _).Should().BeFalse();
        result.StructuredContent!.Value.GetProperty("meta").GetString().Should().Be("keep-me");
    }

    [Fact]
    public void Strip_NoStructuredContent_IsNoOp()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = "{}" } },
        };

        UiResources.StripChartStructuredContent(result);

        result.StructuredContent.Should().BeNull();
    }

    // ── AttachRenderStatus (SPEC v1.5 §2.1) ─────────────────────────────────

    static CallToolResult EmptyResult() => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = "{}" } },
    };

    [Fact]
    public void AttachRenderStatus_Sep1865_SetsDelivered()
    {
        var result = EmptyResult();

        UiResources.AttachRenderStatus(result, ChartRenderingMode.Sep1865, "ls_get_chart");

        result.Meta.Should().NotBeNull(
            "v1.5 chart-emitting tools always carry render_status so the model has a hard signal");
        result.Meta!["render_status"]!.GetValue<string>().Should().Be("delivered");
    }

    [Fact]
    public void AttachRenderStatus_StructuredContent_SetsDelivered()
    {
        var result = EmptyResult();

        UiResources.AttachRenderStatus(result, ChartRenderingMode.StructuredContent, "ls_get_chart");

        result.Meta!["render_status"]!.GetValue<string>().Should().Be("delivered");
    }

    [Fact]
    public void AttachRenderStatus_TextOnly_SetsStrippedTextOnly()
    {
        var result = EmptyResult();

        UiResources.AttachRenderStatus(result, ChartRenderingMode.TextOnly, "ls_get_chart");

        result.Meta!["render_status"]!.GetValue<string>().Should().Be("stripped_text_only");
    }

    [Fact]
    public void AttachRenderStatus_NonChartTool_LeavesMetaUntouched()
    {
        // A non-chart tool result must NOT acquire render_status — leaking
        // the field elsewhere would invite the model to over-read it as a
        // generic "did the host show something" signal.
        var result = EmptyResult();

        UiResources.AttachRenderStatus(result, ChartRenderingMode.TextOnly, "ls_get_quote");

        result.Meta.Should().BeNull();
    }

    [Fact]
    public void AttachRenderStatus_NullToolName_LeavesMetaUntouched()
    {
        var result = EmptyResult();

        UiResources.AttachRenderStatus(result, ChartRenderingMode.Sep1865, null);

        result.Meta.Should().BeNull();
    }

    [Fact]
    public void AttachRenderStatus_PreservesExistingMetaKeys()
    {
        // Coexist with the export guard (data_purpose / do_not_render) and any
        // future meta — render_status must merge, not replace.
        var result = EmptyResult();
        result.Meta = new JsonObject { ["data_purpose"] = "analysis_only" };

        UiResources.AttachRenderStatus(result, ChartRenderingMode.Sep1865, "ls_get_chart");

        result.Meta.Should().NotBeNull();
        result.Meta!["render_status"]!.GetValue<string>().Should().Be("delivered");
        result.Meta["data_purpose"]!.GetValue<string>().Should().Be("analysis_only");
    }

    [Theory]
    [InlineData("ls_get_chart")]
    [InlineData("ls_add_indicator")]
    [InlineData("ls_reframe_chart")]
    [InlineData("ls_get_overseas_chart")]
    [InlineData("ls_get_etf_holdings")]
    [InlineData("ls_get_program_trading")]
    public void IsChartEmittingTool_AllSixPlotlyTools_ReturnsTrue(string toolName)
    {
        UiResources.IsChartEmittingTool(toolName).Should().BeTrue();
    }

    [Fact]
    public void IsChartEmittingTool_NonChartTools_ReturnsFalse()
    {
        UiResources.IsChartEmittingTool("ls_get_quote").Should().BeFalse();
        UiResources.IsChartEmittingTool("ls_search_stock").Should().BeFalse();
        UiResources.IsChartEmittingTool(null).Should().BeFalse();
        UiResources.IsChartEmittingTool("").Should().BeFalse();
    }
}
