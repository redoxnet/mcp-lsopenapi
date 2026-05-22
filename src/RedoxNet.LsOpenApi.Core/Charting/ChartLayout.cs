using System.Text.Json.Nodes;

namespace RedoxNet.LsOpenApi.Core.Charting;

/// <summary>
/// Shared Plotly layout conventions for the chart builders — font sizes and the
/// title / legend placement recipe — so <see cref="CandlestickChartBuilder"/>
/// and <see cref="ProgramTradeChartBuilder"/> render with one consistent look
/// and cannot drift apart.
/// </summary>
internal static class ChartLayout
{
    /// <summary>Base font size — axis ticks, axis titles, legend.</summary>
    public const int FontSize = 14;

    /// <summary>Chart title font size.</summary>
    public const int TitleFontSize = 18;

    /// <summary>Annotation font size.</summary>
    public const int AnnotationFontSize = 12;

    /// <summary>
    /// Neutral line colour for price / index reference series. Readable on light
    /// and dark host themes, and deliberately not red or blue — those are
    /// reserved for the Korean up / down (상승 / 하락) convention.
    /// </summary>
    public const string PriceLineColor = "#7F8C8D";

    /// <summary>
    /// Builds the chart title block: centered, anchored just above the plot area
    /// (<c>yref=paper</c> + bottom padding) so it never collides with the legend
    /// regardless of window size or aspect ratio.
    /// </summary>
    /// <param name="text">Title text.</param>
    public static JsonObject Title(string text) => new()
    {
        ["text"] = text,
        ["font"] = new JsonObject { ["size"] = TitleFontSize },
        ["x"] = 0.5,
        ["xanchor"] = "center",
        ["y"] = 1.0,
        ["yanchor"] = "bottom",
        ["yref"] = "paper",
        ["pad"] = new JsonObject { ["b"] = 44 },
    };

    /// <summary>
    /// Builds the horizontal legend block: left-aligned, anchored just above the
    /// plot area. Inherits <see cref="FontSize"/> — no per-chart override.
    /// </summary>
    public static JsonObject Legend() => new()
    {
        ["orientation"] = "h",
        ["x"] = 0,
        ["xanchor"] = "left",
        ["y"] = 1.0,
        ["yanchor"] = "bottom",
    };

    /// <summary>Builds the layout-level base font block.</summary>
    public static JsonObject Font() => new() { ["size"] = FontSize };
}
