using ModelContextProtocol.Protocol;

namespace RedoxNet.Mcp.LsOpenApi.Apps;

/// <summary>How the connected host consumes chart payloads.</summary>
internal enum ChartRenderingMode
{
    /// <summary>
    /// Host does not render charts. Suppress <c>structuredContent</c> and hide
    /// <c>include_chart</c> so the model is never handed a UI-only payload.
    /// </summary>
    TextOnly,

    /// <summary>
    /// Host renders the chart from <c>structuredContent.chart</c> directly — it
    /// consumes the Plotly spec with its own renderer rather than the SEP-1865
    /// iframe app (e.g. AssistStudio). Emit <c>structuredContent.chart</c>, but
    /// skip the SEP-1865 <c>_meta.ui</c> envelope and <c>ui://</c> resource it
    /// has no use for.
    /// </summary>
    StructuredContent,

    /// <summary>
    /// Host advertises the SEP-1865 <c>io.modelcontextprotocol/ui</c> capability.
    /// Emit <c>structuredContent.chart</c> plus the full <c>_meta.ui</c> envelope
    /// and <c>ui://</c> resource registration.
    /// </summary>
    Sep1865,
}

/// <summary>
/// Resolves how the connected host wants chart payloads delivered, by combining
/// two signals: the advertised SEP-1865 UI capability and a <c>clientInfo</c>
/// allowlist of hosts that render <c>structuredContent.chart</c> directly.
/// </summary>
/// <remarks>
/// The dual signal lets the server gate its chart surface on a single resolved
/// mode regardless of which delivery a host supports. A host on the allowlist
/// that later advertises the capability upgrades from
/// <see cref="ChartRenderingMode.StructuredContent"/> to
/// <see cref="ChartRenderingMode.Sep1865"/> automatically — no server change
/// (SPEC §6).
/// </remarks>
internal static class ChartHostSupport
{
    /// <summary>
    /// Hosts known to render <c>structuredContent.chart</c> directly with their
    /// own renderer (e.g. a bundled Plotly), without advertising the SEP-1865
    /// capability. Matched against <see cref="Implementation.Name"/> from the
    /// <c>initialize</c> handshake.
    /// </summary>
    private static readonly HashSet<string> KnownChartRenderers = new(StringComparer.Ordinal)
    {
        "AssistStudio",
        // Add other known structured-chart hosts here as they are identified.
    };

    /// <summary>
    /// Resolves the chart rendering mode for the current connection.
    /// </summary>
    /// <param name="capabilities">Client capabilities from the <c>initialize</c> request.</param>
    /// <param name="clientInfo">Client implementation metadata from the <c>initialize</c> request.</param>
    public static ChartRenderingMode Resolve(
        ClientCapabilities? capabilities,
        Implementation? clientInfo)
    {
        // The standardized SEP-1865 capability is the preferred signal.
        if (HasUiCapability(capabilities))
            return ChartRenderingMode.Sep1865;

        // Otherwise, a known host that renders structuredContent.chart directly.
        if (clientInfo?.Name is { Length: > 0 } name && KnownChartRenderers.Contains(name))
            return ChartRenderingMode.StructuredContent;

        return ChartRenderingMode.TextOnly;
    }

    /// <summary>Whether the host advertised a usable SEP-1865 UI capability.</summary>
    private static bool HasUiCapability(ClientCapabilities? capabilities) =>
        McpAppsCapability.Read(capabilities) is { SupportsHtmlApp: true };
}
