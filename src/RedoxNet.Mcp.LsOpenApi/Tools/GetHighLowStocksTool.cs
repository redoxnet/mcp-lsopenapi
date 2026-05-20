using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool returning the 신고가 / 신저가 (new-high / new-low) screener via
/// TR t1442.
/// </summary>
[McpServerToolType]
public static class GetHighLowStocksTool
{
    /// <summary>Maximum normalized rows returned by one tool call.</summary>
    public const int MaxRows = 100;

    const int MaxPages = 5;

    /// <summary>jc_num2 bitmask: ETF (1) + ETN (8) — keeps the screen to ordinary listed stocks.</summary>
    const int ExcludeEtfEtnMask = 9;

    /// <summary>
    /// Returns the t1442 new-high / new-low screen as normalized JSON.
    /// </summary>
    [McpServerTool(Name = "ls_get_high_low_stocks")]
    [Description("""
        Returns Korean stocks at a new high or new low via LS t1442. For each stock: current price, change, volume, and the prior reference price the new high/low was measured against.

        USE WHEN: the user asks for 신고가 / 신저가 stocks ("오늘 신고가 종목", "52주 신저가 뭐 있어?").
        AVOID WHEN: the user wants gainers/losers ranked by percent (use ls_get_top_stocks).

        direction: 'high' (신고가, default) or 'low' (신저가).
        period: look-back window — prev_day, 5d, 10d, 20d, 60d, 90d, '52w' (default), ytd.
        maintained: true (default) = 돌파유지 (still holding the breakout); false = 일시돌파 (touched the high/low but did not hold — these rows can be down on the day).
        market: 'all' (default), 'kospi', 'kosdaq'. exclude_etf (default true) drops ETF / ETN from the result.
        """)]
    public static async Task<string> GetHighLowStocks(
        LsApiClient apiClient,
        [Description("'high' (신고가, default) or 'low' (신저가).")]
        string direction = "high",
        [Description("Look-back window: prev_day, 5d, 10d, 20d, 60d, 90d, 52w (default), ytd.")]
        string period = "52w",
        [Description("true (default) = 돌파유지 (still holding); false = 일시돌파 (touched but not holding).")]
        bool maintained = true,
        [Description("Market filter: all (default), kospi, kosdaq.")]
        string market = "all",
        [Description("Maximum rows to return (1-100). Default 20.")]
        int limit = 20,
        [Description("Drop ETF / ETN from the result (default true).")]
        bool exclude_etf = true,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveDirection(direction, out string type1, out string normalizedDirection))
            return McpJson.Error($"direction '{direction}' is not recognized. Use 'high' or 'low'.");
        if (!TryResolvePeriod(period, out string type2, out string normalizedPeriod))
            return McpJson.Error($"period '{period}' is not recognized. Use prev_day, 5d, 10d, 20d, 60d, 90d, 52w, or ytd.");
        if (!TryResolveMarket(market, out string gubun, out string normalizedMarket))
            return McpJson.Error($"market '{market}' is not recognized. Use all, kospi, or kosdaq.");
        if (limit < 1 || limit > MaxRows)
            return McpJson.Error($"limit must be between 1 and {MaxRows}.", new { received = limit });

        string type3 = maintained ? "1" : "0";
        int jcNum2 = exclude_etf ? ExcludeEtfEtnMask : 0;

        try
        {
            var stocks = new List<HighLowStock>(limit);
            int idx = 0;
            for (int page = 0; page < MaxPages && stocks.Count < limit; page++)
            {
                LsTrResponse response = await apiClient.CallTrAsync(
                    "t1442",
                    new JsonObject
                    {
                        ["gubun"] = gubun,
                        ["type1"] = type1,
                        ["type2"] = type2,
                        ["type3"] = type3,
                        ["jc_num"] = 0,
                        ["sprice"] = 0,
                        ["eprice"] = 0,
                        ["volume"] = 0,
                        ["idx"] = idx,
                        ["jc_num2"] = jcNum2,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccess)
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        direction = normalizedDirection,
                    });

                JsonElement? array = response.GetBlock("t1442OutBlock1");
                if (array is null || array.Value.ValueKind != JsonValueKind.Array)
                    break;

                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    string? sign = row.ReadString("sign");
                    string? pastSign = row.ReadString("pastsign");
                    stocks.Add(new HighLowStock(
                        Rank: stocks.Count + 1,
                        Shcode: row.ReadString("shcode"),
                        Name: row.ReadString("hname")?.Trim(),
                        Price: row.ReadLong("price"),
                        Change: (long)IndustryDataCache.ApplySign(row.ReadLong("change"), sign),
                        ChangePct: IndustryDataCache.ApplySign(row.ReadDouble("diff"), sign),
                        Volume: row.ReadLong("volume"),
                        PastPrice: row.ReadLong("pastprice"),
                        PastChangePct: IndustryDataCache.ApplySign(row.ReadDouble("pastdiff"), pastSign)));
                    if (stocks.Count >= limit)
                        break;
                }

                idx = (int)(response.GetBlock("t1442OutBlock")?.ReadLong("idx") ?? 0);
                if (idx <= 0)
                    break;
            }

            var payload = new HighLowPayload
            {
                Direction = normalizedDirection,
                Period = normalizedPeriod,
                Maintained = maintained,
                Market = normalizedMarket,
                Count = stocks.Count,
                Stocks = stocks,
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

    static bool TryResolveDirection(string? raw, out string type1, out string normalized)
    {
        string lower = (raw ?? "high").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "high" or "신고가" or "신고":
                type1 = "0"; normalized = "high"; return true;
            case "low" or "신저가" or "신저":
                type1 = "1"; normalized = "low"; return true;
            default:
                type1 = ""; normalized = ""; return false;
        }
    }

    static bool TryResolvePeriod(string? raw, out string type2, out string normalized)
    {
        string lower = (raw ?? "52w").Trim().ToLowerInvariant().Replace("-", "_");
        switch (lower)
        {
            case "prev_day" or "prev" or "previous" or "전일":
                type2 = "0"; normalized = "prev_day"; return true;
            case "5d" or "5":
                type2 = "1"; normalized = "5d"; return true;
            case "10d" or "10":
                type2 = "2"; normalized = "10d"; return true;
            case "20d" or "20":
                type2 = "3"; normalized = "20d"; return true;
            case "60d" or "60":
                type2 = "4"; normalized = "60d"; return true;
            case "90d" or "90":
                type2 = "5"; normalized = "90d"; return true;
            case "" or "52w" or "52week" or "52주":
                type2 = "6"; normalized = "52w"; return true;
            case "ytd" or "year" or "년중":
                type2 = "7"; normalized = "ytd"; return true;
            default:
                type2 = ""; normalized = ""; return false;
        }
    }

    static bool TryResolveMarket(string? raw, out string gubun, out string normalized)
    {
        string lower = (raw ?? "all").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "all" or "전체":
                gubun = "0"; normalized = "all"; return true;
            case "kospi" or "코스피":
                gubun = "1"; normalized = "kospi"; return true;
            case "kosdaq" or "코스닥":
                gubun = "2"; normalized = "kosdaq"; return true;
            default:
                gubun = ""; normalized = ""; return false;
        }
    }

    sealed record HighLowPayload
    {
        public string Direction { get; init; } = "";
        public string Period { get; init; } = "";
        public bool Maintained { get; init; }
        public string Market { get; init; } = "";
        public int Count { get; init; }
        public IReadOnlyList<HighLowStock> Stocks { get; init; } = Array.Empty<HighLowStock>();
    }

    sealed record HighLowStock(
        int Rank,
        string? Shcode,
        string? Name,
        long Price,
        long Change,
        double ChangePct,
        long Volume,
        long PastPrice,
        double PastChangePct);
}
