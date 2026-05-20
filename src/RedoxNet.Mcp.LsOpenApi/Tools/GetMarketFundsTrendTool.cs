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
/// MCP tool returning the market-funds trend — investor deposits, credit
/// balance, receivables, and fund money — via TR t8428.
/// </summary>
[McpServerToolType]
public static class GetMarketFundsTrendTool
{
    const int MaxCount = 120;
    const int DefaultCount = 20;

    /// <summary>
    /// Returns the t8428 market-funds series wrapped in a normalized envelope.
    /// </summary>
    [McpServerTool(Name = "ls_get_market_funds_trend")]
    [Description("""
        Returns the Korean stock-market liquidity ("증시 주변 자금") trend via LS t8428: per day the index, customer deposits (고객예탁금), deposit change, credit balance (신용잔고), receivables (미수금), futures deposits, and fund money split across equity / mixed / bond / MMF buckets.

        USE WHEN: the user asks about market liquidity, 고객예탁금, 신용잔고, 미수금, 대기자금, or fund flows ("요즘 예탁금 추이", "신용잔고 늘었어?").
        AVOID WHEN: the user wants per-stock investor flow (use ls_get_investor_flow) or an index time series (use ls_get_index_history).

        `market` selects the index context: 'kospi' (default) or 'kosdaq'. All monetary fields are in 억원.
        """)]
    public static async Task<string> GetMarketFundsTrend(
        LsApiClient apiClient,
        [Description("Index context: 'kospi' (default) or 'kosdaq'.")]
        string market = "kospi",
        [Description("Trading days to return (1-120). Default 20.")]
        int count = DefaultCount,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveMarket(market, out string upcode, out string normalizedMarket))
            return McpJson.Error($"market '{market}' is not recognized. Use 'kospi' or 'kosdaq'.");
        if (count < 1 || count > MaxCount)
            return McpJson.Error($"count must be between 1 and {MaxCount}.", new { received = count });

        string tdate = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        // t8428 returns trading days only; pad the calendar window so weekends
        // and holidays don't truncate the requested count.
        int padded = (int)Math.Ceiling(count * 1.6) + 5;
        string fdate = DateTime.Now.Date.AddDays(-padded).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t8428",
                new JsonObject
                {
                    ["fdate"] = fdate,
                    ["tdate"] = tdate,
                    ["gubun"] = "1",
                    ["key_date"] = " ",
                    ["upcode"] = upcode,
                    ["cnt"] = count,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    market = normalizedMarket,
                });

            JsonElement? array = response.GetBlock("t8428OutBlock1");
            var series = new List<MarketFundsPoint>();
            if (array is not null && array.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    string date = row.ReadString("date")?.Trim() ?? "";
                    if (date.Length == 0)
                        continue;
                    if (series.Count >= count)
                        break;
                    string? sign = row.ReadString("sign");
                    series.Add(new MarketFundsPoint(
                        Date: date,
                        Index: row.ReadDouble("jisu"),
                        ChangePct: IndustryDataCache.ApplySign(row.ReadDouble("diff"), sign),
                        InvestorDeposit: row.ReadLong("custmoney"),
                        DepositChange: row.ReadLong("yecha"),
                        CreditBalance: row.ReadLong("trjango"),
                        Receivable: row.ReadLong("outmoney"),
                        FuturesDeposit: row.ReadLong("futymoney"),
                        EquityFund: row.ReadLong("stkmoney"),
                        MixedEquityFund: row.ReadLong("mstkmoney"),
                        MixedBondFund: row.ReadLong("mbndmoney"),
                        BondFund: row.ReadLong("bndmoney"),
                        Mmf: row.ReadLong("mmfmoney")));
                }
            }

            var payload = new MarketFundsPayload
            {
                Market = normalizedMarket,
                From = series.Count > 0 ? series[^1].Date : fdate,
                To = series.Count > 0 ? series[0].Date : tdate,
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

    static MarketFundsSummary? BuildSummary(IReadOnlyList<MarketFundsPoint> series)
    {
        if (series.Count == 0)
            return null;
        // series[0] = most recent row, series[^1] = oldest in the window.
        MarketFundsPoint latest = series[0];
        MarketFundsPoint oldest = series[^1];
        return new MarketFundsSummary(
            InvestorDepositChange: latest.InvestorDeposit - oldest.InvestorDeposit,
            CreditBalanceChange: latest.CreditBalance - oldest.CreditBalance);
    }

    static bool TryResolveMarket(string? raw, out string upcode, out string normalized)
    {
        string lower = (raw ?? "kospi").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "kospi" or "kp" or "코스피":
                upcode = "001"; normalized = "kospi"; return true;
            case "kosdaq" or "kd" or "코스닥":
                upcode = "301"; normalized = "kosdaq"; return true;
            default:
                upcode = ""; normalized = ""; return false;
        }
    }

    sealed record MarketFundsPayload
    {
        public string Market { get; init; } = "";
        public string From { get; init; } = "";
        public string To { get; init; } = "";
        public int Count { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public MarketFundsSummary? Summary { get; init; }

        public IReadOnlyList<MarketFundsPoint> Series { get; init; } = Array.Empty<MarketFundsPoint>();
    }

    sealed record MarketFundsSummary(long InvestorDepositChange, long CreditBalanceChange);

    sealed record MarketFundsPoint(
        string Date,
        double Index,
        double ChangePct,
        long InvestorDeposit,
        long DepositChange,
        long CreditBalance,
        long Receivable,
        long FuturesDeposit,
        long EquityFund,
        long MixedEquityFund,
        long MixedBondFund,
        long BondFund,
        long Mmf);
}
