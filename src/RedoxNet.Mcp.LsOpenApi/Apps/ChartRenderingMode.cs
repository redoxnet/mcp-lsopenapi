using ModelContextProtocol.Protocol;

namespace RedoxNet.Mcp.LsOpenApi.Apps;

/// <summary>How the connected host consumes chart payloads.</summary>
internal enum ChartRenderingMode
{
    /// <summary>Host does not render charts. Suppress structuredContent and hide include_chart.</summary>
    TextOnly,

    /// <summary>
    /// Host renders <c>structuredContent.chart</c> by direct sniffing (legacy private convention,
    /// e.g. AssistStudio v1.0). Emit structuredContent but skip SEP-1865 metadata.
    /// </summary>
    LegacyStructuredContent,

    /// <summary>
    /// Host advertises SEP-1865 <c>io.modelcontextprotocol/ui</c> capability.
    /// Emit structuredContent with full <c>_meta.ui</c> and <c>ui://</c> resource registration.
    /// </summary>
    Sep1865,
}

/// <summary>
/// Resolves how the connected host wants chart payloads delivered, by combining
/// two signals: the advertised SEP-1865 UI capability (preferred) and a
/// <c>clientInfo</c> allowlist (legacy fallback for hosts that sniff
/// <c>structuredContent.chart</c> directly).
/// </summary>
/// <remarks>
/// The dual signal means v1.2 does not blank charts on AssistStudio while its
/// SEP-1865 support is still in flight (SPEC §6): the allowlist catches
/// AssistStudio by <c>clientInfo</c> name today, and the connection upgrades
/// itself to <see cref="ChartRenderingMode.Sep1865"/> automatically once
/// AssistStudio starts advertising the capability — no server change needed.
/// </remarks>
internal static class ChartHostSupport
{
    /// <summary>
    /// Known hosts that render <c>structuredContent.chart</c> by direct
    /// sniffing, without advertising the SEP-1865 capability. Matched against
    /// <see cref="Implementation.Name"/> from the <c>initialize</c> handshake.
    /// </summary>
    private static readonly HashSet<string> LegacyChartRenderers = new(StringComparer.Ordinal)
    {
        "AssistStudio",
        // Add other known sniffing hosts here as they are identified.
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
        // Preferred path — the standardized SEP-1865 capability.
        if (HasUiCapability(capabilities))
            return ChartRenderingMode.Sep1865;

        // Legacy fallback — known hosts that sniff structuredContent.chart directly.
        if (clientInfo?.Name is { Length: > 0 } name && LegacyChartRenderers.Contains(name))
            return ChartRenderingMode.LegacyStructuredContent;

        return ChartRenderingMode.TextOnly;
    }

    /// <summary>
    /// True when the resolved <paramref name="mode"/> means the server should
    /// emit <c>structuredContent.chart</c> at all (either rendering path).
    /// </summary>
    public static bool EmitsChart(ChartRenderingMode mode) =>
        mode != ChartRenderingMode.TextOnly;

    /// <summary>Whether the host advertised a usable SEP-1865 UI capability.</summary>
    private static bool HasUiCapability(ClientCapabilities? capabilities) =>
        McpAppsCapability.Read(capabilities) is { SupportsHtmlApp: true };
}
