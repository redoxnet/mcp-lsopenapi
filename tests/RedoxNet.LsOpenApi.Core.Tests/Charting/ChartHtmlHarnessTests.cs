using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Charting;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

/// <summary>
/// Renders the chart builders against real-shaped sample data into
/// standalone HTML files. Run <c>dotnet test</c>, then open the
/// <c>chart-output/*.html</c> path each test logs to eyeball how a tool
/// response actually plots — the assertions here only sanity-check that a
/// chart of the expected trace type was produced.
/// </summary>
public class ChartHtmlHarnessTests
{
    readonly ITestOutputHelper _output;

    /// <summary>Captures the xUnit output sink so each test can log its HTML path.</summary>
    public ChartHtmlHarnessTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Candlestick + volume + MA(5)/MA(20) overlays from a 60-candle uptrend,
    /// the shape <c>ls_get_chart</c> ships for a daily request with indicators.
    /// </summary>
    [Fact]
    public void Candlestick_WithMovingAverages_WritesViewableHtml()
    {
        List<Candle> candles = TrendingCandles(60);
        IReadOnlyList<IndicatorSpec> specs = [ParseSpec("ma:5"), ParseSpec("ma:20")];
        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicators =
            new IndicatorService().Compute(candles, specs);

        JsonObject envelope = PlotlyChartBuilder.Build("005930", "day", candles, indicators, specs);
        string path = ChartHtmlHarness.Write("candlestick-005930-day", envelope["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"candlestick\"");
    }

    /// <summary>
    /// ETF holdings treemap, the shape <c>ls_get_etf_holdings</c> ships under
    /// <c>structuredContent.chart</c> — uses a semiconductor-ETF-like weight
    /// distribution so the squarify layout and label suppression are visible.
    /// </summary>
    [Fact]
    public void EtfHoldingsTreemap_WritesViewableHtml()
    {
        JsonObject? envelope = EtfHoldingsChartBuilder.Build(
            "091160", SemiconductorEtfHoldings(), cashPercent: 0.4);

        envelope.Should().NotBeNull();
        string path = ChartHtmlHarness.Write("treemap-091160-etf", envelope!["chart"]!["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"treemap\"");
    }

    /// <summary>Parses an indicator spec string, asserting it is well-formed.</summary>
    static IndicatorSpec ParseSpec(string raw)
    {
        IndicatorSpecParser.TryParse(raw, out IndicatorSpec? spec, out string? error)
            .Should().BeTrue(error);
        return spec!;
    }

    /// <summary>
    /// Builds a gentle-uptrend OHLCV series with day-to-day noise — realistic
    /// enough that the candles, volume bars, and MA overlays all read clearly
    /// in the rendered chart. Seeded for a stable artifact across runs.
    /// </summary>
    static List<Candle> TrendingCandles(int count)
    {
        var rng = new Random(7);
        var candles = new List<Candle>(count);
        DateTime day = new(2026, 2, 2);
        decimal price = 70_000m;
        for (int i = 0; i < count; i++)
        {
            decimal open = price;
            decimal close = Math.Max(1_000m, open + 250m + rng.Next(-650, 750));
            decimal high = Math.Max(open, close) + rng.Next(0, 600);
            decimal low = Math.Min(open, close) - rng.Next(0, 600);
            long volume = 8_000_000 + rng.Next(0, 7_000_000);
            candles.Add(new Candle(day.AddDays(i), open, high, low, close, volume));
            price = close;
        }
        return candles;
    }

    /// <summary>
    /// A semiconductor-ETF-shaped holdings list (heavy top-two, long thin
    /// tail) so the treemap exercises both the labelled top-10 cells and the
    /// label-suppressed remainder.
    /// </summary>
    static List<EtfHoldingsChartBuilder.EtfHoldingView> SemiconductorEtfHoldings() =>
    [
        new("000660", "SK하이닉스", 34.91, 1_970_000),
        new("005930", "삼성전자", 20.56, 296_000),
        new("042700", "한미반도체", 9.05, 409_500),
        new("000990", "DB하이텍", 3.04, 182_800),
        new("058470", "리노공업", 3.00, 114_200),
        new("036930", "주성엔지니어링", 2.82, 168_000),
        new("240810", "원익IPS", 2.32, 127_400),
        new("039030", "이오테크닉스", 2.16, 479_000),
        new("440110", "파두", 1.94, 103_500),
        new("222800", "심텍", 1.55, 109_800),
        new("403870", "HPSP", 1.37, 54_700),
        new("357780", "솔브레인", 1.21, 198_000),
        new("095340", "ISC", 0.98, 71_200),
        new("178320", "서진시스템", 0.71, 42_300),
    ];
}
