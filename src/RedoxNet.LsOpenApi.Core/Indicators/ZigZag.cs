using RedoxNet.LsOpenApi.Core.Models;

namespace RedoxNet.LsOpenApi.Core.Indicators;

/// <summary>How a <see cref="ZigZagOptions.ReversalThreshold"/> is interpreted.</summary>
public enum ThresholdMode
{
    /// <summary>Reversal expressed as a fraction of the running extremum (0.12 = 12%).</summary>
    Percent,

    /// <summary>Reversal expressed as a multiple of ATR, for adaptive volatility scaling.</summary>
    AtrMultiple,
}

/// <summary>Configuration for <see cref="ZigZag"/> swing-pivot detection.</summary>
public sealed class ZigZagOptions
{
    /// <summary>
    /// Minimum reversal magnitude. Interpretation depends on <see cref="Mode"/>:
    /// <see cref="ThresholdMode.Percent"/> — a literal fraction (e.g. 0.12 = 12%);
    /// <see cref="ThresholdMode.AtrMultiple"/> — multiples of ATR(<see cref="AtrPeriod"/>).
    /// </summary>
    public double ReversalThreshold { get; init; }

    /// <summary>Threshold interpretation. Defaults to <see cref="ThresholdMode.Percent"/>.</summary>
    public ThresholdMode Mode { get; init; } = ThresholdMode.Percent;

    /// <summary>ATR look-back; used only when <see cref="Mode"/> is <see cref="ThresholdMode.AtrMultiple"/>.</summary>
    public int AtrPeriod { get; init; } = 14;

    /// <summary>Final cap on emitted pivots; the most recent ones are kept.</summary>
    public int MaxPivots { get; init; } = 10;
}

/// <summary>One detected swing pivot, identified by candle index.</summary>
/// <param name="Index">Index into the source candle list.</param>
/// <param name="Price">Pivot price — the swing high for a peak, the swing low for a trough; the latest close for the tentative pivot.</param>
/// <param name="Kind">Peak or trough.</param>
/// <param name="ChangePctFromPrev">Percent move from the previous pivot (from the first bar's close for the first pivot).</param>
/// <param name="IsConfirmed"><see langword="false"/> for the trailing tentative pivot at the latest bar.</param>
public readonly record struct ZigZagPivot(
    int Index,
    decimal Price,
    PivotKind Kind,
    decimal ChangePctFromPrev,
    bool IsConfirmed);

/// <summary>
/// Threshold-reversal ZigZag swing detector.
/// </summary>
/// <remarks>
/// Walks the candle series tracking the running extreme of the current leg. Once
/// a <em>close</em> retraces from that extreme by at least the configured
/// threshold, the extreme is confirmed as a pivot and the leg direction flips.
/// Triggering on the close (rather than the intrabar high/low) keeps a single
/// wide-range bar from self-triggering a spurious pivot, while pivot <em>prices</em>
/// still come from the actual swing high/low.
///
/// Emitted pivots strictly alternate peak/trough. The trailing pivot is always
/// tentative (<see cref="ZigZagPivot.IsConfirmed"/> = <see langword="false"/>):
/// it sits at the latest bar and represents the provisional endpoint of the
/// in-progress swing, which may still extend before it reverses.
/// </remarks>
public static class ZigZag
{
    /// <summary>
    /// Detects swing pivots over <paramref name="candles"/>. Returns an empty list
    /// when there are fewer than two candles or the threshold is non-positive.
    /// </summary>
    public static IReadOnlyList<ZigZagPivot> Compute(IReadOnlyList<Candle> candles, ZigZagOptions options)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(options);
        if (candles.Count < 2 || options.ReversalThreshold <= 0)
            return Array.Empty<ZigZagPivot>();

        decimal[]? atr = options.Mode == ThresholdMode.AtrMultiple
            ? AverageTrueRange(candles, Math.Max(1, options.AtrPeriod))
            : null;

        var confirmed = new List<(int Index, decimal Price, PivotKind Kind)>();

        // hi/lo track the running extreme of the current leg; dir is the leg
        // direction: 0 = unknown (seed phase), +1 = up-leg, -1 = down-leg.
        int hiIdx = 0, loIdx = 0;
        decimal hi = candles[0].High, lo = candles[0].Low;
        int dir = 0;

        for (int i = 1; i < candles.Count; i++)
        {
            Candle c = candles[i];

            if (dir > 0)
            {
                // Up-leg: hi is the active peak candidate; lo tracks the pullback
                // low since hi last moved, which becomes the next trough. A new
                // high therefore also restarts the lo tracker.
                if (c.High > hi) { hi = c.High; hiIdx = i; lo = c.Low; loIdx = i; }
                else if (c.Low < lo) { lo = c.Low; loIdx = i; }

                if (c.Close <= ReversalLevel(hi, atr, i, options, rising: false))
                {
                    confirmed.Add((hiIdx, hi, PivotKind.Peak));
                    dir = -1;
                    // Keep lo/loIdx — they already hold the lowest low since the
                    // peak, the correct trough candidate. Restart hi as the
                    // down-leg's bounce-high tracker so it cannot stay stale on
                    // the just-confirmed peak.
                    hi = c.High; hiIdx = i;
                }
            }
            else if (dir < 0)
            {
                // Down-leg: lo is the active trough candidate; hi tracks the
                // bounce high since lo last moved, which becomes the next peak.
                if (c.Low < lo) { lo = c.Low; loIdx = i; hi = c.High; hiIdx = i; }
                else if (c.High > hi) { hi = c.High; hiIdx = i; }

                if (c.Close >= ReversalLevel(lo, atr, i, options, rising: true))
                {
                    confirmed.Add((loIdx, lo, PivotKind.Trough));
                    dir = 1;
                    // Keep hi/hiIdx — the highest high since the trough. Restart
                    // lo as the up-leg's pullback tracker.
                    lo = c.Low; loIdx = i;
                }
            }
            else
            {
                // Seed phase: track both extremes until the first close clears a
                // threshold and fixes the initial leg direction. Neither tracker
                // is yet anchored to a confirmed pivot, so on the first flip both
                // restart from the confirmation bar.
                if (c.High > hi) { hi = c.High; hiIdx = i; }
                if (c.Low < lo) { lo = c.Low; loIdx = i; }

                if (c.Close <= ReversalLevel(hi, atr, i, options, rising: false))
                {
                    confirmed.Add((hiIdx, hi, PivotKind.Peak));
                    dir = -1;
                    hi = c.High; hiIdx = i;
                    lo = c.Low; loIdx = i;
                }
                else if (c.Close >= ReversalLevel(lo, atr, i, options, rising: true))
                {
                    confirmed.Add((loIdx, lo, PivotKind.Trough));
                    dir = 1;
                    hi = c.High; hiIdx = i;
                    lo = c.Low; loIdx = i;
                }
            }
        }

        int lastIdx = candles.Count - 1;
        decimal lastClose = candles[lastIdx].Close;
        PivotKind tentativeKind = dir switch
        {
            > 0 => PivotKind.Peak,
            < 0 => PivotKind.Trough,
            // No leg ever confirmed: classify the lone tentative pivot by net move.
            _ => lastClose >= candles[0].Close ? PivotKind.Peak : PivotKind.Trough,
        };

        var result = new List<ZigZagPivot>(confirmed.Count + 1);
        decimal prevPrice = candles[0].Close;
        foreach ((int index, decimal price, PivotKind kind) in confirmed)
        {
            result.Add(new ZigZagPivot(index, price, kind, Pct(price, prevPrice), IsConfirmed: true));
            prevPrice = price;
        }
        result.Add(new ZigZagPivot(lastIdx, lastClose, tentativeKind, Pct(lastClose, prevPrice), IsConfirmed: false));

        if (result.Count > options.MaxPivots && options.MaxPivots > 0)
            result = result.GetRange(result.Count - options.MaxPivots, options.MaxPivots);

        return result;
    }

    /// <summary>
    /// Reversal price level for the current leg: <paramref name="extreme"/> moved
    /// by one threshold step. <paramref name="rising"/> selects the up-side level
    /// (above a trough) versus the down-side level (below a peak).
    /// </summary>
    static decimal ReversalLevel(decimal extreme, decimal[]? atr, int index, ZigZagOptions options, bool rising)
    {
        if (options.Mode == ThresholdMode.AtrMultiple && atr is not null)
        {
            decimal band = atr[index] * (decimal)options.ReversalThreshold;
            return rising ? extreme + band : extreme - band;
        }

        decimal pct = (decimal)options.ReversalThreshold;
        return rising ? extreme * (1m + pct) : extreme * (1m - pct);
    }

    /// <summary>
    /// Wilder's Average True Range, aligned 1:1 with <paramref name="candles"/>.
    /// Leading bars (before <paramref name="period"/> is reached) use the running
    /// mean of true range so the series is never null.
    /// </summary>
    static decimal[] AverageTrueRange(IReadOnlyList<Candle> candles, int period)
    {
        int n = candles.Count;
        var atr = new decimal[n];
        if (n == 0)
            return atr;

        decimal prevClose = candles[0].Close;
        decimal runningSum = 0;
        decimal wilder = 0;
        for (int i = 0; i < n; i++)
        {
            Candle c = candles[i];
            decimal trueRange = i == 0
                ? c.High - c.Low
                : Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prevClose), Math.Abs(c.Low - prevClose)));
            prevClose = c.Close;

            if (i < period)
            {
                runningSum += trueRange;
                wilder = runningSum / (i + 1);
            }
            else
            {
                wilder = (wilder * (period - 1) + trueRange) / period;
            }
            atr[i] = wilder;
        }
        return atr;
    }

    static decimal Pct(decimal value, decimal baseValue)
        => baseValue == 0 ? 0 : Math.Round((value - baseValue) / baseValue * 100m, 4, MidpointRounding.AwayFromZero);
}
