namespace RedoxNet.LsOpenApi.Core.Models;

/// <summary>
/// An OHLCV candle returned by an LS chart TR.
/// </summary>
/// <param name="Date">Candle timestamp (date-only for day/week/month, full datetime for minute/tick).</param>
/// <param name="Open">Opening price.</param>
/// <param name="High">Highest traded price during the interval.</param>
/// <param name="Low">Lowest traded price during the interval.</param>
/// <param name="Close">Closing price.</param>
/// <param name="Volume">Trade volume in shares.</param>
/// <param name="Value">Optional trade value in currency.</param>
public sealed record Candle(
    DateTime Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume,
    long? Value = null);
