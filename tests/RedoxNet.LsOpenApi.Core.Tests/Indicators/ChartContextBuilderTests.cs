using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Indicators;

public class ChartContextBuilderTests
{
    static List<Candle> Ramp(int count, decimal start = 100m, decimal step = 1m, long volume = 1000)
    {
        DateTime baseDate = new(2024, 1, 1);
        return Enumerable.Range(0, count)
            .Select(i => new Candle(
                Date: baseDate.AddDays(i),
                Open: start + step * i,
                High: start + step * i + 1m,
                Low: start + step * i - 1m,
                Close: start + step * i,
                Volume: volume,
                Value: 100000))
            .ToList();
    }

    [Fact]
    public void Build_EmptyCandles_ReturnsZeroedContext()
    {
        ChartContext ctx = ChartContextBuilder.Build(
            Array.Empty<Candle>(),
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        ctx.Volume.Latest.Should().Be(0);
        ctx.BullishAlignment.Should().BeFalse();
        ctx.DivergenceFromMa.Should().BeEmpty();
        ctx.MaTrend.Should().BeEmpty();
    }

    [Fact]
    public void Build_DivergenceFromMa_ComputedCorrectly()
    {
        // 30 candles, close = 100..129. Last close = 129. SMA(5) at index 29 = mean(125..129) = 127.
        // Divergence = (129 - 127) / 127 * 100 ≈ 1.5748%
        List<Candle> candles = Ramp(30);
        var service = new IndicatorService();
        IndicatorSpec ma5 = IndicatorSpecParser.Parse("ma:5");
        var indicators = service.Compute(candles, new[] { ma5 });

        ChartContext ctx = ChartContextBuilder.Build(candles, indicators, new[] { ma5 });

        ctx.DivergenceFromMa.Should().ContainKey("ma:5");
        ctx.DivergenceFromMa["ma:5"].Should().BeApproximately(1.5748, 0.01);
    }

    [Fact]
    public void Build_VolumeAvg20_NullWhenFewerCandles()
    {
        List<Candle> candles = Ramp(10);
        ChartContext ctx = ChartContextBuilder.Build(
            candles,
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        ctx.Volume.Avg20.Should().BeNull();
        ctx.Volume.Ratio20.Should().BeNull();
        ctx.Volume.Avg60.Should().BeNull();
    }

    [Fact]
    public void Build_VolumeAvgAndRatio_OnSufficientCandles()
    {
        // 20 candles, all volume = 1000. Latest = 1000, avg_20 = 1000, ratio = 1.0
        List<Candle> candles = Ramp(20, volume: 1000);
        ChartContext ctx = ChartContextBuilder.Build(
            candles,
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        ctx.Volume.Latest.Should().Be(1000);
        ctx.Volume.Avg20.Should().Be(1000);
        ctx.Volume.Ratio20.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void Build_Drawdown_TrackedFromPeriodHigh()
    {
        // Build a custom series with a clear peak in the middle, then decline.
        var candles = new List<Candle>();
        DateTime d = new(2024, 1, 1);
        decimal[] highs = { 100m, 105m, 110m, 115m, 120m /* peak */, 115m, 110m, 105m };
        for (int i = 0; i < highs.Length; i++)
            candles.Add(new Candle(d.AddDays(i), highs[i], highs[i], highs[i] - 5, highs[i] - 2, 1000));

        ChartContext ctx = ChartContextBuilder.Build(
            candles,
            new Dictionary<string, IReadOnlyList<double?>>(),
            Array.Empty<IndicatorSpec>());

        ctx.Drawdown.PeriodHigh.Should().Be(120m);
        ctx.Drawdown.PeriodHighDate.Should().Be(d.AddDays(4));
        ctx.Drawdown.Current.Should().Be(103m); // last close = 105 - 2
        // (103 - 120) / 120 * 100 = -14.1667
        ctx.Drawdown.Pct.Should().BeApproximately(-14.1667, 0.01);
    }

    [Fact]
    public void Build_MaTrend_UpWhenSeriesRising()
    {
        List<Candle> candles = Ramp(30);
        var service = new IndicatorService();
        IndicatorSpec ma5 = IndicatorSpecParser.Parse("ma:5");
        var indicators = service.Compute(candles, new[] { ma5 });

        ChartContext ctx = ChartContextBuilder.Build(candles, indicators, new[] { ma5 });

        ctx.MaTrend["ma:5"].Should().Be("up");
    }

    [Fact]
    public void Build_MaTrend_DownWhenSeriesFalling()
    {
        List<Candle> candles = Ramp(30, start: 200m, step: -2m);
        var service = new IndicatorService();
        IndicatorSpec ma5 = IndicatorSpecParser.Parse("ma:5");
        var indicators = service.Compute(candles, new[] { ma5 });

        ChartContext ctx = ChartContextBuilder.Build(candles, indicators, new[] { ma5 });

        ctx.MaTrend["ma:5"].Should().Be("down");
    }

    [Fact]
    public void Build_BullishAlignment_TrueWhenShortAboveLong()
    {
        // Rising series → MA(5) ≥ MA(12) ≥ MA(20) (because shorter MA tracks faster on rising).
        List<Candle> candles = Ramp(40);
        var service = new IndicatorService();
        IndicatorSpec[] specs =
        {
            IndicatorSpecParser.Parse("ma:5"),
            IndicatorSpecParser.Parse("ma:12"),
            IndicatorSpecParser.Parse("ma:20"),
        };
        var indicators = service.Compute(candles, specs);

        ChartContext ctx = ChartContextBuilder.Build(candles, indicators, specs);

        ctx.BullishAlignment.Should().BeTrue();
    }

    [Fact]
    public void Build_BullishAlignment_FalseOnFallingSeries()
    {
        List<Candle> candles = Ramp(40, start: 200m, step: -2m);
        var service = new IndicatorService();
        IndicatorSpec[] specs =
        {
            IndicatorSpecParser.Parse("ma:5"),
            IndicatorSpecParser.Parse("ma:12"),
            IndicatorSpecParser.Parse("ma:20"),
        };
        var indicators = service.Compute(candles, specs);

        ChartContext ctx = ChartContextBuilder.Build(candles, indicators, specs);

        ctx.BullishAlignment.Should().BeFalse();
    }

    [Fact]
    public void Build_BullishAlignment_FalseWithFewerThanTwoMas()
    {
        List<Candle> candles = Ramp(30);
        var service = new IndicatorService();
        IndicatorSpec[] specs = { IndicatorSpecParser.Parse("ma:5") };
        var indicators = service.Compute(candles, specs);

        ChartContext ctx = ChartContextBuilder.Build(candles, indicators, specs);

        ctx.BullishAlignment.Should().BeFalse();
    }
}
