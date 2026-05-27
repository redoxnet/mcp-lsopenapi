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

    static readonly HashSet<string> KnownOutputModes =
        new(StringComparer.OrdinalIgnoreCase) { "display", "analyze", "export", "reference" };

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
    /// <param name="summary_only">Legacy compact flag. Prefer <paramref name="output_mode"/>. When used without an explicit output mode, it maps to <c>analyze</c>.</param>
    /// <param name="output_mode">Output intent: <c>display</c>, <c>analyze</c>, <c>export</c>, or <c>reference</c>. Raw OHLCV arrays are returned only in <c>export</c>.</param>
    /// <param name="name">Optional human-readable stock name (e.g. '삼성전자'); used only for the inline chart title. The chart TRs do not carry the name.</param>
    /// <param name="with_warmup">
    /// Warm-up policy override for the analytical summary. <see langword="null"/>
    /// (default) auto-applies warm-up when <paramref name="from"/> is unspecified;
    /// <see langword="true"/> forces warm-up even with explicit <paramref name="from"/>;
    /// <see langword="false"/> skips warm-up even when <paramref name="from"/> is null.
    /// </param>
    /// <param name="theme">Optional chart theme override: <c>"light"</c>, <c>"dark"</c>, or <c>"auto"</c>/<see langword="null"/> (default). Stored on the dataset so follow-up ls_add_indicator / ls_reframe_chart inherit it. See docs/MCP-APPS-INTEROP.md §3 Q8.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Text content with a compact analytical summary and dataset handle, plus an optional <c>structuredContent.chart</c> for inline rendering.</returns>
    [McpServerTool(Name = "ls_get_chart")]
    [Description("""
        Returns OHLCV candles for a Korean stock with optional technical indicators and pre-computed analysis context (divergence from MAs, volume averages, drawdown from period high, MA trend, bullish alignment).

        USE WHEN: the user asks for a chart, 차트, 일봉/주봉/월봉/분봉/틱, OHLCV, technical analysis, or multi-timeframe analysis (e.g. "삼성전자 일봉과 주봉, 월봉 같이 보여줘").
        AVOID WHEN: the user wants the current snapshot only — use ls_get_quote instead.

        Single timeframe: period_type='day' → response has top-level summary/context/dataset_id.
        Multi timeframe: period_type='day,week,month' → response has a frames[] array, one compact entry per timeframe.

        Set include_chart=true (single timeframe only) for inline chart rendering on hosts that support MCP Apps. When the connected host can render charts, the Plotly v5 spec ships as structuredContent (not in the model's text context — zero token cost) and the host renders it inline; on a text-only host the parameter is not offered. Multi-timeframe with include_chart is a no-op for inline rendering (call once per period_type for charts); the structured candles/indicators/context payload is unaffected.

        output_mode controls token cost:
        - display: chart rendering; text contains dataset_id + AnalyticalSummary, chart spec goes to structuredContent.
        - analyze: model reasoning; text contains dataset_id + AnalyticalSummary + context, no raw bars.
        - export: ONLY when the user explicitly asks for raw/table/CSV data; includes full OHLCV and indicator arrays in text.
        - reference: follow-up tool handle only; returns dataset_id and metadata.

        Raw candles and full indicator arrays are intentionally absent unless output_mode='export'. summary_only is legacy and maps to analyze when output_mode is omitted.

        Warm-up behavior (with_warmup):
        - Omitted (default): auto-pads the fetch when `from` is unspecified so the summary's long-period indicators (MA60 slope, MA200, change_1y, key_turns) populate. RECOMMENDED for "최근 흐름" / "show me the recent picture" queries.
        - with_warmup=true: forces the analytical warm-up even with explicit `from`. Use when the user asks to analyze trends or long-period indicators inside a narrow window (e.g. "2024년 1~6월에서 MA60 추세도 보고 싶어").
        - with_warmup=false: skips warm-up even when `from` is unspecified. Use for the fastest, narrowest read; long-period indicators may be null.
        Either way, inspect summary.coverage.status and summary.coverage.note to explain any null indicators to the user — coverage tells you whether the missing value is due to a narrow window (can be widened) versus the period not supporting it.

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
        [Description("\"display\" for chart rendering (summary text + structured chart), \"analyze\" for model reasoning (summary only), \"export\" only for explicit raw data/table requests, or \"reference\" for dataset_id-only follow-up handles. Defaults to display when include_chart=true, otherwise analyze.")]
        string? output_mode = null,
        [Description("Optional human-readable stock name (e.g. '삼성전자'). Used only for the inline chart title — pass it when you already know the name so the chart reads '삼성전자 (005930) — 일봉' instead of just the code. The TR responses do not carry the name.")]
        string? name = null,
        [Description("Warm-up policy override. Omit (null) for auto: pad when `from` is unspecified, skip when `from` is given. Pass true to force pad with explicit `from` (analyze trends inside a narrow window). Pass false to skip pad even without `from` (fastest read; long-period indicators may be null — check summary.coverage to explain why).")]
        bool? with_warmup = null,
        [Description("Optional chart theme override: \"light\", \"dark\", or \"auto\" (default). Pass when the user explicitly asks for a dark/light chart (e.g. \"다크 차트로 보여줘\") — the iframe overrides hostContext.theme. Follow-up ls_add_indicator / ls_reframe_chart on the same dataset_id inherit this unless overridden.")]
        string? theme = null,
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

        string outputMode = ResolveOutputMode(output_mode, include_chart, summary_only);
        if (!KnownOutputModes.Contains(outputMode))
            return McpJson.ErrorResult($"Unknown output_mode '{output_mode}'. Use display, analyze, export, or reference.");

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
                    apiClient, shcode, name, p, cappedCount, from, to, minute_unit, parsedIndicators, with_warmup, cancellationToken);
                frames.Add(frame);
            }

            List<ChartDatasetFrame> datasetFrames = frames.Select(f =>
                new ChartDatasetFrame(
                    f.PeriodType,
                    f.TrCode,
                    minute_unit,
                    from,
                    to,
                    f.Candles,
                    f.Indicators,
                    parsedIndicators,
                    f.Context,
                    f.Summary)).ToList();

            string? themeHint = CandlestickChartBuilder.NormalizeThemeHint(theme);
            string datasetId = DatasetHandleCache.Add("chart", new ChartDataset(
                shcode,
                name,
                periods,
                datasetFrames,
                DateTimeOffset.UtcNow,
                themeHint));

            if (periods.Count == 1)
            {
                FrameResult only = frames[0];
                ChartDatasetFrame cached = datasetFrames[0];
                bool includeStructuredChart = include_chart || outputMode == "display";

                object single = outputMode switch
                {
                    "export" => BuildSingleExportResponse(
                        shcode, datasetId, only, cached.Summary, outputMode, summary_only),
                    "reference" => new
                    {
                        shcode,
                        period_type = only.PeriodType,
                        output_mode = outputMode,
                        dataset_id = datasetId,
                        tr_cd = only.TrCode,
                        count = only.Candles.Count,
                        range = cached.Summary.DateRange,
                    },
                    _ => new
                    {
                        shcode,
                        period_type = only.PeriodType,
                        output_mode = outputMode,
                        dataset_id = datasetId,
                        tr_cd = only.TrCode,
                        count = only.Candles.Count,
                        summary = cached.Summary,
                        context = only.Context,
                    },
                };
                string singleText = JsonSerializer.Serialize(single, McpJson.Tool);

                JsonObject? structured = null;
                if (includeStructuredChart)
                {
                    structured = new JsonObject
                    {
                        ["chart"] = CandlestickChartBuilder.Build(
                            shcode, only.PeriodType, only.Candles, only.Indicators, parsedIndicators, name,
                            currency: null, themeHint: themeHint),
                    };
                }
                CallToolResult singleResult = McpJson.OkResult(singleText, structured);
                if (outputMode == "export")
                    McpJson.AttachExportGuard(singleResult);
                return singleResult;
            }

            // Multi-timeframe: structuredContent stays null — the iframe template
            // renders one chart per tool result. Users wanting inline charts call
            // once per period_type. The structured candles/indicators/context
            // payload below covers programmatic / LLM use either way.
            object multi;
            if (outputMode == "export")
            {
                multi = new
                {
                    shcode,
                    output_mode = outputMode,
                    dataset_id = datasetId,
                    period_types = periods,
                    summary_only,
                    frames = frames.Zip(datasetFrames, (f, cached) =>
                    {
                        IReadOnlyList<Candle> view = SummarizeCandles(f.Candles, summary_only);
                        return new
                        {
                            period_type = f.PeriodType,
                            tr_cd = f.TrCode,
                            count = f.Candles.Count,
                            summary = cached.Summary,
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
            }
            else if (outputMode == "reference")
            {
                multi = new
                {
                    shcode,
                    output_mode = outputMode,
                    dataset_id = datasetId,
                    period_types = periods,
                    frames = datasetFrames.Select(f => new
                    {
                        period_type = f.PeriodType,
                        tr_cd = f.TrCode,
                        count = f.Candles.Count,
                        range = f.Summary.DateRange,
                    }),
                };
            }
            else
            {
                multi = new
                {
                    shcode,
                    output_mode = outputMode,
                    dataset_id = datasetId,
                    period_types = periods,
                    frames = datasetFrames.Select(f => new
                    {
                        period_type = f.PeriodType,
                        tr_cd = f.TrCode,
                        count = f.Candles.Count,
                        summary = f.Summary,
                        context = f.Context,
                    }),
                    chart_note = include_chart || outputMode == "display"
                        ? "Inline chart rendering is single-timeframe only. Call once per period_type for inline charts."
                        : null,
                };
            }

            string multiText = JsonSerializer.Serialize(multi, McpJson.Tool);
            CallToolResult multiResult = McpJson.OkResult(multiText);
            if (outputMode == "export")
                McpJson.AttachExportGuard(multiResult);
            return multiResult;
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

    static string ResolveOutputMode(string? outputMode, bool includeChart, bool summaryOnly)
    {
        if (!string.IsNullOrWhiteSpace(outputMode))
            return outputMode.Trim().ToLowerInvariant();
        if (includeChart)
            return "display";
        if (summaryOnly)
            return "analyze";
        return "analyze";
    }

    static object BuildSingleExportResponse(
        string shcode,
        string datasetId,
        FrameResult frame,
        AnalyticalSummary summary,
        string outputMode,
        bool summaryOnly)
    {
        IReadOnlyList<Candle> candleView = SummarizeCandles(frame.Candles, summaryOnly);
        return new
        {
            shcode,
            period_type = frame.PeriodType,
            output_mode = outputMode,
            dataset_id = datasetId,
            tr_cd = frame.TrCode,
            count = frame.Candles.Count,
            summary_only = summaryOnly,
            summary,
            candles = SerializeCandles(candleView, frame.PeriodType),
            indicators = summaryOnly
                ? (object)SummarizeIndicators(frame.Indicators)
                : frame.Indicators.ToDictionary(kv => kv.Key, kv => kv.Value),
            context = frame.Context,
        };
    }

    /// <summary>
    /// Adds an indicator to a cached chart dataset and optionally emits an
    /// updated Plotly chart spec.
    /// </summary>
    [McpServerTool(Name = "ls_add_indicator")]
    [Description("""
        Adds a technical indicator to a chart dataset previously returned by ls_get_chart. Use this for follow-up requests like "MA200도 추가해줘" without sending raw OHLCV back through the model context.

        The tool resolves dataset_id from the process-local handle cache, refetches the selected frame with the requested indicator's warm-up window, updates the same dataset_id, and returns a compact summary plus optional structuredContent.chart. If the dataset has multiple timeframes, pass period_type to choose the frame.
        """)]
    public static async Task<CallToolResult> AddIndicator(
        LsApiClient apiClient,
        [Description("dataset_id returned by ls_get_chart, e.g. 'ds_a8f3c012'.")]
        string dataset_id,
        [Description("Indicator spec to add, e.g. 'ma:200', 'ema:60', 'bb:20,2'.")]
        string indicator,
        [Description("Optional period frame to update when dataset_id contains multiple frames. Required for ambiguous multi-timeframe datasets.")]
        string? period_type = null,
        [Description("If true, return an updated Plotly chart spec through structuredContent.chart. Default true.")]
        bool include_chart = true,
        [Description("Optional chart theme override: \"light\", \"dark\", or \"auto\". Null (default) inherits the dataset's theme from the original ls_get_chart call. Pass to switch theme mid-conversation (e.g. \"이걸 다크로 바꿔서\").")]
        string? theme = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataset_id))
            return McpJson.ErrorResult("dataset_id is required.");
        if (string.IsNullOrWhiteSpace(indicator))
            return McpJson.ErrorResult("indicator is required.");
        if (!IndicatorSpecParser.TryParse(indicator, out IndicatorSpec? addedSpec, out string? error))
            return McpJson.ErrorResult($"Invalid indicator spec '{indicator}'.", new { reason = error });
        IndicatorSpec specToAdd = addedSpec!;

        // Overseas chart datasets share the same handle cache; route to the
        // overseas tool when the handle resolves to one of its payloads.
        CallToolResult? overseas = await OverseasStockTools.TryAddIndicatorAsync(
            apiClient, dataset_id, specToAdd, include_chart, theme, cancellationToken).ConfigureAwait(false);
        if (overseas is not null)
            return overseas;

        if (!DatasetHandleCache.TryGet<ChartDataset>(dataset_id, out ChartDataset? dataset) || dataset is null)
            return McpJson.ErrorResult("Unknown or expired dataset_id.", new { dataset_id });

        ChartDatasetFrame? frame = ResolveFrame(dataset, period_type, out string? frameError);
        if (frame is null)
            return McpJson.ErrorResult(frameError ?? "Unable to resolve dataset frame.", new { dataset_id, period_type });

        List<IndicatorSpec> specs = MergeIndicatorSpecs(frame.IndicatorSpecs, specToAdd);
        try
        {
            FrameResult refreshed = await BuildFrameAsync(
                apiClient,
                dataset.Shcode,
                dataset.Name,
                frame.PeriodType,
                frame.Candles.Count,
                frame.SourceFrom,
                frame.SourceTo,
                frame.MinuteUnit,
                specs,
                withWarmup: null,
                cancellationToken);

            AnalyticalSummary summary = refreshed.Summary;
            var updatedFrame = new ChartDatasetFrame(
                refreshed.PeriodType,
                refreshed.TrCode,
                frame.MinuteUnit,
                frame.SourceFrom,
                frame.SourceTo,
                refreshed.Candles,
                refreshed.Indicators,
                specs,
                refreshed.Context,
                summary);

            // theme=null inherits dataset's stored ThemeHint; non-null overrides
            // and persists so subsequent ls_add_indicator / ls_reframe_chart
            // calls keep the new theme without re-passing it.
            string? themeHint = string.IsNullOrWhiteSpace(theme)
                ? dataset.ThemeHint
                : CandlestickChartBuilder.NormalizeThemeHint(theme);
            ChartDataset updatedDataset = ReplaceFrame(dataset, updatedFrame) with { ThemeHint = themeHint };
            if (!DatasetHandleCache.TryUpdate(dataset_id, updatedDataset))
                return McpJson.ErrorResult("Unknown or expired dataset_id.", new { dataset_id });

            var payload = new
            {
                shcode = dataset.Shcode,
                period_type = updatedFrame.PeriodType,
                dataset_id,
                added_indicator = specToAdd.Raw,
                indicator = SummarizeIndicator(specToAdd.Raw, refreshed.Indicators),
                summary,
                context = refreshed.Context,
            };

            JsonObject? structured = include_chart
                ? new JsonObject
                {
                    ["chart"] = CandlestickChartBuilder.Build(
                        dataset.Shcode,
                        updatedFrame.PeriodType,
                        updatedFrame.Candles,
                        updatedFrame.Indicators,
                        specs,
                        dataset.Name,
                        currency: null,
                        themeHint: themeHint),
                }
                : null;

            return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool), structured);
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
    /// Reframes a cached chart dataset to another period/count and optionally
    /// emits an updated Plotly chart spec.
    /// </summary>
    [McpServerTool(Name = "ls_reframe_chart")]
    [Description("""
        Reframes a chart dataset returned by ls_get_chart to a different period/count using the same symbol and indicator specs. Use this for follow-up requests like "이걸 일봉으로 바꿔서 최근 6개월만 보여줘".

        Until persistent candles.db is introduced, this tool refetches the requested frame from LS using the original shcode and replaces the dataset_id's current view in the process-local handle cache.
        """)]
    public static async Task<CallToolResult> ReframeChart(
        LsApiClient apiClient,
        [Description("dataset_id returned by ls_get_chart, e.g. 'ds_a8f3c012'.")]
        string dataset_id,
        [Description("New period type: 'day', 'week', 'month', 'year', 'min', or 'tick'.")]
        string period_type,
        [Description("Number of candles for the new view (1-500). Default 60.")]
        int count = 60,
        [Description("Optional start date in 'yyyyMMdd'.")]
        string? from = null,
        [Description("Optional end date in 'yyyyMMdd'.")]
        string? to = null,
        [Description("For period_type='min': minute interval (1, 3, 5, 10, 15, 30, 60). Default 5.")]
        int minute_unit = 5,
        [Description("If true, return an updated Plotly chart spec through structuredContent.chart. Default true.")]
        bool include_chart = true,
        [Description("Optional chart theme override: \"light\", \"dark\", or \"auto\". Null (default) inherits the dataset's theme. Pass to switch theme mid-conversation.")]
        string? theme = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dataset_id))
            return McpJson.ErrorResult("dataset_id is required.");
        if (string.IsNullOrWhiteSpace(period_type))
            return McpJson.ErrorResult("period_type is required.");

        // Overseas chart datasets share the same handle cache; route to the
        // overseas tool when the handle resolves to one of its payloads.
        CallToolResult? overseas = await OverseasStockTools.TryReframeAsync(
            apiClient, dataset_id, period_type, count, from, to, minute_unit, include_chart, theme, cancellationToken).ConfigureAwait(false);
        if (overseas is not null)
            return overseas;

        if (!DatasetHandleCache.TryGet<ChartDataset>(dataset_id, out ChartDataset? dataset) || dataset is null)
            return McpJson.ErrorResult("Unknown or expired dataset_id.", new { dataset_id });

        string period = period_type.Trim().ToLowerInvariant();
        if (!KnownPeriodTypes.Contains(period))
            return McpJson.ErrorResult($"Unknown period_type '{period_type}'. Use day, week, month, min, or tick.");

        IReadOnlyList<IndicatorSpec> specs = dataset.Frames.FirstOrDefault()?.IndicatorSpecs ?? Array.Empty<IndicatorSpec>();
        try
        {
            FrameResult frame = await BuildFrameAsync(
                apiClient,
                dataset.Shcode,
                dataset.Name,
                period,
                Math.Clamp(count, 1, 500),
                from,
                to,
                minute_unit,
                specs,
                withWarmup: null,
                cancellationToken);

            AnalyticalSummary summary = frame.Summary;
            var updatedFrame = new ChartDatasetFrame(
                frame.PeriodType,
                frame.TrCode,
                minute_unit,
                from,
                to,
                frame.Candles,
                frame.Indicators,
                specs,
                frame.Context,
                summary);

            string? themeHint = string.IsNullOrWhiteSpace(theme)
                ? dataset.ThemeHint
                : CandlestickChartBuilder.NormalizeThemeHint(theme);
            ChartDataset updatedDataset = ReplaceWithSingleFrame(dataset, updatedFrame) with { ThemeHint = themeHint };
            if (!DatasetHandleCache.TryUpdate(dataset_id, updatedDataset))
                return McpJson.ErrorResult("Unknown or expired dataset_id.", new { dataset_id });

            var payload = new
            {
                shcode = dataset.Shcode,
                period_type = frame.PeriodType,
                dataset_id,
                tr_cd = frame.TrCode,
                count = frame.Candles.Count,
                summary,
                context = frame.Context,
            };

            JsonObject? structured = include_chart
                ? new JsonObject
                {
                    ["chart"] = CandlestickChartBuilder.Build(
                        dataset.Shcode,
                        frame.PeriodType,
                        frame.Candles,
                        frame.Indicators,
                        specs,
                        dataset.Name,
                        currency: null,
                        themeHint: themeHint),
                }
                : null;

            return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool), structured);
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

    static ChartDatasetFrame? ResolveFrame(ChartDataset dataset, string? periodType, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(periodType))
        {
            string period = periodType.Trim().ToLowerInvariant();
            ChartDatasetFrame? frame = dataset.Frames.FirstOrDefault(f => f.PeriodType == period);
            if (frame is null)
                error = $"dataset_id does not contain period_type '{periodType}'.";
            return frame;
        }

        if (dataset.Frames.Count == 1)
            return dataset.Frames[0];

        error = "period_type is required for a multi-timeframe dataset_id.";
        return null;
    }

    static List<IndicatorSpec> MergeIndicatorSpecs(IReadOnlyList<IndicatorSpec> existing, IndicatorSpec added)
    {
        var merged = new List<IndicatorSpec>(existing);
        if (!merged.Any(s => string.Equals(s.Raw, added.Raw, StringComparison.OrdinalIgnoreCase)))
            merged.Add(added);
        return merged;
    }

    static ChartDataset ReplaceFrame(ChartDataset dataset, ChartDatasetFrame updatedFrame)
    {
        var frames = dataset.Frames
            .Where(f => f.PeriodType != updatedFrame.PeriodType)
            .Append(updatedFrame)
            .OrderBy(f => PeriodSortKey(f.PeriodType))
            .ToList();

        var periods = frames.Select(f => f.PeriodType).ToList();
        return dataset with { PeriodTypes = periods, Frames = frames };
    }

    static ChartDataset ReplaceWithSingleFrame(ChartDataset dataset, ChartDatasetFrame updatedFrame)
        => dataset with
        {
            PeriodTypes = new[] { updatedFrame.PeriodType },
            Frames = new[] { updatedFrame },
        };

    static int PeriodSortKey(string period) => period switch
    {
        "tick" => 0,
        "min" => 1,
        "day" => 2,
        "week" => 3,
        "month" => 4,
        "year" => 5,
        _ => 99,
    };

    static object SummarizeIndicator(
        string requestedSpec,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> indicators)
    {
        var latest = new Dictionary<string, double?>(StringComparer.Ordinal);
        foreach ((string key, IReadOnlyList<double?> values) in indicators)
        {
            if (string.Equals(key, requestedSpec, StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith(requestedSpec + ".", StringComparison.OrdinalIgnoreCase))
            {
                latest[key] = values.LastOrDefault(v => v.HasValue);
            }
        }

        return new
        {
            spec = requestedSpec,
            latest,
        };
    }

    /// <summary>
    /// Internal carrier for one timeframe's fully-fetched data
    /// (raw candles + indicator series + pre-computed context + summary).
    /// </summary>
    /// <param name="PeriodType">User-facing period label (<c>day</c>, <c>week</c>, …).</param>
    /// <param name="TrCode">Underlying TR that fetched the candles.</param>
    /// <param name="Candles">Trimmed display candle list (oldest first, at most <c>count</c> bars).</param>
    /// <param name="Indicators">Indicator series keyed by spec, aligned 1:1 with <paramref name="Candles"/>.</param>
    /// <param name="Context">Pre-computed analysis block over the display window.</param>
    /// <param name="Summary">Compact analytical summary computed over the warm-up-inclusive series, so its long-period MAs / slope / 1Y change stay populated even when the display window is short.</param>
    sealed record FrameResult(
        string PeriodType,
        string TrCode,
        IReadOnlyList<Candle> Candles,
        IReadOnlyDictionary<string, IReadOnlyList<double?>> Indicators,
        ChartContext Context,
        AnalyticalSummary Summary);

    /// <summary>
    /// Dispatches one timeframe to the appropriate TR fetcher, computes indicators,
    /// and builds the analysis context and summary. Used in a loop by the
    /// multi-timeframe path.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="shcode">Target stock code.</param>
    /// <param name="name">Optional human-readable stock name, carried into the summary.</param>
    /// <param name="period">Period label (<c>day</c>/<c>week</c>/<c>month</c>/<c>year</c>/<c>min</c>/<c>tick</c>).</param>
    /// <param name="count">Number of candles to request.</param>
    /// <param name="from">Optional start date (yyyyMMdd).</param>
    /// <param name="to">Optional end date (yyyyMMdd).</param>
    /// <param name="minuteUnit">Minute interval; ignored unless <paramref name="period"/> is <c>min</c>.</param>
    /// <param name="specs">Parsed indicator specs.</param>
    /// <param name="withWarmup">
    /// Summary warm-up policy. <see langword="null"/> auto-applies warm-up when
    /// <paramref name="from"/> is unspecified; <see langword="true"/> forces it
    /// even with explicit <paramref name="from"/>; <see langword="false"/> skips
    /// it even when <paramref name="from"/> is null.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Populated <see cref="FrameResult"/>.</returns>
    static async Task<FrameResult> BuildFrameAsync(
        LsApiClient apiClient,
        string shcode,
        string? name,
        string period,
        int count,
        string? from,
        string? to,
        int minuteUnit,
        IReadOnlyList<IndicatorSpec> specs,
        bool? withWarmup,
        CancellationToken ct)
    {
        // Fetch extra leading candles so (a) long-period / recursive indicators
        // are fully warmed up across the display window, and (b) the analytical
        // summary can see enough history for its long MAs, MA slope, and 1Y/5Y
        // change even when the display window is short. The warm-up lead is
        // trimmed off the display candles below, but indicators are computed —
        // and the summary is built — over the full fetched series first.
        //
        // Default policy: apply summary warm-up iff `from` is unspecified — an
        // explicit `from` is a deliberate narrow window we should not silently
        // widen. `withWarmup` overrides the default (force-on or force-off).
        bool applyWarmup = withWarmup ?? string.IsNullOrWhiteSpace(from);
        int indicatorWarmup = RequiredWarmup(specs);
        int summaryWarmup = applyWarmup ? SummaryWarmup(period) : 0;
        int warmup = Math.Max(indicatorWarmup, summaryWarmup);
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

        // Build the summary over the full warm-up-inclusive series, before the
        // display trim, so its long-period MAs / slope / 1Y change populate
        // regardless of how short `count` is. The display vs analytical bar
        // counts and the warmup flag flow into the summary's IndicatorCoverage
        // so the model can explain any null indicators to the user.
        AnalyticalSummary summary = AnalyticalSummaryBuilder.Build(
            shcode,
            name,
            period,
            candles,
            displayBarCount: Math.Min(candles.Count, count),
            warmupApplied: applyWarmup);

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
        return new FrameResult(period, trCode, candles, indicatorResults, context, summary);
    }

    /// <summary>
    /// Extra leading candles fetched purely so <see cref="AnalyticalSummaryBuilder"/>
    /// can populate its long-period moving averages, MA slope, and 1Y change even
    /// when the display window (<c>count</c>) is short. Capped together with
    /// <c>count</c> by <see cref="MaxFetchCount"/>.
    /// </summary>
    /// <param name="period">Period label.</param>
    /// <returns>Summary warm-up candle count.</returns>
    static int SummaryWarmup(string period) => period switch
    {
        "day" => 240,
        "week" => 120,
        "month" => 120,
        "year" => 10,
        "min" => 200,
        "tick" => 0,
        _ => 120,
    };

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
