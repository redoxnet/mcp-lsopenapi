using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns a comprehensive company profile + fundamentals
/// snapshot for a Korean stock (TR <c>t1102</c>).
/// </summary>
/// <remarks>
/// Complements <see cref="GetQuoteTool"/> (10-level order book) by surfacing
/// PER/PBR/EPS, quarterly financials, growth rates, 52-week and YTD ranges,
/// top-5 buy/sell brokerage flow, and foreign-investor activity — i.e. the
/// data an analyst usually wants alongside the current price.
/// </remarks>
[McpServerToolType]
public static class GetStockInfoTool
{
    /// <summary>
    /// Returns the t1102 company-info snapshot.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="shcode">6-digit Korean stock code.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with current price, ranges, fundamentals, brokerage flow, and status flags.</returns>
    [McpServerTool(Name = "ls_get_stock_info")]
    [Description("""
        Returns a company profile + fundamentals snapshot for a Korean stock: PER / PBR / EPS, quarterly sales / operating income / net income (current vs prior period) + growth rates, 52-week and YTD price ranges, top-5 buy and sell brokerages, and foreign-investor activity.

        USE WHEN: the user wants company/financial context ("삼성전자 어떤 회사야?", "PER 얼마야?", "외국인 매수세 어때?", "52주 신고가 근처야?"). Pairs well with ls_get_quote for level 2 book.
        AVOID WHEN: only the latest price + 10-level order book are needed — use ls_get_quote (cheaper payload).
        """)]
    public static async Task<string> GetStockInfo(
        LsApiClient apiClient,
        [Description("6-digit Korean short code, e.g. '005930'.")]
        string shcode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.Error("shcode is required.");

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1102",
                new JsonObject { ["shcode"] = shcode },
                cancellationToken: cancellationToken);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                });

            JsonElement? block = response.GetBlock("t1102OutBlock");
            if (block is null)
                return McpJson.Error("t1102OutBlock was missing from the response.");

            JsonElement b = block.Value;

            var sellers = Enumerable.Range(1, 5).Select(i => new
            {
                rank = i,
                code = b.ReadString($"offernocd{i}"),
                name = b.ReadString($"offerno{i}"),
                avg_price = b.ReadLong($"savg{i}"),
                volume = b.ReadLong($"svol{i}"),
                value = b.ReadLong($"sval{i}"),
                change = b.ReadLong($"scha{i}"),
                change_percent = b.ReadDouble($"sdiff{i}"),
            }).ToList();

            var buyers = Enumerable.Range(1, 5).Select(i => new
            {
                rank = i,
                code = b.ReadString($"bidnocd{i}"),
                name = b.ReadString($"bidno{i}"),
                avg_price = b.ReadLong($"davg{i}"),
                volume = b.ReadLong($"dvol{i}"),
                value = b.ReadLong($"dval{i}"),
                change = b.ReadLong($"dcha{i}"),
                change_percent = b.ReadDouble($"ddiff{i}"),
            }).ToList();

            var payload = new
            {
                shcode,
                name = b.ReadString("hname"),
                market = b.ReadString("janginfo"),
                currency = b.ReadString("tonghwa"),
                listing_date = b.ReadString("listdate"),
                par_value = b.ReadLong("parprice"),
                trade_unit = b.ReadString("memedan"),

                snapshot = new
                {
                    price = b.ReadLong("price"),
                    sign = b.ReadString("sign"),
                    change = b.ReadLong("change"),
                    change_percent = b.ReadDouble("diff"),
                    volume = b.ReadLong("volume"),
                    volume_vs_yesterday_same_time = b.ReadLong("volumediff"),
                    yesterday_same_time_volume = b.ReadLong("jnilvolume"),
                    value = b.ReadLong("value"),
                    open = b.ReadLong("open"),
                    high = b.ReadLong("high"),
                    low = b.ReadLong("low"),
                    open_time = b.ReadString("opentime"),
                    high_time = b.ReadString("hightime"),
                    low_time = b.ReadString("lowtime"),
                    average = b.ReadLong("avg"),
                    upper_limit_price = b.ReadLong("uplmtprice"),
                    lower_limit_price = b.ReadLong("dnlmtprice"),
                    reference_price = b.ReadLong("recprice"),
                    turnover_ratio_percent = b.ReadDouble("exhratio"),
                },

                range = new
                {
                    week52 = new
                    {
                        high = b.ReadLong("high52w"),
                        high_date = b.ReadString("high52wdate"),
                        low = b.ReadLong("low52w"),
                        low_date = b.ReadString("low52wdate"),
                    },
                    ytd = new
                    {
                        high = b.ReadLong("highyear"),
                        high_date = b.ReadString("highyeardate"),
                        low = b.ReadLong("lowyear"),
                        low_date = b.ReadString("lowyeardate"),
                    },
                },

                fundamentals = new
                {
                    per = b.ReadDouble("per"),
                    expected_per = b.ReadDouble("t_per"),
                    pbr = b.ReadDouble("pbrx"),
                    current_period_label = b.ReadString("name"),
                    previous_period_label = b.ReadString("name2"),
                    settlement_month = b.ReadString("gsmm"),
                    sales_current = b.ReadLong("bfsales"),
                    sales_previous = b.ReadLong("bfsales2"),
                    operating_income_current = b.ReadLong("bfoperatingincome"),
                    operating_income_previous = b.ReadLong("bfoperatingincome2"),
                    ordinary_income_current = b.ReadLong("bfordinaryincome"),
                    ordinary_income_previous = b.ReadLong("bfordinaryincome2"),
                    net_income_current = b.ReadLong("bfnetincome"),
                    net_income_previous = b.ReadLong("bfnetincome2"),
                    eps_current = b.ReadDouble("bfeps"),
                    eps_previous = b.ReadDouble("bfeps2"),
                    growth_percent = new
                    {
                        net_income = b.ReadDouble("netrt"),
                        eps = b.ReadDouble("epsrt"),
                        ordinary_income = b.ReadDouble("ordrt"),
                        operating_income = b.ReadDouble("opert"),
                    },
                },

                listing = new
                {
                    shares_in_thousands = b.ReadLong("listing"),
                    capital_in_100m_won = b.ReadLong("capital"),
                    market_cap_in_100m_won = b.ReadLong("total"),
                    equity_ratio_percent = b.ReadLong("jkrate"),
                    issue_price = b.ReadLong("issueprice"),
                    target_price = b.ReadLong("target"),
                },

                foreign_investor = new
                {
                    holdings_shares = b.ReadLong("abscnt"),
                    holdings_percent = b.ReadDouble("vol"),
                    cumulative_net_sell = b.ReadLong("fwsvl"),
                    cumulative_net_buy = b.ReadLong("fwdvl"),
                    sell = new
                    {
                        value = b.ReadLong("ftradmsval"),
                        avg_price = b.ReadLong("ftradmsvag"),
                        change = b.ReadLong("ftradmscha"),
                        change_percent = b.ReadDouble("ftradmsdiff"),
                    },
                    buy = new
                    {
                        value = b.ReadLong("ftradmdval"),
                        avg_price = b.ReadLong("ftradmdvag"),
                        change = b.ReadLong("ftradmdcha"),
                        change_percent = b.ReadDouble("ftradmddiff"),
                    },
                },

                brokerage = new
                {
                    sellers,
                    buyers,
                },

                flags = new
                {
                    is_spac = string.Equals(b.ReadString("spac_gubun"), "Y", StringComparison.OrdinalIgnoreCase),
                    abnormal_rise = b.ReadString("abnormal_rise_gu"),
                    low_liquidity = b.ReadString("low_lqdt_gu"),
                    dividend_class = b.ReadString("alloc_gubun"),
                },

                notes = new
                {
                    short_term = b.ReadString("shterm_text"),
                    type_text = b.ReadString("ty_text"),
                    dividend_text = b.ReadString("alloc_text"),
                    lending_text = b.ReadString("lend_text"),
                    info = new[]
                    {
                        b.ReadString("info1"), b.ReadString("info2"), b.ReadString("info3"),
                        b.ReadString("info4"), b.ReadString("info5"),
                    },
                },
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
