using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns the top-N industry indices for a market, sorted
/// by change percent descending. Wraps t8424 + per-upcode t1511 fanout
/// behind a 60s in-process cache.
/// </summary>
[McpServerToolType]
internal static class GetIndustryIndicesTool
{
    /// <summary>Catalog default rate limit for t1511 is 1/sec; keep top_n bounded.</summary>
    public const int MaxTopN = 200;

    /// <summary>
    /// Returns sorted industry indices ranked by change percent.
    /// </summary>
    [McpServerTool(Name = "ls_get_industry_indices")]
    [Description("""
        Returns the top-N Korean industry/sector indices sorted by change percent descending. Internally fetches the catalog via t8424 then the per-index snapshot via t1511 for each upcode, caches the full array for 60 seconds.

        USE WHEN: the user asks "오늘 강한 업종은?", "업종 등락률", "sector ranking", or wants the market-wide industry leader/laggard board.
        AVOID WHEN: the user names a single index (KOSPI/KOSDAQ) — use ls_get_index_quote. For per-stock ranking inside one industry use ls_get_industry_stocks.

        market: kospi (default), kosdaq, or all. Cold-cache cost scales with N upcodes returned by t8424 — usually a few seconds for kospi.
        top_n: number of rows to return. The same 60s cache is reused for different top_n requests, so 'top 5' then 'top 30' is two cheap lookups.
        """)]
    public static async Task<string> GetIndustryIndices(
        IndustryDataCache cache,
        [Description("Market filter: kospi, kosdaq, or all. Default kospi.")]
        string market = "kospi",
        [Description("Max rows returned (1-200). Default 30.")]
        int top_n = 30,
        CancellationToken cancellationToken = default)
    {
        if (top_n < 1 || top_n > MaxTopN)
            return McpJson.Error($"top_n must be between 1 and {MaxTopN}.");

        string normalizedMarket = (market ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "kospi" => "kospi",
            "kosdaq" => "kosdaq",
            "all" or "전체" => "all",
            _ => "",
        };
        if (normalizedMarket.Length == 0)
            return McpJson.Error($"market '{market}' is not recognized. Use kospi, kosdaq, or all.");

        IndustryIndicesResult result = await cache.GetIndicesAsync(normalizedMarket, cancellationToken).ConfigureAwait(false);

        var slice = result.Rows.Take(top_n).Select((row, idx) => new
        {
            rank = idx + 1,
            upcode = row.Upcode,
            name = row.Name,
            value = row.Value,
            change = row.Change,
            change_pct = row.ChangePct,
        }).ToList();

        var payload = new
        {
            market = normalizedMarket,
            top_n,
            count = slice.Count,
            total_available = result.Rows.Count,
            rows = slice,
            timestamp = GetIndexQuoteTool.SeoulNowIsoString(),
            partial_error = result.Error,
        };
        return JsonSerializer.Serialize(payload, McpJson.Tool);
    }
}
