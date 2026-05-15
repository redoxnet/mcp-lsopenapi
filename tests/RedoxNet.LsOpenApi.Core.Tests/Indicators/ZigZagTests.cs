using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Indicators;

public class ZigZagTests
{
    static readonly DateTime Origin = new(2024, 1, 1);

    /// <summary>A degenerate candle where open = high = low = close, so reversal
    /// triggers depend purely on the close.</summary>
    static Candle Flat(int day, decimal price) =>
        new(Origin.AddDays(day), price, price, price, price, 1000);

    static IReadOnlyList<Candle> FlatSeries(params decimal[] closes)
    {
        var list = new List<Candle>(closes.Length);
        for (int i = 0; i < closes.Length; i++)
            list.Add(Flat(i, closes[i]));
        return list;
    }

    static ZigZagOptions Percent(double threshold, int maxPivots = 10) =>
        new() { ReversalThreshold = threshold, Mode = ThresholdMode.Percent, MaxPivots = maxPivots };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Compute_TooFewCandles_ReturnsEmpty(int count)
    {
        var candles = Enumerable.Range(0, count).Select(i => Flat(i, 100)).ToList();

        ZigZag.Compute(candles, Percent(0.04)).Should().BeEmpty();
    }

    [Fact]
    public void Compute_NonPositiveThreshold_ReturnsEmpty()
    {
        IReadOnlyList<Candle> candles = FlatSeries(100, 120, 90, 130);

        ZigZag.Compute(candles, Percent(0)).Should().BeEmpty();
    }

    [Fact]
    public void Compute_DetectsStrictlyAlternatingPivots()
    {
        // Up 10%, down ~14%, up ~37%, down ~31% — every leg clears the 4% threshold.
        IReadOnlyList<Candle> candles = FlatSeries(100, 110, 95, 130, 90);

        IReadOnlyList<ZigZagPivot> pivots = ZigZag.Compute(candles, Percent(0.04));

        pivots.Select(p => p.Kind).Should().Equal(
            PivotKind.Trough, PivotKind.Peak, PivotKind.Trough, PivotKind.Peak, PivotKind.Trough);
        pivots.Select(p => p.Index).Should().Equal(0, 1, 2, 3, 4);
        pivots.Select(p => p.Price).Should().Equal(100m, 110m, 95m, 130m, 90m);

        // Every pivot except the trailing one is confirmed.
        pivots.Take(pivots.Count - 1).Should().OnlyContain(p => p.IsConfirmed);
        pivots[^1].IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public void Compute_TentativePivotSitsAtLastBar()
    {
        IReadOnlyList<Candle> candles = FlatSeries(100, 103, 106, 109);

        IReadOnlyList<ZigZagPivot> pivots = ZigZag.Compute(candles, Percent(0.04));

        ZigZagPivot tentative = pivots[^1];
        tentative.IsConfirmed.Should().BeFalse();
        tentative.Index.Should().Be(candles.Count - 1);
        tentative.Kind.Should().Be(PivotKind.Peak, "the series is still rising at the last bar");
        tentative.Price.Should().Be(109m);
    }

    [Fact]
    public void Compute_NoLegClearsThreshold_ReturnsOnlyTentativePivot()
    {
        // Drifts within a 1% band — never reverses past the 10% threshold.
        IReadOnlyList<Candle> candles = FlatSeries(100m, 100.5m, 101m, 100.5m);

        IReadOnlyList<ZigZagPivot> pivots = ZigZag.Compute(candles, Percent(0.10));

        pivots.Should().ContainSingle();
        pivots[0].IsConfirmed.Should().BeFalse();
        pivots[0].Index.Should().Be(candles.Count - 1);
        pivots[0].Kind.Should().Be(PivotKind.Peak, "the net move from the first bar is upward");
    }

    [Fact]
    public void Compute_ChangePctFromPrevMeasuresLegSize()
    {
        IReadOnlyList<Candle> candles = FlatSeries(100, 110, 95, 130, 90);

        IReadOnlyList<ZigZagPivot> pivots = ZigZag.Compute(candles, Percent(0.04));

        // First pivot is referenced against the first bar's close.
        pivots[0].ChangePctFromPrev.Should().Be(0m);
        pivots[1].ChangePctFromPrev.Should().Be(10m);            // 100 -> 110
        pivots[2].ChangePctFromPrev.Should().BeApproximately(-13.6364m, 0.001m); // 110 -> 95
        pivots[3].ChangePctFromPrev.Should().BeApproximately(36.8421m, 0.001m);  // 95 -> 130
        pivots[4].ChangePctFromPrev.Should().BeApproximately(-30.7692m, 0.001m); // 130 -> 90 (tentative)
    }

    [Fact]
    public void Compute_RespectsMaxPivots_KeepingMostRecent()
    {
        // A long sawtooth: alternating ±15% legs.
        var closes = new List<decimal> { 100 };
        for (int i = 0; i < 12; i++)
            closes.Add(i % 2 == 0 ? closes[^1] * 1.15m : closes[^1] * 0.85m);

        IReadOnlyList<ZigZagPivot> pivots = ZigZag.Compute(FlatSeries(closes.ToArray()), Percent(0.04, maxPivots: 4));

        pivots.Should().HaveCount(4);
        pivots[^1].IsConfirmed.Should().BeFalse();
        // Still chronological and still alternating after the cap.
        pivots.Select(p => p.Index).Should().BeInAscendingOrder();
        for (int i = 1; i < pivots.Count; i++)
            pivots[i].Kind.Should().NotBe(pivots[i - 1].Kind);
    }

    [Fact]
    public void Compute_WideRangeBarDoesNotEmitDuplicatePeak()
    {
        // Regression: a single wide-range bar can legitimately represent both
        // the peak and the trough of a swing (a month where price both surged
        // and crashed). It must not, however, get re-emitted as the same peak
        // twice — the original asymmetric reset left `hi` stale on trough
        // confirmation, so a later bar whose close fell threshold-far below
        // the stale peak would confirm the same peak a second time.
        //
        // Hand-crafted reproducer matching the shape reported on 035720 monthly:
        //   index 1 is a wide-range bar  (high 70000, low 42000) — emits Peak then Trough
        //   index 2 reversal-up confirms the trough
        //   index 3 close falls back below the stale 70000 — must NOT re-confirm
        var candles = new List<Candle>
        {
            new(Origin.AddDays(0), 20000, 20500, 19500, 20000, 1000),
            new(Origin.AddDays(1), 50000, 70000, 42000, 55000, 1000),
            new(Origin.AddDays(2), 48000, 50000, 45000, 49000, 1000),
            new(Origin.AddDays(3), 47000, 55000, 44000, 46000, 1000),
        };

        IReadOnlyList<ZigZagPivot> pivots = ZigZag.Compute(candles, Percent(0.12));

        // A wide bar may emit one Peak and one Trough at its own index — but
        // never the same (index, kind) pair twice.
        var pairs = pivots.Select(p => (p.Index, p.Kind)).ToList();
        pairs.Should().OnlyHaveUniqueItems();

        // And the kinds must still strictly alternate after the fix.
        for (int i = 1; i < pivots.Count; i++)
            pivots[i].Kind.Should().NotBe(pivots[i - 1].Kind);
    }

    [Fact]
    public void Compute_AtrMode_DetectsAlternatingPivots()
    {
        // Oscillating closes with a small intrabar band; ATR mode should still
        // resolve the swings into alternating, chronological pivots.
        var candles = new List<Candle>();
        decimal[] closes = [100, 112, 94, 118, 92, 120, 90, 124];
        for (int i = 0; i < closes.Length; i++)
        {
            decimal c = closes[i];
            candles.Add(new Candle(Origin.AddDays(i), c, c + 1, c - 1, c, 1000));
        }

        var options = new ZigZagOptions
        {
            ReversalThreshold = 2.0,
            Mode = ThresholdMode.AtrMultiple,
            AtrPeriod = 3,
            MaxPivots = 10,
        };

        IReadOnlyList<ZigZagPivot> pivots = ZigZag.Compute(candles, options);

        pivots.Should().NotBeEmpty();
        pivots.Select(p => p.Index).Should().BeInAscendingOrder();
        for (int i = 1; i < pivots.Count; i++)
            pivots[i].Kind.Should().NotBe(pivots[i - 1].Kind);
        pivots[^1].IsConfirmed.Should().BeFalse();
    }
}
