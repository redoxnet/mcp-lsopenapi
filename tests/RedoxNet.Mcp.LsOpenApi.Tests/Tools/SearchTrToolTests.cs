using System.Text.Json;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public class SearchTrToolTests
{
    [Fact]
    public void SearchTr_KoreanKeyword_FindsExpectedTr()
    {
        string json = SearchTrTool.SearchTr(TrCatalog.Default, keyword: "현재가");

        JsonElement root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("results")[0].GetProperty("tr_cd").GetString().Should().Be("t1101");
    }

    [Fact]
    public void SearchTr_EmptyKeyword_ReturnsError()
    {
        string json = SearchTrTool.SearchTr(TrCatalog.Default, keyword: "");

        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .Should().Contain("keyword");
    }

    [Fact]
    public void SearchTr_LimitClampedToRange()
    {
        string lower = SearchTrTool.SearchTr(TrCatalog.Default, "주식", limit: 0);
        string upper = SearchTrTool.SearchTr(TrCatalog.Default, "주식", limit: 999);

        JsonDocument.Parse(lower).RootElement.GetProperty("limit").GetInt32().Should().Be(1);
        JsonDocument.Parse(upper).RootElement.GetProperty("limit").GetInt32().Should().Be(50);
    }

    [Fact]
    public void SearchTr_PayloadIncludesContinuationFlag()
    {
        string json = SearchTrTool.SearchTr(TrCatalog.Default, "t8410");
        JsonElement first = JsonDocument.Parse(json).RootElement.GetProperty("results")[0];

        first.GetProperty("continuation_supported").GetBoolean().Should().BeTrue();
    }
}
