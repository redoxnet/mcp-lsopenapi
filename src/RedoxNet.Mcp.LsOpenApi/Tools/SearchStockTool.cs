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
    /// <param name="keyword">Korean or English name fragment. Named <c>keyword</c> to match <c>ls_search_tr</c>.</param>
    /// <param name="market">'all', 'kospi', or 'kosdaq'. Default 'all'.</param>
    /// <param name="limit">Max results, default 20, max 100.</param>
    /// <param name="instrument">'all' (default), 'stock' (일반주, etfgubun='0'), or 'etf' (ETF/ETN, etfgubun!='0').</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON list of <c>{ shcode, name, market }</c>.</returns>
    [McpServerTool(Name = "ls_search_stock")]
    [Description("""
        Searches the Korean stock universe (KOSPI + KOSDAQ) by Korean or English name fragment and returns the matching short codes.

        USE WHEN: the user mentions a stock by name but the 6-digit code is needed (e.g. "삼성전자 알려줘", "find Hyundai Motor's code").
        AVOID WHEN: the user already supplied a 6-digit code — use ls_get_quote / ls_get_chart directly.

        Use instrument='stock' to exclude ETFs/ETNs (e.g. "바이오 관련 일반주만 찾아줘"), instrument='etf' to keep only ETF/ETN rows, instrument='all' (default) for the full mix.
        """)]
    public static async Task<string> SearchStock(
        LsApiClient apiClient,
        // Optional at the protocol level so a missing/misnamed argument lands in
        // the validation below with a clear "keyword is required." message,
        // instead of failing in the MCP SDK's parameter binder with an opaque
        // "An error occurred invoking 'ls_search_stock'.".
        [Description("Korean or English name fragment, e.g. '삼성', 'Samsung'.")]
        string keyword = "",
        [Description("Market filter: 'all' (default), 'kospi', or 'kosdaq'.")]
        string market = "all",
        [Description("Max results (1–100). Default 20.")]
        int limit = 20,
        [Description("Instrument filter: 'all' (default), 'stock' (일반주 only), or 'etf' (ETF/ETN only).")]
        string instrument = "all",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return McpJson.Error("keyword is required.");

        int cappedLimit = Math.Clamp(limit, 1, 100);
        string gubun = market?.Trim().ToLowerInvariant() switch
        {
            "kospi" or "1" => "1",
            "kosdaq" or "2" => "2",
            _ => "0",
        };

        string instrumentFilter = instrument?.Trim().ToLowerInvariant() ?? "all";
        if (instrumentFilter is not ("all" or "stock" or "etf"))
            return McpJson.Error($"Unknown instrument '{instrument}'. Use 'all', 'stock', or 'etf'.");

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
                if (!name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                    !shcode.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? etfRaw = row.ReadString("etfgubun");
                bool isEtf = !string.IsNullOrEmpty(etfRaw) && etfRaw != "0";
                if (instrumentFilter == "stock" && isEtf) continue;
                if (instrumentFilter == "etf" && !isEtf) continue;

                string? spacRaw = row.ReadString("spac_gubun");
                matches.Add(new
                {
                    shcode,
                    name,
                    expcode = row.ReadString("expcode"),
                    market = MarketLabel(row.ReadString("gubun")),
                    etf = etfRaw,
                    is_spac = string.Equals(spacRaw, "Y", StringComparison.OrdinalIgnoreCase),
                    status_code = row.ReadString("bu12gubun"),
                });
                if (matches.Count >= cappedLimit)
                    break;
            }

            var payload = new
            {
                keyword,
                market_filter = gubun switch { "1" => "kospi", "2" => "kosdaq", _ => "all" },
                instrument_filter = instrumentFilter,
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

    /// <summary>Maps the LS market gubun digit to a human label (e.g. <c>"1"</c> → <c>"kospi"</c>).</summary>
    /// <param name="gubun">Raw gubun value from the LS row.</param>
    /// <returns>Lowercase market label, or <c>"unknown"</c> when the value isn't recognised.</returns>
    static string MarketLabel(string? gubun) => gubun switch
    {
        "1" => "kospi",
        "2" => "kosdaq",
        _ => gubun ?? "unknown",
    };
}
