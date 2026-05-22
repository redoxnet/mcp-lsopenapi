using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Charting;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

public class ProgramTradeChartBuilderTests
{
    static readonly ProgramChartMeta Meta = new("kospi", "today", "2026-05-21", "금액 (억원)");

    static readonly ProgramTradeChartBuilder.ProgramFlowPoint[] Points =
    [
        new("09:00", 1175.45, 0.45, -1200, 50, -1250, -1200),
        new("10:00", 1195.98, 1.42, 1000, 280, 720, 2200),
        new("11:00", 1202.51, 1.79, 4300, 1290, 3010, 3300),
        new("15:30", 1224.78, 1.62, 21100, 1219, 19881, 16800),
    ];

    // GrossFlow needs the gross buy / sell legs the 7-arg points omit.
    static readonly ProgramTradeChartBuilder.ProgramFlowPoint[] GrossPoints =
    [
        new("09:00", 1175.45, 0.45, -1200, 50, -1250, -1200,
            ArbitrageBuy: 116, ArbitrageSell: 66, NonArbitrageBuy: 5083, NonArbitrageSell: 7898),
        new("10:00", 1195.98, 1.42, 1000, 280, 720, 2200,
            ArbitrageBuy: 950, ArbitrageSell: 670, NonArbitrageBuy: 36576, NonArbitrageSell: 35817),
        new("11:00", 1202.51, 1.79, 4300, 1290, 3010, 3300,
            ArbitrageBuy: 2330, ArbitrageSell: 1037, NonArbitrageBuy: 55254, NonArbitrageSell: 52266),
        new("15:30", 1224.78, 1.62, 21100, 1219, 19881, 16800,
            ArbitrageBuy: 6033, ArbitrageSell: 4814, NonArbitrageBuy: 137516, NonArbitrageSell: 117622),
    ];

    [Fact]
    public void Build_FlowOverview_PinsTopLegendBelowTitle()
    {
        JsonObject env = ProgramTradeChartBuilder.Build(
            ProgramTradeChartView.FlowOverview, Meta, Points);

        JsonObject layout = env["spec"]!["layout"]!.AsObject();

        JsonObject title = layout["title"]!.AsObject();
        title["yref"]!.GetValue<string>().Should().Be("paper");
        title["y"]!.GetValue<double>().Should().Be(1.0);
        title["yanchor"]!.GetValue<string>().Should().Be("bottom");
        title["font"]!["size"]!.GetValue<int>().Should().Be(18);
        title["pad"]!["b"]!.GetValue<int>().Should().Be(44);

        JsonObject legend = layout["legend"]!.AsObject();
        legend["orientation"]!.GetValue<string>().Should().Be("h");
        legend["y"]!.GetValue<double>().Should().Be(1.0);
        legend["yanchor"]!.GetValue<string>().Should().Be("bottom");
        legend["font"].Should().BeNull();

        layout["margin"]!["t"]!.GetValue<int>().Should().Be(76);
    }

    [Fact]
    public void Build_GrossFlow_SplitsBuySellIntoTwinPanels()
    {
        JsonObject env = ProgramTradeChartBuilder.Build(
            ProgramTradeChartView.GrossFlow, Meta, GrossPoints);

        JsonObject spec = env["spec"]!.AsObject();
        JsonArray data = spec["data"]!.AsArray();

        // 비차익 매수 / 매도 (top panel) + 차익 매수 / 매도 (bottom panel).
        data.Should().HaveCount(4);
        foreach (JsonNode? trace in data)
            trace!["type"]!.GetValue<string>().Should().Be("bar");

        JsonObject layout = spec["layout"]!.AsObject();
        layout["barmode"]!.GetValue<string>().Should().Be("relative");

        // Stacked panels: 비차익 occupies the top, 차익 the bottom, non-overlapping.
        JsonArray topDomain = layout["yaxis"]!["domain"]!.AsArray();
        JsonArray bottomDomain = layout["yaxis2"]!["domain"]!.AsArray();
        topDomain[0]!.GetValue<double>().Should().BeGreaterThan(bottomDomain[1]!.GetValue<double>());

        // 차익 traces ride y2 (bottom panel); 비차익 traces stay on the default axis.
        data[0]!["yaxis"].Should().BeNull();                       // 비차익 매수
        data[1]!["yaxis"].Should().BeNull();                       // 비차익 매도
        data[2]!["yaxis"]!.GetValue<string>().Should().Be("y2");   // 차익 매수
        data[3]!["yaxis"]!.GetValue<string>().Should().Be("y2");   // 차익 매도

        // Buy bars rise (+), sell bars fall (−) so churn vs accumulation reads.
        data[0]!["y"]!.AsArray().Should().OnlyContain(v => v!.GetValue<double>() >= 0);
        data[1]!["y"]!.AsArray().Should().OnlyContain(v => v!.GetValue<double>() <= 0);
        data[2]!["y"]!.AsArray().Should().OnlyContain(v => v!.GetValue<double>() >= 0);
        data[3]!["y"]!.AsArray().Should().OnlyContain(v => v!.GetValue<double>() <= 0);
    }

    [Fact]
    public void Build_MarketDaily_StacksArbAndNonArbBarsAgainstIndex()
    {
        // MarketDaily carries one row per day; Time holds the yyyy-MM-dd date.
        ProgramTradeChartBuilder.ProgramFlowPoint[] daily =
        [
            new("2026-05-20", 1125.5, 0, -9984, 1080, -11064, 0),
            new("2026-05-21", 1225.2, 0, 20386, 1393, 18993, 0),
            new("2026-05-22", 1225.2, 0, -11507, 274, -11781, 0),
        ];

        JsonObject env = ProgramTradeChartBuilder.Build(
            ProgramTradeChartView.MarketDaily, Meta, daily);

        JsonObject spec = env["spec"]!.AsObject();
        JsonArray data = spec["data"]!.AsArray();
        data.Should().HaveCount(3);   // 비차익 bar + 차익 bar + index line
        data[0]!["type"]!.GetValue<string>().Should().Be("bar");
        data[1]!["type"]!.GetValue<string>().Should().Be("bar");
        data[2]!["type"]!.GetValue<string>().Should().Be("scatter");
        data[2]!["yaxis"]!.GetValue<string>().Should().Be("y2");

        spec["layout"]!["barmode"]!.GetValue<string>().Should().Be("relative");
        // x values are the raw dates, not composed ISO datetimes.
        data[0]!["x"]![0]!.GetValue<string>().Should().Be("2026-05-20");
    }
}
