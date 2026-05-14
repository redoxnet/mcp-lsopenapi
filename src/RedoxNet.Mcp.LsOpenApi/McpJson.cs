using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace RedoxNet.Mcp.LsOpenApi;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> instances and
/// <see cref="CallToolResult"/> builders for MCP tool responses.
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

    /// <summary>
    /// Builds a successful <see cref="CallToolResult"/>. The model sees
    /// <paramref name="textJson"/> as the tool's text content; MCP Apps
    /// hosts forward <paramref name="structuredContent"/> to the iframe.
    /// </summary>
    /// <param name="textJson">Serialized JSON for the model (already encoded with <see cref="Tool"/>).</param>
    /// <param name="structuredContent">Optional UI-only payload; not added to model context.</param>
    public static CallToolResult OkResult(string textJson, JsonObject? structuredContent = null)
    {
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = textJson } },
            // CallToolResult.StructuredContent is JsonElement?; lift the JsonObject through the serializer.
            StructuredContent = structuredContent is null
                ? null
                : JsonSerializer.SerializeToElement(structuredContent),
        };
    }

    /// <summary>
    /// Builds an error <see cref="CallToolResult"/> using the same envelope
    /// as <see cref="Error(string, object?)"/> and marks <c>isError</c>.
    /// </summary>
    public static CallToolResult ErrorResult(string message, object? details = null)
    {
        return new CallToolResult
        {
            Content = new List<ContentBlock> { new TextContentBlock { Text = Error(message, details) } },
            IsError = true,
        };
    }
}
