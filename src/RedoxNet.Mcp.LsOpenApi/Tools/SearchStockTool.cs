using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that searches the Korean stock universe by name fragment.
/// </summary>
/// <remarks>
/// Backed by TR <c>t8436</c> (the API-recommended variant of <c>t8430</c>),
/// which returns the full universe in a single call along with SPAC and
/// management-status flags. The tool filters client-side by substring
/// match and caps the returned list, so the LLM never has to handle the
/// full 2,000+ rows.
/// </remarks>
[McpServerToolType]
public static class SearchStockTool
{
    /// <summary>
    /// Returns up to <paramref name="limit"/> matching stocks.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="query">Korean or English name fragment.</param>
    /// <param name="market">'all', 'kospi', or 'kosdaq'. Default 'all'.</param>
    /// <param name="limit">Max results, default 20, max 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON list of <c>{ shcode, name, market }</c>.</returns>
    [McpServerTool(Name = "ls_search_stock")]
    [Description("""
        Searches the Korean stock universe (KOSPI + KOSDAQ) by Korean or English name fragment and returns the matching short codes.

        USE WHEN: the user mentions a stock by name but the 6-digit code is needed (e.g. "삼성전자 알려줘", "find Hyundai Motor's code").
        AVOID WHEN: the user already supplied a 6-digit code — use ls_get_quote / ls_get_chart directly.
        """)]
    public static async Task<string> SearchStock(
        LsApiClient apiClient,
        [Description("Korean or English name fragment, e.g. '삼성', 'Samsung'.")]
        string query,
        [Description("Market filter: 'all' (default), 'kospi', or 'kosdaq'.")]
        string market = "all",
        [Description("Max results (1–100). Default 20.")]
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return McpJson.Error("query is required.");

        int cappedLimit = Math.Clamp(limit, 1, 100);
        string gubun = market?.Trim().ToLowerInvariant() switch
        {
            "kospi" or "1" => "1",
            "kosdaq" or "2" => "2",
            _ => "0",
        };

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t8436",
                new JsonObject { ["gubun"] = gubun },
                cancellationToken: cancellationToken);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                });

            JsonElement? block = response.GetBlock("t8436OutBlock");
            if (block is null || block.Value.ValueKind != JsonValueKind.Array)
                return McpJson.Error("t8436OutBlock array was missing from the response.");

            var matches = new List<object>();
            foreach (JsonElement row in block.Value.EnumerateArray())
            {
                string? name = row.ReadString("hname");
                string? shcode = row.ReadString("shcode");
                if (name is null || shcode is null)
                    continue;
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                    !shcode.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? spacRaw = row.ReadString("spac_gubun");
                matches.Add(new
                {
                    shcode,
                    name,
                    expcode = row.ReadString("expcode"),
                    market = MarketLabel(row.ReadString("gubun")),
                    etf = row.ReadString("etfgubun"),
                    is_spac = string.Equals(spacRaw, "Y", StringComparison.OrdinalIgnoreCase),
                    status_code = row.ReadString("bu12gubun"),
                });
                if (matches.Count >= cappedLimit)
                    break;
            }

            var payload = new
            {
                query,
                market_filter = gubun switch { "1" => "kospi", "2" => "kosdaq", _ => "all" },
                limit = cappedLimit,
                count = matches.Count,
                results = matches,
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

    static string MarketLabel(string? gubun) => gubun switch
    {
        "1" => "kospi",
        "2" => "kosdaq",
        _ => gubun ?? "unknown",
    };
}
