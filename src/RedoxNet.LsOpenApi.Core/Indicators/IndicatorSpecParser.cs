using System.Globalization;

namespace RedoxNet.LsOpenApi.Core.Indicators;

/// <summary>
/// Parses indicator specifications used by the chart tool.
/// </summary>
/// <remarks>
/// Supported forms (case-insensitive):
/// <list type="bullet">
///   <item><description><c>ma:N</c> — simple moving average of period N.</description></item>
///   <item><description><c>ema:N</c> — exponential moving average of period N.</description></item>
///   <item><description><c>rsi:N</c> — relative strength index over N periods.</description></item>
///   <item><description><c>macd:F,S,Sig</c> — MACD with fast/slow/signal periods.</description></item>
///   <item><description><c>bb:N,SD</c> — Bollinger Bands with period N and standard deviation SD.</description></item>
/// </list>
/// </remarks>
public static class IndicatorSpecParser
{
    /// <summary>
    /// Parses a single spec string.
    /// </summary>
    /// <param name="raw">Raw spec, e.g. <c>"ma:12"</c>.</param>
    /// <returns>The parsed spec.</returns>
    /// <exception cref="FormatException">Thrown when the spec is malformed.</exception>
    public static IndicatorSpec Parse(string raw)
    {
        if (!TryParse(raw, out IndicatorSpec? spec, out string? error))
            throw new FormatException(error);
        return spec!;
    }

    /// <summary>
    /// Attempts to parse a spec string.
    /// </summary>
    /// <param name="raw">Raw spec.</param>
    /// <param name="spec">When successful, the parsed spec.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryParse(string? raw, out IndicatorSpec? spec)
        => TryParse(raw, out spec, out _);

    /// <summary>
    /// Attempts to parse a spec string, returning a structured error.
    /// </summary>
    /// <param name="raw">Raw spec.</param>
    /// <param name="spec">When successful, the parsed spec.</param>
    /// <param name="error">When unsuccessful, an explanation.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryParse(string? raw, out IndicatorSpec? spec, out string? error)
    {
        spec = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Indicator spec must not be empty.";
            return false;
        }

        string trimmed = raw.Trim();
        int colon = trimmed.IndexOf(':');
        string kind;
        string argsPart;

        if (colon < 0)
        {
            kind = trimmed.ToLowerInvariant();
            argsPart = string.Empty;
        }
        else
        {
            kind = trimmed[..colon].Trim().ToLowerInvariant();
            argsPart = trimmed[(colon + 1)..].Trim();
        }

        if (string.IsNullOrEmpty(kind))
        {
            error = $"Indicator spec '{raw}' is missing a kind.";
            return false;
        }

        var args = new List<double>();
        if (!string.IsNullOrEmpty(argsPart))
        {
            string[] tokens = argsPart.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string token in tokens)
            {
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    error = $"Indicator spec '{raw}' has non-numeric argument '{token}'.";
                    return false;
                }
                args.Add(value);
            }
        }

        if (!ValidateArgs(kind, args, out error))
            return false;

        spec = new IndicatorSpec(kind, args, trimmed);
        return true;
    }

    static bool ValidateArgs(string kind, List<double> args, out string? error)
    {
        error = null;
        switch (kind)
        {
            case "ma":
            case "ema":
            case "rsi":
                if (args.Count != 1 || args[0] < 1)
                {
                    error = $"{kind} requires exactly one positive period argument.";
                    return false;
                }
                break;
            case "macd":
                if (args.Count != 3 || args.Any(a => a < 1))
                {
                    error = "macd requires three positive period arguments (fast, slow, signal).";
                    return false;
                }
                break;
            case "bb":
                if (args.Count != 2 || args[0] < 1 || args[1] <= 0)
                {
                    error = "bb requires a positive period and a positive standard-deviation multiplier.";
                    return false;
                }
                break;
            default:
                error = $"Unknown indicator kind '{kind}'. Supported: ma, ema, rsi, macd, bb.";
                return false;
        }
        return true;
    }
}
