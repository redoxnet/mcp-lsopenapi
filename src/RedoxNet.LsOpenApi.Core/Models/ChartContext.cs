namespace RedoxNet.LsOpenApi.Core.Models;

/// <summary>
/// Pre-computed analysis context attached to a chart response, so LLM
/// consumers don't have to recompute (and miscompute) basic summaries
/// from raw OHLCV.
/// </summary>
/// <param name="DivergenceFromMa">Latest close vs each moving-average indicator, as percent (e.g. 1.5 = close is 1.5% above the MA).</param>
/// <param name="Volume">Latest volume and trailing averages.</param>
/// <param name="Drawdown">Distance from the period high.</param>
/// <param name="MaTrend">Direction of each moving-average over the last few periods (<c>"up"</c>, <c>"down"</c>, <c>"flat"</c>).</param>
/// <param name="BullishAlignment">True when shorter-period MAs sit above longer-period MAs (classic bullish stack).</param>
public sealed record ChartContext(
    IReadOnlyDictionary<string, double> DivergenceFromMa,
    VolumeContext Volume,
    DrawdownInfo Drawdown,
    IReadOnlyDictionary<string, string> MaTrend,
    bool BullishAlignment);

/// <summary>
/// Latest candle volume vs trailing averages.
/// </summary>
/// <param name="Latest">Volume of the most recent candle.</param>
/// <param name="Avg20">Mean volume over the last 20 candles, or <see langword="null"/> when fewer are available.</param>
/// <param name="Ratio20">Latest / <see cref="Avg20"/>, or <see langword="null"/>.</param>
/// <param name="Avg60">Mean volume over the last 60 candles, or <see langword="null"/>.</param>
/// <param name="Ratio60">Latest / <see cref="Avg60"/>, or <see langword="null"/>.</param>
public sealed record VolumeContext(
    long Latest,
    long? Avg20,
    double? Ratio20,
    long? Avg60,
    double? Ratio60);

/// <summary>
/// Drawdown from the period high.
/// </summary>
/// <param name="PeriodHigh">Maximum high in the fetched window.</param>
/// <param name="PeriodHighDate">Date of <see cref="PeriodHigh"/> (local time).</param>
/// <param name="Current">Latest close.</param>
/// <param name="Pct">(<see cref="Current"/> − <see cref="PeriodHigh"/>) / <see cref="PeriodHigh"/> × 100. Negative when below the high.</param>
public sealed record DrawdownInfo(
    decimal PeriodHigh,
    DateTime PeriodHighDate,
    decimal Current,
    double Pct);
