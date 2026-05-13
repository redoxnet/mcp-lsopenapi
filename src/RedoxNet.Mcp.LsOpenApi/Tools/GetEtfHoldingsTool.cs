using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns the PDF (portfolio deposit file / 구성종목) of a
/// Korean ETF along with the ETF's own summary stats. Backed by TR <c>t1904</c>.
/// </summary>
[McpServerToolType]
public static class GetEtfHoldingsTool
{
    /// <summary>
    /// Returns the ETF's constituent list via TR <c>t1904</c>.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="shcode">6-digit Korean short code of an ETF.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with an ETF summary (NAV, AUM, constituent count) and the holdings array sorted by weight as returned by LS.</returns>
    [McpServerTool(Name = "ls_get_etf_holdings")]
    [Description("""
        Returns the PDF (portfolio deposit file / 구성종목) of a Korean ETF: each holding's short code, name, weight (%), price, change, market value, and a summary block (ETF price, NAV, constituent count, total AUM, cash portion).

        USE WHEN: the user asks "이 ETF 안에 뭐 들어있어?", "KODEX 2차전지 비중 1위 종목", "TIGER 미국S&P500 구성종목", "ETF 보유종목", "ETF holdings/composition/PDF".
        AVOID WHEN: the user wants the ETF's price/NAV/괴리율 only — `ls_get_etf_info` is lighter; for a regular stock use `ls_get_stock_info`.
        """)]
    public static async Task<string> GetEtfHoldings(
        LsApiClient apiClient,
        [Description("6-digit Korean short code of an ETF, e.g. '069500'.")]
        string shcode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.Error("shcode is required.");

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1904",
                new JsonObject { ["shcode"] = shcode },
                cancellationToken: cancellationToken);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                });

            JsonElement? summaryBlock = response.GetBlock("t1904OutBlock");
            JsonElement? holdingsBlock = response.GetBlock("t1904OutBlock1");

            if (summaryBlock is null)
                return McpJson.Error(
                    "LS returned success (rsp_cd=00000) but no t1904OutBlock — PDF (구성종목) data is unavailable for this shcode. Common causes: (1) the LS virtual server has spotty PDF coverage for certain ETFs, (2) intraday PDF publication lag, (3) the shcode is not a Korean ETF that publishes PDF via t1904. Inspect the raw response with `ls_call_tr` (tr_cd='t1904', in_block={shcode}) to debug, or check the ETF issuer's official PDF page.",
                    new { shcode, rsp_cd = response.RspCode, rsp_msg = response.RspMessage });

            JsonElement s = summaryBlock.Value;

            var holdings = new List<object>();
            if (holdingsBlock is { ValueKind: JsonValueKind.Array })
            {
                foreach (JsonElement row in holdingsBlock.Value.EnumerateArray())
                {
                    holdings.Add(new
                    {
                        shcode = row.ReadString("shcode"),
                        name = row.ReadString("hname"),
                        weight_percent = row.ReadDouble("weight"),
                        price = row.ReadLong("price"),
                        sign = row.ReadString("sign"),
                        change = row.ReadLong("change"),
                        change_percent = row.ReadDouble("diff"),
                        volume = row.ReadLong("volume"),
                        value = row.ReadLong("value"),
                        market_value = row.ReadLong("sigatvalue"),
                        etf_valuation = row.ReadLong("pvalue"),
                        par_price = row.ReadLong("parprice"),
                        profit_date = row.ReadString("profitdate"),
                    });
                }
            }

            var payload = new
            {
                shcode,
                date = s.ReadString("date"),
                confirmed_today = string.Equals(s.ReadString("chk_tday"), "1", StringComparison.Ordinal),

                price = s.ReadLong("price"),
                sign = s.ReadString("sign"),
                change = s.ReadLong("change"),
                change_percent = s.ReadDouble("diff"),
                volume = s.ReadLong("volume"),

                nav = s.ReadDouble("nav"),
                nav_change = s.ReadDouble("navchange"),
                nav_change_percent = s.ReadDouble("navdiff"),
                previous_nav = s.ReadDouble("jnilnav"),

                holdings_count = s.ReadLong("etfnum"),
                cu_units = s.ReadLong("etfcunum"),
                total_assets = s.ReadLong("etftotcap"),
                total_market_value = s.ReadLong("tot_sigatval"),
                total_valuation = s.ReadLong("tot_pval"),
                cash = s.ReadLong("cash"),

                holdings,
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
