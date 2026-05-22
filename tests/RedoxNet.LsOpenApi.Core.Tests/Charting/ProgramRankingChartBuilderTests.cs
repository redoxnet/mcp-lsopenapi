using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Charting;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

public class ProgramRankingChartBuilderTests
{
    static readonly ProgramRankingChartMeta Meta =
        new("kospi", "순매수 상위", "순매수 금액 (억원)", "2026-05-22");

    // Rank-ascending; the third row is a net seller so colour-by-sign is exercised.
    static readonly ProgramRankingRow[] Rows =
    [
        new(1, "셀트리온", "068270", 171.0, 0.04, 4.75),
        new(2, "SK", "034730", 67.3, 0.02, 7.29),
        new(3, "LG", "003550", -28.3, 0.02, -3.90),
    ];

    [Fact]
    public void Build_ProducesHorizontalBarEnvelope()
    {
        JsonObject env = ProgramRankingChartBuilder.Build(Meta, Rows);

        env["type"]!.GetValue<string>().Should().Be("plotly");
        env["version"]!.GetValue<string>().Should().Be("5");

        JsonArray data = env["spec"]!["data"]!.AsArray();
        data.Should().HaveCount(1);
        JsonObject trace = data[0]!.AsObject();
        trace["type"]!.GetValue<string>().Should().Be("bar");
        trace["orientation"]!.GetValue<string>().Should().Be("h");

        env["spec"]!["layout"]!["showlegend"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void Build_PlacesRankOneAtTop()
    {
        JsonObject env = ProgramRankingChartBuilder.Build(Meta, Rows);
        JsonArray y = env["spec"]!["data"]![0]!["y"]!.AsArray();

        // Plotly draws the last horizontal-bar entry at the top of the axis.
        y[^1]!.GetValue<string>().Should().Be("셀트리온");
        y[0]!.GetValue<string>().Should().Be("LG");
    }

    [Fact]
    public void Build_ColorsNetBuyRedAndNetSellBlue()
    {
        JsonObject env = ProgramRankingChartBuilder.Build(Meta, Rows);
        JsonArray colors = env["spec"]!["data"]![0]!["marker"]!["color"]!.AsArray();

        // Arrays are reversed: index 0 = LG (net sell), index 2 = 셀트리온 (net buy).
        colors[0]!.GetValue<string>().Should().Be("#3498DB");
        colors[2]!.GetValue<string>().Should().Be("#E03131");
    }
}
