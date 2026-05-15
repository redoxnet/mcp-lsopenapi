using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;
using RedoxNet.LsOpenApi.Core.Charting;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns OHLCV candles with optional indicator overlays and
/// pre-computed analysis context. Supports multi-timeframe queries.
/// </summary>
/// <remarks>
/// Dispatches by <c>period_type</c>:
/// <list type="bullet">
///   <item><description><c>day</c> / <c>week</c> / <c>month</c> → TR <c>t8410</c>.</description></item>
///   <item><description><c>min</c> → TR <c>t8412</c> with <c>ncnt</c> = <c>minute_unit</c>.</description></item>
///   <item><description><c>tick</c> → TR <c>t1301</c>.</description></item>
/// </list>
/// Pass multiple period types as a comma-separated string (e.g.
/// <c>"day,week,month"</c>) to receive one frame per timeframe in the same
/// response. The per-TR rate limiter sequences calls automatically.
/// </remarks>
[McpServerToolType]
public static class GetChartTool
{
    static readonly IndicatorService Indicators = new();

    static readonly HashSet<string> KnownPeriodTypes =
        new(StringComparer.OrdinalIgnoreCase) { "day", "week", "month", "year", "min", "tick" };

    /// <summary>
    /// Upper bound on candles fetched in one TR call (display window +
    /// indicator warm-up). LS's chart TRs cap a single page at ~500 rows, so
    /// asking for more is silently truncated. When <c>count + warmup</c> exceeds
    /// this, the warm-up lead is squeezed first; long-period indicators then
    /// keep some leading nulls at very high <c>count</c> values.
    /// </summary>
    const int MaxFetchCount = 500;

    /// <summary>
    /// Returns the chart(s) with optional indicators and analysis context.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="shcode">6-digit Korean stock code.</param>
    /// <param name="period_type">One of 'day', 'week', 'month', 'min', 'tick' — or a comma-separated list (e.g. 'day,week,month').</param>
    /// <param name="count">Number of candles per timeframe, default 60, max 500.</param>
    /// <param name="from">Optional start date 'yyyyMMdd'.</param>
    /// <param name="to">Optional end date 'yyyyMMdd'.</param>
    /// <param name="minute_unit">For period_type='min': minute interval (1/3/5/10/15/30/60).</param>
    /// <param name="indicators">Optional indicator specs (e.g. ['ma:5','rsi:14','macd:12,26,9','bb:20,2']).</param>
    /// <param name="include_chart">If true (and period_type is a single timeframe), the tool ships a Plotly v5 spec as structuredContent so MCP Apps (SEP-1865) hosts render the chart inline. Default false.</param>
    /// <param name="summary_only">If true, return only the last 5 candles and the last value of each indicator series; the <c>context</c> block is preserved. Default false.</param>
    /// <param name="name">Optional human-readable stock name (e.g. '삼성전자'); used only for the inline chart title. The chart TRs do not carry the name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Text content with candles/indicators/context plus an optional <c>structuredContent.chart</c> for inline rendering.</returns>
    [McpServerTool(Name = "ls_get_chart")]
    [Description("""
        Returns OHLCV candles for a Korean stock with optional technical indicators and pre-computed analysis context (divergence from MAs, volume averages, drawdown from period high, MA trend, bullish alignment).

        USE WHEN: the user asks for a chart, 차트, 일봉/주봉/월봉/분봉/틱, OHLCV, technical analysis, or multi-timeframe analysis (e.g. "삼성전자 일봉과 주봉, 월봉 같이 보여줘").
        AVOID WHEN: the user wants the current snapshot only — use ls_get_quote instead.

        Single timeframe: period_type='day' → response has top-level candles/indicators/context.
        Multi timeframe: period_type='day,week,month' → response has a frames[] array, one entry per timeframe, each with its own context.

        Set include_chart=true (single timeframe only) for inline chart rendering on MCP Apps hosts — Claude Desktop, Claude.ai, ChatGPT, Goose, VS Code. The Plotly v5 spec ships as structuredContent (not in the model's text context — zero token cost) and the host's iframe renders it via the ui://lsopenapi/plotly template. Multi-timeframe with include_chart is a no-op for inline rendering (call once per period_type for charts); the structured candles/indicators/context payload is unaffected.

        Set summary_only=true to shrink the payload: only the last 5 candles plus the last value of each indicator series are returned, but the full pre-computed 'context' block is kept. Use this for the screening/triage pass over many stocks or timeframes, then re-call with summary_only=false on the single frame you want to deep-dive.

        Indicator examples: 'ma:5', 'ma:20', 'ema:12', 'rsi:14', 'macd:12,26,9', 'bb:20,2'.
        """)]
    public static async Task<CallToolResult> GetChart(
        LsApiClient apiClient,
        [Description("6-digit Korean short code, e.g. '005930'.")]
        string shcode,
        [Description("Period type: 'day', 'week', 'month', 'year', 'min', or 'tick'. Multiple via comma-separated string: 'day,week,month'.")]
        string period_type,
        [Description("Number of candles per timeframe (1–500). Default 60.")]
        int count = 60,
        [Description("Optional start date in 'yyyyMMdd' format.")]
        string? from = null,
        [Description("Optional end date in 'yyyyMMdd' format.")]
        string? to = null,
        [Description("For period_type='min': minute interval (1, 3, 5, 10, 15, 30, 60). Default 5.")]
        int minute_unit = 5,
        [Description("Optional indicator specs, e.g. ['ma:5','ma:20','rsi:14','macd:12,26,9','bb:20,2'].")]
        string[]? indicators = null,
        [Description("If true (single timeframe only), ship a Plotly v5 spec as structuredContent so MCP Apps hosts render an inline chart. Default false.")]
        bool include_chart = false,
        [Description("If true, keep only the last 5 candles and the last value of each indicator series (context is kept intact). Use for the screening pass when scanning many stocks/timeframes. Default false.")]
        bool summary_only = false,
        [Description("Optional human-readable stock name (e.g. '삼성전자'). Used only for the inline chart title — pass it when you already know the name so the chart reads '삼성전자 (005930) — 일봉' instead of just the code. The TR responses do not carry the name.")]
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.ErrorResult("shcode is required.");
        if (string.IsNullOrWhiteSpace(period_type))
            return McpJson.ErrorResult("period_type is required.");

        int cappedCount = Math.Clamp(count, 1, 500);

        List<string> periods = period_type
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (periods.Count == 0)
            return McpJson.ErrorResult("period_type is required.");

        foreach (string p in periods)
        {
            if (!KnownPeriodTypes.Contains(p))
                return McpJson.ErrorResult($"Unknown period_type '{p}'. Use day, week, month, min, or tick.");
        }

        List<IndicatorSpec> parsedIndicators = new();
        if (indicators is { Length: > 0 })
        {
            foreach (string raw in indicators)
            {
                if (!IndicatorSpecParser.TryParse(raw, out IndicatorSpec? spec, out string? error))
                    return McpJson.ErrorResult($"Invalid indicator spec '{raw}'.", new { reason = error });
                parsedIndicators.Add(spec!);
            }
        }

        try
        {
            var frames = new List<FrameResult>();
            foreach (string p in periods)
            {
                FrameResult frame = await BuildFrameAsync(
                    apiClient, shcode, p, cappedCount, from, to, minute_unit, parsedIndicators, cancellationToken);
                frames.Add(frame);
            }

            if (periods.Count == 1)
            {
                FrameResult only = frames[0];
                IReadOnlyList<Candle> candleView = SummarizeCandles(only.Candles, summary_only);
                var single = new
                {
                    shcode,
                    period_type = only.PeriodType,
                    tr_cd = only.TrCode,
                    count = only.Candles.Count,
                    summary_only,
                    candles = SerializeCandles(candleView, only.PeriodType),
                    indicators = summary_only
                        ? (object)SummarizeIndicators(only.Indicators)
                        : only.Indicators.ToDictionary(kv => kv.Key, kv => kv.Value),
                    context = only.Context,
                    chart_available = include_chart,
                };
                string singleText = JsonSerializer.Serialize(single, McpJson.Tool);

                JsonObject? structured = null;
                if (include_chart)
                {
                    structured = new JsonObject
                    {
                        ["chart"] = PlotlyChartBuilder.Build(
                            shcode, only.PeriodType, only.Candles, only.Indicators, parsedIndicators, name),
                    };
                }
                return McpJson.OkResult(singleText, structured);
            }

            // Multi-timeframe: structuredContent stays null — the iframe template
            // renders one chart per tool result. Users wanting inline charts call
            // once per period_type. The structured candles/indicators/context
            // payload below covers programmatic / LLM use either way.
            var multi = new
            {
                shcode,
                period_types = periods,
                summary_only,
                frames = frames.Select(f =>
                {
                    IReadOnlyList<Candle> view = SummarizeCandles(f.Candles, summary_only);
                    return new
                    {
                        period_type = f.PeriodType,
                        tr_cd = f.TrCode,
                        count = f.Candles.Count,
                        candles = SerializeCandles(view, f.PeriodType),
                        indicators = summary_only
                            ? (object)SummarizeIndicators(f.Indicators)
                            : f.Indicators.ToDictionary(kv => kv.Key, kv => kv.Value),
                        context = f.Context,
                    };
                }),
                chart_note = include_chart
                    ? "Inline chart rendering is single-timeframe only. Call once per period_type for inline charts."
                    : null,
            };
            string multiText = JsonSerializer.Serialize(multi, McpJson.Tool);
            return McpJson.OkResult(multiText);
        }
        catch (LsAuthException ex)
        {
            return McpJson.ErrorResult("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.ErrorResult("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
    }

    /// <summary>
    /// Internal carrier for one timeframe's fully-fetched data
    /// (raw candles + indicator series + pre-computed context).
    /// </summary>
    /// <param name="PeriodType">User-facing period label (<c>day</c>, <c>week</c>, …).</param>
    /// <param name="TrCode">Underlying TR that fetched the candles.</param>
    /// <param name="Candles">Ordered candle list (oldest first).</param>
    /// <param name="Indicators">Indicator series keyed by spec, aligned 1:1 with <paramref name="Candles"/>.</param>
    /// <param name="Context">Pre-computed analysis block.</param>
    sealed record FrameResult(
        string PeriodType,
        string TrCode,
        IReadOnlyList<Candle> Candles,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> Indicators,
        ChartContext Context);

    /// <summary>
    /// Dispatches one timeframe to the appropriate TR fetcher, computes indicators,
    /// and builds the analysis context. Used in a loop by the multi-timeframe path.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="shcode">Target stock code.</param>
    /// <param name="period">Period label (<c>day</c>/<c>week</c>/<c>month</c>/<c>year</c>/<c>min</c>/<c>tick</c>).</param>
    /// <param name="count">Number of candles to request.</param>
    /// <param name="from">Optional start date (yyyyMMdd).</param>
    /// <param name="to">Optional end date (yyyyMMdd).</param>
    /// <param name="minuteUnit">Minute interval; ignored unless <paramref name="period"/> is <c>min</c>.</param>
    /// <param name="specs">Parsed indicator specs.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Populated <see cref="FrameResult"/>.</returns>
    static async Task<FrameResult> BuildFrameAsync(
        LsApiClient apiClient,
        string shcode,
        string period,
        int count,
        string? from,
        string? to,
        int minuteUnit,
        IReadOnlyList<IndicatorSpec> specs,
        CancellationToken ct)
    {
        // Fetch extra leading candles so long-period / recursive indicators are
        // fully warmed up across the display window. The warm-up lead is
        // trimmed off below, so the caller sees at most `count` candles,
        // but with indicator series populated from the very first one.
        int warmup = RequiredWarmup(specs);
        int fetchCount = Math.Min(count + warmup, MaxFetchCount);
        int effectiveWarmup = Math.Max(0, fetchCount - count);

        (List<Candle> candles, string trCode) = period switch
        {
            "day" => (await FetchDailyAsync(apiClient, shcode, "2", fetchCount, effectiveWarmup, from, to, ct), "t8410"),
            "week" => (await FetchDailyAsync(apiClient, shcode, "3", fetchCount, effectiveWarmup, from, to, ct), "t8410"),
            "month" => (await FetchDailyAsync(apiClient, shcode, "4", fetchCount, effectiveWarmup, from, to, ct), "t8410"),
            "year" => (await FetchDailyAsync(apiClient, shcode, "5", fetchCount, effectiveWarmup, from, to, ct), "t8410"),
            "min" => (await FetchMinuteAsync(apiClient, shcode, minuteUnit, fetchCount, effectiveWarmup, from, to, ct), "t8412"),
            "tick" => (await FetchTickAsync(apiClient, shcode, fetchCount, ct), "t1301"),
            _ => throw new InvalidOperationException($"Unknown period '{period}'."),
        };

        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicatorResults =
            specs.Count == 0
                ? new Dictionary<string, IReadOnlyList<double?>>()
                : Indicators.Compute(candles, specs);

        // Trim the warm-up lead. Indicators were computed over the full fetched
        // series, so the trimmed series stay fully populated across `count`.
        if (candles.Count > count)
        {
            int drop = candles.Count - count;
            candles = candles.GetRange(drop, count);
            if (indicatorResults.Count > 0)
            {
                indicatorResults = indicatorResults.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyList<double?>)kv.Value.Skip(drop).ToList(),
                    StringComparer.Ordinal);
            }
        }

        ChartContext context = ChartContextBuilder.Build(candles, indicatorResults, specs);
        return new FrameResult(period, trCode, candles, indicatorResults, context);
    }

    /// <summary>
    /// Largest look-back, in candles, needed before the display window so every
    /// requested indicator is fully populated across the candles the caller
    /// sees. SMA and Bollinger get one full period of leading bars; EMA, RSI,
    /// and MACD smooth recursively, so they get a larger convergence window.
    /// </summary>
    /// <param name="specs">Parsed indicator specs.</param>
    /// <returns>Warm-up candle count; 0 when no indicators are requested.</returns>
    static int RequiredWarmup(IReadOnlyList<IndicatorSpec> specs)
    {
        int warmup = 0;
        foreach (IndicatorSpec spec in specs)
        {
            int need = spec.Kind switch
            {
                "ma" or "bb" => (int)spec.Args[0],
                "ema" or "rsi" => (int)spec.Args[0] * 3,
                "macd" => ((int)spec.Args[1] + (int)spec.Args[2]) * 3,
                _ => 0,
            };
            warmup = Math.Max(warmup, need);
        }
        return warmup;
    }

    /// <summary>Number of trailing candles surfaced when <c>summary_only=true</c>.</summary>
    const int SummaryTailCount = 5;

    /// <summary>
    /// Returns the candle tail used by <c>summary_only</c>. Pass-through when
    /// the flag is unset or the input is already shorter than
    /// <see cref="SummaryTailCount"/>.
    /// </summary>
    /// <param name="candles">Full candle list.</param>
    /// <param name="summaryOnly">Whether to keep only the tail.</param>
    /// <returns>Either the original list or its last <see cref="SummaryTailCount"/> rows.</returns>
    static IReadOnlyList<Candle> SummarizeCandles(IReadOnlyList<Candle> candles, bool summaryOnly)
    {
        if (!summaryOnly || candles.Count <= SummaryTailCount)
            return candles;
        return candles.Skip(candles.Count - SummaryTailCount).ToList();
    }

    /// <summary>
    /// Collapses each indicator series down to its last value, for the
    /// <c>summary_only</c> path. Preserves the spec keys.
    /// </summary>
    /// <param name="series">Indicator series keyed by spec.</param>
    /// <returns>Dictionary of spec → final scalar (or <see langword="null"/> if the series is empty).</returns>
    static Dictionary<string, double?> SummarizeIndicators(
        IReadOnlyDictionary<string, IReadOnlyList<double?>> series)
    {
        var summary = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach ((string key, IReadOnlyList<double?> values) in series)
            summary[key] = values.Count == 0 ? null : values[^1];
        return summary;
    }

    /// <summary>
    /// Renders candles for the JSON tool response, formatting the date per
    /// period type (intraday → <c>yyyy-MM-ddTHH:mm:ss</c>, daily+ → <c>yyyy-MM-dd</c>).
    /// </summary>
    /// <param name="candles">Ordered candle list.</param>
    /// <param name="periodType">Period label that selects the date format.</param>
    /// <returns>Lazy sequence of anonymous objects for serialization.</returns>
    static IEnumerable<object> SerializeCandles(IReadOnlyList<Candle> candles, string periodType) =>
        candles.Select(c => new
        {
            date = c.Date.ToString(
                periodType is "min" or "tick" ? "yyyy-MM-ddTHH:mm:ss" : "yyyy-MM-dd",
                CultureInfo.InvariantCulture),
            open = c.Open,
            high = c.High,
            low = c.Low,
            close = c.Close,
            volume = c.Volume,
            value = c.Value,
        });

    /// <summary>
    /// Fetches day/week/month/year candles via TR <c>t8410</c>. Auto-derives a
    /// generous lookback range when <paramref name="from"/> is unset so
    /// <c>qrycnt</c> actually bounds the result.
    /// </summary>
    /// <param name="apiClient">LS API client.</param>
    /// <param name="shcode">Stock code.</param>
    /// <param name="gubun">t8410 period gubun (<c>2</c>=day / <c>3</c>=week / <c>4</c>=month / <c>5</c>=year).</param>
    /// <param name="count">Total candles to request (display window + warm-up).</param>
    /// <param name="warmup">Leading warm-up portion of <paramref name="count"/>; pushes an explicit <paramref name="from"/> back so indicators are populated from the first displayed candle. Zero leaves <paramref name="from"/> untouched.</param>
    /// <param name="from">Optional explicit start (yyyyMMdd).</param>
    /// <param name="to">Optional explicit end (yyyyMMdd).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered candles (oldest first).</returns>
    static async Task<List<Candle>> FetchDailyAsync(
        LsApiClient apiClient,
        string shcode,
        string gubun,
        int count,
        int warmup,
        string? from,
        string? to,
        CancellationToken ct)
    {
        // LS empirically returns only today's partial candle when sdate/edate
        // are absent. Default the range to "the last `count` periods back from
        // today" so qrycnt actually caps the result instead of being a no-op.
        DateTime today = DateTime.Today;
        string effectiveEnd = !string.IsNullOrWhiteSpace(to)
            ? to
            : today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        string effectiveStart;
        if (!string.IsNullOrWhiteSpace(from))
        {
            // Push the explicit start back by the warm-up window so indicators
            // are populated from the first displayed candle. warmup==0 (no
            // indicators) leaves `from` exactly as the caller gave it.
            effectiveStart = warmup > 0
                && DateTime.TryParseExact(from, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fromDate)
                ? fromDate.AddDays(-CalendarDaysBack(gubun, warmup)).ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : from;
        }
        else
        {
            effectiveStart = ComputeDefaultDailyStart(today, gubun, count)
                .ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        var inBlock = new JsonObject
        {
            ["shcode"] = shcode,
            ["gubun"] = gubun,
            ["qrycnt"] = count,
            ["sdate"] = effectiveStart,
            ["edate"] = effectiveEnd,
            ["comp_yn"] = "N",
        };

        LsTrResponse response = await apiClient.CallTrAsync("t8410", inBlock, cancellationToken: ct);
        EnsureSuccess(response);

        return ParseDailyCandles(response.GetBlock("t8410OutBlock1"));
    }

    /// <summary>
    /// Generous calendar-day span that safely covers <paramref name="count"/>
    /// candles of the given period, even across weekends and holidays.
    /// </summary>
    static int CalendarDaysBack(string gubun, int count) => gubun switch
    {
        "2" => Math.Max(count * 3 + 7, 14),    // day: ~70% busy days + buffer
        "3" => Math.Max(count * 8, 30),        // week
        "4" => Math.Max(count * 32, 60),       // month
        "5" => Math.Max(count * 367, 365 * 2), // year
        _ => Math.Max(count * 3 + 7, 14),
    };

    /// <summary>
    /// Generous lookback window for the t8410 InBlock. Returns a date that is
    /// safely far enough back to cover <paramref name="count"/> candles of the
    /// given period, even with weekends + holidays.
    /// </summary>
    static DateTime ComputeDefaultDailyStart(DateTime end, string gubun, int count)
        => end.AddDays(-CalendarDaysBack(gubun, count));

    /// <summary>Fetches minute candles via TR <c>t8412</c>.</summary>
    /// <param name="apiClient">LS API client.</param>
    /// <param name="shcode">Stock code.</param>
    /// <param name="minuteUnit">Minute interval (1/3/5/10/15/30/60).</param>
    /// <param name="count">Total candles to request (display window + warm-up).</param>
    /// <param name="warmup">Leading warm-up portion of <paramref name="count"/>; pushes an explicit <paramref name="from"/> back so indicators are populated from the first displayed candle.</param>
    /// <param name="from">Optional start date (yyyyMMdd).</param>
    /// <param name="to">Optional end date (yyyyMMdd).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered minute candles.</returns>
    static async Task<List<Candle>> FetchMinuteAsync(
        LsApiClient apiClient,
        string shcode,
        int minuteUnit,
        int count,
        int warmup,
        string? from,
        string? to,
        CancellationToken ct)
    {
        string? effectiveStart = from;
        if (warmup > 0
            && !string.IsNullOrWhiteSpace(from)
            && DateTime.TryParseExact(from, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fromDate))
        {
            effectiveStart = fromDate.AddDays(-CalendarDaysBack("2", warmup)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        }

        var inBlock = new JsonObject
        {
            ["shcode"] = shcode,
            ["ncnt"] = minuteUnit,
            ["qrycnt"] = count,
            ["nday"] = "0",
            ["comp_yn"] = "N",
        };
        if (!string.IsNullOrWhiteSpace(effectiveStart)) inBlock["sdate"] = effectiveStart;
        if (!string.IsNullOrWhiteSpace(to)) inBlock["edate"] = to;

        LsTrResponse response = await apiClient.CallTrAsync("t8412", inBlock, cancellationToken: ct);
        EnsureSuccess(response);

        return ParseMinuteCandles(response.GetBlock("t8412OutBlock1"));
    }

    /// <summary>Fetches tick-level executions via TR <c>t1301</c> and caps the result locally.</summary>
    /// <param name="apiClient">LS API client.</param>
    /// <param name="shcode">Stock code.</param>
    /// <param name="count">Maximum ticks to return (LS does not honor a count parameter for t1301).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered tick "candles" (open=high=low=close=trade price).</returns>
    static async Task<List<Candle>> FetchTickAsync(
        LsApiClient apiClient,
        string shcode,
        int count,
        CancellationToken ct)
    {
        var inBlock = new JsonObject
        {
            ["shcode"] = shcode,
            ["cvolume"] = 0,
        };

        LsTrResponse response = await apiClient.CallTrAsync("t1301", inBlock, cancellationToken: ct);
        EnsureSuccess(response);

        return ParseTickCandles(response.GetBlock("t1301OutBlock1"), count);
    }

    /// <summary>
    /// Throws <see cref="LsTrException"/> when the response carries a non-success
    /// <c>rsp_cd</c>. HTTP-level errors are already raised by the API client; this
    /// covers LS business-level errors that come back with a 200.
    /// </summary>
    /// <param name="response">Response to validate.</param>
    /// <exception cref="LsTrException">When <see cref="LsTrResponse.IsSuccess"/> is false.</exception>
    static void EnsureSuccess(LsTrResponse response)
    {
        if (!response.IsSuccess)
            throw new LsTrException(
                response.TrCode,
                $"LS reported business error rsp_cd={response.RspCode} ({response.RspMessage}).",
                statusCode: response.StatusCode,
                responseBody: response.RawBody);
    }

    /// <summary>Parses <c>t8410OutBlock1</c> into <see cref="Candle"/>s. Skips rows missing or unparseable dates.</summary>
    /// <param name="array">The OutBlock1 JSON array, or <see langword="null"/>.</param>
    /// <returns>Ordered candles; empty when input is null/non-array.</returns>
    static List<Candle> ParseDailyCandles(JsonElement? array)
    {
        var candles = new List<Candle>();
        if (array is null || array.Value.ValueKind != JsonValueKind.Array)
            return candles;

        foreach (JsonElement row in array.Value.EnumerateArray())
        {
            string? date = row.ReadString("date");
            if (string.IsNullOrEmpty(date)) continue;
            if (!DateTime.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
                continue;

            candles.Add(new Candle(
                Date: parsed,
                Open: row.ReadLong("open"),
                High: row.ReadLong("high"),
                Low: row.ReadLong("low"),
                Close: row.ReadLong("close"),
                Volume: row.ReadLong("jdiff_vol"),
                Value: row.ReadLong("value")));
        }
        return candles;
    }

    /// <summary>Parses <c>t8412OutBlock1</c> (minute candles) into <see cref="Candle"/>s.</summary>
    /// <param name="array">The OutBlock1 JSON array.</param>
    /// <returns>Ordered minute candles; empty when the input is missing/invalid.</returns>
    static List<Candle> ParseMinuteCandles(JsonElement? array)
    {
        var candles = new List<Candle>();
        if (array is null || array.Value.ValueKind != JsonValueKind.Array)
            return candles;

        foreach (JsonElement row in array.Value.EnumerateArray())
        {
            string? date = row.ReadString("date");
            string? time = row.ReadString("time");
            if (string.IsNullOrEmpty(date) || string.IsNullOrEmpty(time)) continue;

            string combined = date.PadLeft(8, '0') + time.PadLeft(6, '0');
            if (!DateTime.TryParseExact(combined, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
                continue;

            candles.Add(new Candle(
                Date: parsed,
                Open: row.ReadLong("open"),
                High: row.ReadLong("high"),
                Low: row.ReadLong("low"),
                Close: row.ReadLong("close"),
                Volume: row.ReadLong("jdiff_vol"),
                Value: row.ReadLong("value")));
        }
        return candles;
    }

    /// <summary>
    /// Parses <c>t1301OutBlock1</c> (tick executions) into <see cref="Candle"/>s,
    /// capping at <paramref name="max"/> entries. Each tick becomes a degenerate
    /// candle where open = high = low = close = trade price.
    /// </summary>
    /// <param name="array">The OutBlock1 JSON array.</param>
    /// <param name="max">Upper bound on the number of ticks to return.</param>
    /// <returns>Ordered tick "candles".</returns>
    static List<Candle> ParseTickCandles(JsonElement? array, int max)
    {
        var candles = new List<Candle>();
        if (array is null || array.Value.ValueKind != JsonValueKind.Array)
            return candles;

        DateTime today = DateTime.Today;
        foreach (JsonElement row in array.Value.EnumerateArray())
        {
            string? chetime = row.ReadString("chetime");
            if (string.IsNullOrEmpty(chetime)) continue;
            if (!DateTime.TryParseExact(chetime.PadLeft(6, '0'), "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime tod))
                continue;

            long price = row.ReadLong("price");
            candles.Add(new Candle(
                Date: today.Add(tod.TimeOfDay),
                Open: price,
                High: price,
                Low: price,
                Close: price,
                Volume: row.ReadLong("cvolume"),
                Value: null));

            if (candles.Count >= max)
                break;
        }
        return candles;
    }
}
