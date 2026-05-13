using System.Text.Encodings.Web;
using System.Text.Json;

namespace RedoxNet.LsOpenApi.Core;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances used by the LS Core SDK.
/// </summary>
/// <remarks>
/// Use these instead of constructing options per call so JSON behavior stays
/// consistent and there is no per-call allocator overhead.
/// </remarks>
public static class LsCoreJson
{
    /// <summary>
    /// Options for wire-level JSON (token endpoint responses, TR bodies).
    /// Case-insensitive property matching, non-ASCII characters emitted as-is
    /// for Korean payloads.
    /// </summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Options for tool/response payloads: indented, snake_case, non-ASCII as-is.
    /// </summary>
    public static readonly JsonSerializerOptions Tool = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
