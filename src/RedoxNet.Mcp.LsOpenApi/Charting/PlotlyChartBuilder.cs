using System.Globalization;
using System.Text.Json.Nodes;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;

namespace RedoxNet.Mcp.LsOpenApi.Charting;

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
internal static class PlotlyChartBuilder
{
    /// <summary>Color for rising candles / volume bars (Korean convention).</summary>
    public const string ColorRising = "#E74C3C";

    /// <summary>Color for falling candles / volume bars (Korean convention).</summary>
    public const string ColorFalling = "#3498DB";

    /// <summary>Chart type discriminator emitted under the top-level <c>chart</c> field.</summary>
    public const string ChartType = "plotly";

    /// <summary>Plotly major version this builder targets.</summary>
    public const string PlotlyVersion = "5";

    static readonly string[] MaPalette =
    {
        "#F39C12", // orange
        "#27AE60", // green
        "#8E44AD", // purple
        "#16A085", // teal
        "#2C3E50", // navy
        "#D35400", // dark orange
    };

    /// <summary>
    /// Builds the <c>chart</c> envelope ready to embed under a tool response.
    /// </summary>
    /// <param name="shcode">Stock code, used in the chart title.</param>
    /// <param name="periodType">Period type (e.g. <c>"day"</c>), used in the chart title.</param>
    /// <param name="candles">Candle list (oldest first).</param>
    /// <param name="indicators">Indicator series keyed by spec, aligned 1:1 with <paramref name="candles"/>.</param>
    /// <param name="specs">Parsed indicator specs (used to discover overlay kinds).</param>
    /// <returns>
    /// <c>{ "type": "plotly", "version": "5", "spec": { "data": [...], "layout": {...} } }</c>
    /// </returns>
    public static JsonObject Build(
        string shcode,
        string periodType,
        IReadOnlyList<Candle> candles,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicators,
        IReadOnlyList<IndicatorSpec> specs)
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

        return new JsonObject
        {
            ["type"] = ChartType,
            ["version"] = PlotlyVersion,
            ["spec"] = new JsonObject
            {
                ["data"] = data,
                ["layout"] = BuildLayout(shcode, periodType),
            },
        };
    }

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

    static JsonObject BuildLayout(string shcode, string periodType)
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

        return new JsonObject
        {
            ["title"] = new JsonObject { ["text"] = $"{shcode} — {periodLabel}" },
            ["hovermode"] = "x unified",
            ["showlegend"] = true,
            ["legend"] = new JsonObject
            {
                ["orientation"] = "h",
                ["x"] = 0,
                ["y"] = 1.05,
            },
            ["xaxis"] = new JsonObject
            {
                ["type"] = "category",
                ["rangeslider"] = new JsonObject { ["visible"] = false },
                ["showspikes"] = true,
            },
            ["yaxis"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = "Price" },
                ["domain"] = new JsonArray { 0.3, 1.0 },
                ["side"] = "right",
            },
            ["yaxis2"] = new JsonObject
            {
                ["title"] = new JsonObject { ["text"] = "Volume" },
                ["domain"] = new JsonArray { 0.0, 0.25 },
                ["side"] = "right",
            },
            ["margin"] = new JsonObject
            {
                ["l"] = 40, ["r"] = 60, ["t"] = 40, ["b"] = 40,
            },
        };
    }

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
