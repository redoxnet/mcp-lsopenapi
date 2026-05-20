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

/// <summary>
/// Result of <see cref="ResponseShape.ParseSections"/>: the selected section
/// names in canonical order, plus any requested names that were not recognized.
/// </summary>
internal sealed record SectionSelection(
    IReadOnlyList<string> Selected,
    IReadOnlyList<string> Unknown);

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

    /// <summary>
    /// Parses an A-pattern <c>sections</c> argument. A null or blank-only request
    /// resolves to <paramref name="defaults"/>. The selection is returned in
    /// <paramref name="allowed"/> (canonical) order regardless of request order;
    /// unrecognized names are reported in <see cref="SectionSelection.Unknown"/>
    /// so the tool can surface a validation error.
    /// </summary>
    public static SectionSelection ParseSections(
        IEnumerable<string>? requested,
        IReadOnlyList<string> allowed,
        IReadOnlyList<string> defaults)
    {
        List<string> req = (requested ?? Enumerable.Empty<string>())
            .Select(s => s?.Trim() ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToList();

        if (req.Count == 0)
            return new SectionSelection(defaults, Array.Empty<string>());

        var allowedSet = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var requestedSet = new HashSet<string>(req, StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<string> selected = allowed.Where(requestedSet.Contains).ToList();
        IReadOnlyList<string> unknown = req
            .Where(s => !allowedSet.Contains(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SectionSelection(selected, unknown);
    }
}
