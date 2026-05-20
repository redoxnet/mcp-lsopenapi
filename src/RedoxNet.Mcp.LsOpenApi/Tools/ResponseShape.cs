using System.Text.Json.Serialization;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// Verbosity tiers for pattern-B (aggregation) tools — SPEC-v0.9 §2.2 / §4.2.
/// </summary>
internal enum VerbosityMode
{
    /// <summary>Aggregate digest only; per-row data omitted.</summary>
    Summary,

    /// <summary>Digest + a recent slice of rows.</summary>
    Compact,

    /// <summary>All rows, no digest — the pre-v0.9 (v0.8-compatible) shape.</summary>
    Full,
}

/// <summary>
/// Pattern-A truncation echo (SPEC-v0.9 §4.4): a homogeneous array paired with
/// its full count and shown count. <see cref="Items"/> is null when the caller
/// projected the array out entirely (e.g. <c>themes_limit=0</c>), which keeps a
/// single shape — never a polymorphic in-band marker — for deserializers.
/// </summary>
internal sealed record Slice<T>(
    int Count,
    int Shown,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<T>? Items);

/// <summary>Factory for <see cref="Slice{T}"/> projections.</summary>
internal static class Slice
{
    /// <summary>
    /// Projects <paramref name="items"/> per a caller-supplied limit.
    /// <list type="bullet">
    /// <item><paramref name="limit"/> null → <paramref name="defaultLimit"/> applies.</item>
    /// <item>limit &lt; 0 → all items (full-restore).</item>
    /// <item>limit == 0 → no items; <c>Items</c> is null when <paramref name="omitWhenZero"/>, else empty.</item>
    /// <item>limit &gt; 0 → the first <c>min(limit, count)</c> items.</item>
    /// </list>
    /// </summary>
    public static Slice<T> Of<T>(
        IReadOnlyList<T> items,
        int? limit,
        int defaultLimit,
        bool omitWhenZero = true)
    {
        int count = items.Count;
        int effective = limit ?? defaultLimit;

        if (effective < 0)
            return new Slice<T>(count, count, items);

        if (effective == 0)
            return new Slice<T>(count, 0, omitWhenZero ? null : Array.Empty<T>());

        int shown = Math.Min(effective, count);
        IReadOnlyList<T> taken = shown == count ? items : items.Take(shown).ToList();
        return new Slice<T>(count, shown, taken);
    }
}

/// <summary>
/// Shared response-shaping helpers for the MCP wrapper layer (SPEC-v0.9).
/// Centralizes the verbosity / projection conventions so pattern-A and
/// pattern-B tools stay consistent.
/// </summary>
internal static class ResponseShape
{
    /// <summary>
    /// Parses a verbosity argument. A null/blank argument resolves to
    /// <paramref name="fallback"/> (returns true); a non-blank but unrecognized
    /// value returns false so the tool can surface a validation error.
    /// </summary>
    public static bool TryParseVerbosity(string? raw, VerbosityMode fallback, out VerbosityMode mode)
    {
        string s = (raw ?? string.Empty).Trim().ToLowerInvariant();
        switch (s)
        {
            case "":
                mode = fallback;
                return true;
            case "summary":
                mode = VerbosityMode.Summary;
                return true;
            case "compact":
                mode = VerbosityMode.Compact;
                return true;
            case "full":
                mode = VerbosityMode.Full;
                return true;
            default:
                mode = fallback;
                return false;
        }
    }

    /// <summary>The wire (snake_case) token for a verbosity mode.</summary>
    public static string ToWire(this VerbosityMode mode) => mode switch
    {
        VerbosityMode.Summary => "summary",
        VerbosityMode.Compact => "compact",
        VerbosityMode.Full => "full",
        _ => "summary",
    };
}
