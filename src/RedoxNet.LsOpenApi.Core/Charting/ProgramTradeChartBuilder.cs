using System.Text.Json.Nodes;

namespace RedoxNet.LsOpenApi.Core.Charting;

/// <summary>
/// Program-trading (프로그램매매) chart views. Each answers a distinct question;
/// <c>ls_get_program_trading</c> selects one via its <c>chart_view</c> parameter.
/// </summary>
internal enum ProgramTradeChartView
{
    /// <summary>K200 price vs cumulative total / arbitrage / non-arbitrage flow —
    /// the default institutional accumulation / distribution narrative.</summary>
    FlowOverview,

    /// <summary>Basis vs cumulative arbitrage flow — arbitrage timing, basis-driven flow.</summary>
    BasisArbitrage,

    /// <summary>Per-minute bars coloured by arbitrage / non-arbitrage — spike
    /// concentration at the open and close auctions.</summary>
    IntensityBars,

    /// <summary>Today's cumulative against an N-day baseline envelope. Needs
    /// accumulated intraday history — deferred to v1.5.</summary>
    BaselineComparison,

    /// <summary>Per-5-minute gross buying vs selling, split into 비차익 / 차익
    /// panels — separates one-directional accumulation from two-way churn.</summary>
    GrossFlow,

    /// <summary>Per-day 비차익 / 차익 net buying as stacked bars vs the index —
    /// the market's multi-day program-trading history (TR t1633).</summary>
    MarketDaily,
}

/// <summary>Title / axis context for a program-trading chart.</summary>
/// <param name="Market">Market key — <c>kospi</c> or <c>kosdaq</c>.</param>
/// <param name="DayLabel">Session key — <c>today</c> or <c>yesterday</c>.</param>
/// <param name="DateText">Resolved session date, <c>yyyy-MM-dd</c> — also the date
/// component of every x value, so the chart renders on a real time axis.</param>
/// <param name="MeasureLabel">Y-axis measure suffix, e.g. <c>금액 (억원)</c> or <c>수량 (천주)</c>.</param>
internal sealed record ProgramChartMeta(
    string Market,
    string DayLabel,
    string DateText,
    string MeasureLabel);

/// <summary>
/// Builds Plotly chart specs for market-wide program-trading flow.
/// </summary>
/// <remarks>
/// v1.1 slice 2a implements <see cref="ProgramTradeChartView.FlowOverview"/>; the
/// remaining views land in later slices. The builder takes a flat
/// <see cref="ProgramFlowPoint"/> list (values already in the caller's display
/// unit) so it stays testable without the LS API client layer.
/// <para>
/// Intraday x values are real ISO datetimes (<c>{DateText}T{HH:mm}:00</c>) so
/// Plotly uses a true time axis — irregular / downsampled minutes keep their
/// real spacing, unlike a categorical axis which would stretch the dense
/// closing-auction window.
/// </para>
/// </remarks>
internal static class ProgramTradeChartBuilder
{
    /// <summary>Chart type discriminator emitted under the envelope <c>type</c> field.</summary>
    public const string ChartType = "plotly";

    /// <summary>Plotly major version this builder targets.</summary>
    public const string PlotlyVersion = "5";

    // Program-trading colour convention: 차익 red, 비차익 gold, 전체 purple.
    // Gold (not orange) keeps 비차익 clearly distinct from the red 차익. Mid-tone
    // hues so they read on both light and dark host themes.
    const string K200Color = ChartLayout.PriceLineColor;   // neutral grey — not up/down
    const string TotalColor = "#9C36B5";
    const string NonArbColor = "#B8860B";
    const string ArbColor = "#E03131";
    const string BasisColor = "#1098AD";
    const string AuctionShade = "rgba(120,120,120,0.10)";

    /// <summary>
    /// One minute of market program-trading flow — the builder's flat input row.
    /// Cumulative fields are in the caller's display unit (e.g. 억원).
    /// </summary>
    /// <param name="Time">Display time, <c>HH:mm</c>.</param>
    /// <param name="Kospi200">KOSPI200 index level.</param>
    /// <param name="Basis">KOSPI200 futures basis.</param>
    /// <param name="Net">Total program net buying, cumulative from the session open.</param>
    /// <param name="Arbitrage">Arbitrage (차익) net buying, cumulative.</param>
    /// <param name="NonArbitrage">Non-arbitrage (비차익) net buying, cumulative.</param>
    /// <param name="MinuteNet">Per-minute total net delta.</param>
    /// <param name="ArbitrageBuy">Arbitrage gross buying, cumulative (for GrossFlow).</param>
    /// <param name="ArbitrageSell">Arbitrage gross selling, cumulative (for GrossFlow).</param>
    /// <param name="NonArbitrageBuy">Non-arbitrage gross buying, cumulative (for GrossFlow).</param>
    /// <param name="NonArbitrageSell">Non-arbitrage gross selling, cumulative (for GrossFlow).</param>
    public sealed record ProgramFlowPoint(
        string Time,
        double Kospi200,
        double Basis,
        double Net,
        double Arbitrage,
        double NonArbitrage,
        double MinuteNet,
        double ArbitrageBuy = 0,
        double ArbitrageSell = 0,
        double NonArbitrageBuy = 0,
        double NonArbitrageSell = 0);

    /// <summary>
    /// Builds the <c>{ type, version, spec }</c> chart envelope for one view.
    /// </summary>
    /// <param name="view">Which chart view to render.</param>
    /// <param name="meta">Title / axis context.</param>
    /// <param name="points">The minute series, chronological (oldest first).</param>
    /// <returns>A Plotly envelope; <c>envelope["spec"]</c> is the <c>{ data, layout }</c> object.</returns>
    /// <exception cref="NotSupportedException">The view is not implemented yet.</exception>
    public static JsonObject Build(
        ProgramTradeChartView view,
        ProgramChartMeta meta,
        IReadOnlyList<ProgramFlowPoint> points)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            throw new ArgumentException("points must not be empty.", nameof(points));

        JsonObject spec = view switch
        {
            ProgramTradeChartView.FlowOverview => BuildFlowOverview(meta, points),
            ProgramTradeChartView.BasisArbitrage => BuildBasisArbitrage(meta, points),
            ProgramTradeChartView.IntensityBars => BuildIntensityBars(meta, points),
            ProgramTradeChartView.GrossFlow => BuildGrossFlow(meta, points),
            ProgramTradeChartView.MarketDaily => BuildMarketDaily(meta, points),
            _ => throw new NotSupportedException(
                $"Program-trading chart view '{view}' is not implemented yet."),
        };

        return new JsonObject
        {
            ["type"] = ChartType,
            ["version"] = PlotlyVersion,
            ["spec"] = spec,
        };
    }

    /// <summary>
    /// FlowOverview: K200 index on the left axis; cumulative 전체 / 비차익 / 차익
    /// net buying on the right axis (Naver 3-line convention). The closing
    /// single-price auction window is shaded; trough / 15:30 / close are annotated.
    /// </summary>
    static JsonObject BuildFlowOverview(ProgramChartMeta meta, IReadOnlyList<ProgramFlowPoint> p)
    {
        string d = meta.DateText;
        string marketLabel = string.Equals(meta.Market, "kosdaq", StringComparison.OrdinalIgnoreCase)
            ? "KOSDAQ" : "KOSPI200";
        string indexLabel = $"{marketLabel} 지수";

        JsonArray X()
        {
            var a = new JsonArray();
            foreach (ProgramFlowPoint pt in p) a.Add(Iso(d, pt.Time));
            return a;
        }
        JsonArray Y(Func<ProgramFlowPoint, double> sel)
        {
            var a = new JsonArray();
            foreach (ProgramFlowPoint pt in p) a.Add(sel(pt));
            return a;
        }

        var traceK200 = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = indexLabel,
            ["x"] = X(), ["y"] = Y(pt => pt.Kospi200),
            ["line"] = new JsonObject { ["color"] = K200Color, ["width"] = 1.6 },
            ["hovertemplate"] = $"{indexLabel} %{{y:.2f}}<extra></extra>",
        };
        // 전체 순매수 is the backdrop: a faint filled region, not a third bold
        // line — so 비차익 / 차익 read as the actual decomposition.
        var traceTotal = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = "전체 순매수",
            ["x"] = X(), ["y"] = Y(pt => pt.Net), ["yaxis"] = "y2",
            ["fill"] = "tozeroy",
            ["fillcolor"] = "rgba(156,54,181,0.12)",
            ["line"] = new JsonObject { ["color"] = TotalColor, ["width"] = 1.0 },
            ["hovertemplate"] = "전체 %{y:,.0f}<extra></extra>",
        };
        var traceNonArb = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = "비차익 순매수",
            ["x"] = X(), ["y"] = Y(pt => pt.NonArbitrage), ["yaxis"] = "y2",
            ["line"] = new JsonObject { ["color"] = NonArbColor, ["width"] = 1.9 },
            ["hovertemplate"] = "비차익 %{y:,.0f}<extra></extra>",
        };
        var traceArb = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = "차익 순매수",
            ["x"] = X(), ["y"] = Y(pt => pt.Arbitrage), ["yaxis"] = "y2",
            ["line"] = new JsonObject { ["color"] = ArbColor, ["width"] = 1.6 },
            ["hovertemplate"] = "차익 %{y:,.0f}<extra></extra>",
        };

        string dayWord = string.Equals(meta.DayLabel, "yesterday", StringComparison.OrdinalIgnoreCase)
            ? "어제" : "오늘";
        string title = $"{marketLabel} 프로그램매매 — {meta.DateText} ({dayWord})";

        var layout = new JsonObject
        {
            ["title"] = ChartLayout.Title(title),
            ["font"] = ChartLayout.Font(),
            ["hovermode"] = "x unified",
            ["showlegend"] = true,
            ["legend"] = ChartLayout.Legend(),
            ["xaxis"] = new JsonObject
            {
                ["type"] = "date",
                ["tickformat"] = "%H:%M",
                ["hoverformat"] = "%H:%M",
                ["dtick"] = 3600000,                       // one hour, in ms
                ["tick0"] = $"{d}T09:00:00",
                ["tickangle"] = 0,
                ["showgrid"] = false,
            },
            ["yaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = indexLabel },
                ["automargin"] = true,
            },
            ["yaxis2"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = $"누적 순매수 {meta.MeasureLabel}" },
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

        JsonArray shapes = BuildAuctionShade(d, p);
        if (shapes.Count > 0) layout["shapes"] = shapes;

        JsonArray annotations = BuildAnnotations(d, p);
        if (annotations.Count > 0) layout["annotations"] = annotations;

        return new JsonObject
        {
            ["data"] = new JsonArray { traceK200, traceTotal, traceNonArb, traceArb },
            ["layout"] = layout,
        };
    }

    /// <summary>
    /// BasisArbitrage: KOSPI200 futures basis on the left axis vs cumulative
    /// arbitrage (차익) net buying (filled) on the right — shows how mechanically
    /// the arbitrage flow tracks the basis. The closing auction window is shaded.
    /// </summary>
    static JsonObject BuildBasisArbitrage(ProgramChartMeta meta, IReadOnlyList<ProgramFlowPoint> p)
    {
        string d = meta.DateText;
        string marketLabel = string.Equals(meta.Market, "kosdaq", StringComparison.OrdinalIgnoreCase)
            ? "KOSDAQ" : "KOSPI200";

        JsonArray X()
        {
            var a = new JsonArray();
            foreach (ProgramFlowPoint pt in p) a.Add(Iso(d, pt.Time));
            return a;
        }
        JsonArray Y(Func<ProgramFlowPoint, double> sel)
        {
            var a = new JsonArray();
            foreach (ProgramFlowPoint pt in p) a.Add(sel(pt));
            return a;
        }

        var traceBasis = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = "베이시스",
            ["x"] = X(), ["y"] = Y(pt => pt.Basis),
            ["line"] = new JsonObject { ["color"] = BasisColor, ["width"] = 1.6 },
            ["hovertemplate"] = "베이시스 %{y:.2f}<extra></extra>",
        };
        var traceArb = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = "누적 차익 순매수",
            ["x"] = X(), ["y"] = Y(pt => pt.Arbitrage), ["yaxis"] = "y2",
            ["fill"] = "tozeroy",
            ["fillcolor"] = "rgba(224,49,49,0.12)",
            ["line"] = new JsonObject { ["color"] = ArbColor, ["width"] = 1.8 },
            ["hovertemplate"] = "차익 누적 %{y:,.0f}<extra></extra>",
        };

        string dayWord = string.Equals(meta.DayLabel, "yesterday", StringComparison.OrdinalIgnoreCase)
            ? "어제" : "오늘";
        string title = $"{marketLabel} 베이시스 vs 차익거래 — {meta.DateText} ({dayWord})";

        var layout = new JsonObject
        {
            ["title"] = ChartLayout.Title(title),
            ["font"] = ChartLayout.Font(),
            ["hovermode"] = "x unified",
            ["showlegend"] = true,
            ["legend"] = ChartLayout.Legend(),
            ["xaxis"] = new JsonObject
            {
                ["type"] = "date",
                ["tickformat"] = "%H:%M",
                ["hoverformat"] = "%H:%M",
                ["dtick"] = 3600000,
                ["tick0"] = $"{d}T09:00:00",
                ["tickangle"] = 0,
                ["showgrid"] = false,
            },
            ["yaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = "베이시스" },
                ["zeroline"] = true,
                ["automargin"] = true,
            },
            ["yaxis2"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = $"누적 차익 {meta.MeasureLabel}" },
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

        JsonArray shapes = BuildAuctionShade(d, p);
        if (shapes.Count > 0) layout["shapes"] = shapes;

        return new JsonObject
        {
            ["data"] = new JsonArray { traceBasis, traceArb },
            ["layout"] = layout,
        };
    }

    /// <summary>
    /// IntensityBars: per-5-minute net buying in two stacked panels — 비차익
    /// (top) and 차익 (bottom). Each bar is that bucket's net delta of the
    /// cumulative field; the zero line splits net buying from net selling, so
    /// open / closing-auction spikes stand out without the 매수·매도 detail
    /// that <see cref="ProgramTradeChartView.GrossFlow"/> adds.
    /// </summary>
    static JsonObject BuildIntensityBars(ProgramChartMeta meta, IReadOnlyList<ProgramFlowPoint> p)
    {
        string d = meta.DateText;
        string marketLabel = string.Equals(meta.Market, "kosdaq", StringComparison.OrdinalIgnoreCase)
            ? "KOSDAQ" : "KOSPI200";

        static string BucketFloor(string hhmm)
        {
            int m = (MinutesOf(hhmm) / 5) * 5;
            return $"{m / 60:00}:{m % 60:00}";
        }

        // Aggregate into 5-minute buckets: each carries that bucket's net delta
        // of the cumulative 비차익 / 차익 fields.
        var times = new List<string>();
        var nonArb = new List<double>();
        var arb = new List<double>();
        double pNonArb = 0, pArb = 0;
        int curBucket = -1, lastIdx = -1;

        void Flush(int idx)
        {
            ProgramFlowPoint e = p[idx];
            times.Add(Iso(d, BucketFloor(e.Time)));
            nonArb.Add(e.NonArbitrage - pNonArb);
            arb.Add(e.Arbitrage - pArb);
            pNonArb = e.NonArbitrage;
            pArb = e.Arbitrage;
        }

        for (int i = 0; i < p.Count; i++)
        {
            int bk = MinutesOf(p[i].Time) / 5;
            if (bk != curBucket && lastIdx >= 0)
                Flush(lastIdx);
            curBucket = bk;
            lastIdx = i;
        }
        if (lastIdx >= 0) Flush(lastIdx);

        JsonArray Bx()
        {
            var a = new JsonArray();
            foreach (string t in times) a.Add(t);
            return a;
        }
        JsonArray By(List<double> v)
        {
            var a = new JsonArray();
            foreach (double x in v) a.Add(x);
            return a;
        }

        var traceNonArb = new JsonObject
        {
            ["type"] = "bar", ["name"] = "비차익",
            ["x"] = Bx(), ["y"] = By(nonArb),
            ["marker"] = new JsonObject { ["color"] = NonArbColor },
            ["hovertemplate"] = "비차익 순매수 %{y:,.0f}<extra></extra>",
        };
        var traceArb = new JsonObject
        {
            ["type"] = "bar", ["name"] = "차익", ["yaxis"] = "y2",
            ["x"] = Bx(), ["y"] = By(arb),
            ["marker"] = new JsonObject { ["color"] = ArbColor },
            ["hovertemplate"] = "차익 순매수 %{y:,.0f}<extra></extra>",
        };

        string dayWord = string.Equals(meta.DayLabel, "yesterday", StringComparison.OrdinalIgnoreCase)
            ? "어제" : "오늘";
        string title = $"{marketLabel} 프로그램매매 순매수 (5분) — {meta.DateText} ({dayWord})";

        var layout = new JsonObject
        {
            ["title"] = ChartLayout.Title(title),
            ["font"] = ChartLayout.Font(),
            ["hovermode"] = "x unified",
            ["showlegend"] = true,
            ["legend"] = ChartLayout.Legend(),
            ["xaxis"] = new JsonObject
            {
                ["type"] = "date",
                ["tickformat"] = "%H:%M",
                ["hoverformat"] = "%H:%M",
                ["dtick"] = 3600000,
                ["tick0"] = $"{d}T09:00:00",
                ["tickangle"] = 0,
                ["showgrid"] = false,
                ["anchor"] = "y2",
            },
            ["yaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = $"비차익 순매수 {meta.MeasureLabel}" },
                ["domain"] = new JsonArray { 0.56, 1.0 },
                ["zeroline"] = true,
                ["automargin"] = true,
            },
            ["yaxis2"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = $"차익 순매수 {meta.MeasureLabel}" },
                ["domain"] = new JsonArray { 0.0, 0.44 },
                ["anchor"] = "x",
                ["zeroline"] = true,
                ["automargin"] = true,
            },
            ["margin"] = new JsonObject { ["l"] = 64, ["r"] = 40, ["t"] = 76, ["b"] = 48 },
            ["paper_bgcolor"] = "rgba(0,0,0,0)",
            ["plot_bgcolor"] = "rgba(0,0,0,0)",
        };

        JsonArray shapes = BuildAuctionShade(d, p);
        if (shapes.Count > 0) layout["shapes"] = shapes;

        return new JsonObject
        {
            ["data"] = new JsonArray { traceNonArb, traceArb },
            ["layout"] = layout,
        };
    }

    /// <summary>
    /// GrossFlow: per-5-minute gross buying vs selling in two stacked panels —
    /// 비차익 (top) and 차익 (bottom). Buy bars rise from zero, sell bars fall
    /// below it, so one-directional accumulation (both sides active, buy
    /// consistently dominant) is visually distinct from two-way churn.
    /// </summary>
    static JsonObject BuildGrossFlow(ProgramChartMeta meta, IReadOnlyList<ProgramFlowPoint> p)
    {
        string d = meta.DateText;
        string marketLabel = string.Equals(meta.Market, "kosdaq", StringComparison.OrdinalIgnoreCase)
            ? "KOSDAQ" : "KOSPI200";

        static string BucketFloor(string hhmm)
        {
            int m = (MinutesOf(hhmm) / 5) * 5;
            return $"{m / 60:00}:{m % 60:00}";
        }

        // Aggregate into 5-minute buckets: each bucket carries the gross buy /
        // sell that occurred within it (delta of the cumulative fields).
        var times = new List<string>();
        var nbBuy = new List<double>();
        var nbSell = new List<double>();
        var arbBuy = new List<double>();
        var arbSell = new List<double>();
        double pNbBuy = 0, pNbSell = 0, pArbBuy = 0, pArbSell = 0;
        int curBucket = -1, lastIdx = -1;

        void Flush(int idx)
        {
            ProgramFlowPoint e = p[idx];
            times.Add(Iso(d, BucketFloor(e.Time)));
            nbBuy.Add(e.NonArbitrageBuy - pNbBuy);
            nbSell.Add(-(e.NonArbitrageSell - pNbSell));   // negative → drawn downward
            arbBuy.Add(e.ArbitrageBuy - pArbBuy);
            arbSell.Add(-(e.ArbitrageSell - pArbSell));
            pNbBuy = e.NonArbitrageBuy;
            pNbSell = e.NonArbitrageSell;
            pArbBuy = e.ArbitrageBuy;
            pArbSell = e.ArbitrageSell;
        }

        for (int i = 0; i < p.Count; i++)
        {
            int bk = MinutesOf(p[i].Time) / 5;
            if (bk != curBucket && lastIdx >= 0)
                Flush(lastIdx);
            curBucket = bk;
            lastIdx = i;
        }
        if (lastIdx >= 0) Flush(lastIdx);

        JsonArray Bx()
        {
            var a = new JsonArray();
            foreach (string t in times) a.Add(t);
            return a;
        }
        JsonArray By(List<double> v)
        {
            var a = new JsonArray();
            foreach (double x in v) a.Add(x);
            return a;
        }

        const string PaleGold = "rgba(184,134,11,0.38)";
        const string PaleRed = "rgba(224,49,49,0.38)";

        var traceNbBuy = new JsonObject
        {
            ["type"] = "bar", ["name"] = "비차익 매수",
            ["x"] = Bx(), ["y"] = By(nbBuy),
            ["marker"] = new JsonObject { ["color"] = NonArbColor },
            ["hovertemplate"] = "비차익 매수 %{y:,.0f}<extra></extra>",
        };
        var traceNbSell = new JsonObject
        {
            ["type"] = "bar", ["name"] = "비차익 매도",
            ["x"] = Bx(), ["y"] = By(nbSell),
            ["marker"] = new JsonObject { ["color"] = PaleGold },
            ["hovertemplate"] = "비차익 매도 %{y:,.0f}<extra></extra>",
        };
        var traceArbBuy = new JsonObject
        {
            ["type"] = "bar", ["name"] = "차익 매수", ["yaxis"] = "y2",
            ["x"] = Bx(), ["y"] = By(arbBuy),
            ["marker"] = new JsonObject { ["color"] = ArbColor },
            ["hovertemplate"] = "차익 매수 %{y:,.0f}<extra></extra>",
        };
        var traceArbSell = new JsonObject
        {
            ["type"] = "bar", ["name"] = "차익 매도", ["yaxis"] = "y2",
            ["x"] = Bx(), ["y"] = By(arbSell),
            ["marker"] = new JsonObject { ["color"] = PaleRed },
            ["hovertemplate"] = "차익 매도 %{y:,.0f}<extra></extra>",
        };

        string dayWord = string.Equals(meta.DayLabel, "yesterday", StringComparison.OrdinalIgnoreCase)
            ? "어제" : "오늘";
        string title = $"{marketLabel} 프로그램매매 매수·매도 분해 — {meta.DateText} ({dayWord})";

        var layout = new JsonObject
        {
            ["title"] = ChartLayout.Title(title),
            ["font"] = ChartLayout.Font(),
            ["barmode"] = "relative",
            ["hovermode"] = "x unified",
            ["showlegend"] = true,
            ["legend"] = ChartLayout.Legend(),
            ["xaxis"] = new JsonObject
            {
                ["type"] = "date",
                ["tickformat"] = "%H:%M",
                ["hoverformat"] = "%H:%M",
                ["dtick"] = 3600000,
                ["tick0"] = $"{d}T09:00:00",
                ["tickangle"] = 0,
                ["showgrid"] = false,
                ["anchor"] = "y2",
            },
            ["yaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = $"비차익 {meta.MeasureLabel}" },
                ["domain"] = new JsonArray { 0.56, 1.0 },
                ["zeroline"] = true,
                ["automargin"] = true,
            },
            ["yaxis2"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = $"차익 {meta.MeasureLabel}" },
                ["domain"] = new JsonArray { 0.0, 0.44 },
                ["anchor"] = "x",
                ["zeroline"] = true,
                ["automargin"] = true,
            },
            ["margin"] = new JsonObject { ["l"] = 64, ["r"] = 40, ["t"] = 76, ["b"] = 48 },
            ["paper_bgcolor"] = "rgba(0,0,0,0)",
            ["plot_bgcolor"] = "rgba(0,0,0,0)",
        };

        JsonArray shapes = BuildAuctionShade(d, p);
        if (shapes.Count > 0) layout["shapes"] = shapes;

        return new JsonObject
        {
            ["data"] = new JsonArray { traceNbBuy, traceNbSell, traceArbBuy, traceArbSell },
            ["layout"] = layout,
        };
    }

    /// <summary>
    /// MarketDaily: per-day 비차익 / 차익 net buying as stacked bars against the
    /// index line — the market's multi-day program-trading history. Each point
    /// carries that day's value directly (no cumulative diff), and
    /// <see cref="ProgramFlowPoint.Time"/> holds the <c>yyyy-MM-dd</c> date.
    /// </summary>
    static JsonObject BuildMarketDaily(ProgramChartMeta meta, IReadOnlyList<ProgramFlowPoint> p)
    {
        string marketLabel = string.Equals(meta.Market, "kosdaq", StringComparison.OrdinalIgnoreCase)
            ? "KOSDAQ" : "KOSPI200";

        JsonArray X()
        {
            var a = new JsonArray();
            foreach (ProgramFlowPoint pt in p) a.Add(pt.Time);
            return a;
        }
        JsonArray Y(Func<ProgramFlowPoint, double> sel)
        {
            var a = new JsonArray();
            foreach (ProgramFlowPoint pt in p) a.Add(sel(pt));
            return a;
        }

        var traceNonArb = new JsonObject
        {
            ["type"] = "bar", ["name"] = "비차익",
            ["x"] = X(), ["y"] = Y(pt => pt.NonArbitrage),
            ["marker"] = new JsonObject { ["color"] = NonArbColor },
            ["hovertemplate"] = "비차익 %{y:,.0f}<extra></extra>",
        };
        var traceArb = new JsonObject
        {
            ["type"] = "bar", ["name"] = "차익",
            ["x"] = X(), ["y"] = Y(pt => pt.Arbitrage),
            ["marker"] = new JsonObject { ["color"] = ArbColor },
            ["hovertemplate"] = "차익 %{y:,.0f}<extra></extra>",
        };
        var traceIndex = new JsonObject
        {
            ["type"] = "scatter", ["mode"] = "lines", ["name"] = $"{marketLabel} 지수",
            ["x"] = X(), ["y"] = Y(pt => pt.Kospi200), ["yaxis"] = "y2",
            ["line"] = new JsonObject { ["color"] = K200Color, ["width"] = 1.8 },
            ["hovertemplate"] = $"{marketLabel} %{{y:.2f}}<extra></extra>",
        };

        // Category x-axis — discrete trading days only, so weekends and holidays
        // do not open gaps (the candlestick chart uses the same approach). Ticks
        // are an evenly-spaced subset of the date strings, labelled MM/dd.
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
            string date = p[idx].Time;   // yyyy-MM-dd
            tickvals.Add(date);
            ticktext.Add(date.Length >= 10 ? date[5..].Replace('-', '/') : date);
        }
        xaxis["tickmode"] = "array";
        xaxis["tickvals"] = tickvals;
        xaxis["ticktext"] = ticktext;

        var layout = new JsonObject
        {
            ["title"] = ChartLayout.Title($"{marketLabel} 일별 프로그램매매 — {meta.DateText}"),
            ["font"] = ChartLayout.Font(),
            ["barmode"] = "relative",
            ["bargap"] = 0.2,
            ["hovermode"] = "x unified",
            ["showlegend"] = true,
            ["legend"] = ChartLayout.Legend(),
            ["xaxis"] = xaxis,
            ["yaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = $"일별 순매수 {meta.MeasureLabel}" },
                ["zeroline"] = true,
                ["automargin"] = true,
            },
            ["yaxis2"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = $"{marketLabel} 지수" },
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
            ["data"] = new JsonArray { traceNonArb, traceArb, traceIndex },
            ["layout"] = layout,
        };
    }

    /// <summary>Shades the 15:20–15:30 closing single-price auction window.</summary>
    static JsonArray BuildAuctionShade(string dateText, IReadOnlyList<ProgramFlowPoint> p)
    {
        var shapes = new JsonArray();
        // Skip when the session never reached the closing auction.
        if (MinutesOf(p[^1].Time) < 15 * 60 + 20) return shapes;

        shapes.Add(new JsonObject
        {
            ["type"] = "rect",
            ["xref"] = "x",
            ["yref"] = "paper",
            ["x0"] = $"{dateText}T15:20:00",
            ["x1"] = $"{dateText}T15:30:00",
            ["y0"] = 0,
            ["y1"] = 1,
            ["fillcolor"] = AuctionShade,
            ["line"] = new JsonObject { ["width"] = 0 },
            ["layer"] = "below",
        });
        return shapes;
    }

    /// <summary>
    /// The cumulative-total trough annotation (y2). Arrowed coloured text, no
    /// background box — reads on light and dark surfaces alike. The EOD point is
    /// not annotated (the chart obviously ends at the close), and the
    /// closing-auction window is left to the grey shade to convey on its own.
    /// </summary>
    static JsonArray BuildAnnotations(string dateText, IReadOnlyList<ProgramFlowPoint> p)
    {
        var annotations = new JsonArray();

        int trough = 0;
        for (int i = 1; i < p.Count; i++)
            if (p[i].Net < p[trough].Net) trough = i;
        annotations.Add(Annotation(
            Iso(dateText, p[trough].Time), p[trough].Net,
            $"전체 저점 {p[trough].Net:N0}", TotalColor, ay: 30));

        return annotations;
    }

    /// <summary>One arrowed annotation anchored on the cumulative-total axis (y2).</summary>
    static JsonObject Annotation(string x, double y, string text, string color, int ay) =>
        new()
        {
            ["x"] = x,
            ["y"] = y,
            ["xref"] = "x",
            ["yref"] = "y2",
            ["text"] = text,
            ["showarrow"] = true,
            ["arrowhead"] = 3,
            ["arrowsize"] = 1,
            ["arrowwidth"] = 1,
            ["arrowcolor"] = color,
            ["ax"] = 0,
            ["ay"] = ay,
            ["font"] = new JsonObject { ["size"] = ChartLayout.AnnotationFontSize, ["color"] = color },
        };

    /// <summary>Composes an ISO datetime (<c>yyyy-MM-ddTHH:mm:00</c>) for the time axis.</summary>
    static string Iso(string dateText, string hhmm) => $"{dateText}T{hhmm}:00";

    /// <summary>Parses an <c>HH:mm</c> time string into minutes-of-day.</summary>
    static int MinutesOf(string hhmm)
    {
        if (hhmm.Length >= 5
            && int.TryParse(hhmm.AsSpan(0, 2), out int h)
            && int.TryParse(hhmm.AsSpan(3, 2), out int m))
            return h * 60 + m;
        return 0;
    }
}
