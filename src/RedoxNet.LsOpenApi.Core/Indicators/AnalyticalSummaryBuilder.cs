using RedoxNet.LsOpenApi.Core.Models;

namespace RedoxNet.LsOpenApi.Core.Indicators;

/// <summary>
/// Builds compact, model-facing chart summaries from already-trimmed candles.
/// </summary>
public static class AnalyticalSummaryBuilder
{
    const int MaxKeyTurns = 10;

    const string StatusOk = "ok";
    const string StatusInsufficient = "insufficient_data";
    const string StatusDisabled = "disabled";

    /// <summary>
    /// Computes a compact analytical summary over the supplied candle window.
    /// </summary>
    /// <param name="symbol">Short code.</param>
    /// <param name="name">Optional human-readable name.</param>
    /// <param name="period">Period label (<c>day</c>, <c>week</c>, <c>month</c>, …).</param>
    /// <param name="candles">
    /// Analytical candle series — typically the warm-up-inclusive series, so
    /// long-period indicators populate even when the display window is short.
    /// </param>
    /// <param name="displayBarCount">
    /// Bars visible to the caller after display trimming. Defaults to
    /// <c>candles.Count</c> when omitted.
    /// </param>
    /// <param name="warmupApplied">
    /// <see langword="true"/> when the summary warm-up policy padded
    /// <paramref name="candles"/> with extra leading history. Drives the
    /// <see cref="IndicatorCoverage.Note"/> hint so the model can offer to
    /// re-run with broader history.
    /// </param>
    public static AnalyticalSummary Build(
        string symbol,
        string? name,
        string period,
        IReadOnlyList<Candle> candles,
        int? displayBarCount = null,
        bool warmupApplied = false)
    {
        int display = displayBarCount ?? candles.Count;

        if (candles.Count == 0)
        {
            return new AnalyticalSummary(
                symbol,
                name,
                period,
                0,
                "",
                0,
                "",
                null,
                null,
                null,
                new Dictionary<string, decimal?>(),
                null,
                null,
                null,
                null,
                null,
                Array.Empty<InflectionPoint>(),
                new IndicatorCoverage(
                    WarmupApplied: warmupApplied,
                    AnalyticalBarCount: 0,
                    DisplayBarCount: display,
                    Status: new Dictionary<string, string>(),
                    Note: "No candles available; the analytical summary is empty."));
        }

        Candle latest = candles[^1];
        IReadOnlyDictionary<string, decimal?> movingAverages = BuildMovingAverages(period, candles);
        movingAverages.TryGetValue("MA60", out decimal? ma60);

        (decimal? peakHigh, string? peakHighDate, decimal? drawdownPct) = ComputePeakDrawdown(period, candles, latest);

        decimal? change1Y = ChangeFromBarsBack(candles, BarsPerYear(period));
        decimal? change5Y = ChangeFromBarsBack(candles, BarsPerYear(period) * 5);
        string? ma60Slope = ClassifyMaSlope(candles, 60, period);
        IReadOnlyList<InflectionPoint> keyTurns = BuildKeyTurns(candles, period);

        IndicatorCoverage coverage = BuildCoverage(
            period,
            candles.Count,
            display,
            warmupApplied,
            movingAverages,
            ma60Slope,
            change1Y,
            change5Y,
            keyTurns);

        return new AnalyticalSummary(
            Symbol: symbol,
            Name: name,
            Period: period,
            BarCount: candles.Count,
            DateRange: $"{FormatDate(candles[0].Date, period)} ~ {FormatDate(latest.Date, period)}",
            LatestClose: latest.Close,
            LatestDate: FormatDate(latest.Date, period),
            ChangePctFromPrev: ChangeFromBarsBack(candles, 1),
            ChangePct1Y: change1Y,
            ChangePct5Y: change5Y,
            MovingAverages: movingAverages,
            Ma60DeviationPct: ma60 is null ? null : Percent(latest.Close - ma60.Value, ma60.Value),
            Ma60Slope: ma60Slope,
            PeakHigh: peakHigh,
            PeakHighDate: peakHighDate,
            DrawdownFromPeakPct: drawdownPct,
            KeyTurns: keyTurns,
            Coverage: coverage);
    }

    /// <summary>
    /// Assembles the per-indicator availability map and an optional model-facing
    /// note. Status values: <c>ok</c>, <c>insufficient_data</c>, <c>disabled</c>.
    /// </summary>
    static IndicatorCoverage BuildCoverage(
        string period,
        int analyticalBarCount,
        int displayBarCount,
        bool warmupApplied,
        IReadOnlyDictionary<string, decimal?> movingAverages,
        string? ma60Slope,
        decimal? change1Y,
        decimal? change5Y,
        IReadOnlyList<InflectionPoint> keyTurns)
    {
        var status = new Dictionary<string, string>(StringComparer.Ordinal);

        // Moving averages: one entry per MA the period actually selects. MAs not
        // in the period's set are absent (rather than "disabled") because the
        // summary doesn't claim those should exist for this period.
        foreach ((string key, decimal? value) in movingAverages)
            status[key] = value is null ? StatusInsufficient : StatusOk;

        status["ma60_slope"] = ma60Slope is null ? StatusInsufficient : StatusOk;

        int barsPerYear = BarsPerYear(period);
        status["change_1y"] = barsPerYear == 0
            ? StatusDisabled
            : change1Y is null ? StatusInsufficient : StatusOk;
        status["change_5y"] = barsPerYear == 0
            ? StatusDisabled
            : change5Y is null ? StatusInsufficient : StatusOk;

        status["key_turns"] = keyTurns.Count == 0 ? StatusInsufficient : StatusOk;

        string[] missing = status
            .Where(kv => kv.Value == StatusInsufficient)
            .Select(kv => kv.Key)
            .ToArray();

        string? note = null;
        if (missing.Length > 0)
        {
            string list = string.Join(", ", missing);
            note = warmupApplied
                ? $"Insufficient history for: {list}. The analytical window covers {analyticalBarCount} {period} bar(s); these indicators need a longer series than what is available."
                : $"Narrow analysis window ({analyticalBarCount} {period} bar(s)); these long-period indicators are unavailable: {list}. Re-run without an explicit date range, or pass with_warmup=true, to populate them.";
        }

        return new IndicatorCoverage(
            WarmupApplied: warmupApplied,
            AnalyticalBarCount: analyticalBarCount,
            DisplayBarCount: displayBarCount,
            Status: status,
            Note: note);
    }

    static IReadOnlyDictionary<string, decimal?> BuildMovingAverages(string period, IReadOnlyList<Candle> candles)
    {
        int[] periods = period switch
        {
            "month" or "year" => [20, 60, 120],
            "week" => [20, 60],
            "day" => [5, 20, 60, 120, 200],
            _ => [20, 60],
        };

        var result = new Dictionary<string, decimal?>(StringComparer.Ordinal);
        foreach (int p in periods)
            result[$"MA{p}"] = SimpleMovingAverage(candles, p, candles.Count - 1);
        return result;
    }

    static decimal? SimpleMovingAverage(IReadOnlyList<Candle> candles, int period, int endIndex)
    {
        if (period <= 0 || endIndex < 0 || endIndex + 1 < period)
            return null;

        decimal sum = 0;
        int start = endIndex - period + 1;
        for (int i = start; i <= endIndex; i++)
            sum += candles[i].Close;
        return sum / period;
    }

    /// <summary>
    /// Classifies the moving-average slope as <c>rising</c>, <c>flat</c>, or
    /// <c>falling</c> by least-squares fitting a line through the MA values over
    /// the trailing look-back window. A regression fit is used rather than a
    /// two-point delta so a single noisy endpoint cannot flip the verdict.
    /// </summary>
    static string? ClassifyMaSlope(IReadOnlyList<Candle> candles, int period, string chartPeriod)
    {
        int lookback = chartPeriod switch
        {
            "month" or "year" => 3,
            "week" => 4,
            "day" => 5,
            _ => 5,
        };

        // Window of MA values: the latest plus `lookback` earlier points.
        int windowSize = lookback + 1;
        int last = candles.Count - 1;
        int first = last - windowSize + 1;
        if (first < 0)
            return null;

        var maValues = new List<decimal>(windowSize);
        for (int i = first; i <= last; i++)
        {
            decimal? ma = SimpleMovingAverage(candles, period, i);
            if (ma is null)
                return null;
            maValues.Add(ma.Value);
        }

        // Least-squares slope over x = 0..windowSize-1.
        int n = maValues.Count;
        decimal sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (int i = 0; i < n; i++)
        {
            decimal x = i;
            decimal y = maValues[i];
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumXX += x * x;
        }

        decimal denominator = n * sumXX - sumX * sumX;
        decimal meanY = sumY / n;
        if (denominator == 0 || meanY == 0)
            return null;

        decimal slopePerBar = (n * sumXY - sumX * sumY) / denominator;
        // Fitted change across the whole window, expressed as a percent of the
        // window's mean MA level — same units as the old two-point delta.
        decimal changePct = slopePerBar * (n - 1) / meanY * 100m;

        decimal threshold = chartPeriod is "month" or "year" ? 0.5m : 0.3m;
        if (changePct > threshold) return "rising";
        if (changePct < -threshold) return "falling";
        return "flat";
    }

    static (decimal? PeakHigh, string? PeakHighDate, decimal? DrawdownPct) ComputePeakDrawdown(
        string period,
        IReadOnlyList<Candle> candles,
        Candle latest)
    {
        Candle peak = candles[0];
        foreach (Candle c in candles)
        {
            if (c.High > peak.High)
                peak = c;
        }

        decimal? drawdown = peak.High == 0 ? null : Percent(latest.Close - peak.High, peak.High);
        return (peak.High, FormatDate(peak.Date, period), drawdown);
    }

    static decimal? ChangeFromBarsBack(IReadOnlyList<Candle> candles, int barsBack)
    {
        if (barsBack <= 0 || candles.Count <= barsBack)
            return null;

        decimal previous = candles[^(barsBack + 1)].Close;
        if (previous == 0)
            return null;
        return Percent(candles[^1].Close - previous, previous);
    }

    static int BarsPerYear(string period) => period switch
    {
        "day" => 252,
        "week" => 52,
        "month" => 12,
        "year" => 1,
        _ => 0,
    };

    /// <summary>
    /// Detects the notable swing turns over the candle window using a
    /// threshold-reversal <see cref="ZigZag"/>. The threshold is period-aware so
    /// the same notion of "notable" scales from intraday to yearly bars.
    /// </summary>
    static IReadOnlyList<InflectionPoint> BuildKeyTurns(IReadOnlyList<Candle> candles, string period)
    {
        IReadOnlyList<ZigZagPivot> pivots = ZigZag.Compute(candles, DefaultZigZagOptions(period));
        if (pivots.Count == 0)
            return Array.Empty<InflectionPoint>();

        var turns = new List<InflectionPoint>(pivots.Count);
        foreach (ZigZagPivot p in pivots)
        {
            turns.Add(new InflectionPoint(
                FormatDate(candles[p.Index].Date, period),
                p.Price,
                p.Kind,
                p.ChangePctFromPrev,
                p.IsConfirmed));
        }
        return turns;
    }

    /// <summary>
    /// Period-aware ZigZag defaults. The percentage threshold widens with the
    /// bar period so a "swing" on a yearly chart is not the same size as a swing
    /// on a minute chart. ATR mode is available via <see cref="ZigZagOptions"/>
    /// for callers mixing very different volatility regimes, but percent is the
    /// default because "a 12% reversal" reads more clearly than "a 3×ATR reversal".
    /// </summary>
    static ZigZagOptions DefaultZigZagOptions(string period)
    {
        double threshold = period switch
        {
            "year" => 0.20,
            "month" => 0.12,
            "week" => 0.08,
            "day" => 0.04,
            "min" => 0.015,
            "tick" => 0.01,
            _ => 0.04,
        };
        return new ZigZagOptions
        {
            ReversalThreshold = threshold,
            Mode = ThresholdMode.Percent,
            MaxPivots = MaxKeyTurns,
        };
    }

    static decimal Percent(decimal numerator, decimal denominator)
        => denominator == 0 ? 0 : Math.Round(numerator / denominator * 100m, 4, MidpointRounding.AwayFromZero);

    static string FormatDate(DateTime date, string period)
        => period switch
        {
            "min" or "tick" => date.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            "month" => date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            "year" => date.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            _ => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        };
}
