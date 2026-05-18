using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP wrapper for t3341 — fundamental-metric ranking across KOSPI / KOSDAQ.
/// </summary>
[McpServerToolType]
public static class GetFundamentalsRankTool
{
    /// <summary>Cap on aggregated rows; one t3341 page is ~100 rows, so two pages cover the common 30 / 50 / 100 sizes without paying for a third call.</summary>
    const int MaxCount = 200;

    /// <summary>Defensive cap on continuation calls — every page costs one LS request at 1 TPS.</summary>
    const int MaxPages = 4;

    [McpServerTool(Name = "ls_get_fundamentals_rank")]
    [Description("""
        Returns a fundamental-metric ranking of Korean stocks via t3341 (재무순위종합). One TR call covers all metrics — pass the metric name as `field`.

        Supported `field` values (LS-side ordering rules in parentheses):
        - "per" (forced ascending — undervalued first)
        - "pbr" (forced ascending)
        - "peg" (forced ascending)
        - "eps", "bps", "roe" (LS default — typically descending)
        - "sales_growth", "operating_income_growth", "ordinary_income_growth" (LS default)
        - "debt_to_equity" (부채비율), "retained_earnings_ratio" (유보율)

        USE WHEN: the user asks for screener-style ranking — "PER 낮은 종목", "ROE 상위 30", "코스닥 영업이익 증가율 1위" — without specifying individual symbols.
        AVOID WHEN: the user already has a single symbol in mind (use ls_get_stock_info for that symbol's fundamentals instead).

        market: 'both' (default) / 'kospi' / 'kosdaq'.
        count: number of rows to return (1 - 200, default 30). The wrapper pages through t3341 as needed.

        Each row echoes the full fundamental snapshot LS attaches (rank, shcode, name, per/pbr/peg/eps/bps/roe/growth metrics) so the model doesn't need a follow-up call to compare two metrics on the same stock.
        """)]
    public static async Task<string> GetFundamentalsRank(
        LsApiClient apiClient,
        [Description("Metric name. See the description for the supported set; aliases like 'pe' / 'pb' / 'peg' / 'eps' / 'bps' / 'roe' / 'sales_growth' / 'operating_income_growth' / 'debt_to_equity' / 'retained_earnings_ratio' are accepted.")]
        string field,
        [Description("Market scope: 'both' (default), 'kospi', or 'kosdaq'.")]
        string market = "both",
        [Description("Number of rows to return (1-200, default 30).")]
        int count = 30)
    {
        if (!TryResolveField(field, out string gubun1, out string normalizedField))
            return McpJson.Error($"field '{field}' is not recognized. See the tool description for the supported set.");
        if (!TryResolveMarket(market, out string gubun, out string normalizedMarket))
            return McpJson.Error($"market '{market}' is not recognized. Use 'both', 'kospi', or 'kosdaq'.");
        if (count < 1 || count > MaxCount)
            return McpJson.Error($"count must be between 1 and {MaxCount}.", new { received = count });

        var rows = new List<FundamentalsRankRow>(count);
        int? totalAvailable = null;
        // t3341 declares idx as Number (length 4); sending it as a JSON string
        // ("0") triggers HTTP 500 from LS. Keep it as int across the loop.
        int idx = 0;
        try
        {
            for (int page = 0; page < MaxPages && rows.Count < count; page++)
            {
                LsTrResponse response = await apiClient.CallTrAsync(
                    "t3341",
                    new JsonObject
                    {
                        ["gubun"] = gubun,
                        ["gubun1"] = gubun1,
                        ["gubun2"] = "1",
                        ["idx"] = idx,
                    }).ConfigureAwait(false);

                if (!response.IsSuccess)
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        field = normalizedField,
                        market = normalizedMarket,
                    });

                JsonElement? summary = response.GetBlock("t3341OutBlock");
                if (summary is not null && totalAvailable is null)
                    totalAvailable = (int)summary.Value.ReadLong("cnt");

                JsonElement? array = response.GetBlock("t3341OutBlock1");
                if (array is null || array.Value.ValueKind != JsonValueKind.Array)
                    break;

                int beforeCount = rows.Count;
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    if (rows.Count >= count)
                        break;
                    string? shcode = row.ReadString("shcode")?.Trim();
                    if (string.IsNullOrEmpty(shcode))
                        continue;
                    rows.Add(new FundamentalsRankRow(
                        Rank: (int)row.ReadLong("rank"),
                        Shcode: shcode,
                        Name: (row.ReadString("hname") ?? "").Trim(),
                        Per: row.ReadDouble("per"),
                        Pbr: row.ReadDouble("pbr"),
                        Peg: row.ReadDouble("peg"),
                        Eps: row.ReadDouble("eps"),
                        Bps: row.ReadDouble("bps"),
                        Roe: row.ReadDouble("roe"),
                        SalesGrowth: row.ReadDouble("salesgrowth"),
                        // The LS guide ships "operatingincomegrowt" (no trailing 'h') — wired
                        // as such on the live response. Mirroring the typo so the wire field
                        // names match the catalog without an extra translation layer.
                        OperatingIncomeGrowth: row.ReadDouble("operatingincomegrowt"),
                        OrdinaryIncomeGrowth: row.ReadDouble("ordinaryincomegrowth"),
                        DebtToEquity: row.ReadDouble("liabilitytoequity"),
                        RetainedEarningsRatio: row.ReadDouble("enterpriseratio")));
                }

                if (summary is null) break;
                int nextIdx = (int)summary.Value.ReadLong("idx");
                if (rows.Count == beforeCount || nextIdx == 0 || nextIdx == idx)
                    break;
                idx = nextIdx;
            }
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }

        var payload = new FundamentalsRankPayload
        {
            Field = normalizedField,
            Market = normalizedMarket,
            Count = rows.Count,
            TotalAvailable = totalAvailable,
            Rows = rows,
        };
        return JsonSerializer.Serialize(payload, McpJson.Tool);
    }

    static bool TryResolveField(string? raw, out string gubun1, out string normalized)
    {
        string lower = (raw ?? "").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "sales_growth" or "salesgrowth" or "매출액증가율":
                gubun1 = "1"; normalized = "sales_growth"; return true;
            case "operating_income_growth" or "영업이익증가율":
                gubun1 = "2"; normalized = "operating_income_growth"; return true;
            case "ordinary_income_growth" or "세전계속이익증가율":
                gubun1 = "3"; normalized = "ordinary_income_growth"; return true;
            case "debt_to_equity" or "liabilitytoequity" or "부채비율":
                gubun1 = "4"; normalized = "debt_to_equity"; return true;
            case "retained_earnings_ratio" or "enterpriseratio" or "유보율":
                gubun1 = "5"; normalized = "retained_earnings_ratio"; return true;
            case "eps":
                gubun1 = "6"; normalized = "eps"; return true;
            case "bps":
                gubun1 = "7"; normalized = "bps"; return true;
            case "roe":
                gubun1 = "8"; normalized = "roe"; return true;
            case "per" or "pe":
                gubun1 = "9"; normalized = "per"; return true;
            case "pbr" or "pb":
                gubun1 = "a"; normalized = "pbr"; return true;
            case "peg":
                gubun1 = "b"; normalized = "peg"; return true;
            default:
                gubun1 = ""; normalized = ""; return false;
        }
    }

    static bool TryResolveMarket(string? raw, out string gubun, out string normalized)
    {
        string lower = (raw ?? "both").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "both" or "전체" or "all":
                gubun = "0"; normalized = "both"; return true;
            case "kospi" or "코스피":
                gubun = "1"; normalized = "kospi"; return true;
            case "kosdaq" or "코스닥":
                gubun = "2"; normalized = "kosdaq"; return true;
            default:
                gubun = ""; normalized = ""; return false;
        }
    }

    sealed record FundamentalsRankPayload
    {
        public string Field { get; init; } = "";
        public string Market { get; init; } = "";
        public int Count { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public int? TotalAvailable { get; init; }
        public IReadOnlyList<FundamentalsRankRow> Rows { get; init; } = Array.Empty<FundamentalsRankRow>();
    }

    sealed record FundamentalsRankRow(
        int Rank,
        string Shcode,
        string Name,
        double Per,
        double Pbr,
        double Peg,
        double Eps,
        double Bps,
        double Roe,
        double SalesGrowth,
        double OperatingIncomeGrowth,
        double OrdinaryIncomeGrowth,
        double DebtToEquity,
        double RetainedEarningsRatio);
}
