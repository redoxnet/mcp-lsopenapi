using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool returning a daily/weekly/monthly time series for a Korean index
/// (KOSPI / KOSDAQ / KOSPI200 / KRX100 / sector indices) via TR t1514.
/// </summary>
/// <remarks>
/// v0.9 response-shape SPEC §4.2 (pattern B): emits a <c>summary</c> aggregate
/// block and a <c>verbosity</c> axis so a 60-bar series can collapse to a
/// ~400-token digest without the model having to read every bar.
/// <para>
/// v0.10 SPEC §5.3 (pattern C): an <c>output_mode="export"</c> caches the whole
/// series under a <c>dataset_id</c> (kind-tagged <see cref="DatasetHandleCache"/>)
/// and returns only the digest + handle; a follow-up call with that
/// <c>dataset_id</c> drills the cached bars with no further API call.
/// </para>
/// </remarks>
[McpServerToolType]
public static class GetIndexHistoryTool
{
    /// <summary>Upper bound on candle count for output_mode="summary". LS caps a single t1514 page well above this; we cap to keep the inline payload small.</summary>
    const int MaxCount = 500;

    /// <summary>Upper bound on candle count for output_mode="export" — bars are cached, not returned inline, so a multi-year series is allowed.</summary>
    const int MaxExportCount = 2500;

    /// <summary>Bars kept in 'compact' verbosity — a recent window layered on top of the summary digest.</summary>
    const int CompactTailBars = 5;

    /// <summary>
    /// Returns the t1514 series wrapped in a normalized envelope.
    /// </summary>
    [McpServerTool(Name = "ls_get_index_history")]
    [Description("""
        Returns a daily / weekly / monthly time series for a Korean market index: closing value, change vs. previous bar, OHLC, volume + transaction value, market breadth, and per-bar foreign / institutional net flow.

        USE WHEN: the user asks for "코스피 최근 한 달 추이", "KOSDAQ 일봉", "KRX 100 주간 흐름" or any index time-series question.
        AVOID WHEN: the user wants stock candles (use ls_get_chart), or a single live snapshot of one index (use ls_get_index_quote).

        index_code aliases: kospi→001, kosdaq→301, kospi200→101, krx100→501. Numeric 3-char codes pass through.

        period_type semantics mirror t1514.gubun2: 'day' (=1), 'week' (=2), 'month' (=3). 'min' is intentionally not supported — minute-level index time series belong to a separate roadmap item.

        verbosity controls payload size — keep the default; widen it only when the user truly needs every bar:
        - 'summary' (default): the aggregate digest only — period open/close, total change, period high/low with dates, biggest up/down days, average breadth & flows. This ALREADY answers trend / 추세 / 흐름 / range / "how much did it move" questions. Do NOT request bars for those.
        - 'compact': the digest + the 5 most recent bars. Use only when the user asks specifically about the most recent days.
        - 'full': every per-bar point, no digest — roughly 30× the summary's token cost, and the whole series then lingers in conversation context for every later turn. Use ONLY when the user explicitly wants the entire per-bar series or needs a custom per-bar calculation the digest cannot give.

        output_mode controls whether the series is returned inline or cached for drill-down:
        - 'summary' (default): the response is shaped by `verbosity` above.
        - 'export': fetches up to `count` bars (raise `count` toward 2500 for a multi-year series), caches the whole series under a `dataset_id`, and returns ONLY the aggregate digest + that handle — no per-bar points enter context. Then call ls_get_index_history again with that `dataset_id` (plus optional `from` / `to` / `recent_n`) to slice the cached bars with no further API call. Use 'export' for long series — e.g. 5+ years of daily bars — where even verbosity='full' would be too large.

        Envelope:
        - summary aggregates the period: open/close, total change, period high/low, biggest up/down bars, average breadth, total flows. summary._meta carries the breadth and flow units.
        - points[].change / change_pct are signed (LS ships unsigned magnitudes + a separate sign code; the wrapper applies the sign).
        - cts_date is emitted only when LS reports more pages — pass it back as the cts_date argument to fetch the next page.
        - flows.foreign_net / institution_net are net buy in 천주 units, matching the t1514.frgsvolume / orgsvolume conventions.
        - breadth: advance + decline + unchanged ≈ total; limit_up / limit_down are subsets of advance / decline.
        """)]
    public static async Task<string> GetIndexHistory(
        LsApiClient apiClient,
        [Description("Index code. Aliases: 'kospi' (001), 'kosdaq' (301), 'kospi200' (101), 'krx100' (501). Or a 3-character LS upcode like '002'.")]
        string index_code = "kospi",
        [Description("Bar size: 'day' (default), 'week', or 'month'.")]
        string period_type = "day",
        [Description("Number of bars to fetch. 1–500 for output_mode='summary'; 1–2500 for 'export' (raise it for a multi-year series). Default 60.")]
        int count = 60,
        [Description("Optional pagination cursor. Pass the cts_date echoed by a prior response to fetch the next (older) page.")]
        string? cts_date = null,
        [Description("Response shape. 'summary' (default) is a digest that already answers trend / range questions — prefer it. 'compact' adds the 5 most recent bars. 'full' returns every bar (~30x the tokens, and it lingers in context) — only when the user explicitly needs the whole per-bar series.")]
        string verbosity = "summary",
        [Description("'summary' (default) returns the series inline per verbosity. 'export' caches the full series under a dataset_id and returns only the digest + handle for follow-up drill.")]
        string output_mode = "summary",
        [Description("Drill mode: pass a dataset_id from a prior output_mode='export' call to slice its cached bars with no API call. When set, index_code / period_type / count / cts_date / verbosity / output_mode are all ignored.")]
        string? dataset_id = null,
        [Description("Drill mode only: keep cached bars on or after this yyyyMMdd date.")]
        string? from = null,
        [Description("Drill mode only: keep cached bars on or before this yyyyMMdd date.")]
        string? to = null,
        [Description("Drill mode only: after from/to filtering, keep only the most recent N bars.")]
        int? recent_n = null,
        CancellationToken cancellationToken = default)
    {
        // Drill path: a dataset_id slices a previously exported series with no API call.
        if (!string.IsNullOrWhiteSpace(dataset_id))
            return DrillCachedDataset(dataset_id!.Trim(), from, to, recent_n);

        string upcode = GetIndexQuoteTool.NormalizeIndexCode(index_code);
        if (string.IsNullOrEmpty(upcode))
            return McpJson.Error($"index_code '{index_code}' is not recognized. Use one of: kospi, kosdaq, kospi200, krx100, or a 3-character upcode.");

        if (!TryResolvePeriod(period_type, out string gubun2, out string normalizedPeriod))
            return McpJson.Error($"period_type '{period_type}' is not recognized. Use 'day', 'week', or 'month'.");

        if (!ResponseShape.TryParseVerbosity(verbosity, VerbosityMode.Summary, out VerbosityMode mode))
            return McpJson.Error($"verbosity '{verbosity}' is not recognized. Use 'summary', 'compact', or 'full'.");

        string normalizedMode = (output_mode ?? "summary").Trim().ToLowerInvariant();
        if (normalizedMode is not ("summary" or "export"))
            return McpJson.Error($"output_mode '{output_mode}' is not recognized. Use 'summary' or 'export'.");
        bool isExport = normalizedMode == "export";

        int maxCount = isExport ? MaxExportCount : MaxCount;
        if (count < 1 || count > maxCount)
            return McpJson.Error($"count must be between 1 and {maxCount} for output_mode='{normalizedMode}'.", new { received = count });

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1514",
                new JsonObject
                {
                    ["upcode"] = upcode,
                    ["gubun1"] = " ",
                    ["gubun2"] = gubun2,
                    ["cts_date"] = string.IsNullOrWhiteSpace(cts_date) ? " " : cts_date.Trim(),
                    ["cnt"] = count,
                    ["rate_gbn"] = "1",
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    requested_code = upcode,
                });

            JsonElement? summaryBlock = response.GetBlock("t1514OutBlock");
            string? nextCts = summaryBlock?.ReadString("cts_date")?.Trim();
            if (string.IsNullOrEmpty(nextCts))
                nextCts = null;

            JsonElement? array = response.GetBlock("t1514OutBlock1");
            var points = new List<IndexHistoryPoint>(count);
            if (array is not null && array.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    string date = row.ReadString("date")?.Trim() ?? "";
                    if (string.IsNullOrEmpty(date))
                        continue;
                    string? sign = row.ReadString("sign");
                    double rawChange = row.ReadDouble("change");
                    double rawPct = row.ReadDouble("diff");
                    points.Add(new IndexHistoryPoint(
                        Date: date,
                        Close: row.ReadDouble("jisu"),
                        Change: IndustryDataCache.ApplySign(rawChange, sign),
                        ChangePct: IndustryDataCache.ApplySign(rawPct, sign),
                        Open: row.ReadDouble("openjisu"),
                        High: row.ReadDouble("highjisu"),
                        Low: row.ReadDouble("lowjisu"),
                        Volume: row.ReadLong("volume"),
                        // t1514 ships both value1 and value2; the guide labels them identically
                        // (거래대금1 / 거래대금2) and observed responses report the same number,
                        // so we surface only one to keep the envelope compact.
                        Value: row.ReadLong("value1"),
                        Breadth: new HistoryBreadth(
                            Advance: row.ReadLong("high"),
                            Decline: row.ReadLong("low"),
                            Unchanged: row.ReadLong("unchg"),
                            LimitUp: row.ReadLong("up"),
                            LimitDown: row.ReadLong("down")),
                        Flows: new HistoryFlows(
                            ForeignNet: row.ReadLong("frgsvolume"),
                            InstitutionNet: row.ReadLong("orgsvolume"))));
                }
            }

            // §5.3 export: cache the whole series, return only the digest + handle.
            if (isExport)
                return BuildExportResponse(upcode, normalizedPeriod, points, nextCts);

            // §4.2 summary: summary is present unless 'full'. Points depend on
            // mode — 'summary' omits them, 'compact' keeps only the most recent
            // bars, 'full' keeps all (the v0.8-compatible shape). An empty
            // result always emits points[] so the caller sees the empty set.
            bool emitSummary = mode != VerbosityMode.Full && points.Count > 0;
            IReadOnlyList<IndexHistoryPoint>? emittedPoints = (mode, points.Count) switch
            {
                (VerbosityMode.Summary, > 0) => null,
                (VerbosityMode.Compact, > 0) => points
                    .OrderBy(p => p.Date, StringComparer.Ordinal)
                    .TakeLast(CompactTailBars)
                    .ToList(),
                _ => points,
            };

            var payload = new IndexHistoryPayload
            {
                IndexCode = upcode,
                PeriodType = normalizedPeriod,
                Verbosity = mode.ToWire(),
                Count = points.Count,
                Summary = emitSummary ? BuildSummary(points) : null,
                Points = emittedPoints,
                CtsDate = nextCts,
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
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

    /// <summary>
    /// §5.3 export: caches the whole series under a kind="index_history" handle
    /// and returns only the aggregate digest + that handle.
    /// </summary>
    static string BuildExportResponse(string upcode, string period, List<IndexHistoryPoint> points, string? nextCts)
    {
        // Store ascending by date so the drill slicer can range-filter directly.
        List<IndexHistoryPoint> ascending = points.OrderBy(p => p.Date, StringComparer.Ordinal).ToList();
        string datasetId = DatasetHandleCache.Add(
            "index_history",
            new IndexHistoryDataset(upcode, period, ascending, DateTimeOffset.UtcNow));

        var payload = new IndexHistoryExportPayload
        {
            IndexCode = upcode,
            PeriodType = period,
            DatasetId = datasetId,
            Count = ascending.Count,
            Summary = ascending.Count > 0 ? BuildSummary(ascending) : null,
            CtsDate = nextCts,
        };
        return JsonSerializer.Serialize(payload, McpJson.Tool);
    }

    /// <summary>
    /// §5.3 drill: resolves an exported dataset and slices the cached bars by
    /// <paramref name="from"/> / <paramref name="to"/> / <paramref name="recentN"/>
    /// with no LS call. Self-contained — not wired to the chart indicator pipeline.
    /// </summary>
    static string DrillCachedDataset(string datasetId, string? from, string? to, int? recentN)
    {
        if (!DatasetHandleCache.TryGet<IndexHistoryDataset>(datasetId, out IndexHistoryDataset? ds) || ds is null)
            return McpJson.Error("Unknown or expired dataset_id.", new { dataset_id = datasetId });

        string? fromKey = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        string? toKey = string.IsNullOrWhiteSpace(to) ? null : to.Trim();

        // Dates are yyyyMMdd, so an ordinal string compare is chronological.
        IEnumerable<IndexHistoryPoint> query = ds.Points;
        if (fromKey is not null)
            query = query.Where(p => string.CompareOrdinal(p.Date, fromKey) >= 0);
        if (toKey is not null)
            query = query.Where(p => string.CompareOrdinal(p.Date, toKey) <= 0);
        List<IndexHistoryPoint> sliced = query.ToList();

        int? appliedRecentN = recentN is > 0 ? recentN : null;
        if (appliedRecentN is not null && sliced.Count > appliedRecentN.Value)
            sliced = sliced.Skip(sliced.Count - appliedRecentN.Value).ToList();

        var payload = new IndexHistoryDrillPayload
        {
            IndexCode = ds.IndexCode,
            PeriodType = ds.PeriodType,
            DatasetId = datasetId,
            From = fromKey,
            To = toKey,
            RecentN = appliedRecentN,
            Count = sliced.Count,
            Summary = sliced.Count > 0 ? BuildSummary(sliced) : null,
            Points = sliced,
        };
        return JsonSerializer.Serialize(payload, McpJson.Tool);
    }

    /// <summary>Builds the §4.2 aggregate block from a non-empty point list.</summary>
    static IndexHistorySummary BuildSummary(IReadOnlyList<IndexHistoryPoint> pts)
    {
        // Dates are yyyyMMdd, so an ordinal string sort is chronological.
        List<IndexHistoryPoint> byDate = pts.OrderBy(p => p.Date, StringComparer.Ordinal).ToList();
        IndexHistoryPoint first = byDate[0];
        IndexHistoryPoint last = byDate[^1];
        IndexHistoryPoint hi = pts.MaxBy(p => p.High)!;
        IndexHistoryPoint lo = pts.MinBy(p => p.Low)!;
        IndexHistoryPoint up = pts.MaxBy(p => p.ChangePct)!;
        IndexHistoryPoint down = pts.MinBy(p => p.ChangePct)!;

        double open = first.Open;
        double close = last.Close;
        double changePctTotal = open != 0 ? Math.Round((close - open) / open * 100.0, 2) : 0.0;

        return new IndexHistorySummary(
            Period: new SummaryPeriod(first.Date, last.Date, pts.Count),
            Open: open,
            Close: close,
            ChangeTotal: Math.Round(close - open, 2),
            ChangePctTotal: changePctTotal,
            High: new DateValue(hi.Date, hi.High),
            Low: new DateValue(lo.Date, lo.Low),
            BiggestUp: new DateChangePct(up.Date, up.ChangePct),
            BiggestDown: new DateChangePct(down.Date, down.ChangePct),
            BreadthAvg: new SummaryBreadth(
                Advance: (long)Math.Round(pts.Average(p => (double)p.Breadth.Advance)),
                Decline: (long)Math.Round(pts.Average(p => (double)p.Breadth.Decline)),
                Unchanged: (long)Math.Round(pts.Average(p => (double)p.Breadth.Unchanged))),
            FlowsTotal: new HistoryFlows(
                ForeignNet: pts.Sum(p => p.Flows.ForeignNet),
                InstitutionNet: pts.Sum(p => p.Flows.InstitutionNet)),
            Meta: new SummaryMeta("stocks_per_day", "thousand_shares"));
    }

    static bool TryResolvePeriod(string? raw, out string gubun2, out string normalized)
    {
        string lower = (raw ?? "day").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "day" or "d" or "일" or "일봉":
                gubun2 = "1"; normalized = "day"; return true;
            case "week" or "w" or "주" or "주봉":
                gubun2 = "2"; normalized = "week"; return true;
            case "month" or "m" or "월" or "월봉":
                gubun2 = "3"; normalized = "month"; return true;
            default:
                gubun2 = ""; normalized = ""; return false;
        }
    }

    sealed record IndexHistoryPayload
    {
        public string IndexCode { get; init; } = "";
        public string PeriodType { get; init; } = "";
        public string Verbosity { get; init; } = "";
        public int Count { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IndexHistorySummary? Summary { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<IndexHistoryPoint>? Points { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CtsDate { get; init; }
    }

    /// <summary>output_mode="export" envelope — digest + dataset handle, no per-bar points.</summary>
    sealed record IndexHistoryExportPayload
    {
        public string IndexCode { get; init; } = "";
        public string PeriodType { get; init; } = "";
        public string OutputMode => "export";
        public string DatasetId { get; init; } = "";
        public int Count { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IndexHistorySummary? Summary { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CtsDate { get; init; }

        public string DrillHint =>
            "Pass dataset_id back to ls_get_index_history with from / to / recent_n to slice these cached bars without another API call.";
    }

    /// <summary>Drill envelope — the sliced cached bars plus a digest of the slice.</summary>
    sealed record IndexHistoryDrillPayload
    {
        public string IndexCode { get; init; } = "";
        public string PeriodType { get; init; } = "";
        public string DatasetId { get; init; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? From { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? To { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RecentN { get; init; }

        public int Count { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IndexHistorySummary? Summary { get; init; }

        public IReadOnlyList<IndexHistoryPoint> Points { get; init; } = Array.Empty<IndexHistoryPoint>();
    }

    /// <summary>
    /// Cached payload behind an export <c>dataset_id</c> — the whole bar series
    /// (ascending by date) plus the index metadata needed to echo it on a drill.
    /// </summary>
    sealed record IndexHistoryDataset(
        string IndexCode,
        string PeriodType,
        IReadOnlyList<IndexHistoryPoint> Points,
        DateTimeOffset CreatedAtUtc);

    sealed record IndexHistorySummary(
        SummaryPeriod Period,
        double Open,
        double Close,
        double ChangeTotal,
        double ChangePctTotal,
        DateValue High,
        DateValue Low,
        DateChangePct BiggestUp,
        DateChangePct BiggestDown,
        SummaryBreadth BreadthAvg,
        HistoryFlows FlowsTotal,
        [property: JsonPropertyName("_meta")] SummaryMeta Meta);

    sealed record SummaryPeriod(string From, string To, int TradingDays);
    sealed record DateValue(string Date, double Value);
    sealed record DateChangePct(string Date, double ChangePct);
    sealed record SummaryBreadth(long Advance, long Decline, long Unchanged);
    sealed record SummaryMeta(string BreadthUnit, string FlowsUnit);

    sealed record IndexHistoryPoint(
        string Date,
        double Close,
        double Change,
        double ChangePct,
        double Open,
        double High,
        double Low,
        long Volume,
        long Value,
        HistoryBreadth Breadth,
        HistoryFlows Flows);

    sealed record HistoryBreadth(long Advance, long Decline, long Unchanged, long LimitUp, long LimitDown);
    sealed record HistoryFlows(long ForeignNet, long InstitutionNet);
}
