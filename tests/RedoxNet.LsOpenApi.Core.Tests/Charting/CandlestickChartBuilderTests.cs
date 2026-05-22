using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Charting;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

public class CandlestickChartBuilderTests
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
        JsonObject envelope = CandlestickChartBuilder.Build(
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
        JsonObject env = CandlestickChartBuilder.Build(
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
        JsonObject env = CandlestickChartBuilder.Build(
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
        JsonObject env = CandlestickChartBuilder.Build(
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

        JsonObject env = CandlestickChartBuilder.Build("005930", "day", candles, indicators, new[] { ma5, ema12 });

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

        JsonObject env = CandlestickChartBuilder.Build("005930", "day", candles, indicators, new[] { bb });

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

        JsonObject env = CandlestickChartBuilder.Build("005930", "day", candles, indicators, new[] { rsi });

        // Only candlestick + volume, RSI skipped (would need its own subplot).
        env["spec"]!["data"]!.AsArray().Count.Should().Be(2);
    }

    [Fact]
    public void Build_Layout_HasExpectedKeys()
    {
        JsonObject env = CandlestickChartBuilder.Build(
            "005930", "day", SampleCandles(),
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        JsonObject layout = env["spec"]!["layout"]!.AsObject();
        layout["hovermode"]!.GetValue<string>().Should().Be("x unified");
        layout["showlegend"]!.GetValue<bool>().Should().BeTrue();
        layout["xaxis"]!["rangeslider"]!["visible"]!.GetValue<bool>().Should().BeFalse();
        layout["title"]!["yref"]!.GetValue<string>().Should().Be("paper");
        layout["title"]!["y"]!.GetValue<double>().Should().Be(1.0);
        layout["title"]!["yanchor"]!.GetValue<string>().Should().Be("bottom");
        layout["title"]!["pad"]!["b"]!.GetValue<int>().Should().Be(44);
        layout["legend"]!["y"]!.GetValue<double>().Should().Be(1.0);
        layout["legend"]!["yanchor"]!.GetValue<string>().Should().Be("bottom");
        layout["margin"]!["t"]!.GetValue<int>().Should().Be(76);

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

        JsonObject env = CandlestickChartBuilder.Build("005930", "day", candles, indicators, new[] { ma5 });

        JsonArray y = env["spec"]!["data"]![1]!["y"]!.AsArray();
        y[0].Should().BeNull();
        y[3].Should().BeNull();
        y[4].Should().NotBeNull();
    }

    [Fact]
    public void Build_XAxis_TicksAreEvenlySpacedSubsetOfRawX()
    {
        var candles = SampleCandles(30);
        JsonObject env = CandlestickChartBuilder.Build(
            "005930", "day", candles,
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        JsonObject xaxis = env["spec"]!["layout"]!["xaxis"]!.AsObject();
        xaxis["type"]!.GetValue<string>().Should().Be("category");
        xaxis["tickmode"]!.GetValue<string>().Should().Be("array");

        JsonArray tickvals = xaxis["tickvals"]!.AsArray();
        JsonArray ticktext = xaxis["ticktext"]!.AsArray();
        tickvals.Count.Should().Be(ticktext.Count);
        tickvals.Count.Should().BeLessThanOrEqualTo(8).And.BeGreaterThan(1);

        // Every tick value must be a verbatim entry of the candlestick trace's x
        // (category axis matches ticks by string equality).
        var traceX = env["spec"]!["data"]![0]!["x"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToHashSet();
        tickvals.Select(n => n!.GetValue<string>()).Should().OnlyContain(v => traceX.Contains(v));

        // Labels are the short MM/dd form, not the raw ISO string.
        ticktext[0]!.GetValue<string>().Should().MatchRegex(@"^\d{2}/\d{2}$");
    }

    [Fact]
    public void Build_Layout_HasPeriodHighAndLowAnnotations()
    {
        var candles = new List<Candle>
        {
            new(new DateTime(2026, 3, 2), 100, 110, 90, 105, 1000),
            new(new DateTime(2026, 3, 3), 105, 180, 95, 150, 1100),  // period high 180
            new(new DateTime(2026, 3, 4), 150, 160, 40, 70, 1200),   // period low 40
            new(new DateTime(2026, 3, 5), 70, 120, 65, 115, 1300),
        };
        JsonObject env = CandlestickChartBuilder.Build(
            "005930", "day", candles,
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        JsonArray annotations = env["spec"]!["layout"]!["annotations"]!.AsArray();
        annotations.Count.Should().Be(2);

        JsonObject high = annotations[0]!.AsObject();
        high["text"]!.GetValue<string>().Should().Contain("최고").And.Contain("180");
        high["y"]!.GetValue<decimal>().Should().Be(180);
        high["x"]!.GetValue<string>().Should().Be("2026-03-03");

        JsonObject low = annotations[1]!.AsObject();
        low["text"]!.GetValue<string>().Should().Contain("최저").And.Contain("40");
        low["y"]!.GetValue<decimal>().Should().Be(40);
        low["x"]!.GetValue<string>().Should().Be("2026-03-04");
    }

    [Fact]
    public void Build_MaOverlay_FirstLineUsesNaverGreen()
    {
        var candles = SampleCandles(20);
        IndicatorSpec ma5 = IndicatorSpecParser.Parse("ma:5");
        var indicators = new IndicatorService().Compute(candles, new[] { ma5 });

        JsonObject env = CandlestickChartBuilder.Build("005930", "day", candles, indicators, new[] { ma5 });

        env["spec"]!["data"]![1]!["line"]!["color"]!.GetValue<string>().Should().Be("#2F9E44");
    }

    [Fact]
    public void Build_Title_PrefersNameWhenProvided()
    {
        var empty = new Dictionary<string, IReadOnlyList<double?>>();

        JsonObject withName = CandlestickChartBuilder.Build(
            "005930", "day", SampleCandles(), empty, Array.Empty<IndicatorSpec>(), name: "삼성전자");
        withName["spec"]!["layout"]!["title"]!["text"]!.GetValue<string>()
            .Should().Be("삼성전자 (005930) — 일봉");

        JsonObject noName = CandlestickChartBuilder.Build(
            "005930", "day", SampleCandles(), empty, Array.Empty<IndicatorSpec>());
        noName["spec"]!["layout"]!["title"]!["text"]!.GetValue<string>()
            .Should().Be("005930 — 일봉");
    }
}
