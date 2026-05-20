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
/// </remarks>
[McpServerToolType]
public static class GetIndexHistoryTool
{
    /// <summary>Upper bound on candle count per request. LS caps a single t1514 page well above this; we cap to keep payload small.</summary>
    const int MaxCount = 500;

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

        verbosity controls payload size:
        - 'summary' (default): only the aggregate summary block, no per-bar points. Cheapest — covers most trend questions.
        - 'compact': summary block + the 5 most recent bars only. Digest plus recent context.
        - 'full': every per-bar point, no summary block (the pre-v0.9, v0.8-compatible shape).

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
        [Description("Number of bars to return, 1–500. Default 60.")]
        int count = 60,
        [Description("Optional pagination cursor. Pass the cts_date echoed by a prior response to fetch the next (older) page.")]
        string? cts_date = null,
        [Description("Response shape: 'summary' (default: digest only), 'compact' (summary + points), or 'full' (points only).")]
        string verbosity = "summary",
        CancellationToken cancellationToken = default)
    {
        string upcode = GetIndexQuoteTool.NormalizeIndexCode(index_code);
        if (string.IsNullOrEmpty(upcode))
            return McpJson.Error($"index_code '{index_code}' is not recognized. Use one of: kospi, kosdaq, kospi200, krx100, or a 3-character upcode.");

        if (!TryResolvePeriod(period_type, out string gubun2, out string normalizedPeriod))
            return McpJson.Error($"period_type '{period_type}' is not recognized. Use 'day', 'week', or 'month'.");

        if (count < 1 || count > MaxCount)
            return McpJson.Error($"count must be between 1 and {MaxCount}.", new { received = count });

        if (!ResponseShape.TryParseVerbosity(verbosity, VerbosityMode.Summary, out VerbosityMode mode))
            return McpJson.Error($"verbosity '{verbosity}' is not recognized. Use 'summary', 'compact', or 'full'.");

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

            // §4.2: summary is present unless 'full'. Points depend on mode —
            // 'summary' omits them, 'compact' keeps only the most recent bars,
            // 'full' keeps all (the v0.8-compatible shape). An empty result
            // always emits points[] so the caller sees the empty set.
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
