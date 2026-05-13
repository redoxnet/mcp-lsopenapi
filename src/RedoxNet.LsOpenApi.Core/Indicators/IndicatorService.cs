using RedoxNet.LsOpenApi.Core.Models;
using Skender.Stock.Indicators;

namespace RedoxNet.LsOpenApi.Core.Indicators;

/// <summary>
/// Computes technical indicators over a candle series using Skender.Stock.Indicators.
/// </summary>
/// <remarks>
/// Indicator outputs are aligned 1:1 with the input candle list. Warm-up
/// periods are returned as <see langword="null"/> entries so callers can
/// merge results into a per-bar payload safely.
/// </remarks>
public sealed class IndicatorService
{
    /// <summary>
    /// Computes the given indicator specs.
    /// </summary>
    /// <param name="candles">Ordered candle list (oldest first).</param>
    /// <param name="specs">Parsed indicator specs.</param>
    /// <returns>Map of series name → values aligned with <paramref name="candles"/>.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<double?>> Compute(
        IReadOnlyList<Candle> candles,
        IReadOnlyList<IndicatorSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(specs);

        var result = new Dictionary<string, IReadOnlyList<double?>>(StringComparer.Ordinal);
        if (candles.Count == 0 || specs.Count == 0)
            return result;

        List<Quote> quotes = candles.Select(c => new Quote
        {
            Date = c.Date,
            Open = c.Open,
            High = c.High,
            Low = c.Low,
            Close = c.Close,
            Volume = c.Volume,
        }).ToList();

        foreach (IndicatorSpec spec in specs)
        {
            switch (spec.Kind)
            {
                case "ma":
                    result[spec.Raw] = MapNullable(quotes.GetSma((int)spec.Args[0]).Select(r => (double?)r.Sma).ToList());
                    break;
                case "ema":
                    result[spec.Raw] = MapNullable(quotes.GetEma((int)spec.Args[0]).Select(r => (double?)r.Ema).ToList());
                    break;
                case "rsi":
                    result[spec.Raw] = MapNullable(quotes.GetRsi((int)spec.Args[0]).Select(r => (double?)r.Rsi).ToList());
                    break;
                case "macd":
                {
                    List<MacdResult> macd = quotes
                        .GetMacd((int)spec.Args[0], (int)spec.Args[1], (int)spec.Args[2])
                        .ToList();
                    result[$"{spec.Raw}.macd"] = MapNullable(macd.Select(r => (double?)r.Macd).ToList());
                    result[$"{spec.Raw}.signal"] = MapNullable(macd.Select(r => (double?)r.Signal).ToList());
                    result[$"{spec.Raw}.histogram"] = MapNullable(macd.Select(r => (double?)r.Histogram).ToList());
                    break;
                }
                case "bb":
                {
                    List<BollingerBandsResult> bb = quotes
                        .GetBollingerBands((int)spec.Args[0], spec.Args[1])
                        .ToList();
                    result[$"{spec.Raw}.lower"] = MapNullable(bb.Select(r => (double?)r.LowerBand).ToList());
                    result[$"{spec.Raw}.middle"] = MapNullable(bb.Select(r => (double?)r.Sma).ToList());
                    result[$"{spec.Raw}.upper"] = MapNullable(bb.Select(r => (double?)r.UpperBand).ToList());
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Indicator kind '{spec.Kind}' is recognized by the parser but not implemented in IndicatorService.");
            }
        }

        return result;
    }

    /// <summary>
    /// Identity helper kept for symmetry with the other dispatchers — Skender
    /// already produces nullable doubles, so no conversion is needed.
    /// </summary>
    /// <param name="source">Indicator series.</param>
    /// <returns>The same list.</returns>
    static IReadOnlyList<double?> MapNullable(IReadOnlyList<double?> source) => source;
}
