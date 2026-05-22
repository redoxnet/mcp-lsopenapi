using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RedoxNet.Mcp.LsOpenApi.Apps;

/// <summary>
/// MCP Apps (SEP-1865) UI resources served by this server. Hosts that
/// implement the extension pre-fetch the HTML template, sandbox it in an
/// iframe, and forward tool results so the View can render inline.
/// </summary>
/// <remarks>
/// <para>
/// A single generic Plotly template handles every chart-emitting tool — the
/// shape of the spec (candlestick, treemap, line, ...) is decided server-side
/// and travels through <c>structuredContent.chart.spec</c>. The template
/// supports a per-tool side panel (e.g. ETF top-10 list) under
/// <c>structuredContent.panel</c>.
/// </para>
/// <para>
/// The whole UI surface — the <c>include_chart</c> parameter, the
/// <c>_meta.ui</c> envelope, this resource, and <c>structuredContent.chart</c>
/// in tool results — is gated on the host advertising the
/// <see cref="McpAppsCapability"/> extension (SPEC v1.2). Non-UI hosts get a
/// text-only surface so the model is never handed a UI-only Plotly blob.
/// </para>
/// </remarks>
internal static class UiResources
{
    /// <summary>URI of the generic Plotly UI resource. Tools point their
    /// <c>_meta.ui.resourceUri</c> at this value.</summary>
    public const string PlotlyResourceUri = "ui://lsopenapi/plotly";

    /// <summary>MCP Apps mandates this MIME for HTML templates.</summary>
    private const string PlotlyMimeType = "text/html;profile=mcp-app";

    /// <summary>Resource manifest name produced by MSBuild for
    /// <c>Apps/PlotlyTemplate.html</c> under RootNamespace
    /// <c>RedoxNet.Mcp.LsOpenApi</c>.</summary>
    private const string EmbeddedResourceName =
        "RedoxNet.Mcp.LsOpenApi.Apps.PlotlyTemplate.html";

    private static readonly Lazy<string> PlotlyHtml = new(LoadPlotlyTemplate);

    /// <summary>
    /// Names of tools whose results this server emits as Plotly specs under
    /// <c>structuredContent.chart</c>. The capability filters
    /// (<see cref="ApplyChartSurface"/>) gate the UI surface of exactly these
    /// tools.
    /// </summary>
    /// <remarks>
    /// All five tools that call <c>CandlestickChartBuilder.Build</c> /
    /// <c>EtfHoldingsChartBuilder.Build</c> / the program-trading builders —
    /// <c>ls_add_indicator</c> and <c>ls_reframe_chart</c> included (SPEC v1.2
    /// W3a closes the §4.3 gap where the two follow-up tools emitted a chart but
    /// were absent here).
    /// </remarks>
    private static readonly HashSet<string> PlotlyEmittingToolNames = new(StringComparer.Ordinal)
    {
        "ls_get_chart",
        "ls_add_indicator",
        "ls_reframe_chart",
        "ls_get_etf_holdings",
        "ls_get_program_trading",
    };

    /// <summary>Handler for <c>resources/list</c>. Advertises the one UI
    /// resource this server publishes — but only to hosts that advertise the
    /// MCP Apps capability, since a non-UI host has no use for it.</summary>
    public static ValueTask<ListResourcesResult> ListAsync(
        RequestContext<ListResourcesRequestParams> request,
        CancellationToken cancellationToken)
    {
        // SPEC v1.2 W3b: the ui:// resource is only meaningful to a full
        // SEP-1865 host — a legacy sniffing host renders structuredContent
        // directly and never fetches it; a non-UI host has no use for it.
        if (ChartHostSupport.Resolve(
                request.Server.ClientCapabilities, request.Server.ClientInfo)
            != ChartRenderingMode.Sep1865)
        {
            return ValueTask.FromResult(
                new ListResourcesResult { Resources = new List<Resource>() });
        }

        var result = new ListResourcesResult
        {
            Resources = new List<Resource>
            {
                new()
                {
                    Uri = PlotlyResourceUri,
                    Name = "plotly-chart",
                    Title = "Plotly Chart Renderer",
                    Description = "Inline chart renderer for tools that emit Plotly v5 specs " +
                                  "(candlestick, treemap, scatter, bar, ...). Used by ls_get_chart, " +
                                  "ls_get_etf_holdings, and ls_get_program_trading " +
                                  "via MCP Apps (SEP-1865).",
                    MimeType = PlotlyMimeType,
                    Meta = BuildResourceUiMeta(),
                },
            },
        };
        return ValueTask.FromResult(result);
    }

    /// <summary>Handler for <c>resources/read</c>. Returns the embedded HTML
    /// template for <see cref="PlotlyResourceUri"/>; any other URI is
    /// rejected. Not capability-gated — a host that asks for the template by
    /// URI gets it; advertisement is what <see cref="ListAsync"/> gates.</summary>
    public static ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken)
    {
        string? uri = request.Params?.Uri;
        if (!string.Equals(uri, PlotlyResourceUri, StringComparison.Ordinal))
            throw new McpException($"Unknown resource URI: {uri ?? "<null>"}");

        var result = new ReadResourceResult
        {
            Contents = new List<ResourceContents>
            {
                new TextResourceContents
                {
                    Uri = PlotlyResourceUri,
                    MimeType = PlotlyMimeType,
                    Text = PlotlyHtml.Value,
                    // SEP-1865: the host reads CSP from the resources/read
                    // content _meta (not just the resources/list entry), so
                    // the iframe sandbox is allowed to load the Plotly CDN.
                    Meta = BuildResourceUiMeta(),
                },
            },
        };
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Applies the capability-gated chart surface to <paramref name="tool"/>
    /// from a <c>tools/list</c> filter, per the resolved
    /// <see cref="ChartRenderingMode"/>. For a chart-emitting tool:
    /// <list type="bullet">
    ///   <item><description><see cref="ChartRenderingMode.Sep1865"/> — keep
    ///   <c>include_chart</c> and attach the SEP-1865 <c>_meta.ui</c> envelope
    ///   so the host pairs the tool with the Plotly resource;</description></item>
    ///   <item><description><see cref="ChartRenderingMode.LegacyStructuredContent"/>
    ///   — keep <c>include_chart</c> (the host sniffs the chart directly) but
    ///   skip <c>_meta.ui</c>, which it would ignore;</description></item>
    ///   <item><description><see cref="ChartRenderingMode.TextOnly"/> — strip
    ///   <c>include_chart</c> from the input schema so the model never asks for
    ///   a chart the host can't show.</description></item>
    /// </list>
    /// No-op for non-chart tools. Mutates <paramref name="tool"/> in place.
    /// </summary>
    /// <param name="tool">Tool descriptor to patch in place.</param>
    /// <param name="mode">Chart rendering mode resolved for the connected host.</param>
    public static void ApplyChartSurface(Tool tool, ChartRenderingMode mode)
    {
        if (tool?.Name is null || !PlotlyEmittingToolNames.Contains(tool.Name))
            return;

        switch (mode)
        {
            case ChartRenderingMode.Sep1865:
                // Preserve any pre-existing meta (none today, but future-proof).
                JsonObject meta = tool.Meta ?? new JsonObject();
                meta["ui"] = BuildUiObject();
                tool.Meta = meta;
                break;

            case ChartRenderingMode.LegacyStructuredContent:
                // Host sniffs structuredContent.chart directly — keep
                // include_chart callable, but skip the SEP-1865 _meta.ui
                // envelope it has no use for.
                break;

            case ChartRenderingMode.TextOnly:
                RemoveInputProperty(tool, "include_chart");
                break;
        }
    }

    /// <summary>
    /// Strips the UI-only chart payload (the <c>chart</c> and <c>panel</c> keys)
    /// from a tool result's <c>structuredContent</c> in a <c>tools/call</c>
    /// filter, for hosts that can't render it.
    /// </summary>
    /// <remarks>
    /// <c>structuredContent</c> is a generic MCP field — hosts that support
    /// structured tool output but not MCP Apps feed it into the model context.
    /// Leaving the Plotly spec there buries the analytical summary under a
    /// UI-only JSON blob (SPEC §6, Spike B). When the keys removed leave the
    /// object empty, <c>structuredContent</c> is dropped entirely.
    /// </remarks>
    /// <param name="result">Tool result to patch in place.</param>
    public static void StripChartStructuredContent(CallToolResult result)
    {
        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } sc)
            return;

        JsonObject? obj = JsonObject.Create(sc);
        if (obj is null)
            return;

        // Non-short-circuit | so both keys are evaluated and removed.
        bool removed = obj.Remove("chart") | obj.Remove("panel");
        if (!removed)
            return;

        result.StructuredContent = obj.Count == 0
            ? null
            : JsonSerializer.SerializeToElement(obj);
    }

    /// <summary>
    /// Builds the nested SEP-1865 <c>ui</c> object a chart-emitting tool
    /// advertises in <c>tools/list</c>: <c>resourceUri</c> points at the UI
    /// template; <c>visibility</c> <c>["model", "app"]</c> lets both the LLM and
    /// the host's app surface invoke the tool.
    /// </summary>
    static JsonObject BuildUiObject() => new()
    {
        ["resourceUri"] = PlotlyResourceUri,
        ["visibility"] = new JsonArray("model", "app"),
    };

    /// <summary>
    /// Removes a property from a tool's published <see cref="Tool.InputSchema"/>
    /// — used to hide <c>include_chart</c> from non-UI hosts. No-op when the
    /// schema or the property is absent. Keeps <c>required</c> consistent.
    /// </summary>
    static void RemoveInputProperty(Tool tool, string propertyName)
    {
        JsonElement schema = tool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object)
            return;

        JsonNode? node = JsonNode.Parse(schema.GetRawText());
        if (node is not JsonObject root || root["properties"] is not JsonObject props)
            return;
        if (!props.Remove(propertyName))
            return;

        // include_chart is optional, so it should never be in "required" — but
        // keep the schema internally consistent if some future change adds it.
        if (root["required"] is JsonArray required)
        {
            for (int i = required.Count - 1; i >= 0; i--)
            {
                if (required[i] is JsonValue v &&
                    v.TryGetValue(out string? s) && s == propertyName)
                {
                    required.RemoveAt(i);
                }
            }
        }

        // The InputSchema setter validates the root stays a {"type":"object"}
        // schema; removing a property keeps that invariant.
        tool.InputSchema = JsonSerializer.SerializeToElement(root);
    }

    /// <summary>
    /// CSP advertised on the UI resource so the host's iframe sandbox allows the
    /// Plotly CDN. Without this, hosts default to a tight policy that blocks
    /// the script tag.
    /// </summary>
    /// <remarks>
    /// SEP-1865 shape: <c>_meta.ui.csp</c> takes domain lists, not raw CSP
    /// directives. The host already grants <c>'self'</c>, <c>'unsafe-inline'</c>
    /// (script/style) and <c>data:</c> (img) — servers declare only the extra
    /// origins. <c>resourceDomains</c> maps to <c>script-src</c> /
    /// <c>style-src</c> / <c>img-src</c> / <c>font-src</c> / <c>media-src</c>,
    /// which is all the Plotly CDN needs; the template makes no network calls
    /// of its own, so <c>connectDomains</c> is omitted.
    /// </remarks>
    private static JsonObject BuildResourceUiMeta() => new()
    {
        ["ui"] = new JsonObject
        {
            ["csp"] = new JsonObject
            {
                ["resourceDomains"] = new JsonArray("https://cdn.plot.ly"),
            },
            // Host border defaults vary; the spec recommends being explicit.
            // A candlestick chart reads better inside a bordered card.
            ["prefersBorder"] = true,
        },
    };

    private static string LoadPlotlyTemplate()
    {
        Assembly asm = typeof(UiResources).Assembly;
        using Stream? stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: '{EmbeddedResourceName}'. " +
                "Confirm Apps/PlotlyTemplate.html is included as <EmbeddedResource> in the csproj.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
