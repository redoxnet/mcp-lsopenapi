using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool returning a daily/weekly/monthly time series for a Korean index
/// (KOSPI / KOSDAQ / KOSPI200 / KRX100 / sector indices) via TR t1514.
/// </summary>
/// <remarks>
/// v0.7 A2: thin wrapper. Dataset-handle integration (so <c>ls_add_indicator</c>
/// / <c>ls_reframe_chart</c> can pipe off this series) is deferred to v0.8.
/// </remarks>
[McpServerToolType]
public static class GetIndexHistoryTool
{
    /// <summary>Upper bound on candle count per request. LS caps a single t1514 page well above this; we cap to keep payload small.</summary>
    const int MaxCount = 500;

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

        Envelope:
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
        CancellationToken cancellationToken = default)
    {
        string upcode = GetIndexQuoteTool.NormalizeIndexCode(index_code);
        if (string.IsNullOrEmpty(upcode))
            return McpJson.Error($"index_code '{index_code}' is not recognized. Use one of: kospi, kosdaq, kospi200, krx100, or a 3-character upcode.");

        if (!TryResolvePeriod(period_type, out string gubun2, out string normalizedPeriod))
            return McpJson.Error($"period_type '{period_type}' is not recognized. Use 'day', 'week', or 'month'.");

        if (count < 1 || count > MaxCount)
            return McpJson.Error($"count must be between 1 and {MaxCount}.", new { received = count });

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

            var payload = new IndexHistoryPayload
            {
                IndexCode = upcode,
                PeriodType = normalizedPeriod,
                Count = points.Count,
                Points = points,
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
        public int Count { get; init; }
        public IReadOnlyList<IndexHistoryPoint> Points { get; init; } = Array.Empty<IndexHistoryPoint>();

        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? CtsDate { get; init; }
    }

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
