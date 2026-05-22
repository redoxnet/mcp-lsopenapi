using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace RedoxNet.Mcp.LsOpenApi.Apps;

/// <summary>
/// The MCP Apps (SEP-1865) UI extension capability a host advertises during
/// <c>initialize</c>. Used to gate this server's inline-chart surface — the
/// <c>include_chart</c> parameter, the <c>_meta.ui</c> envelope, the
/// <c>ui://lsopenapi/plotly</c> resource, and the <c>structuredContent.chart</c>
/// payload — on whether the host can actually render it.
/// </summary>
/// <remarks>
/// <para>
/// SEP-1865 capability shape (host → server, inside the <c>initialize</c>
/// request's <c>capabilities</c>):
/// <code>
/// "extensions": {
///   "io.modelcontextprotocol/ui": { "mimeTypes": ["text/html;profile=mcp-app"] }
/// }
/// </code>
/// </para>
/// <para>
/// A host that does not advertise this extension gets a text-only surface. This
/// matters because <c>structuredContent</c> is a generic MCP field (revision
/// 2025-06), not a UI-only one: hosts that support structured tool output but
/// not MCP Apps feed it straight into the model context. Shipping a Plotly spec
/// there would bury the analytical summary under a UI-only JSON blob
/// (SPEC §6, Spike B).
/// </para>
/// </remarks>
internal sealed record McpAppsCapability(IReadOnlyList<string> MimeTypes)
{
    /// <summary>SEP-1865 extension identifier for the MCP Apps UI extension.</summary>
    public const string ExtensionId = "io.modelcontextprotocol/ui";

    /// <summary>MIME profile this server's <c>ui://lsopenapi/plotly</c> template is served as.</summary>
    public const string HtmlAppMimeType = "text/html;profile=mcp-app";

    /// <summary>
    /// Whether the host accepts the HTML MCP-app profile this server's chart
    /// template uses — i.e. it can actually render the inline charts.
    /// </summary>
    public bool SupportsHtmlApp =>
        MimeTypes.Contains(HtmlAppMimeType, StringComparer.Ordinal);

    /// <summary>
    /// Reads the MCP Apps UI capability from a host's advertised capabilities.
    /// Returns <c>null</c> when the extension is absent or malformed.
    /// </summary>
    /// <param name="capabilities">The host's <see cref="ClientCapabilities"/>, or <c>null</c> before <c>initialize</c>.</param>
    public static McpAppsCapability? Read(ClientCapabilities? capabilities)
    {
        // ClientCapabilities.Extensions is the spec-defined channel for SEP-1865
        // extension negotiation. The SDK marks it [Experimental(MCPEXP001)] — we
        // opt in deliberately and contain the suppression to this one read.
#pragma warning disable MCPEXP001
        IDictionary<string, object>? extensions = capabilities?.Extensions;
#pragma warning restore MCPEXP001
        if (extensions is null ||
            !extensions.TryGetValue(ExtensionId, out object? raw) ||
            raw is null)
        {
            return null;
        }

        return new McpAppsCapability(ExtractMimeTypes(raw));
    }

    /// <summary>
    /// Pulls the <c>mimeTypes</c> string array out of the per-extension settings
    /// object. The value arrives as a <see cref="JsonElement"/> off the wire;
    /// anything else is normalized through the serializer so unit tests can pass
    /// a plain object. An empty / missing array yields an empty list.
    /// </summary>
    static IReadOnlyList<string> ExtractMimeTypes(object raw)
    {
        JsonElement el;
        try
        {
            el = raw is JsonElement je ? je : JsonSerializer.SerializeToElement(raw);
        }
        catch (NotSupportedException)
        {
            return Array.Empty<string>();
        }

        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty("mimeTypes", out JsonElement mimeTypes) ||
            mimeTypes.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(mimeTypes.GetArrayLength());
        foreach (JsonElement item in mimeTypes.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                result.Add(s);
        }
        return result;
    }
}
