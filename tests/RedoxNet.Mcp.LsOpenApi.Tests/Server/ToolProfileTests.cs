using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Server;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Server;

/// <summary>
/// Pins the <see cref="ToolProfile"/> resolution + visibility logic
/// (SPEC-v0.10 §2.4–2.5). The env-var reading is exercised via the
/// <see cref="ToolProfile.Resolve"/> seam so the tests stay parallel-safe.
/// </summary>
public sealed class ToolProfileTests
{
    [Fact]
    public void Resolve_Defaults_ToStandardNonStrict()
    {
        ToolProfile profile = ToolProfile.Resolve(null, null);

        profile.IsAll.Should().BeFalse();
        profile.Strict.Should().BeFalse();
    }

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("  all  ")]
    public void Resolve_AllProfile_IsCaseAndWhitespaceInsensitive(string raw)
    {
        ToolProfile.Resolve(raw, null).IsAll.Should().BeTrue();
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("")]
    [InlineData("weird")]
    public void Resolve_NonAllValues_FallBackToStandard(string raw)
    {
        ToolProfile.Resolve(raw, null).IsAll.Should().BeFalse();
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void Resolve_StrictFlag_IsCaseInsensitive(string raw)
    {
        ToolProfile.Resolve(null, raw).Strict.Should().BeTrue();
    }

    [Fact]
    public void Resolve_Strict_DefaultsFalse()
    {
        ToolProfile.Resolve(null, "false").Strict.Should().BeFalse();
        ToolProfile.Resolve(null, null).Strict.Should().BeFalse();
    }

    [Fact]
    public void Standard_HidesCatalogTrio_KeepsSemanticTools()
    {
        ToolProfile standard = ToolProfile.Resolve(null, null);

        standard.IsVisible("ls_search_tr").Should().BeFalse();
        standard.IsVisible("ls_describe_tr").Should().BeFalse();
        standard.IsVisible("ls_call_tr").Should().BeFalse();

        standard.IsVisible("ls_get_quote").Should().BeTrue();
        standard.IsVisible("ls_get_stock_info").Should().BeTrue();
    }

    [Fact]
    public void All_ExposesEveryTool()
    {
        ToolProfile all = ToolProfile.Resolve("all", null);

        all.IsVisible("ls_search_tr").Should().BeTrue();
        all.IsVisible("ls_describe_tr").Should().BeTrue();
        all.IsVisible("ls_call_tr").Should().BeTrue();
        all.IsVisible("ls_get_quote").Should().BeTrue();
    }

    [Fact]
    public void CatalogTools_AreExactlyTheThreeTrTools()
    {
        ToolProfile.CatalogTools.Should().BeEquivalentTo(
            new[] { "ls_search_tr", "ls_describe_tr", "ls_call_tr" });
    }
}
