using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns a compact price snapshot for multiple Korean stocks
/// in a single LS call (TR <c>t8407</c>).
/// </summary>
/// <remarks>
/// Per-row payload: price, sign, change, change %, OHLC, volume + value,
/// 상하한가, best ask / best bid (1단), 매도/매수 총잔량, 체결강도.
///
/// For a single stock with the full 10-level order book, prefer
/// <see cref="GetQuoteTool"/>; this tool intentionally drops levels 2–10
/// to keep the multi-stock payload small.
/// </remarks>
[McpServerToolType]
public static class GetMultiQuoteTool
{
    /// <summary>Maximum stocks per call (LS-side cap; conservative).</summary>
    public const int MaxStocks = 50;

    /// <summary>
    /// Returns current snapshots for each of <paramref name="shcodes"/> via TR <c>t8407</c>.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="shcodes">Array of 6-digit Korean stock codes (max 50).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON list of compact per-stock snapshots.</returns>
    [McpServerTool(Name = "ls_get_multi_quote")]
    [Description("""
        Returns a compact current-price snapshot for multiple Korean stocks in a single call. Each entry includes price, OHLC, volume, best ask/bid (1 level), 매도/매수 총잔량, and 체결강도(chdegree).

        USE WHEN: comparing 2+ stocks side-by-side ("삼성전자 vs SK하이닉스 vs LS증권 비교"), or fetching a small watchlist's prices efficiently. Up to 50 codes per call.
        AVOID WHEN: 10-level order book is needed for a single stock — use ls_get_quote instead.
        """)]
    public static async Task<string> GetMultiQuote(
        LsApiClient apiClient,
        [Description("Array of 6-digit Korean short codes, e.g. ['005930','000660','078020']. Max 50.")]
        string[] shcodes,
        CancellationToken cancellationToken = default)
    {
        if (shcodes is null || shcodes.Length == 0)
            return McpJson.Error("shcodes is required (at least 1 code).");
        if (shcodes.Length > MaxStocks)
            return McpJson.Error($"At most {MaxStocks} shcodes per call. Split into batches.");

        var normalized = new List<string>(shcodes.Length);
        foreach (string raw in shcodes)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return McpJson.Error("shcodes contains an empty entry.");
            string trimmed = raw.Trim();
            if (trimmed.Length != 6 || !trimmed.All(char.IsDigit))
                return McpJson.Error($"shcode '{raw}' is not a 6-digit numeric code.");
            normalized.Add(trimmed);
        }

        var inBlock = new JsonObject
        {
            ["qrycnt"] = normalized.Count,
            ["shcode"] = string.Concat(normalized),
        };

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t8407", inBlock, cancellationToken: cancellationToken);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                });

            JsonElement? block = response.GetBlock("t8407OutBlock1");
            if (block is null || block.Value.ValueKind != JsonValueKind.Array)
                return McpJson.Error("t8407OutBlock1 array was missing from the response.");

            // LS empirically returns up to 50 rows regardless of qrycnt,
            // padding with default codes when fewer were requested. Index by
            // shcode, then emit only the codes the caller asked for, in the
            // order they asked for them.
            var byShcode = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonElement row in block.Value.EnumerateArray())
            {
                string? rowShcode = row.ReadString("shcode");
                if (string.IsNullOrEmpty(rowShcode))
                    continue;
                byShcode[rowShcode] = row;
            }

            var quotes = new List<object>(normalized.Count);
            foreach (string code in normalized)
            {
                if (!byShcode.TryGetValue(code, out JsonElement row))
                    continue;

                quotes.Add(new
                {
                    shcode = code,
                    name = row.ReadString("hname"),
                    price = row.ReadLong("price"),
                    sign = row.ReadString("sign"),
                    change = row.ReadLong("change"),
                    change_percent = row.ReadDouble("diff"),
                    volume = row.ReadLong("volume"),
                    cvolume = row.ReadLong("cvolume"),
                    value = row.ReadLong("value"),
                    open = row.ReadLong("open"),
                    high = row.ReadLong("high"),
                    low = row.ReadLong("low"),
                    previous_close = row.ReadLong("jnilclose"),
                    upper_limit_price = row.ReadLong("uplmtprice"),
                    lower_limit_price = row.ReadLong("dnlmtprice"),
                    best_ask = row.ReadLong("offerho"),
                    best_ask_size = row.ReadLong("offerrem"),
                    best_bid = row.ReadLong("bidho"),
                    best_bid_size = row.ReadLong("bidrem"),
                    total_ask_size = row.ReadLong("totofferrem"),
                    total_bid_size = row.ReadLong("totbidrem"),
                    chdegree = ParseChdegree(row.ReadString("chdegree")),
                });
            }

            var payload = new
            {
                count = quotes.Count,
                quotes,
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
    /// Parses LS's <c>chdegree</c> field ("000056.80" form) to a double.
    /// Returns <c>null</c> when the value is missing or unparseable.
    /// </summary>
    static double? ParseChdegree(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }
}
