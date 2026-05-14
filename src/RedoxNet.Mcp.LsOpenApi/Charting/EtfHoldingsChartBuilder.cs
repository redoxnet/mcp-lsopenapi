using System.Globalization;
using System.Text.Json.Nodes;

namespace RedoxNet.Mcp.LsOpenApi.Charting;

/// <summary>
/// Builds the Plotly treemap + side-panel pair that
/// <c>ls_get_etf_holdings</c> ships under <c>structuredContent</c> when
/// <c>include_chart=true</c>. The treemap answers "이 ETF가 어디에 베팅하고 있나"
/// at a glance; the side panel carries the exact figures the treemap can't
/// fit (top-10 weights + cumulative + concentration badge).
/// </summary>
/// <remarks>
/// Design choices (see ADR-style discussion in the v0.2.0 release notes):
/// <list type="bullet">
///   <item><description><b>Single color, not weight-graded</b> — size already
///   encodes weight. Coloring by weight is redundant; the persona's question
///   ("어디에 베팅하는가") is answered by area, not hue.</description></item>
///   <item><description><b>Top-10 labels only</b> — beyond rank 10 the cells
///   are too small for Korean labels; hover/list fills in the rest.</description></item>
///   <item><description><b>Cash mentioned in the panel, not the treemap</b> —
///   t1904 doesn't classify each holding's asset class. Adding a synthetic
///   cash cell would risk double-counting against weights LS already rolled
///   up. A panel note carries the cash percentage without distorting the
///   treemap.</description></item>
///   <item><description><b>Concentration is a characteristic, not a warning</b>
///   — thematic ETFs are *expected* to be concentrated. 4-bucket neutral
///   badges (분산형 / 균형형 / 집중형 / 초집중형) describe shape rather than
///   render judgement.</description></item>
/// </list>
/// </remarks>
internal static class EtfHoldingsChartBuilder
{
    /// <summary>Korean blue (#3498DB). Same hex as the falling-candle color in
    /// <see cref="PlotlyChartBuilder"/>; here it carries no directional
    /// meaning, just provides a consistent palette across the package.</summary>
    const string HoldingColor = "#3498DB";

    /// <summary>Chart type discriminator emitted under the top-level <c>chart</c> field.</summary>
    public const string ChartType = "plotly";

    /// <summary>Plotly major version this builder targets.</summary>
    public const string PlotlyVersion = "5";

    /// <summary>Maximum holdings whose name + weight% is rendered inside the
    /// cell. Smaller cells get hover-only labels.</summary>
    const int VisibleLabelCount = 10;

    /// <summary>One ETF constituent flattened down to the fields the chart
    /// builder cares about. Keeps the builder testable without depending on
    /// the LS API client layer.</summary>
    /// <param name="Shcode">6-digit Korean short code of the holding.</param>
    /// <param name="Name">Display name in Korean (LS <c>hname</c>); falls back to <see cref="Shcode"/> when null.</param>
    /// <param name="WeightPercent">LS-reported weight, already a percentage (0–100).</param>
    /// <param name="MarketValue">Per-holding evaluation in 백만원 (LS <c>value</c>); used in the hover tooltip.</param>
    public sealed record EtfHoldingView(string Shcode, string? Name, double WeightPercent, long MarketValue);

    /// <summary>
    /// Builds the <c>structuredContent</c> payload: <c>chart</c> envelope (Plotly
    /// treemap spec) plus a <c>panel</c> the iframe renders next to/under the
    /// treemap. Returns <see langword="null"/> when there is nothing to chart.
    /// </summary>
    /// <param name="shcode">ETF short code; appears in the panel title.</param>
    /// <param name="holdings">Constituents, ideally pre-sorted by weight descending (LS <c>sgb=1</c> already does this).</param>
    /// <param name="cashPercent">Cash buffer as a percentage of NAV. Surfaces in panel notes only.</param>
    /// <returns>JsonObject with <c>chart</c> + <c>panel</c>, or <see langword="null"/> when holdings is empty.</returns>
    public static JsonObject? Build(
        string shcode,
        IReadOnlyList<EtfHoldingView> holdings,
        double? cashPercent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shcode);
        ArgumentNullException.ThrowIfNull(holdings);

        if (holdings.Count == 0)
            return null;

        // Defensive: ensure descending sort even if the caller didn't.
        // We don't mutate the caller's list.
        var sorted = holdings.OrderByDescending(h => h.WeightPercent).ToList();

        double[] cumulative = ComputeCumulativeWeights(sorted);
        string concentrationBadge = ClassifyConcentration(cumulative);

        return new JsonObject
        {
            ["chart"] = BuildChart(shcode, sorted, cumulative),
            ["panel"] = BuildPanel(shcode, sorted, cumulative, concentrationBadge, cashPercent),
        };
    }

    /// <summary>Builds the Plotly treemap spec (one trace, flat hierarchy).</summary>
    static JsonObject BuildChart(
        string shcode,
        IReadOnlyList<EtfHoldingView> sorted,
        double[] cumulative)
    {
        var labels = new JsonArray();
        var parents = new JsonArray();
        var values = new JsonArray();
        var text = new JsonArray();
        var customData = new JsonArray();
        var colors = new JsonArray();

        for (int i = 0; i < sorted.Count; i++)
        {
            EtfHoldingView h = sorted[i];
            string name = !string.IsNullOrEmpty(h.Name) ? h.Name : h.Shcode;

            labels.Add(name);
            parents.Add(string.Empty); // flat: every cell sits at the root
            values.Add(h.WeightPercent);

            // Suppress in-cell text for ranks 11+; the panel's top-10 list
            // and hover tooltips carry the same information for those cells.
            string cellText = i < VisibleLabelCount
                ? $"{name}<br>{h.WeightPercent:0.00}%"
                : string.Empty;
            text.Add(cellText);

            // [0] = cumulative weight %, [1] = market value (백만원).
            customData.Add(new JsonArray(cumulative[i], h.MarketValue));
            colors.Add(HoldingColor);
        }

        var trace = new JsonObject
        {
            ["type"] = "treemap",
            ["labels"] = labels,
            ["parents"] = parents,
            ["values"] = values,
            ["text"] = text,
            ["textinfo"] = "text",
            ["textposition"] = "middle center",
            ["textfont"] = new JsonObject { ["size"] = 12 },
            ["customdata"] = customData,
            ["hovertemplate"] =
                "<b>%{label}</b><br>" +
                "비중 %{value:.2f}%<br>" +
                "누적 %{customdata[0]:.1f}%<br>" +
                "평가금액 %{customdata[1]:,}백만원" +
                "<extra></extra>",
            ["marker"] = new JsonObject { ["colors"] = colors },
            // Plotly default tiling.packing="squarify" — largest cell goes
            // top-left; the spec doesn't need to set it explicitly. `sort: true`
            // (also default) keeps cells in descending value order.
        };

        var layout = new JsonObject
        {
            ["margin"] = new JsonObject { ["t"] = 0, ["r"] = 0, ["b"] = 0, ["l"] = 0 },
            ["paper_bgcolor"] = "rgba(0,0,0,0)",
            ["plot_bgcolor"] = "rgba(0,0,0,0)",
            ["showlegend"] = false,
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

    /// <summary>Builds the side-panel JSON that the iframe template
    /// formats next to/under the treemap.</summary>
    static JsonObject BuildPanel(
        string shcode,
        IReadOnlyList<EtfHoldingView> sorted,
        double[] cumulative,
        string concentrationBadge,
        double? cashPercent)
    {
        var topHoldings = new JsonArray();
        int topN = Math.Min(VisibleLabelCount, sorted.Count);
        for (int i = 0; i < topN; i++)
        {
            EtfHoldingView h = sorted[i];
            topHoldings.Add(new JsonObject
            {
                ["name"] = !string.IsNullOrEmpty(h.Name) ? h.Name : h.Shcode,
                ["shcode"] = h.Shcode,
                ["weight_pct"] = Round2(h.WeightPercent),
                ["cumulative_pct"] = Round2(cumulative[i]),
            });
        }

        double top5Cumulative = cumulative.Length > 0
            ? cumulative[Math.Min(4, cumulative.Length - 1)]
            : 0;

        var notes = new JsonArray();
        EtfHoldingView? heavyweight = sorted.FirstOrDefault(h => h.WeightPercent >= 30.0);
        if (heavyweight is not null)
        {
            string name = !string.IsNullOrEmpty(heavyweight.Name) ? heavyweight.Name : heavyweight.Shcode;
            notes.Add(string.Format(
                CultureInfo.InvariantCulture,
                "단일 종목 비중 30% 초과: {0} ({1:0.0}%)",
                name, heavyweight.WeightPercent));
        }
        if (cashPercent is double cp && cp > 0.05)
        {
            notes.Add(string.Format(
                CultureInfo.InvariantCulture, "현금 비중 {0:0.0}%", cp));
        }

        return new JsonObject
        {
            ["kind"] = "etf_holdings",
            ["title"] = $"구성종목 ({shcode})",
            ["concentration"] = new JsonObject
            {
                ["badge"] = concentrationBadge,
                ["top5_pct"] = Round2(top5Cumulative),
            },
            ["top_holdings"] = topHoldings,
            ["notes"] = notes,
        };
    }

    /// <summary>Returns cumulative weight %s aligned 1:1 with the input.</summary>
    static double[] ComputeCumulativeWeights(IReadOnlyList<EtfHoldingView> sorted)
    {
        double[] cum = new double[sorted.Count];
        double running = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            running += sorted[i].WeightPercent;
            cum[i] = running;
        }
        return cum;
    }

    /// <summary>
    /// 4-bucket concentration classification based on the top-5 cumulative
    /// weight. The cutoffs (35 / 60 / 80) come from empirical scanning of
    /// Korean ETF holdings: typical broad-market trackers (KODEX 200) land in
    /// 분산형, sector ETFs in 균형형 / 집중형, thematic / top-N ETFs in
    /// 초집중형. Buckets are characteristics, not warnings.
    /// </summary>
    static string ClassifyConcentration(double[] cumulative)
    {
        if (cumulative.Length == 0) return "정보 없음";
        double top5 = cumulative[Math.Min(4, cumulative.Length - 1)];
        return top5 switch
        {
            < 35.0 => "분산형",
            < 60.0 => "균형형",
            < 80.0 => "집중형",
            _ => "초집중형",
        };
    }

    static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
