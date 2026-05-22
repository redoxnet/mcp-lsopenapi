using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Charting;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

public class ProgramStockChartBuilderTests
{
    static readonly ProgramStockChartMeta Meta = new("005930", "삼성전자", "2026-05-22");

    static readonly ProgramStockPoint[] Intraday =
    [
        new("09:01", 298500, -62.7),
        new("09:30", 295500, -1996.7),
        new("09:39", 296500, -2101.3),
    ];

    static readonly ProgramStockPoint[] Daily =
    [
        new("2026-05-20", 276000, -7603.1),
        new("2026-05-21", 299500, 17067.9),
        new("2026-05-22", 296000, -2101.4),
    ];

    [Fact]
    public void Build_IntradayFlow_PairsPriceAndCumulativeNet()
    {
        JsonObject env = ProgramStockChartBuilder.Build(
            ProgramStockChartView.IntradayFlow, Meta, Intraday);

        env["type"]!.GetValue<string>().Should().Be("plotly");
        JsonArray data = env["spec"]!["data"]!.AsArray();
        data.Should().HaveCount(2);
        data[0]!["type"]!.GetValue<string>().Should().Be("scatter");
        data[1]!["type"]!.GetValue<string>().Should().Be("scatter");
        data[1]!["yaxis"]!.GetValue<string>().Should().Be("y2");
        data[1]!["fill"]!.GetValue<string>().Should().Be("tozeroy");
    }

    [Fact]
    public void Build_DailyBars_SplitsNetBuyAndSellIntoSeparateTraces()
    {
        JsonObject env = ProgramStockChartBuilder.Build(
            ProgramStockChartView.DailyBars, Meta, Daily);

        JsonArray data = env["spec"]!["data"]!.AsArray();
        data.Should().HaveCount(3);   // 순매수 + 순매도 + 종가

        // 순매수 trace — red, value only on net-buy days.
        JsonObject buy = data[0]!.AsObject();
        buy["type"]!.GetValue<string>().Should().Be("bar");
        buy["name"]!.GetValue<string>().Should().Be("프로그램 순매수");
        buy["marker"]!["color"]!.GetValue<string>().Should().Be("#E03131");
        JsonArray buyY = buy["y"]!.AsArray();
        buyY[1]!.GetValue<double>().Should().BeApproximately(17067.9, 0.01);  // 2026-05-21
        buyY[0].Should().BeNull("net-sell days carry no buy bar");

        // 순매도 trace — blue, value only on net-sell days.
        JsonObject sell = data[1]!.AsObject();
        sell["name"]!.GetValue<string>().Should().Be("프로그램 순매도");
        sell["marker"]!["color"]!.GetValue<string>().Should().Be("#3498DB");
        sell["y"]!.AsArray()[0]!.GetValue<double>().Should().BeApproximately(-7603.1, 0.01);

        // The price rides the secondary axis as a neutral-grey line.
        JsonObject price = data[2]!.AsObject();
        price["type"]!.GetValue<string>().Should().Be("scatter");
        price["yaxis"]!.GetValue<string>().Should().Be("y2");
        price["line"]!["color"]!.GetValue<string>().Should().Be("#7F8C8D");
    }
}
