namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// Identifies which LS증권 OpenAPI environment the client targets.
/// </summary>
public enum LsMarket
{
    /// <summary>Paper trading / 모의투자 environment.</summary>
    Virtual = 0,

    /// <summary>Real trading / 실투자 environment.</summary>
    Real = 1,
}

/// <summary>
/// Helpers for parsing and rendering <see cref="LsMarket"/> values.
/// </summary>
public static class LsMarketExtensions
{
    /// <summary>
    /// Parses a market name, accepting <c>"real"</c>, <c>"virtual"</c>, <c>"paper"</c>,
    /// and <c>"prod"</c> (case-insensitive). Defaults to <see cref="LsMarket.Real"/>
    /// when the input is missing.
    /// </summary>
    /// <param name="value">User-supplied market name. May be <see langword="null"/>.</param>
    /// <returns>The parsed market.</returns>
    public static LsMarket Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return LsMarket.Real;

        return value.Trim().ToLowerInvariant() switch
        {
            "real" or "prod" or "production" or "live" => LsMarket.Real,
            "virtual" or "paper" or "mock" or "sandbox" or "test" => LsMarket.Virtual,
            _ => LsMarket.Real,
        };
    }

    /// <summary>
    /// Returns the canonical lowercase name (<c>"real"</c> or <c>"virtual"</c>).
    /// </summary>
    /// <param name="market">The market value to render.</param>
    /// <returns>Lowercase canonical name.</returns>
    public static string ToCanonical(this LsMarket market) =>
        market == LsMarket.Real ? "real" : "virtual";
}
