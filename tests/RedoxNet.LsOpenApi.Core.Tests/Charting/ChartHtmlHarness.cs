using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

/// <summary>
/// Writes a Plotly chart spec to a standalone, browser-viewable HTML file so
/// the chart-builder output can be eyeballed against real-shaped data.
/// </summary>
/// <remarks>
/// This is a visual-debugging aid, not an assertion helper — the value is the
/// artifact, not a pass/fail. Files land under <c>{test bin}/chart-output/</c>;
/// each harness test logs the absolute path via <c>ITestOutputHelper</c>.
/// Plotly.js is pulled from the CDN, so the generated page needs network
/// access when opened in a browser (the page itself never runs in the test).
/// </remarks>
internal static class ChartHtmlHarness
{
    /// <summary>Plotly.js CDN build used by the generated pages — pinned to the
    /// same major as the spec the builders target (Plotly v5 / plotly.js 2.x).</summary>
    const string PlotlyCdn = "https://cdn.plot.ly/plotly-2.35.2.min.js";

    /// <summary>Relaxed escaping so Korean labels stay readable in the HTML
    /// source (browser rendering is unaffected either way). A TypeInfoResolver
    /// is required for JsonNode.ToJsonString with custom options on .NET 8+.</summary>
    static readonly JsonSerializerOptions SpecJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>
    /// Directory the chart HTML files are written to. Lives next to the test
    /// assembly so it is easy to find after a run and is wiped by <c>clean</c>.
    /// Created on first <see cref="Write"/>.
    /// </summary>
    public static string OutputDir { get; } =
        Path.Combine(AppContext.BaseDirectory, "chart-output");

    /// <summary>
    /// Writes <paramref name="spec"/> — a Plotly <c>{ "data": [...], "layout": {...} }</c>
    /// object, i.e. the <c>spec</c> node a chart builder emits — to
    /// <c>{OutputDir}/{name}.html</c> and returns the absolute path.
    /// </summary>
    /// <param name="name">File stem (no extension); also the page title.</param>
    /// <param name="spec">The Plotly <c>{ data, layout }</c> object to render.</param>
    /// <returns>Absolute path of the written HTML file.</returns>
    public static string Write(string name, JsonNode spec)
    {
        Directory.CreateDirectory(OutputDir);
        string path = Path.Combine(OutputDir, name + ".html");

        string html =
            "<!DOCTYPE html>\n" +
            "<html lang=\"en\">\n" +
            "<head>\n" +
            "  <meta charset=\"utf-8\" />\n" +
            "  <title>" + name + "</title>\n" +
            "  <script src=\"" + PlotlyCdn + "\" charset=\"utf-8\"></script>\n" +
            "  <style>html,body{margin:0}#chart{width:100vw;height:100vh}</style>\n" +
            "</head>\n" +
            "<body>\n" +
            "  <div id=\"chart\"></div>\n" +
            "  <script>\n" +
            "    var spec = " + spec.ToJsonString(SpecJson) + ";\n" +
            "    Plotly.newPlot('chart', spec.data, spec.layout, { responsive: true });\n" +
            "  </script>\n" +
            "</body>\n" +
            "</html>\n";

        File.WriteAllText(path, html);
        return path;
    }
}
