using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns a price snapshot + 10-level order book for a stock.
/// </summary>
[McpServerToolType]
public static class GetQuoteTool
{
    /// <summary>
    /// Returns the current price snapshot via TR <c>t1101</c>.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="shcode">6-digit Korean stock code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON containing current price, bid/ask levels, session OHLC, and accumulated volume.</returns>
    [McpServerTool(Name = "ls_get_quote")]
    [Description("""
        Returns a real-time snapshot for a single Korean stock: last price, 10-level order book, session open/high/low, accumulated volume, and percent change.

        USE WHEN: the user asks for a quote, current price, 호가, 시세, 주가, or how a specific stock is doing right now.
        AVOID WHEN: the user wants historical data — use ls_get_chart for OHLCV bars instead.
        """)]
    public static async Task<string> GetQuote(
        LsApiClient apiClient,
        [Description("6-digit Korean short code, e.g. '005930' for Samsung Electronics.")]
        string shcode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.Error("shcode is required.");

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1101",
                new JsonObject { ["shcode"] = shcode },
                cancellationToken: cancellationToken);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                });

            JsonElement? block = response.GetBlock("t1101OutBlock");
            if (block is null)
                return McpJson.Error("t1101OutBlock was missing from the response.");

            JsonElement b = block.Value;
            var levels = Enumerable.Range(1, 10).Select(i => new
            {
                level = i,
                ask = b.ReadLong($"offerho{i}"),
                ask_size = b.ReadLong($"offerrem{i}"),
                ask_change = b.ReadLong($"preoffercha{i}"),
                bid = b.ReadLong($"bidho{i}"),
                bid_size = b.ReadLong($"bidrem{i}"),
                bid_change = b.ReadLong($"prebidcha{i}"),
            }).ToList();

            var payload = new
            {
                shcode,
                name = b.ReadString("hname"),
                price = b.ReadLong("price"),
                sign = b.ReadString("sign"),
                change = b.ReadLong("change"),
                change_percent = b.ReadDouble("diff"),
                volume = b.ReadLong("volume"),
                previous_close = b.ReadLong("jnilclose"),
                open = b.ReadLong("open"),
                high = b.ReadLong("high"),
                low = b.ReadLong("low"),
                upper_limit_price = b.ReadLong("uplmtprice"),
                lower_limit_price = b.ReadLong("dnlmtprice"),
                total_ask_volume = b.ReadLong("offer"),
                total_bid_volume = b.ReadLong("bid"),
                extended_hours_ask_volume = b.ReadLong("tmoffer"),
                extended_hours_bid_volume = b.ReadLong("tmbid"),
                quote_time = b.ReadString("hotime"),
                quote_status = b.ReadString("ho_status"),
                expected_price = b.ReadLong("yeprice"),
                expected_volume = b.ReadLong("yevolume"),
                expected_change = b.ReadLong("yechange"),
                expected_change_percent = b.ReadDouble("yediff"),
                order_book = levels,
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
}
