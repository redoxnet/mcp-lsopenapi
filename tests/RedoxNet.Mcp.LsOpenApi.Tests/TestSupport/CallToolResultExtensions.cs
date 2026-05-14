using ModelContextProtocol.Protocol;

namespace RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;

/// <summary>
/// Test-side helpers for unwrapping <see cref="CallToolResult"/> into the
/// flat string shape pre-existing tests assert against.
/// </summary>
internal static class CallToolResultExtensions
{
    /// <summary>
    /// Returns the first <see cref="TextContentBlock"/>'s body. Tools in this
    /// server always emit exactly one text block (plus optional structured
    /// content), so the indexer is safe for these tests.
    /// </summary>
    public static string TextContent(this CallToolResult result) =>
        ((TextContentBlock)result.Content[0]).Text;

    /// <summary>
    /// Awaits the task and returns the text content. Lets existing tests keep
    /// the <c>string result = await GetChartTool.GetChart(...).TextContent()</c>
    /// pattern without per-file restructuring.
    /// </summary>
    public static async Task<string> TextContent(this Task<CallToolResult> task) =>
        (await task).TextContent();
}
