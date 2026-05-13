using System.Globalization;
using RedoxNet.LsOpenApi.Core.Models;

namespace RedoxNet.LsOpenApi.Core.Indicators;

/// <summary>
/// Builds a <see cref="ChartContext"/> from already-fetched candles and
/// indicator values. Pure computation; no I/O.
/// </summary>
public static class ChartContextBuilder
{
    /// <summary>Slope threshold (fraction of base value) under which a MA is reported as <c>"flat"</c>.</summary>
    public const double FlatSlopeThreshold = 0.001;

    /// <summary>How many bars back the trend window looks. 5 by default per spec.</summary>
    public const int TrendWindow = 5;

    /// <summary>
    /// Builds a context block. Safe to call on empty inputs (returns a zeroed context).
    /// </summary>
    /// <param name="candles">Ordered candle list (oldest first).</param>
    /// <param name="indicators">Indicator series keyed by spec, aligned 1:1 with <paramref name="candles"/>.</param>
    /// <param name="specs">Parsed indicator specs (used to discover which series are moving averages).</param>
    /// <returns>The computed <see cref="ChartContext"/>.</returns>
    public static ChartContext Build(
        IReadOnlyList<Candle> candles,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicators,
        IReadOnlyList<IndicatorSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(indicators);
        ArgumentNullException.ThrowIfNull(specs);

        if (candles.Count == 0)
        {
            return new ChartContext(
                DivergenceFromMa: new Dictionary<string, double>(),
                Volume: new VolumeContext(0, null, null, null, null),
                Drawdown: new DrawdownInfo(0, default, 0, 0),
                MaTrend: new Dictionary<string, string>(),
                BullishAlignment: null);
        }

        // Last close once.
        double lastClose = (double)candles[^1].Close;

        // 1) Identify MA-like specs (ma:N and ema:N). Map raw spec -> period for ordering.
        var maPeriods = new List<(string Key, int Period, IReadOnlyList<double?> Series)>();
        foreach (IndicatorSpec spec in specs)
        {
            if (spec.Kind is not ("ma" or "ema"))
                continue;
            if (!indicators.TryGetValue(spec.Raw, out IReadOnlyList<double?>? series))
                continue;
            if (spec.Args.Count < 1)
                continue;
            maPeriods.Add((spec.Raw, (int)spec.Args[0], series));
        }

        // 2) Divergence from each MA — only when the MA has a non-null last value.
        var divergence = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string key, _, IReadOnlyList<double?> series) in maPeriods)
        {
            if (series.Count == 0) continue;
            double? lastMa = series[^1];
            if (lastMa is null or 0) continue;
            divergence[key] = Math.Round((lastClose - lastMa.Value) / lastMa.Value * 100.0, 4);
        }

        // 3) Volume context.
        long latestVolume = candles[^1].Volume;
        long? avg20 = AverageVolume(candles, 20);
        long? avg60 = AverageVolume(candles, 60);
        var volume = new VolumeContext(
            Latest: latestVolume,
            Avg20: avg20,
            Ratio20: avg20 is > 0 ? Math.Round(latestVolume / (double)avg20.Value, 4) : null,
            Avg60: avg60,
            Ratio60: avg60 is > 0 ? Math.Round(latestVolume / (double)avg60.Value, 4) : null);

        // 4) Drawdown — period high in the fetched window.
        int highIndex = 0;
        decimal periodHigh = candles[0].High;
        for (int i = 1; i < candles.Count; i++)
        {
            if (candles[i].High > periodHigh)
            {
                periodHigh = candles[i].High;
                highIndex = i;
            }
        }
        decimal current = candles[^1].Close;
        double dd = periodHigh == 0 ? 0 : Math.Round((double)((current - periodHigh) / periodHigh) * 100.0, 4);
        var drawdown = new DrawdownInfo(
            PeriodHigh: periodHigh,
            PeriodHighDate: candles[highIndex].Date,
            Current: current,
            Pct: dd);

        // 5) MA trend over the last TrendWindow bars.
        var maTrend = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, _, IReadOnlyList<double?> series) in maPeriods)
        {
            maTrend[key] = ComputeTrend(series);
        }

        // 6) Bullish alignment — all MAs in ascending order by period: shorter MA ≥ longer MA.
        // Tristate: null when at least one MA has no last value (warm-up) — distinguishes
        // "stack is bearish" from "stack cannot yet be judged".
        bool? alignment = null;
        if (maPeriods.Count >= 2)
        {
            var ordered = maPeriods
                .OrderBy(t => t.Period)
                .Select(t => t.Series.Count > 0 ? t.Series[^1] : null)
                .ToList();

            if (ordered.All(v => v.HasValue))
            {
                bool stacked = true;
                for (int i = 0; i < ordered.Count - 1; i++)
                {
                    if (ordered[i]!.Value < ordered[i + 1]!.Value)
                    {
                        stacked = false;
                        break;
                    }
                }
                alignment = stacked;
            }
        }

        return new ChartContext(divergence, volume, drawdown, maTrend, alignment);
    }

    static long? AverageVolume(IReadOnlyList<Candle> candles, int window)
    {
        if (candles.Count < window)
            return null;
        long sum = 0;
        for (int i = candles.Count - window; i < candles.Count; i++)
            sum += candles[i].Volume;
        return sum / window;
    }

    static string ComputeTrend(IReadOnlyList<double?> series)
    {
        if (series.Count == 0) return "flat";
        double? latest = series[^1];
        if (latest is null) return "flat";

        int lookbackIndex = Math.Max(0, series.Count - 1 - TrendWindow);
        // Walk backwards if the lookback point is null, to find the nearest valid value.
        double? past = null;
        for (int i = lookbackIndex; i >= 0; i--)
        {
            if (series[i].HasValue)
            {
                past = series[i];
                break;
            }
        }
        if (past is null or 0) return "flat";

        double change = (latest.Value - past.Value) / Math.Abs(past.Value);
        if (change > FlatSlopeThreshold) return "up";
        if (change < -FlatSlopeThreshold) return "down";
        return "flat";
    }
}
