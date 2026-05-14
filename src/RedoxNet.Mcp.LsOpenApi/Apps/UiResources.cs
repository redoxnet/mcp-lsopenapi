using System.Reflection;
using System.Text;
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
/// A single generic Plotly template handles every chart-emitting tool — the
/// shape of the spec (candlestick, treemap, line, ...) is decided server-side
/// and travels through <c>structuredContent.chart.spec</c>. The template
/// supports a per-tool side panel (e.g. ETF top-10 list) under
/// <c>structuredContent.panel</c>.
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

    /// <summary>Handler for <c>resources/list</c>. Advertises the one UI
    /// resource this server publishes.</summary>
    public static ValueTask<ListResourcesResult> ListAsync(
        RequestContext<ListResourcesRequestParams> request,
        CancellationToken cancellationToken)
    {
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
                                  "(candlestick, treemap, scatter, ...). Used by ls_get_chart and " +
                                  "ls_get_etf_holdings via MCP Apps (SEP-1865).",
                    MimeType = PlotlyMimeType,
                    Meta = BuildResourceUiMeta(),
                },
            },
        };
        return ValueTask.FromResult(result);
    }

    /// <summary>Handler for <c>resources/read</c>. Returns the embedded HTML
    /// template for <see cref="PlotlyResourceUri"/>; any other URI is
    /// rejected.</summary>
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
    /// Names of tools whose results this server emits as Plotly specs.
    /// Used by <see cref="PatchToolMetaIfChartEmitting"/> to attach the
    /// SEP-1865 <c>_meta.ui</c> envelope at <c>tools/list</c> time.
    /// </summary>
    private static readonly HashSet<string> PlotlyEmittingToolNames = new(StringComparer.Ordinal)
    {
        "ls_get_chart",
        "ls_get_etf_holdings",
    };

    /// <summary>
    /// Builds the nested <c>_meta.ui</c> object a chart-emitting tool advertises
    /// in <c>tools/list</c>. Hosts use this to pair the tool with
    /// <see cref="PlotlyResourceUri"/>.
    /// </summary>
    /// <remarks>
    /// SEP-1865 fields:
    /// <list type="bullet">
    ///   <item><c>resourceUri</c> — points at the UI template.</item>
    ///   <item><c>visibility</c> — <c>["model", "app"]</c> so both the LLM and the
    ///   host's app surface can invoke the tool.</item>
    /// </list>
    /// </remarks>
    public static JsonObject BuildToolUiMeta() => new()
    {
        ["ui"] = new JsonObject
        {
            ["resourceUri"] = PlotlyResourceUri,
            ["visibility"] = new JsonArray("model", "app"),
        },
    };

    /// <summary>
    /// Mutates <paramref name="tool"/>.<see cref="Tool.Meta"/> to add the
    /// SEP-1865 <c>ui</c> object when the tool is one of our chart-emitters.
    /// Called from a <c>tools/list</c> filter so the attribute-based tool
    /// registration stays untouched.
    /// </summary>
    /// <param name="tool">Tool descriptor to patch in place.</param>
    public static void PatchToolMetaIfChartEmitting(Tool tool)
    {
        if (tool?.Name is null || !PlotlyEmittingToolNames.Contains(tool.Name))
            return;

        // Preserve any pre-existing meta (none today, but future-proof).
        JsonObject meta = tool.Meta ?? new JsonObject();
        meta["ui"] = new JsonObject
        {
            ["resourceUri"] = PlotlyResourceUri,
            ["visibility"] = new JsonArray("model", "app"),
        };
        tool.Meta = meta;
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
