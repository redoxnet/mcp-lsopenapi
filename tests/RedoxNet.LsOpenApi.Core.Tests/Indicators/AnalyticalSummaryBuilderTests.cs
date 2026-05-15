using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Indicators;

public class AnalyticalSummaryBuilderTests
{
    static IReadOnlyList<Candle> RisingCandles(int count)
    {
        var candles = new List<Candle>(count);
        DateTime d = new(2024, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal close = 1000 + i * 10;
            candles.Add(new Candle(
                d.AddDays(i),
                close - 2,
                close + 5,
                close - 5,
                close,
                1000 + i));
        }
        return candles;
    }

    [Fact]
    public void Build_ComputesMovingAverageDeviationAndSlope()
    {
        AnalyticalSummary summary = AnalyticalSummaryBuilder.Build(
            "005930",
            "삼성전자",
            "day",
            RisingCandles(80));

        summary.Symbol.Should().Be("005930");
        summary.Name.Should().Be("삼성전자");
        summary.BarCount.Should().Be(80);
        summary.MovingAverages.Should().ContainKey("MA60");
        summary.MovingAverages["MA60"].Should().NotBeNull();
        summary.Ma60DeviationPct.Should().BePositive();
        summary.Ma60Slope.Should().Be("rising");
        summary.DrawdownFromPeakPct.Should().BeNegative();
    }

    [Fact]
    public void Build_KeyTurnsAlternateBoundedAndChronological()
    {
        var candles = new List<Candle>();
        DateTime d = new(2024, 1, 1);
        decimal[] closes =
        [
            100, 105, 110, 104, 99, 108, 116, 109, 101, 112,
            121, 114, 104, 118, 129, 120, 111, 126, 138, 127,
            115, 130, 142, 131, 119, 135, 148, 136, 122, 139
        ];

        for (int i = 0; i < closes.Length; i++)
        {
            decimal close = closes[i];
            candles.Add(new Candle(
                d.AddDays(i),
                close,
                close + 3,
                close - 3,
                close,
                1000));
        }

        AnalyticalSummary summary = AnalyticalSummaryBuilder.Build("005930", null, "day", candles);

        summary.KeyTurns.Should().NotBeEmpty();
        summary.KeyTurns.Count.Should().BeLessThanOrEqualTo(10);
        summary.KeyTurns.Select(p => p.Date).Should().BeInAscendingOrder();

        // Pivots strictly alternate peak/trough.
        for (int i = 1; i < summary.KeyTurns.Count; i++)
            summary.KeyTurns[i].Kind.Should().NotBe(summary.KeyTurns[i - 1].Kind);

        // Only the trailing pivot is tentative — the in-progress swing.
        summary.KeyTurns[^1].IsConfirmed.Should().BeFalse();
        summary.KeyTurns.Take(summary.KeyTurns.Count - 1).Should().OnlyContain(p => p.IsConfirmed);
    }

    [Fact]
    public void Build_NarrowWindow_FlagsInsufficientIndicatorsWithNote()
    {
        // 30 daily candles is too short for MA60/120/200, MA60 slope, 1Y/5Y.
        AnalyticalSummary summary = AnalyticalSummaryBuilder.Build(
            "005930",
            null,
            "day",
            RisingCandles(30),
            displayBarCount: 30,
            warmupApplied: false);

        summary.Coverage.WarmupApplied.Should().BeFalse();
        summary.Coverage.AnalyticalBarCount.Should().Be(30);
        summary.Coverage.DisplayBarCount.Should().Be(30);

        summary.Coverage.Status["MA20"].Should().Be("ok");
        summary.Coverage.Status["MA60"].Should().Be("insufficient_data");
        summary.Coverage.Status["MA200"].Should().Be("insufficient_data");
        summary.Coverage.Status["ma60_slope"].Should().Be("insufficient_data");
        summary.Coverage.Status["change_1y"].Should().Be("insufficient_data");
        summary.Coverage.Status["key_turns"].Should().Be("ok");

        summary.Coverage.Note.Should().NotBeNull();
        summary.Coverage.Note.Should().Contain("with_warmup=true");
    }

    [Fact]
    public void Build_DeepHistoryMonth_AllStatusesOkAndNoNote()
    {
        // 130 month bars covers MA120 + 5Y change + slope.
        AnalyticalSummary summary = AnalyticalSummaryBuilder.Build(
            "005930",
            null,
            "month",
            RisingCandles(130),
            displayBarCount: 60,
            warmupApplied: true);

        summary.Coverage.WarmupApplied.Should().BeTrue();
        summary.Coverage.AnalyticalBarCount.Should().Be(130);
        summary.Coverage.DisplayBarCount.Should().Be(60);

        summary.Coverage.Status.Values.Should().OnlyContain(v => v == "ok");
        summary.Coverage.Note.Should().BeNull();
    }

    [Fact]
    public void Build_TickPeriod_ChangeOverYearIsDisabled()
    {
        var candles = new List<Candle>();
        DateTime t = new(2024, 1, 1, 9, 0, 0);
        for (int i = 0; i < 30; i++)
        {
            decimal price = 1000 + i;
            candles.Add(new Candle(t.AddSeconds(i), price, price, price, price, 100));
        }

        AnalyticalSummary summary = AnalyticalSummaryBuilder.Build(
            "005930",
            null,
            "tick",
            candles);

        summary.Coverage.Status["change_1y"].Should().Be("disabled");
        summary.Coverage.Status["change_5y"].Should().Be("disabled");
    }
}
