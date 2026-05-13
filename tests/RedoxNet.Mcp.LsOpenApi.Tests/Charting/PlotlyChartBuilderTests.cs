using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;
using RedoxNet.Mcp.LsOpenApi.Charting;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Charting;

public class PlotlyChartBuilderTests
{
    static List<Candle> SampleCandles(int count = 5)
    {
        DateTime baseDate = new(2024, 1, 1);
        var rng = new Random(42);
        var candles = new List<Candle>();
        for (int i = 0; i < count; i++)
        {
            decimal open = 100 + i;
            decimal close = open + (rng.Next(-2, 3));
            decimal high = Math.Max(open, close) + 2;
            decimal low = Math.Min(open, close) - 2;
            candles.Add(new Candle(baseDate.AddDays(i), open, high, low, close, 1000 + i * 100, 100000));
        }
        return candles;
    }

    [Fact]
    public void Build_EnvelopeShape_ContainsTypeVersionAndSpec()
    {
        var candles = SampleCandles();
        JsonObject envelope = PlotlyChartBuilder.Build(
            "005930", "day", candles,
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        envelope["type"]!.GetValue<string>().Should().Be("plotly");
        envelope["version"]!.GetValue<string>().Should().Be("5");
        envelope["spec"].Should().NotBeNull();
        envelope["spec"]!["data"].Should().BeOfType<JsonArray>();
        envelope["spec"]!["layout"].Should().NotBeNull();
    }

    [Fact]
    public void Build_AlwaysIncludesCandlestickAndVolumeTraces()
    {
        JsonObject env = PlotlyChartBuilder.Build(
            "005930", "day", SampleCandles(),
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        JsonArray data = env["spec"]!["data"]!.AsArray();

        data.Count.Should().Be(2);
        data[0]!["type"]!.GetValue<string>().Should().Be("candlestick");
        data[1]!["type"]!.GetValue<string>().Should().Be("bar");
        data[1]!["yaxis"]!.GetValue<string>().Should().Be("y2");
    }

    [Fact]
    public void Build_CandlestickUsesKoreanColors()
    {
        JsonObject env = PlotlyChartBuilder.Build(
            "005930", "day", SampleCandles(),
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        JsonObject candle = env["spec"]!["data"]![0]!.AsObject();
        candle["increasing"]!["line"]!["color"]!.GetValue<string>().Should().Be("#E74C3C");
        candle["decreasing"]!["line"]!["color"]!.GetValue<string>().Should().Be("#3498DB");
    }

    [Fact]
    public void Build_VolumeBarColorsPerCandle()
    {
        var candles = new List<Candle>
        {
            new(new DateTime(2024, 1, 1), 100, 105, 95, 102, 1000),  // up
            new(new DateTime(2024, 1, 2), 102, 103, 95,  98, 1100),  // down
            new(new DateTime(2024, 1, 3),  98, 105, 97, 103, 1200),  // up
        };
        JsonObject env = PlotlyChartBuilder.Build(
            "005930", "day", candles,
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        JsonArray colors = env["spec"]!["data"]![1]!["marker"]!["color"]!.AsArray();
        colors.Count.Should().Be(3);
        colors[0]!.GetValue<string>().Should().Be("#E74C3C");
        colors[1]!.GetValue<string>().Should().Be("#3498DB");
        colors[2]!.GetValue<string>().Should().Be("#E74C3C");
    }

    [Fact]
    public void Build_MaIndicators_AddedAsOverlayTraces()
    {
        var candles = SampleCandles(20);
        IndicatorSpec ma5 = IndicatorSpecParser.Parse("ma:5");
        IndicatorSpec ema12 = IndicatorSpecParser.Parse("ema:12");
        var service = new IndicatorService();
        var indicators = service.Compute(candles, new[] { ma5, ema12 });

        JsonObject env = PlotlyChartBuilder.Build("005930", "day", candles, indicators, new[] { ma5, ema12 });

        JsonArray data = env["spec"]!["data"]!.AsArray();
        // candlestick + ma:5 + ema:12 + volume
        data.Count.Should().Be(4);

        List<string> traceTypes = data.Select(d => d!["type"]!.GetValue<string>()).ToList();
        traceTypes.Should().Equal("candlestick", "scatter", "scatter", "bar");

        data[1]!["yaxis"]!.GetValue<string>().Should().Be("y");
        data[2]!["yaxis"]!.GetValue<string>().Should().Be("y");
    }

    [Fact]
    public void Build_BollingerBands_AddedAsThreeOverlayTraces()
    {
        var candles = SampleCandles(40);
        IndicatorSpec bb = IndicatorSpecParser.Parse("bb:20,2");
        var service = new IndicatorService();
        var indicators = service.Compute(candles, new[] { bb });

        JsonObject env = PlotlyChartBuilder.Build("005930", "day", candles, indicators, new[] { bb });

        JsonArray data = env["spec"]!["data"]!.AsArray();
        // candlestick + bb upper + bb middle + bb lower + volume
        data.Count.Should().Be(5);

        List<string> names = data
            .Skip(1)
            .Take(3)
            .Select(d => d!["name"]!.GetValue<string>())
            .ToList();
        names.Should().AllSatisfy(n => n.Should().Contain("BB"));
    }

    [Fact]
    public void Build_Rsi_NotRenderedAsOverlay()
    {
        var candles = SampleCandles(40);
        IndicatorSpec rsi = IndicatorSpecParser.Parse("rsi:14");
        var service = new IndicatorService();
        var indicators = service.Compute(candles, new[] { rsi });

        JsonObject env = PlotlyChartBuilder.Build("005930", "day", candles, indicators, new[] { rsi });

        // Only candlestick + volume, RSI skipped (would need its own subplot).
        env["spec"]!["data"]!.AsArray().Count.Should().Be(2);
    }

    [Fact]
    public void Build_Layout_HasExpectedKeys()
    {
        JsonObject env = PlotlyChartBuilder.Build(
            "005930", "day", SampleCandles(),
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        JsonObject layout = env["spec"]!["layout"]!.AsObject();
        layout["hovermode"]!.GetValue<string>().Should().Be("x unified");
        layout["showlegend"]!.GetValue<bool>().Should().BeTrue();
        layout["xaxis"]!["rangeslider"]!["visible"]!.GetValue<bool>().Should().BeFalse();

        JsonArray priceDomain = layout["yaxis"]!["domain"]!.AsArray();
        priceDomain[0]!.GetValue<double>().Should().Be(0.3);
        priceDomain[1]!.GetValue<double>().Should().Be(1.0);

        JsonArray volumeDomain = layout["yaxis2"]!["domain"]!.AsArray();
        volumeDomain[0]!.GetValue<double>().Should().Be(0.0);
        volumeDomain[1]!.GetValue<double>().Should().Be(0.25);

        layout["title"]!["text"]!.GetValue<string>().Should().Contain("005930");
        layout["title"]!["text"]!.GetValue<string>().Should().Contain("일봉");
    }

    [Fact]
    public void Build_LineTrace_NullWarmupValuesPreserved()
    {
        var candles = SampleCandles(20);
        IndicatorSpec ma5 = IndicatorSpecParser.Parse("ma:5");
        var service = new IndicatorService();
        var indicators = service.Compute(candles, new[] { ma5 });

        JsonObject env = PlotlyChartBuilder.Build("005930", "day", candles, indicators, new[] { ma5 });

        JsonArray y = env["spec"]!["data"]![1]!["y"]!.AsArray();
        y[0].Should().BeNull();
        y[3].Should().BeNull();
        y[4].Should().NotBeNull();
    }
}
