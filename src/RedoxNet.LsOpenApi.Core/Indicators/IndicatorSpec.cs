namespace RedoxNet.LsOpenApi.Core.Indicators;

/// <summary>
/// Parsed indicator specification produced by <see cref="IndicatorSpecParser"/>.
/// </summary>
/// <param name="Kind">Lowercase indicator kind: <c>"ma"</c>, <c>"ema"</c>, <c>"rsi"</c>, <c>"macd"</c>, <c>"bb"</c>.</param>
/// <param name="Args">Numeric arguments parsed from the input (mixed int/double per indicator).</param>
/// <param name="Raw">The original input string. Used as the key in indicator result dictionaries.</param>
public sealed record IndicatorSpec(string Kind, IReadOnlyList<double> Args, string Raw);
