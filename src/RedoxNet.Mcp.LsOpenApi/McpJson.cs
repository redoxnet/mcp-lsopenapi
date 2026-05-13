using System.Text.Encodings.Web;
using System.Text.Json;

namespace RedoxNet.Mcp.LsOpenApi;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances for MCP tool responses.
/// </summary>
/// <remarks>
/// Tool payloads use indented snake_case with relaxed escaping so Korean
/// text shows up verbatim in the chat transcript instead of <c>\uXXXX</c>
/// escapes.
/// </remarks>
internal static class McpJson
{
    /// <summary>Standard tool response: indented snake_case, non-ASCII as-is.</summary>
    public static readonly JsonSerializerOptions Tool = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Serializes an error envelope. Keeps the shape stable so the LLM can
    /// recognize tool failures and explain them to the user.
    /// </summary>
    /// <param name="message">Short error message.</param>
    /// <param name="details">Optional structured details.</param>
    /// <returns>The serialized JSON.</returns>
    public static string Error(string message, object? details = null) =>
        JsonSerializer.Serialize(new { error = message, details }, Tool);
}
