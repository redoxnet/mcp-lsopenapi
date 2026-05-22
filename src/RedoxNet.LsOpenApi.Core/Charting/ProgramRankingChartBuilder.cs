using System.Text.Json.Nodes;

namespace RedoxNet.LsOpenApi.Core.Charting;

/// <summary>
/// One stock's row in a program-trading ranking (TR t1636). Values are already
/// in the caller's display unit, so the builder stays unit-agnostic.
/// </summary>
/// <param name="Rank">LS-assigned rank within the sort.</param>
/// <param name="Name">Stock name (display).</param>
/// <param name="Shcode">6-digit short code.</param>
/// <param name="NetValue">Program net buying in the display unit (억원 or 주); negative = net selling.</param>
/// <param name="MktCapRatio">Net buying as a percentage of market cap — the normalized footprint metric.</param>
/// <param name="Diff">Price change for the session, percent.</param>
internal sealed record ProgramRankingRow(
    int Rank,
    string Name,
    string Shcode,
    double NetValue,
    double MktCapRatio,
    double Diff);

/// <summary>Title / axis context for a program-trading ranking chart.</summary>
/// <param name="Market">Market key — <c>kospi</c> or <c>kosdaq</c>.</param>
/// <param name="SortLabel">Human sort label, e.g. <c>순매수 상위</c>.</param>
/// <param name="MeasureLabel">X-axis measure label, e.g. <c>순매수 금액 (억원)</c>.</param>
/// <param name="DateText">Snapshot date, <c>yyyy-MM-dd</c>.</param>
internal sealed record ProgramRankingChartMeta(
    string Market,
    string SortLabel,
    string MeasureLabel,
    string DateText);

/// <summary>
/// Builds a horizontal-bar Plotly spec for a per-stock program-trading ranking
/// (TR t1636). Rank 1 sits at the top; bars are coloured red for net buying and
/// blue for net selling, so a mixed ranking still reads at a glance.
/// </summary>
internal static class ProgramRankingChartBuilder
{
    /// <summary>Chart type discriminator emitted under the envelope <c>type</c> field.</summary>
    public const string ChartType = "plotly";

    /// <summary>Plotly major version this builder targets.</summary>
    public const string PlotlyVersion = "5";

    // Korean-convention colours: net buy red, net sell blue.
    const string BuyColor = "#E03131";
    const string SellColor = "#3498DB";

    /// <summary>
    /// Builds the <c>{ type, version, spec }</c> chart envelope for one ranking.
    /// </summary>
    /// <param name="meta">Title / axis context.</param>
    /// <param name="rows">The ranking rows, rank-ascending (rank 1 first).</param>
    /// <returns>A Plotly envelope; <c>envelope["spec"]</c> is the <c>{ data, layout }</c> object.</returns>
    /// <exception cref="ArgumentException">The row list is empty.</exception>
    public static JsonObject Build(
        ProgramRankingChartMeta meta,
        IReadOnlyList<ProgramRankingRow> rows)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            throw new ArgumentException("rows must not be empty.", nameof(rows));

        // Plotly draws the first horizontal-bar entry at the bottom of the axis,
        // so feed the rows in reverse — rank 1 then lands at the top.
        var x = new JsonArray();
        var y = new JsonArray();
        var colors = new JsonArray();
        var customdata = new JsonArray();
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            ProgramRankingRow r = rows[i];
            x.Add(r.NetValue);
            y.Add(r.Name);
            colors.Add(r.NetValue >= 0 ? BuyColor : SellColor);
            customdata.Add(r.MktCapRatio);
        }

        var trace = new JsonObject
        {
            ["type"] = "bar",
            ["orientation"] = "h",
            ["x"] = x,
            ["y"] = y,
            ["marker"] = new JsonObject { ["color"] = colors },
            ["customdata"] = customdata,
            ["textposition"] = "outside",
            ["texttemplate"] = "%{x:,.0f}",
            ["cliponaxis"] = false,
            ["hovertemplate"] = "%{y}<br>%{x:,.1f}  (시총대비 %{customdata:.2f}%)<extra></extra>",
        };

        string marketLabel = string.Equals(meta.Market, "kosdaq", StringComparison.OrdinalIgnoreCase)
            ? "KOSDAQ" : "KOSPI";
        string title = $"{marketLabel} 프로그램매매 {meta.SortLabel} {rows.Count}종목 — {meta.DateText}";

        var layout = new JsonObject
        {
            ["title"] = ChartLayout.Title(title),
            ["font"] = ChartLayout.Font(),
            ["showlegend"] = false,
            ["bargap"] = 0.32,
            ["xaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = meta.MeasureLabel },
                ["zeroline"] = true,
                ["showgrid"] = true,
                ["automargin"] = true,
            },
            ["yaxis"] = new JsonObject
            {
                ["type"] = "category",
                ["automargin"] = true,
            },
            ["margin"] = new JsonObject { ["l"] = 8, ["r"] = 64, ["t"] = 72, ["b"] = 48 },
            ["paper_bgcolor"] = "rgba(0,0,0,0)",
            ["plot_bgcolor"] = "rgba(0,0,0,0)",
        };

        return new JsonObject
        {
            ["type"] = ChartType,
            ["version"] = PlotlyVersion,
            ["spec"] = new JsonObject
            {
                ["data"] = new JsonArray { trace },
                ["layout"] = layout,
            },
        };
    }
}
