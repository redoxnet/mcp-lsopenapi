using System.Globalization;
using System.Text.Json;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// Defensive readers for LS response JSON.
/// </summary>
/// <remarks>
/// LS sometimes returns numeric fields as strings (legacy TRs) and sometimes
/// as JSON numbers. These helpers paper over the difference so semantic
/// tools can keep working when LS adjusts the wire format.
/// </remarks>
internal static class JsonElementExtensions
{
    /// <summary>
    /// Reads a numeric field as <see cref="long"/>, tolerating LS's habit of
    /// shipping numeric values as JSON strings on legacy TRs.
    /// </summary>
    /// <param name="element">The JSON object to read from.</param>
    /// <param name="property">Property name (case-sensitive).</param>
    /// <param name="fallback">Value returned when the property is missing or unparseable.</param>
    /// <returns>The parsed long, or <paramref name="fallback"/>.</returns>
    public static long ReadLong(this JsonElement element, string property, long fallback = 0)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long n) => n,
            JsonValueKind.Number => (long)value.GetDouble(),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out long n) => n,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) => (long)d,
            _ => fallback,
        };
    }

    /// <summary>
    /// Reads a numeric field as <see cref="double"/>, handling JSON strings as
    /// fallback (LS frequently uses fixed-width zero-padded numerics on legacy TRs).
    /// </summary>
    /// <param name="element">The JSON object to read from.</param>
    /// <param name="property">Property name.</param>
    /// <param name="fallback">Value returned when the property is missing or unparseable.</param>
    /// <returns>The parsed double, or <paramref name="fallback"/>.</returns>
    public static double ReadDouble(this JsonElement element, string property, double fallback = 0)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d) => d,
            _ => fallback,
        };
    }

    /// <summary>
    /// Reads a numeric field as <see cref="decimal"/>, tolerating JSON strings.
    /// </summary>
    /// <param name="element">The JSON object to read from.</param>
    /// <param name="property">Property name.</param>
    /// <param name="fallback">Value returned when the property is missing or unparseable.</param>
    /// <returns>The parsed decimal, or <paramref name="fallback"/>.</returns>
    public static decimal ReadDecimal(this JsonElement element, string property, decimal fallback = 0)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out decimal n) => n,
            JsonValueKind.Number => (decimal)value.GetDouble(),
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal n) => n,
            _ => fallback,
        };
    }

    /// <summary>
    /// Reads a string field, coercing numeric and boolean kinds into a string
    /// representation when LS happens to ship them that way.
    /// </summary>
    /// <param name="element">The JSON object to read from.</param>
    /// <param name="property">Property name.</param>
    /// <returns>The string value, or <see langword="null"/> when the property is absent or an unsupported kind.</returns>
    public static string? ReadString(this JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean().ToString(),
            _ => null,
        };
    }
}
