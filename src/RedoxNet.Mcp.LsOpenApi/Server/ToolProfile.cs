namespace RedoxNet.Mcp.LsOpenApi.Server;

/// <summary>
/// Resolves the <c>LS_TOOL_PROFILE</c> setting and decides which tools are
/// exposed in <c>tools/list</c>. SPEC-v0.10 §2.4–2.5.
/// </summary>
/// <remarks>
/// Two profiles ship in v0.10.0: <c>standard</c> (default) hides the three
/// catalog tools; <c>all</c> exposes them. The profile axis controls
/// <em>only</em> the catalog trio — the domain dispatchers are a
/// profile-independent v0.10.0 change.
/// </remarks>
internal sealed class ToolProfile
{
    /// <summary>
    /// Catalog tools hidden from <c>tools/list</c> under the <c>standard</c>
    /// profile — developer-fallback TR access, not first-line routing candidates.
    /// </summary>
    public static readonly IReadOnlySet<string> CatalogTools =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "ls_search_tr", "ls_describe_tr", "ls_call_tr",
        };

    ToolProfile(bool isAll, bool strict)
    {
        IsAll = isAll;
        Strict = strict;
    }

    /// <summary>True when the <c>all</c> profile is active (catalog tools exposed).</summary>
    public bool IsAll { get; }

    /// <summary>
    /// True when <c>LS_TOOL_PROFILE_STRICT=true</c> — a <c>tools/call</c> for a
    /// profile-hidden tool is rejected instead of silently honored.
    /// </summary>
    public bool Strict { get; }

    /// <summary>
    /// Resolves a profile from raw env-var strings. Testable seam used by
    /// <see cref="FromEnvironment"/>. An unrecognized profile value falls back
    /// to <c>standard</c>.
    /// </summary>
    public static ToolProfile Resolve(string? profileRaw, string? strictRaw)
    {
        bool isAll = string.Equals(profileRaw?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        bool strict = string.Equals(strictRaw?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        return new ToolProfile(isAll, strict);
    }

    /// <summary>Resolves the profile from the <c>LS_TOOL_PROFILE*</c> environment variables.</summary>
    public static ToolProfile FromEnvironment() => Resolve(
        Environment.GetEnvironmentVariable("LS_TOOL_PROFILE"),
        Environment.GetEnvironmentVariable("LS_TOOL_PROFILE_STRICT"));

    /// <summary>True when <paramref name="toolName"/> is visible under this profile.</summary>
    public bool IsVisible(string toolName) => IsAll || !CatalogTools.Contains(toolName);
}
