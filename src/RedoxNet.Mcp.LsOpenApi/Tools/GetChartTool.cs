using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;
using RedoxNet.Mcp.LsOpenApi.Charting;

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
    /// <param name="include_chart">If true, attach a Plotly v5 JSON spec under <c>chart</c> for client-side rendering. Default false.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with candles, indicators, and a <c>context</c> block of pre-computed analytics.</returns>
    [McpServerTool(Name = "ls_get_chart")]
    [Description("""
        Returns OHLCV candles for a Korean stock with optional technical indicators and pre-computed analysis context (divergence from MAs, volume averages, drawdown from period high, MA trend, bullish alignment).

        USE WHEN: the user asks for a chart, 차트, 일봉/주봉/월봉/분봉/틱, OHLCV, technical analysis, or multi-timeframe analysis (e.g. "삼성전자 일봉과 주봉, 월봉 같이 보여줘").
        AVOID WHEN: the user wants the current snapshot only — use ls_get_quote instead.

        Single timeframe: period_type='day' → response has top-level candles/indicators/context.
        Multi timeframe: period_type='day,week,month' → response has a frames[] array, one entry per timeframe, each with its own context.

        Set include_chart=true to also receive a Plotly v5 JSON spec under 'chart' (single) or each frame's 'chart' (multi). Clients render it via Plotly.js with no server-side image rendering.

        Indicator examples: 'ma:5', 'ma:20', 'ema:12', 'rsi:14', 'macd:12,26,9', 'bb:20,2'.
        """)]
    public static async Task<string> GetChart(
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
        [Description("If true, attach a Plotly v5 JSON spec under 'chart' for client-side rendering. Default false.")]
        bool include_chart = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.Error("shcode is required.");
        if (string.IsNullOrWhiteSpace(period_type))
            return McpJson.Error("period_type is required.");

        int cappedCount = Math.Clamp(count, 1, 500);

        List<string> periods = period_type
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (periods.Count == 0)
            return McpJson.Error("period_type is required.");

        foreach (string p in periods)
        {
            if (!KnownPeriodTypes.Contains(p))
                return McpJson.Error($"Unknown period_type '{p}'. Use day, week, month, min, or tick.");
        }

        List<IndicatorSpec> parsedIndicators = new();
        if (indicators is { Length: > 0 })
        {
            foreach (string raw in indicators)
            {
                if (!IndicatorSpecParser.TryParse(raw, out IndicatorSpec? spec, out string? error))
                    return McpJson.Error($"Invalid indicator spec '{raw}'.", new { reason = error });
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
                var single = new
                {
                    shcode,
                    period_type = only.PeriodType,
                    tr_cd = only.TrCode,
                    count = only.Candles.Count,
                    candles = SerializeCandles(only.Candles, only.PeriodType),
                    indicators = only.Indicators.ToDictionary(kv => kv.Key, kv => kv.Value),
                    context = only.Context,
                    chart = include_chart
                        ? PlotlyChartBuilder.Build(shcode, only.PeriodType, only.Candles, only.Indicators, parsedIndicators)
                        : null,
                };
                return JsonSerializer.Serialize(single, McpJson.Tool);
            }

            var multi = new
            {
                shcode,
                period_types = periods,
                frames = frames.Select(f => new
                {
                    period_type = f.PeriodType,
                    tr_cd = f.TrCode,
                    count = f.Candles.Count,
                    candles = SerializeCandles(f.Candles, f.PeriodType),
                    indicators = f.Indicators.ToDictionary(kv => kv.Key, kv => kv.Value),
                    context = f.Context,
                    chart = include_chart
                        ? PlotlyChartBuilder.Build(shcode, f.PeriodType, f.Candles, f.Indicators, parsedIndicators)
                        : null,
                }),
            };
            return JsonSerializer.Serialize(multi, McpJson.Tool);
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
    }

    sealed record FrameResult(
        string PeriodType,
        string TrCode,
        IReadOnlyList<Candle> Candles,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> Indicators,
        ChartContext Context);

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
        (List<Candle> candles, string trCode) = period switch
        {
            "day" => (await FetchDailyAsync(apiClient, shcode, "2", count, from, to, ct), "t8410"),
            "week" => (await FetchDailyAsync(apiClient, shcode, "3", count, from, to, ct), "t8410"),
            "month" => (await FetchDailyAsync(apiClient, shcode, "4", count, from, to, ct), "t8410"),
            "year" => (await FetchDailyAsync(apiClient, shcode, "5", count, from, to, ct), "t8410"),
            "min" => (await FetchMinuteAsync(apiClient, shcode, minuteUnit, count, from, to, ct), "t8412"),
            "tick" => (await FetchTickAsync(apiClient, shcode, count, ct), "t1301"),
            _ => throw new InvalidOperationException($"Unknown period '{period}'."),
        };

        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicatorResults =
            specs.Count == 0
                ? new Dictionary<string, IReadOnlyList<double?>>()
                : Indicators.Compute(candles, specs);

        ChartContext context = ChartContextBuilder.Build(candles, indicatorResults, specs);
        return new FrameResult(period, trCode, candles, indicatorResults, context);
    }

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

    static async Task<List<Candle>> FetchDailyAsync(
        LsApiClient apiClient,
        string shcode,
        string gubun,
        int count,
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
        string effectiveStart = !string.IsNullOrWhiteSpace(from)
            ? from
            : ComputeDefaultDailyStart(today, gubun, count).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

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
    /// Generous lookback window for the t8410 InBlock. Returns a date that is
    /// safely far enough back to cover <paramref name="count"/> candles of the
    /// given period, even with weekends + holidays.
    /// </summary>
    static DateTime ComputeDefaultDailyStart(DateTime end, string gubun, int count)
    {
        int daysBack = gubun switch
        {
            "2" => Math.Max(count * 3 + 7, 14),    // day: ~70% busy days + buffer
            "3" => Math.Max(count * 8, 30),        // week
            "4" => Math.Max(count * 32, 60),       // month
            "5" => Math.Max(count * 367, 365 * 2), // year
            _ => Math.Max(count * 3 + 7, 14),
        };
        return end.AddDays(-daysBack);
    }

    static async Task<List<Candle>> FetchMinuteAsync(
        LsApiClient apiClient,
        string shcode,
        int minuteUnit,
        int count,
        string? from,
        string? to,
        CancellationToken ct)
    {
        var inBlock = new JsonObject
        {
            ["shcode"] = shcode,
            ["ncnt"] = minuteUnit,
            ["qrycnt"] = count,
            ["nday"] = "0",
            ["comp_yn"] = "N",
        };
        if (!string.IsNullOrWhiteSpace(from)) inBlock["sdate"] = from;
        if (!string.IsNullOrWhiteSpace(to)) inBlock["edate"] = to;

        LsTrResponse response = await apiClient.CallTrAsync("t8412", inBlock, cancellationToken: ct);
        EnsureSuccess(response);

        return ParseMinuteCandles(response.GetBlock("t8412OutBlock1"));
    }

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

    static void EnsureSuccess(LsTrResponse response)
    {
        if (!response.IsSuccess)
            throw new LsTrException(
                response.TrCode,
                $"LS reported business error rsp_cd={response.RspCode} ({response.RspMessage}).",
                statusCode: response.StatusCode,
                responseBody: response.RawBody);
    }

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
