namespace RedoxNet.LsOpenApi.Core.Models;

/// <summary>
/// Compact chart-analysis payload intended for model reasoning. It carries the
/// signals a model usually needs without exposing full OHLCV arrays.
/// </summary>
/// <param name="Symbol">Short code.</param>
/// <param name="Name">Optional human-readable name.</param>
/// <param name="Period">Period label (<c>day</c>, <c>week</c>, <c>month</c>, etc.).</param>
/// <param name="BarCount">Number of bars represented by the summary.</param>
/// <param name="DateRange">Formatted first-to-last bar range.</param>
/// <param name="LatestClose">Latest close.</param>
/// <param name="LatestDate">Formatted latest bar date.</param>
/// <param name="ChangePctFromPrev">Latest close change from the previous bar, percent.</param>
/// <param name="ChangePct1Y">Latest close change from roughly one year ago, percent.</param>
/// <param name="ChangePct5Y">Latest close change from roughly five years ago, percent.</param>
/// <param name="MovingAverages">Selected moving-average snapshots keyed as <c>MA20</c>, <c>MA60</c>, etc.</param>
/// <param name="Ma60DeviationPct">Latest close vs MA60, percent.</param>
/// <param name="Ma60Slope">MA60 slope classification: <c>rising</c>, <c>flat</c>, or <c>falling</c>.</param>
/// <param name="PeakHigh">Highest high in the represented window.</param>
/// <param name="PeakHighDate">Date of <paramref name="PeakHigh"/>.</param>
/// <param name="DrawdownFromPeakPct">Latest close vs peak high, percent.</param>
/// <param name="KeyTurns">Small bounded list of notable swing points.</param>
/// <param name="Coverage">
/// Per-indicator availability and a one-line note so callers see <em>why</em> a
/// value is <see langword="null"/> without having to infer from the window size.
/// </param>
public sealed record AnalyticalSummary(
    string Symbol,
    string? Name,
    string Period,
    int BarCount,
    string DateRange,
    decimal LatestClose,
    string LatestDate,
    decimal? ChangePctFromPrev,
    decimal? ChangePct1Y,
    decimal? ChangePct5Y,
    IReadOnlyDictionary<string, decimal?> MovingAverages,
    decimal? Ma60DeviationPct,
    string? Ma60Slope,
    decimal? PeakHigh,
    string? PeakHighDate,
    decimal? DrawdownFromPeakPct,
    IReadOnlyList<InflectionPoint> KeyTurns,
    IndicatorCoverage Coverage);

/// <summary>
/// A compact swing pivot suitable for text reasoning.
/// </summary>
/// <param name="Date">Formatted bar date.</param>
/// <param name="Price">Pivot price — the swing high for a peak, the swing low for a trough; the latest close for the tentative pivot.</param>
/// <param name="Kind">Whether this pivot is a peak or a trough.</param>
/// <param name="ChangePctFromPrev">Percent move from the previous pivot (from the first bar for the first pivot).</param>
/// <param name="IsConfirmed">
/// <see langword="false"/> for the trailing tentative pivot at the latest bar — the
/// in-progress swing has not yet reversed past the threshold, so this point may still move.
/// </param>
public sealed record InflectionPoint(
    string Date,
    decimal Price,
    PivotKind Kind,
    decimal ChangePctFromPrev,
    bool IsConfirmed);

/// <summary>Swing pivot direction.</summary>
public enum PivotKind
{
    /// <summary>A swing high — a local top.</summary>
    Peak,

    /// <summary>A swing low — a local bottom.</summary>
    Trough,
}

/// <summary>
/// Indicator-availability metadata for an <see cref="AnalyticalSummary"/>.
/// Surfaces <em>why</em> indicators may be <see langword="null"/> — narrow window,
/// not applicable to this period, or genuine insufficiency — so the model can
/// explain it to the user instead of silently propagating nulls.
/// </summary>
/// <param name="WarmupApplied">
/// <see langword="true"/> when the analytical-summary warm-up policy padded the
/// fetch with extra leading history (typically when <c>from</c> was unspecified
/// or <c>with_warmup=true</c> was forced).
/// </param>
/// <param name="AnalyticalBarCount">Total bars the summary was computed over (display window + any warm-up lead).</param>
/// <param name="DisplayBarCount">Bars actually visible to the caller after trimming.</param>
/// <param name="Status">
/// Per-indicator availability — one of <c>ok</c>, <c>insufficient_data</c>, or
/// <c>disabled</c>. Keys include each selected moving average
/// (<c>MA20</c>, <c>MA60</c>, …), plus <c>ma60_slope</c>, <c>change_1y</c>,
/// <c>change_5y</c>, and <c>key_turns</c>.
/// </param>
/// <param name="Note">
/// Optional human-readable note for the model to relay. Populated only when at
/// least one indicator is <c>insufficient_data</c>; <see langword="null"/>
/// otherwise.
/// </param>
public sealed record IndicatorCoverage(
    bool WarmupApplied,
    int AnalyticalBarCount,
    int DisplayBarCount,
    IReadOnlyDictionary<string, string> Status,
    string? Note);
