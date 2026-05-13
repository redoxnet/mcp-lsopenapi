using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Indicators;

public class IndicatorServiceTests
{
    static List<Candle> SampleCandles(int count, decimal start = 100m, decimal step = 1m)
    {
        DateTime baseDate = new(2024, 1, 1);
        return Enumerable.Range(0, count)
            .Select(i => new Candle(
                Date: baseDate.AddDays(i),
                Open: start + step * i,
                High: start + step * i + 1,
                Low: start + step * i - 1,
                Close: start + step * i,
                Volume: 1000,
                Value: 100000))
            .ToList();
    }

    [Fact]
    public void Compute_NoSpecs_ReturnsEmpty()
    {
        var service = new IndicatorService();
        IReadOnlyDictionary<string, IReadOnlyList<double?>> result =
            service.Compute(SampleCandles(20), Array.Empty<IndicatorSpec>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void Compute_NoCandles_ReturnsEmpty()
    {
        var service = new IndicatorService();
        IReadOnlyDictionary<string, IReadOnlyList<double?>> result =
            service.Compute(Array.Empty<Candle>(), new[] { IndicatorSpecParser.Parse("ma:5") });

        result.Should().BeEmpty();
    }

    [Fact]
    public void Compute_Sma_AlignsWithCandlesAndWarmupNull()
    {
        var service = new IndicatorService();
        List<Candle> candles = SampleCandles(10);

        IReadOnlyDictionary<string, IReadOnlyList<double?>> result =
            service.Compute(candles, new[] { IndicatorSpecParser.Parse("ma:3") });

        IReadOnlyList<double?> sma = result["ma:3"];
        sma.Should().HaveCount(candles.Count);
        sma[0].Should().BeNull();
        sma[1].Should().BeNull();
        sma[2].Should().NotBeNull();
        // Closes are 100, 101, 102 — SMA(3) at index 2 is 101.
        sma[2]!.Value.Should().BeApproximately(101.0, 1e-9);
    }

    [Fact]
    public void Compute_Macd_ReturnsThreeSeries()
    {
        var service = new IndicatorService();
        List<Candle> candles = SampleCandles(60);

        IReadOnlyDictionary<string, IReadOnlyList<double?>> result =
            service.Compute(candles, new[] { IndicatorSpecParser.Parse("macd:12,26,9") });

        result.Should().ContainKey("macd:12,26,9.macd");
        result.Should().ContainKey("macd:12,26,9.signal");
        result.Should().ContainKey("macd:12,26,9.histogram");
        result["macd:12,26,9.macd"].Should().HaveCount(candles.Count);
    }

    [Fact]
    public void Compute_Bollinger_ReturnsThreeBands()
    {
        var service = new IndicatorService();
        List<Candle> candles = SampleCandles(40);

        IReadOnlyDictionary<string, IReadOnlyList<double?>> result =
            service.Compute(candles, new[] { IndicatorSpecParser.Parse("bb:20,2") });

        result.Should().ContainKey("bb:20,2.lower");
        result.Should().ContainKey("bb:20,2.middle");
        result.Should().ContainKey("bb:20,2.upper");
    }

    [Fact]
    public void Compute_MultipleSpecs_BothComputed()
    {
        var service = new IndicatorService();
        List<Candle> candles = SampleCandles(30);

        IReadOnlyDictionary<string, IReadOnlyList<double?>> result =
            service.Compute(candles, new[]
            {
                IndicatorSpecParser.Parse("ma:5"),
                IndicatorSpecParser.Parse("rsi:14"),
            });

        result.Should().ContainKeys("ma:5", "rsi:14");
    }
}
