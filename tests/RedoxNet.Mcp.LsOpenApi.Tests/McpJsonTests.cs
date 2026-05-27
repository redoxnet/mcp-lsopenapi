using System.Collections.Generic;
using System.Text.Json.Nodes;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests;

/// <summary>
/// Covers helpers in <see cref="McpJson"/> that ship policy-loaded metadata
/// to the model. Test scope is intentionally narrow — the JSON envelope
/// helpers (<see cref="McpJson.OkResult"/> / <see cref="McpJson.ErrorResult"/>)
/// are exercised end-to-end by every tool test.
/// </summary>
public class McpJsonTests
{
    static CallToolResult EmptyResult() => new()
    {
        Content = new List<ContentBlock> { new TextContentBlock { Text = "{}" } },
    };

    // ── AttachExportGuard (SPEC v1.5 §2.4) ──────────────────────────────────

    [Fact]
    public void AttachExportGuard_SetsDataPurposeAndDoNotRender()
    {
        var result = EmptyResult();

        McpJson.AttachExportGuard(result);

        result.Meta.Should().NotBeNull();
        result.Meta!["data_purpose"]!.GetValue<string>().Should().Be("analysis_only");
        result.Meta["do_not_render"]!.GetValue<string>().Should().Be(McpJson.ExportDoNotRenderText);
    }

    [Theory]
    // The guard text is the model's only inline reminder when it considers
    // self-synthesis. Pin the load-bearing phrases so a future paragraph
    // shuffle doesn't quietly drop the "do not render charts from this"
    // contract.
    [InlineData("Server-computed indicators")] // anchors the "we have the truth" framing
    [InlineData("different adjustment mode")]  // names the divergence cause
    [InlineData("analysis")]                   // the legitimate use
    [InlineData("not for chart synthesis")]    // the explicit ban (phrasing)
    [InlineData("pandas")]                     // concrete legitimate path
    public void ExportDoNotRenderText_CarriesLoadBearingPhrasing(string phrase)
    {
        McpJson.ExportDoNotRenderText.Should().Contain(phrase);
    }

    [Fact]
    public void AttachExportGuard_MergesWithExistingMeta()
    {
        // Coexist with render_status (always attached by the call filter on
        // chart tools — SPEC v1.5 §2.1). The guard must not clobber it.
        var result = EmptyResult();
        result.Meta = new JsonObject { ["render_status"] = "delivered" };

        McpJson.AttachExportGuard(result);

        result.Meta.Should().NotBeNull();
        result.Meta!["render_status"]!.GetValue<string>().Should().Be("delivered");
        result.Meta["data_purpose"]!.GetValue<string>().Should().Be("analysis_only");
        result.Meta["do_not_render"].Should().NotBeNull();
    }

    [Fact]
    public void AttachExportGuard_IsIdempotent()
    {
        // Tools call the helper on the export path; calling twice (e.g. from a
        // future composition) must not double the value or change keys.
        var result = EmptyResult();

        McpJson.AttachExportGuard(result);
        McpJson.AttachExportGuard(result);

        result.Meta.Should().NotBeNull();
        result.Meta!["data_purpose"]!.GetValue<string>().Should().Be("analysis_only");
        result.Meta["do_not_render"]!.GetValue<string>().Should().Be(McpJson.ExportDoNotRenderText);
    }
}
