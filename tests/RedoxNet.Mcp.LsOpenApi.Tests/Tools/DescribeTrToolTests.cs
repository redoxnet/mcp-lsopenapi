using System.Text.Json;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public class DescribeTrToolTests
{
    [Fact]
    public void DescribeTr_KnownCode_ReturnsFullSchema()
    {
        string json = DescribeTrTool.DescribeTr(TrCatalog.Default, "t1101");

        JsonElement root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("tr_cd").GetString().Should().Be("t1101");
        root.GetProperty("path").GetString().Should().Be("/stock/market-data");
        root.GetProperty("in_blocks")[0].GetProperty("fields")[0].GetProperty("name").GetString()
            .Should().Be("shcode");
        root.GetProperty("continuation").GetProperty("supported").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void DescribeTr_UnknownCode_ReturnsError()
    {
        string json = DescribeTrTool.DescribeTr(TrCatalog.Default, "tNOPE");
        JsonElement root = JsonDocument.Parse(json).RootElement;
        root.GetProperty("error").GetString().Should().Contain("tNOPE");
    }

    [Fact]
    public void DescribeTr_EmptyCode_ReturnsError()
    {
        string json = DescribeTrTool.DescribeTr(TrCatalog.Default, "");
        JsonDocument.Parse(json).RootElement.GetProperty("error").GetString()
            .Should().Contain("tr_cd");
    }

    [Fact]
    public void DescribeTr_ChartTr_DeclaresSingleKeyField()
    {
        string json = DescribeTrTool.DescribeTr(TrCatalog.Default, "t8410");
        JsonElement cont = JsonDocument.Parse(json).RootElement.GetProperty("continuation");

        cont.GetProperty("supported").GetBoolean().Should().BeTrue();
        JsonElement keys = cont.GetProperty("key_fields");
        keys.GetArrayLength().Should().Be(1);
        keys[0].GetString().Should().Be("cts_date");
    }

    [Fact]
    public void DescribeTr_MinuteTr_DeclaresMultipleKeyFields()
    {
        string json = DescribeTrTool.DescribeTr(TrCatalog.Default, "t8412");
        JsonElement cont = JsonDocument.Parse(json).RootElement.GetProperty("continuation");

        cont.GetProperty("supported").GetBoolean().Should().BeTrue();
        JsonElement keys = cont.GetProperty("key_fields");
        keys.GetArrayLength().Should().Be(2);
        keys[0].GetString().Should().Be("cts_date");
        keys[1].GetString().Should().Be("cts_time");
    }
}
