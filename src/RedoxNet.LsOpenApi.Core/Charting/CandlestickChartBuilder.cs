using System.Globalization;
using System.Text.Json.Nodes;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;

namespace RedoxNet.LsOpenApi.Core.Charting;

/// <summary>
/// Builds a Plotly v5 JSON spec for a candlestick + volume chart, optionally
/// overlaying moving-average and Bollinger-Band series on the price subplot.
/// </summary>
/// <remarks>
/// Pure JSON-shape builder using <see cref="System.Text.Json.Nodes"/>. No
/// charting library dependency. The output object is suitable for embedding
/// under a <c>chart.spec</c> field in tool responses; clients render it with
/// Plotly.js (CDN or bundled) without any server-side execution.
///
/// Korean broker color convention is applied: rising = red (<c>#E74C3C</c>),
/// falling = blue (<c>#3498DB</c>).
///
/// Indicator handling:
/// <list type="bullet">
///   <item><description><c>ma:N</c>, <c>ema:N</c>, <c>bb:N,SD</c> → price-subplot overlays.</description></item>
///   <item><description><c>rsi</c>, <c>macd</c> → not rendered (would need separate subplots; out of v1.0 scope).</description></item>
/// </list>
/// </remarks>
internal static class CandlestickChartBuilder
{
    /// <summary>Color for rising candles / volume bars (Korean convention).</summary>
    public const string ColorRising = "#E74C3C";

    /// <summary>Color for falling candles / volume bars (Korean convention).</summary>
    public const string ColorFalling = "#3498DB";

    /// <summary>Chart type discriminator emitted under the top-level <c>chart</c> field.</summary>
    public const string ChartType = "plotly";

    /// <summary>Plotly major version this builder targets.</summary>
    public const string PlotlyVersion = "5";

    /// <summary>
    /// Round-robin colour palette for moving-average and EMA overlays, in the
    /// order they are enrolled in <see cref="IndicatorSpec"/>. Ordered to match
    /// the Korean retail convention (Naver Finance: 5 green / 20 red / 60 orange
    /// / 120 purple), which is what users see when MAs are passed shortest-first.
    /// </summary>
    static readonly string[] MaPalette =
    {
        "#2F9E44", // green  — MA5
        "#E03131", // red    — MA20
        "#F08C00", // orange — MA60
        "#9C36B5", // purple — MA120
        "#1098AD", // cyan   — extras
        "#495057", // gray
    };

    /// <summary>
    /// Builds the <c>chart</c> envelope ready to embed under a tool response.
    /// </summary>
    /// <param name="shcode">Stock code, used in the chart title.</param>
    /// <param name="periodType">Period type (e.g. <c>"day"</c>), used in the chart title.</param>
    /// <param name="candles">Candle list (oldest first).</param>
    /// <param name="indicators">Indicator series keyed by spec, aligned 1:1 with <paramref name="candles"/>.</param>
    /// <param name="specs">Parsed indicator specs (used to discover overlay kinds).</param>
    /// <param name="name">
    /// Optional human-readable stock name. When supplied the title reads
    /// <c>"{name} ({shcode}) — {label}"</c>; otherwise just <c>"{shcode} — {label}"</c>.
    /// The chart TRs do not carry the name, so the caller passes it through.
    /// </param>
    /// <param name="currency">
    /// Optional ISO-ish currency code (<c>"USD"</c>, <c>"JPY"</c>, <c>"HKD"</c>, …)
    /// for the price y-axis unit. <see langword="null"/> or <c>"KRW"</c>
    /// keeps the default Korean-market label (<c>주가 (원)</c>); other codes
    /// substitute a matching symbol (<c>$</c>, <c>¥</c>, …) or fall through
    /// to the raw code so overseas tools can be wired in without a code change.
    /// </param>
    /// <param name="themeHint">
    /// Optional tool-side theme override (<c>"light"</c>, <c>"dark"</c>, or
    /// <see langword="null"/>/<c>"auto"</c>). When non-null and not auto, the
    /// value is embedded in <c>layout._themeHint</c> so
    /// <c>PlotlyTemplate.applyTheme()</c> overrides <c>hostContext.theme</c> and
    /// the iframe <c>prefers-color-scheme</c>. Lets the user pin a chart palette
    /// regardless of host signal — see docs/MCP-APPS-INTEROP.md §3 Q8.
    /// </param>
    /// <returns>
    /// <c>{ "type": "plotly", "version": "5", "spec": { "data": [...], "layout": {...} } }</c>
    /// </returns>
    public static JsonObject Build(
        string shcode,
        string periodType,
        IReadOnlyList<Candle> candles,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicators,
        IReadOnlyList<IndicatorSpec> specs,
        string? name = null,
        string? currency = null,
        string? themeHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shcode);
        ArgumentException.ThrowIfNullOrWhiteSpace(periodType);
        ArgumentNullException.ThrowIfNull(candles);
        ArgumentNullException.ThrowIfNull(indicators);
        ArgumentNullException.ThrowIfNull(specs);

        var data = new JsonArray();
        JsonArray xAxis = BuildXAxis(candles, periodType);

        data.Add(BuildCandlestick(candles, xAxis));
        AddOverlays(data, xAxis, indicators, specs);
        data.Add(BuildVolume(candles, xAxis));

        JsonObject layout = BuildLayout(shcode, name, periodType, candles, xAxis, currency);
        string? normalized = NormalizeThemeHint(themeHint);
        if (normalized is not null)
            layout["_themeHint"] = normalized;

        return new JsonObject
        {
            ["type"] = ChartType,
            ["version"] = PlotlyVersion,
            ["spec"] = new JsonObject
            {
                ["data"] = data,
                ["layout"] = layout,
            },
        };
    }

    /// <summary>
    /// Accepts <c>light</c>/<c>dark</c>/<c>auto</c>/<see langword="null"/>
    /// (case-insensitive). Returns the lowercased value for the two explicit
    /// modes, or <see langword="null"/> when the hint should be omitted (auto
    /// or unrecognized — iframe falls through to hostContext.theme).
    /// </summary>
    public static string? NormalizeThemeHint(string? themeHint)
    {
        if (string.IsNullOrWhiteSpace(themeHint))
            return null;
        string lower = themeHint.Trim().ToLowerInvariant();
        return lower switch
        {
            "light" => "light",
            "dark" => "dark",
            _ => null, // "auto" or anything else → no hint, host-driven
        };
    }

    /// <summary>
    /// Maps a currency code to the unit symbol shown on the price y-axis.
    /// Codes that have a customary one-character symbol get it (<c>USD → $</c>,
    /// <c>JPY → ¥</c>, …); anything else falls through to the raw code so a
    /// caller passing an unfamiliar currency still sees a recognizable label.
    /// </summary>
    /// <param name="currency">ISO-ish currency code, or <see langword="null"/>.</param>
    /// <returns>The display symbol or code, or <c>"원"</c> for KRW / null.</returns>
    internal static string PriceUnitLabel(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return "원";

        return currency.Trim().ToUpperInvariant() switch
        {
            "KRW" => "원",
            "USD" => "$",
            "JPY" or "CNY" => "¥",
            "EUR" => "€",
            "GBP" => "£",
            "HKD" => "HK$",
            _ => currency.Trim().ToUpperInvariant(),
        };
    }

    /// <summary>
    /// Builds the categorical x-axis values used by every trace.
    /// Intraday periods get a full timestamp; daily and above use the date only.
    /// </summary>
    /// <param name="candles">Candle list.</param>
    /// <param name="periodType">Period label.</param>
    /// <returns>JSON array of date/time strings, one per candle.</returns>
    static JsonArray BuildXAxis(IReadOnlyList<Candle> candles, string periodType)
    {
        string format = periodType is "min" or "tick"
            ? "yyyy-MM-dd HH:mm:ss"
            : "yyyy-MM-dd";

        var array = new JsonArray();
        foreach (Candle c in candles)
            array.Add(c.Date.ToString(format, CultureInfo.InvariantCulture));
        return array;
    }

    /// <summary>
    /// Builds the OHLC candlestick trace with the Korean rising-red / falling-blue
    /// fill convention.
    /// </summary>
    /// <param name="candles">Candle list.</param>
    /// <param name="xAxis">Shared x-axis array.</param>
    /// <returns>Plotly trace JSON object.</returns>
    static JsonObject BuildCandlestick(IReadOnlyList<Candle> candles, JsonArray xAxis)
    {
        var open = new JsonArray();
        var high = new JsonArray();
        var low = new JsonArray();
        var close = new JsonArray();
        foreach (Candle c in candles)
        {
            open.Add(c.Open);
            high.Add(c.High);
            low.Add(c.Low);
            close.Add(c.Close);
        }

        return new JsonObject
        {
            ["type"] = "candlestick",
            ["name"] = "OHLC",
            ["x"] = CloneArray(xAxis),
            ["open"] = open,
            ["high"] = high,
            ["low"] = low,
            ["close"] = close,
            ["increasing"] = new JsonObject
            {
                ["line"] = new JsonObject { ["color"] = ColorRising },
                ["fillcolor"] = ColorRising,
            },
            ["decreasing"] = new JsonObject
            {
                ["line"] = new JsonObject { ["color"] = ColorFalling },
                ["fillcolor"] = ColorFalling,
            },
            ["yaxis"] = "y",
        };
    }

    /// <summary>
    /// Appends moving-average / Bollinger overlays to the trace list. RSI and
    /// MACD specs are skipped because they would require their own y-axis subplot.
    /// </summary>
    /// <param name="data">Trace list to append into.</param>
    /// <param name="xAxis">Shared x-axis array.</param>
    /// <param name="indicators">Indicator series keyed by spec.</param>
    /// <param name="specs">Parsed specs in user order; drives the palette cycle.</param>
    static void AddOverlays(
        JsonArray data,
        JsonArray xAxis,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicators,
        IReadOnlyList<IndicatorSpec> specs)
    {
        int paletteIndex = 0;

        foreach (IndicatorSpec spec in specs)
        {
            switch (spec.Kind)
            {
                case "ma":
                case "ema":
                    if (indicators.TryGetValue(spec.Raw, out IReadOnlyList<double?>? series))
                    {
                        string color = MaPalette[paletteIndex % MaPalette.Length];
                        paletteIndex++;
                        data.Add(BuildLineTrace(spec.Raw.ToUpperInvariant(), xAxis, series, color, dash: null));
                    }
                    break;

                case "bb":
                {
                    string lowerKey = spec.Raw + ".lower";
                    string middleKey = spec.Raw + ".middle";
                    string upperKey = spec.Raw + ".upper";
                    const string bbColor = "#7F8C8D"; // slate

                    if (indicators.TryGetValue(upperKey, out IReadOnlyList<double?>? upper))
                        data.Add(BuildLineTrace($"BB upper ({spec.Args[0]:0}, {spec.Args[1]})", xAxis, upper, bbColor, dash: "dot"));
                    if (indicators.TryGetValue(middleKey, out IReadOnlyList<double?>? middle))
                        data.Add(BuildLineTrace($"BB middle ({spec.Args[0]:0})", xAxis, middle, bbColor, dash: "dash"));
                    if (indicators.TryGetValue(lowerKey, out IReadOnlyList<double?>? lower))
                        data.Add(BuildLineTrace($"BB lower ({spec.Args[0]:0}, {spec.Args[1]})", xAxis, lower, bbColor, dash: "dot"));
                    break;
                }

                // rsi / macd intentionally skipped — they need their own subplot scale.
            }
        }
    }

    /// <summary>
    /// Builds one line trace from an indicator series. Null values become JSON
    /// nulls so Plotly draws gaps rather than interpolating across warm-up
    /// periods.
    /// </summary>
    /// <param name="name">Legend label.</param>
    /// <param name="xAxis">Shared x-axis array.</param>
    /// <param name="series">Indicator values, aligned with <paramref name="xAxis"/>.</param>
    /// <param name="color">Hex line color.</param>
    /// <param name="dash">Optional Plotly dash style (e.g. <c>"dot"</c>); pass <see langword="null"/> for solid.</param>
    /// <returns>Plotly trace JSON object.</returns>
    static JsonObject BuildLineTrace(string name, JsonArray xAxis, IReadOnlyList<double?> series, string color, string? dash)
    {
        var y = new JsonArray();
        foreach (double? v in series)
        {
            if (v.HasValue)
                y.Add(v.Value);
            else
                y.Add(null);
        }

        var line = new JsonObject { ["color"] = color, ["width"] = 1.2 };
        if (!string.IsNullOrEmpty(dash))
            line["dash"] = dash;

        return new JsonObject
        {
            ["type"] = "scatter",
            ["mode"] = "lines",
            ["name"] = name,
            ["x"] = CloneArray(xAxis),
            ["y"] = y,
            ["line"] = line,
            ["yaxis"] = "y",
            ["connectgaps"] = false,
        };
    }

    /// <summary>
    /// Builds the volume bar trace on the secondary y-axis. Bar colors mirror
    /// the candle direction (red if close ≥ open, blue otherwise).
    /// </summary>
    /// <param name="candles">Candle list.</param>
    /// <param name="xAxis">Shared x-axis array.</param>
    /// <returns>Plotly bar trace JSON object.</returns>
    static JsonObject BuildVolume(IReadOnlyList<Candle> candles, JsonArray xAxis)
    {
        var y = new JsonArray();
        var colors = new JsonArray();
        foreach (Candle c in candles)
        {
            y.Add(c.Volume);
            colors.Add(c.Close >= c.Open ? ColorRising : ColorFalling);
        }

        return new JsonObject
        {
            ["type"] = "bar",
            ["name"] = "Volume",
            ["x"] = CloneArray(xAxis),
            ["y"] = y,
            ["marker"] = new JsonObject { ["color"] = colors },
            ["yaxis"] = "y2",
            ["showlegend"] = false,
        };
    }

    /// <summary>
    /// Builds the Plotly <c>layout</c> object: title, axes (with price on top,
    /// volume on bottom), legend, hover and margin settings, an evenly-spaced
    /// date-tick x-axis, and period high/low annotations.
    /// </summary>
    /// <param name="shcode">Stock code for the title.</param>
    /// <param name="name">Optional human-readable stock name for the title.</param>
    /// <param name="periodType">Period label; mapped to Korean (일/주/월/분/틱) for display.</param>
    /// <param name="candles">Candle list (oldest first) — drives ticks and annotations.</param>
    /// <param name="xAxis">Shared x-axis array, aligned 1:1 with <paramref name="candles"/>.</param>
    /// <param name="currency">Currency code for the price y-axis unit; <see langword="null"/> keeps the default <c>원</c>.</param>
    /// <returns>Plotly layout JSON object.</returns>
    static JsonObject BuildLayout(
        string shcode,
        string? name,
        string periodType,
        IReadOnlyList<Candle> candles,
        JsonArray xAxis,
        string? currency)
    {
        string periodLabel = periodType switch
        {
            "day" => "일봉",
            "week" => "주봉",
            "month" => "월봉",
            "min" => "분봉",
            "tick" => "틱",
            _ => periodType,
        };

        // Title prefers the human-readable name when the caller supplies it
        // ("삼성전자 (005930) — 일봉"); the TRs themselves carry only the code.
        string title = string.IsNullOrWhiteSpace(name)
            ? $"{shcode} — {periodLabel}"
            : $"{name} ({shcode}) — {periodLabel}";

        var layout = new JsonObject
        {
            ["title"] = ChartLayout.Title(title),
            ["font"] = ChartLayout.Font(),
            ["hovermode"] = "x unified",
            ["showlegend"] = true,
            ["legend"] = ChartLayout.Legend(),
            ["xaxis"] = BuildXAxisLayout(candles, xAxis, periodType),
            ["yaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = $"주가 ({PriceUnitLabel(currency)})" },
                ["domain"] = new JsonArray { 0.3, 1.0 },
                ["side"] = "right",
            },
            ["yaxis2"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = "거래량 (주)" },
                ["domain"] = new JsonArray { 0.0, 0.25 },
                ["side"] = "right",
            },
            ["margin"] = new JsonObject
            {
                ["l"] = 40, ["r"] = 60, ["t"] = 76, ["b"] = 40,
            },
            // Transparent backgrounds let the host card show through, so dark
            // hosts that don't propagate hostContext.theme into the iframe
            // (observed 2026-05-27 in VS Code Copilot Chat) still render a
            // chart that visually integrates with the surrounding panel.
            // PlotlyTemplate.applyTheme() still drives axis/grid/font colors
            // off hostContext.theme || prefers-color-scheme.
            ["paper_bgcolor"] = "rgba(0,0,0,0)",
            ["plot_bgcolor"] = "rgba(0,0,0,0)",
        };

        JsonArray annotations = BuildExtremaAnnotations(candles, xAxis, periodType);
        if (annotations.Count > 0)
            layout["annotations"] = annotations;

        return layout;
    }

    /// <summary>
    /// Builds the x-axis layout: a categorical axis (every candle evenly
    /// spaced, no weekend/holiday gaps) whose ticks are an evenly-spaced
    /// subset of the raw x values — Naver-style. The trace <c>x</c> arrays keep
    /// the verbatim ISO strings; <c>tickvals</c> holds the chosen subset (which
    /// must match those strings exactly so the category axis can place them)
    /// and <c>ticktext</c> holds the short display label (e.g. <c>"05/14"</c>).
    /// </summary>
    /// <param name="candles">Candle list, aligned 1:1 with <paramref name="xAxis"/>.</param>
    /// <param name="xAxis">Raw x-axis string values.</param>
    /// <param name="periodType">Period label; selects the tick label format.</param>
    /// <returns>Plotly x-axis layout JSON object.</returns>
    static JsonObject BuildXAxisLayout(
        IReadOnlyList<Candle> candles,
        JsonArray xAxis,
        string periodType)
    {
        var axis = new JsonObject
        {
            ["type"] = "category",
            ["rangeslider"] = new JsonObject { ["visible"] = false },
            ["showspikes"] = true,
            ["tickangle"] = 0,
        };

        if (candles.Count == 0)
            return axis;

        // ~8 ticks, evenly spaced over the candle index range. Rounding can
        // collide on a short series, so de-dup consecutive picks.
        const int DesiredTicks = 8;
        string fmt = TickFormat(periodType);
        int desired = Math.Min(candles.Count, DesiredTicks);
        var tickvals = new JsonArray();
        var ticktext = new JsonArray();
        int lastIdx = -1;
        for (int t = 0; t < desired; t++)
        {
            int idx = desired == 1
                ? candles.Count - 1
                : (int)Math.Round((double)t * (candles.Count - 1) / (desired - 1));
            if (idx == lastIdx)
                continue;
            lastIdx = idx;
            tickvals.Add(xAxis[idx]!.GetValue<string>());
            ticktext.Add(candles[idx].Date.ToString(fmt, CultureInfo.InvariantCulture));
        }

        axis["tickmode"] = "array";
        axis["tickvals"] = tickvals;
        axis["ticktext"] = ticktext;
        return axis;
    }

    /// <summary>
    /// Builds the period-high and period-low annotations (Naver-style: red
    /// "최고" above the highest candle, blue "최저" below the lowest), anchored
    /// on the price axis. Returns an empty array when there are no candles.
    /// </summary>
    /// <param name="candles">Candle list.</param>
    /// <param name="xAxis">Raw x-axis strings, aligned 1:1 with <paramref name="candles"/>.</param>
    /// <param name="periodType">Period label; selects the date format in the label text.</param>
    /// <returns>Plotly <c>annotations</c> array (0 or 2 entries).</returns>
    static JsonArray BuildExtremaAnnotations(
        IReadOnlyList<Candle> candles,
        JsonArray xAxis,
        string periodType)
    {
        var annotations = new JsonArray();
        if (candles.Count == 0)
            return annotations;

        int hiIdx = 0, loIdx = 0;
        for (int i = 1; i < candles.Count; i++)
        {
            if (candles[i].High > candles[hiIdx].High) hiIdx = i;
            if (candles[i].Low < candles[loIdx].Low) loIdx = i;
        }

        string fmt = TickFormat(periodType);
        Candle hi = candles[hiIdx];
        Candle lo = candles[loIdx];

        annotations.Add(ExtremumAnnotation(
            xAxis[hiIdx]!.GetValue<string>(), hi.High,
            $"최고 {hi.High.ToString("N0", CultureInfo.InvariantCulture)} " +
            $"({hi.Date.ToString(fmt, CultureInfo.InvariantCulture)})",
            ColorRising, ay: -26));
        annotations.Add(ExtremumAnnotation(
            xAxis[loIdx]!.GetValue<string>(), lo.Low,
            $"최저 {lo.Low.ToString("N0", CultureInfo.InvariantCulture)} " +
            $"({lo.Date.ToString(fmt, CultureInfo.InvariantCulture)})",
            ColorFalling, ay: 26));
        return annotations;
    }

    /// <summary>
    /// Builds one price-axis annotation with a short arrow to the candle.
    /// No background box — just coloured text + arrow, so it reads on light
    /// and dark surfaces alike.
    /// </summary>
    /// <param name="x">Category x value (must match a trace x entry).</param>
    /// <param name="y">Price the arrow points at.</param>
    /// <param name="text">Annotation label.</param>
    /// <param name="color">Text + arrow colour.</param>
    /// <param name="ay">Vertical text offset in pixels (negative = above the point).</param>
    /// <returns>Plotly annotation JSON object.</returns>
    static JsonObject ExtremumAnnotation(string x, decimal y, string text, string color, int ay) =>
        new()
        {
            ["x"] = x,
            ["y"] = y,
            ["xref"] = "x",
            ["yref"] = "y",
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

    /// <summary>
    /// .NET date format for tick labels and annotation dates, by period type:
    /// intraday → time, month → <c>yy/MM</c>, year → <c>yyyy</c>, day/week → <c>MM/dd</c>.
    /// </summary>
    /// <param name="periodType">Period label.</param>
    /// <returns>A <see cref="DateTime.ToString(string, IFormatProvider)"/> format string.</returns>
    static string TickFormat(string periodType) => periodType switch
    {
        "min" => "HH:mm",
        "tick" => "HH:mm:ss",
        "month" => "yy/MM",
        "year" => "yyyy",
        _ => "MM/dd", // day, week
    };

    /// <summary>
    /// Returns a deep-enough copy of an x-axis array so each trace can own its
    /// own array. <see cref="JsonNode"/>s can only have a single parent, so
    /// re-using the same array across traces would throw.
    /// </summary>
    /// <param name="source">Original x-axis (string values).</param>
    /// <returns>Independent <see cref="JsonArray"/> with the same string values.</returns>
    static JsonArray CloneArray(JsonArray source)
    {
        // JsonNodes can only have a single parent. Each trace's `x` must own
        // a fresh array. The members are primitives (strings here), so a
        // shallow value clone is sufficient.
        var clone = new JsonArray();
        foreach (JsonNode? node in source)
        {
            if (node is null)
                clone.Add(null);
            else
                clone.Add(JsonValue.Create(node.GetValue<string>()));
        }
        return clone;
    }
}
