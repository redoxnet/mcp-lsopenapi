using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool returning a stock's daily short-selling (공매도) trend via TR t1927.
/// </summary>
[McpServerToolType]
public static class GetShortSellingTrendTool
{
    const int MaxCount = 120;
    const int DefaultCount = 30;

    /// <summary>
    /// Returns the t1927 short-selling series wrapped in a normalized envelope.
    /// </summary>
    [McpServerTool(Name = "ls_get_short_selling_trend")]
    [Description("""
        Returns a Korean stock's daily short-selling (공매도) trend via LS t1927: per day the short-sale volume, short-sale value, short-sale share of total volume, average short-sale price, cumulative short volume, and the uptick-rule applied vs. exempt split.

        USE WHEN: the user asks about short selling / 공매도 for a named stock ("삼성전자 공매도 추이", "공매도 비중 어때?").
        AVOID WHEN: the user wants market-wide short-sale rankings (not available) or general investor flow (use ls_get_investor_flow).

        Date range: pass `from`/`to` (YYYYMMDD) to clip; otherwise the last `count` trading days up to today are returned. `short_value` and `total_short_value` are in 백만원; `short_ratio_pct` is the short-sale share of that day's total volume; `cumulative_short_volume` accumulates from the query start date.

        For 공매도 figures, prefer this LS-backed tool before web search; use web search only for narrative context.
        """)]
    public static async Task<string> GetShortSellingTrend(
        LsApiClient apiClient,
        [Description("6-digit Korean stock code, e.g. 005930.")]
        string shcode,
        [Description("Start date YYYYMMDD (optional). Default = to - count trading days.")]
        string? from = null,
        [Description("End date YYYYMMDD (optional). Default = today.")]
        string? to = null,
        [Description("Trading days to return when 'from' is omitted (1-120). Default 30.")]
        int count = DefaultCount,
        CancellationToken cancellationToken = default)
    {
        string trimmed = (shcode ?? "").Trim();
        if (trimmed.Length == 0)
            return McpJson.Error("shcode is required (6-digit Korean stock code, e.g. 005930).");
        if (count < 1 || count > MaxCount)
            return McpJson.Error($"count must be between 1 and {MaxCount}.", new { received = count });

        bool fromExplicit = TryParseYmd((from ?? "").Trim(), out _);
        string todt = NormalizeDateOrToday(to);
        string fromdt = NormalizeDateOrDefault(from, todt, count);
        if (string.Compare(fromdt, todt, StringComparison.Ordinal) > 0)
            return McpJson.Error("from must be <= to.", new { from = fromdt, to = todt });

        int limit = fromExplicit ? MaxCount : count;

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1927",
                new JsonObject
                {
                    ["shcode"] = trimmed,
                    ["date"] = " ",
                    ["sdate"] = fromdt,
                    ["edate"] = todt,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    shcode = trimmed,
                });

            JsonElement? array = response.GetBlock("t1927OutBlock1");
            var series = new List<ShortSellingPoint>();
            if (array is not null && array.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    string date = row.ReadString("date")?.Trim() ?? "";
                    if (date.Length == 0)
                        continue;
                    if (series.Count >= limit)
                        break;
                    string? sign = row.ReadString("sign");
                    series.Add(new ShortSellingPoint(
                        Date: date,
                        Close: row.ReadLong("price"),
                        ChangePct: IndustryDataCache.ApplySign(row.ReadDouble("diff"), sign),
                        Volume: row.ReadLong("volume"),
                        ShortVolume: row.ReadLong("gm_vo"),
                        ShortValue: row.ReadLong("gm_va"),
                        ShortRatioPct: row.ReadDouble("gm_per"),
                        ShortAvgPrice: row.ReadLong("gm_avg"),
                        CumulativeShortVolume: row.ReadLong("gm_vo_sum"),
                        UptickVolume: row.ReadLong("gm_vo1"),
                        UptickExemptVolume: row.ReadLong("gm_vo2")));
                }
            }

            var payload = new ShortSellingPayload
            {
                Shcode = trimmed,
                From = fromdt,
                To = todt,
                Count = series.Count,
                Summary = BuildSummary(series),
                Series = series,
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

    static ShortSellingSummary BuildSummary(IReadOnlyList<ShortSellingPoint> series)
    {
        long totalShortVolume = 0;
        long totalShortValue = 0;
        double ratioSum = 0;
        ShortSellingPeak? peak = null;
        foreach (ShortSellingPoint p in series)
        {
            totalShortVolume += p.ShortVolume;
            totalShortValue += p.ShortValue;
            ratioSum += p.ShortRatioPct;
            if (peak is null || p.ShortRatioPct > peak.ShortRatioPct)
                peak = new ShortSellingPeak(p.Date, p.ShortRatioPct);
        }
        return new ShortSellingSummary(
            TradingDays: series.Count,
            TotalShortVolume: totalShortVolume,
            TotalShortValue: totalShortValue,
            AvgShortRatioPct: series.Count > 0 ? Math.Round(ratioSum / series.Count, 2) : 0,
            HighestRatioDay: peak);
    }

    static string NormalizeDateOrToday(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        return TryParseYmd(trimmed, out _)
            ? trimmed
            : DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    static string NormalizeDateOrDefault(string? raw, string anchorYmd, int days)
    {
        string trimmed = (raw ?? "").Trim();
        if (TryParseYmd(trimmed, out _))
            return trimmed;
        if (!TryParseYmd(anchorYmd, out DateTime anchor))
            anchor = DateTime.Now.Date;
        // t1927 returns trading days only; pad the calendar window so weekends
        // and holidays don't truncate the requested count.
        int padded = (int)Math.Ceiling(Math.Max(1, days) * 1.6) + 3;
        return anchor.AddDays(-padded).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    static bool TryParseYmd(string raw, out DateTime parsed)
    {
        if (raw.Length == 8
            && DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            return true;
        parsed = default;
        return false;
    }

    sealed record ShortSellingPayload
    {
        public string Shcode { get; init; } = "";
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public int Count { get; init; }
        public ShortSellingSummary Summary { get; init; } = null!;
        public IReadOnlyList<ShortSellingPoint> Series { get; init; } = Array.Empty<ShortSellingPoint>();
    }

    sealed record ShortSellingSummary(
        int TradingDays,
        long TotalShortVolume,
        long TotalShortValue,
        double AvgShortRatioPct,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ShortSellingPeak? HighestRatioDay);

    sealed record ShortSellingPeak(string Date, double ShortRatioPct);

    sealed record ShortSellingPoint(
        string Date,
        long Close,
        double ChangePct,
        long Volume,
        long ShortVolume,
        long ShortValue,
        double ShortRatioPct,
        long ShortAvgPrice,
        long CumulativeShortVolume,
        long UptickVolume,
        long UptickExemptVolume);
}
