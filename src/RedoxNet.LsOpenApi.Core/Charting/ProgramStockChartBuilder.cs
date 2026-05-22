using System.Text.Json.Nodes;

namespace RedoxNet.LsOpenApi.Core.Charting;

/// <summary>Per-stock program-trading chart views (TR t1637).</summary>
internal enum ProgramStockChartView
{
    /// <summary>Intraday cumulative program net buying vs the stock price.</summary>
    IntradayFlow,

    /// <summary>Per-day program net buying as coloured bars vs the closing price.</summary>
    DailyBars,
}

/// <summary>
/// One observation of a single stock's program-trading flow (TR t1637).
/// <see cref="Net"/> is in the caller's display unit (억원).
/// </summary>
/// <param name="Label">Intraday: <c>HH:mm</c>. Daily: <c>yyyy-MM-dd</c>.</param>
/// <param name="Price">Stock price at the observation.</param>
/// <param name="Net">Program net buying — cumulative intraday, per-day for the daily view.</param>
internal sealed record ProgramStockPoint(string Label, double Price, double Net);

/// <summary>Title / axis context for a per-stock program-trading chart.</summary>
/// <param name="Shcode">6-digit short code.</param>
/// <param name="Name">Stock name; empty falls back to the bare code in the title.</param>
/// <param name="DateText">Intraday: session date <c>yyyy-MM-dd</c> (also the x-axis date).
/// Daily: a free range label for the title.</param>
internal sealed record ProgramStockChartMeta(string Shcode, string Name, string DateText);

/// <summary>
/// Builds Plotly chart specs for a single stock's program-trading flow (TR
/// t1637) — an intraday cumulative line or a per-day coloured bar series, each
/// against the stock price.
/// </summary>
internal static class ProgramStockChartBuilder
{
    /// <summary>Chart type discriminator emitted under the envelope <c>type</c> field.</summary>
    public const string ChartType = "plotly";

    /// <summary>Plotly major version this builder targets.</summary>
    public const string PlotlyVersion = "5";

    const string PriceColor = ChartLayout.PriceLineColor;   // neutral grey — not up/down
    const string NetColor = "#9C36B5";
    const string NetFill = "rgba(156,54,181,0.14)";
    const string BuyColor = "#E03131";   // net buy — Korean-convention red
    const string SellColor = "#3498DB";  // net sell — blue

    /// <summary>
    /// Builds the <c>{ type, version, spec }</c> chart envelope for one view.
    /// </summary>
    /// <param name="view">Which view to render.</param>
    /// <param name="meta">Title / axis context.</param>
    /// <param name="points">The series, chronological (oldest first).</param>
    /// <returns>A Plotly envelope; <c>envelope["spec"]</c> is the <c>{ data, layout }</c> object.</returns>
    /// <exception cref="ArgumentException">The point list is empty.</exception>
    public static JsonObject Build(
        ProgramStockChartView view,
        ProgramStockChartMeta meta,
        IReadOnlyList<ProgramStockPoint> points)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("points must not be empty.", nameof(points));

        JsonObject spec = view switch
        {
            ProgramStockChartView.IntradayFlow => BuildIntradayFlow(meta, points),
            ProgramStockChartView.DailyBars => BuildDailyBars(meta, points),
            _ => throw new NotSupportedException($"Program-stock chart view '{view}' is not implemented."),
        };

        return new JsonObject
        {
            ["type"] = ChartType,
            ["version"] = PlotlyVersion,
            ["spec"] = spec,
        };
    }

    /// <summary>Stock label for the title — <c>name (code)</c>, or the bare code.</summary>
    static string TitleSubject(ProgramStockChartMeta meta) =>
        string.IsNullOrWhiteSpace(meta.Name) ? meta.Shcode : $"{meta.Name} ({meta.Shcode})";

    /// <summary>
    /// IntradayFlow: the stock price on the left axis; cumulative program net
    /// buying (filled to zero) on the right — one stock's accumulation /
    /// distribution narrative for the session.
    /// </summary>
    static JsonObject BuildIntradayFlow(ProgramStockChartMeta meta, IReadOnlyList<ProgramStockPoint> p)
    {
        string d = meta.DateText;

        JsonArray X()
        {
            var a = new JsonArray();
            foreach (ProgramStockPoint pt in p) a.Add($"{d}T{pt.Label}:00");
            return a;
        }
        JsonArray Y(Func<ProgramStockPoint, double> sel)
        {
            var a = new JsonArray();
            foreach (ProgramStockPoint pt in p) a.Add(sel(pt));
            return a;
        }

        var tracePrice = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = "주가",
            ["x"] = X(), ["y"] = Y(pt => pt.Price),
            ["line"] = new JsonObject { ["color"] = PriceColor, ["width"] = 1.6 },
            ["hovertemplate"] = "주가 %{y:,.0f}<extra></extra>",
        };
        var traceNet = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = "프로그램 순매수",
            ["x"] = X(), ["y"] = Y(pt => pt.Net), ["yaxis"] = "y2",
            ["fill"] = "tozeroy", ["fillcolor"] = NetFill,
            ["line"] = new JsonObject { ["color"] = NetColor, ["width"] = 2.0 },
            ["hovertemplate"] = "프로그램 순매수 %{y:,.0f}<extra></extra>",
        };

        var layout = new JsonObject
        {
            ["title"] = ChartLayout.Title($"{TitleSubject(meta)} 프로그램매매 추이 — {d}"),
            ["font"] = ChartLayout.Font(),
            ["hovermode"] = "x unified",
            ["showlegend"] = true,
            ["legend"] = ChartLayout.Legend(),
            ["xaxis"] = new JsonObject
            {
                // Auto-ticked: Plotly picks a sensible interval and always
                // renders time labels, whether the session is partial or full.
                ["type"] = "date",
                ["tickformat"] = "%H:%M",
                ["hoverformat"] = "%H:%M",
                ["tickangle"] = 0,
                ["showgrid"] = false,
            },
            ["yaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = "주가 (원)" },
                ["automargin"] = true,
            },
            ["yaxis2"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = "누적 순매수 금액 (억원)" },
                ["overlaying"] = "y",
                ["side"] = "right",
                ["showgrid"] = false,
                ["zeroline"] = true,
                ["automargin"] = true,
            },
            ["margin"] = new JsonObject { ["l"] = 64, ["r"] = 88, ["t"] = 76, ["b"] = 48 },
            ["paper_bgcolor"] = "rgba(0,0,0,0)",
            ["plot_bgcolor"] = "rgba(0,0,0,0)",
        };

        return new JsonObject
        {
            ["data"] = new JsonArray { tracePrice, traceNet },
            ["layout"] = layout,
        };
    }

    /// <summary>
    /// DailyBars: per-day program net buying as bars (red net buy / blue net
    /// sell) on the left axis, with the closing price line on the right —
    /// the multi-day accumulation pattern for one stock.
    /// </summary>
    static JsonObject BuildDailyBars(ProgramStockChartMeta meta, IReadOnlyList<ProgramStockPoint> p)
    {
        JsonArray X()
        {
            var a = new JsonArray();
            foreach (ProgramStockPoint pt in p) a.Add(pt.Label);
            return a;
        }

        // Split net buying / selling into two traces so the legend reads
        // unambiguously; one side is null at each bar, so they never overlap.
        var buyY = new JsonArray();
        var sellY = new JsonArray();
        var priceY = new JsonArray();
        foreach (ProgramStockPoint pt in p)
        {
            buyY.Add(pt.Net > 0 ? JsonValue.Create(pt.Net) : null);
            sellY.Add(pt.Net < 0 ? JsonValue.Create(pt.Net) : null);
            priceY.Add(pt.Price);
        }

        var traceBuy = new JsonObject
        {
            ["type"] = "bar", ["name"] = "프로그램 순매수",
            ["x"] = X(), ["y"] = buyY,
            ["marker"] = new JsonObject { ["color"] = BuyColor },
            ["hovertemplate"] = "순매수 %{y:,.0f}<extra></extra>",
        };
        var traceSell = new JsonObject
        {
            ["type"] = "bar", ["name"] = "프로그램 순매도",
            ["x"] = X(), ["y"] = sellY,
            ["marker"] = new JsonObject { ["color"] = SellColor },
            ["hovertemplate"] = "순매도 %{y:,.0f}<extra></extra>",
        };
        var tracePrice = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = "종가", ["yaxis"] = "y2",
            ["x"] = X(), ["y"] = priceY,
            ["line"] = new JsonObject { ["color"] = PriceColor, ["width"] = 1.8 },
            ["hovertemplate"] = "종가 %{y:,.0f}<extra></extra>",
        };

        // Category x-axis — discrete trading days, no weekend / holiday gaps.
        var xaxis = new JsonObject
        {
            ["type"] = "category",
            ["tickangle"] = 0,
            ["showgrid"] = false,
        };
        int desiredTicks = Math.Min(p.Count, 8);
        var tickvals = new JsonArray();
        var ticktext = new JsonArray();
        int lastTick = -1;
        for (int t = 0; t < desiredTicks; t++)
        {
            int idx = desiredTicks == 1
                ? p.Count - 1
                : (int)Math.Round((double)t * (p.Count - 1) / (desiredTicks - 1));
            if (idx == lastTick) continue;
            lastTick = idx;
            string date = p[idx].Label;   // yyyy-MM-dd
            tickvals.Add(date);
            ticktext.Add(date.Length >= 10 ? date[5..].Replace('-', '/') : date);
        }
        xaxis["tickmode"] = "array";
        xaxis["tickvals"] = tickvals;
        xaxis["ticktext"] = ticktext;

        var layout = new JsonObject
        {
            ["title"] = ChartLayout.Title($"{TitleSubject(meta)} 일별 프로그램매매 — {meta.DateText}"),
            ["font"] = ChartLayout.Font(),
            ["barmode"] = "relative",
            ["hovermode"] = "x unified",
            ["showlegend"] = true,
            ["legend"] = ChartLayout.Legend(),
            ["bargap"] = 0.2,
            ["xaxis"] = xaxis,
            ["yaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = "순매수 금액 (억원)" },
                ["zeroline"] = true,
                ["automargin"] = true,
            },
            ["yaxis2"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = "종가 (원)" },
                ["overlaying"] = "y",
                ["side"] = "right",
                ["showgrid"] = false,
                ["automargin"] = true,
            },
            ["margin"] = new JsonObject { ["l"] = 64, ["r"] = 80, ["t"] = 76, ["b"] = 48 },
            ["paper_bgcolor"] = "rgba(0,0,0,0)",
            ["plot_bgcolor"] = "rgba(0,0,0,0)",
        };

        return new JsonObject
        {
            ["data"] = new JsonArray { traceBuy, traceSell, tracePrice },
            ["layout"] = layout,
        };
    }
}
