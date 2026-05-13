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
