using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns an ETF-specific quote snapshot, including NAV,
/// tracking index value, premium/discount, AUM, and the liquidity provider
/// list. Backed by TR <c>t1901</c>.
/// </summary>
[McpServerToolType]
public static class GetEtfInfoTool
{
    /// <summary>
    /// Returns ETF-specific market data via TR <c>t1901</c>.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="shcode">6-digit Korean short code of an ETF or ETN.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON with price, NAV, tracking basis, premium/discount, AUM, 52-week range, LPs, and related futures.</returns>
    [McpServerTool(Name = "ls_get_etf_info")]
    [Description("""
        Returns an ETF-specific snapshot for a Korean ETF or ETN: price + NAV + 괴리율 (divergence) + 추적오차율 (tracking error) + reference index + AUM + LP list + 52-week and year ranges + related futures.

        USE WHEN: the user asks about an ETF/ETN (e.g. "KODEX 200 NAV", "TIGER 미국S&P500 괴리율", "ETF 시가총액", "추적오차", "LP 누구야?", "기초지수"), or when a `ls_search_stock` result has `etf != "0"`.
        AVOID WHEN: the shcode is a regular stock — `ls_get_stock_info` returns fundamentals (PER/PBR/EPS, 거래원) which ETFs lack; `ls_get_quote` returns the 10-level order book.

        For PDF (구성종목 / "ETF 안에 뭐 들어있어?") use `ls_get_etf_holdings`.

        Notes:
        - `divergence_percent` = 괴리율 (NAV vs market price)
        - `tracking_error_percent` = 추적오차율 (how closely the ETF tracks its underlying index)
        - `total_assets` is in 억원 (100M KRW); `listing_shares` is the raw share count (LS reports in 천, the tool converts).
        """)]
    public static async Task<string> GetEtfInfo(
        LsApiClient apiClient,
        [Description("6-digit Korean short code, e.g. '069500' for KODEX 200.")]
        string shcode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.Error("shcode is required.");

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1901",
                new JsonObject { ["shcode"] = shcode },
                cancellationToken: cancellationToken);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                });

            JsonElement? block = response.GetBlock("t1901OutBlock");
            if (block is null)
                return McpJson.Error("t1901OutBlock was missing from the response.");

            JsonElement b = block.Value;

            string?[] lpNames = new[]
            {
                b.ReadString("lp_nm1"),
                b.ReadString("lp_nm2"),
                b.ReadString("lp_nm3"),
                b.ReadString("lp_nm4"),
                b.ReadString("lp_nm5"),
            };
            var liquidityProviders = lpNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .ToList();

            object? futures = null;
            string? futCode = b.ReadString("futcode");
            if (!string.IsNullOrWhiteSpace(futCode))
            {
                futures = new
                {
                    code = futCode,
                    name = b.ReadString("futname"),
                    price = b.ReadDouble("futprice"),
                    sign = b.ReadString("futsign"),
                    change = b.ReadDouble("futchange"),
                    change_percent = b.ReadDouble("futdiff"),
                };
            }

            // 참고지수 (reference / underlying index — e.g. KOSPI 200 for KODEX 200).
            // Surfaces the question "what does this ETF actually track?".
            object? referenceIndex = null;
            string? refName = b.ReadString("upname2");
            string? refCode = b.ReadString("upcode2");
            if (!string.IsNullOrWhiteSpace(refName) || !string.IsNullOrWhiteSpace(refCode))
            {
                referenceIndex = new
                {
                    name = refName,
                    code = refCode,
                    price = b.ReadDouble("upprice2"),
                };
            }

            // listing field is in 천 (thousand) units per LS docs; surface raw shares.
            long listingThousand = b.ReadLong("listing");

            var payload = new
            {
                shcode,
                name = b.ReadString("hname"),
                etp_type = b.ReadString("etp_gb"),
                etn_kind = b.ReadString("etn_kind_cd"),
                etn_early_redemption_flag = b.ReadString("etn_elback_yn"),
                index_asset_class = b.ReadString("idx_asset_class1"),
                investment_caution = b.ReadString("ty_text"),
                replication_method = b.ReadString("etf_cp"),
                leverage = b.ReadLong("leverage"),
                tax_class = b.ReadString("taxgubun"),
                issuer = b.ReadString("issuernmk"),
                asset_manager = b.ReadString("opcom_nmk"),
                listing_shares = listingThousand * 1000L,
                listing_date = b.ReadString("listdate"),
                distribution_date = b.ReadString("payday"),

                price = b.ReadLong("price"),
                sign = b.ReadString("sign"),
                change = b.ReadLong("change"),
                change_percent = b.ReadDouble("diff"),
                reference_price = b.ReadLong("recprice"),
                open = b.ReadLong("open"),
                high = b.ReadLong("high"),
                low = b.ReadLong("low"),
                upper_limit_price = b.ReadLong("uplmtprice"),
                lower_limit_price = b.ReadLong("dnlmtprice"),
                substitute_price = b.ReadLong("subprice"),
                volume = b.ReadLong("volume"),
                volume_change = b.ReadLong("volumediff"),
                previous_volume = b.ReadLong("jnilvolume"),
                value = b.ReadLong("value"),
                turnover_percent = b.ReadDouble("vol"),
                open_time = b.ReadString("opentime"),
                high_time = b.ReadString("hightime"),
                low_time = b.ReadString("lowtime"),
                vi_class = b.ReadString("vi_gubun"),

                nav = b.ReadDouble("nav"),
                nav_sign = b.ReadString("navsign"),
                nav_change = b.ReadDouble("navchange"),
                nav_change_percent = b.ReadDouble("navdiff"),
                previous_nav = b.ReadDouble("jnilnav"),
                // LS field semantics (per official docs):
                //   kasis   = 괴리율 (NAV vs market price)
                //   cocrate = 추적오차율 (tracking error vs underlying index)
                // Earlier drafts swapped these — pinned by 2026-05-13 KODEX 200 E2E
                // where the LLM mislabeled tracking error as 괴리율.
                divergence_percent = b.ReadDouble("kasis"),
                tracking_error_percent = b.ReadDouble("cocrate"),
                foreign_ownership_percent = b.ReadDouble("exhratio"),
                spread_percent = b.ReadDouble("spread"),

                total_assets = b.ReadLong("etftotcap"),
                lp_holding_volume = b.ReadString("lp_holdvol"),
                liquidity_providers = liquidityProviders,

                high_52w = b.ReadLong("high52w"),
                high_52w_date = b.ReadString("high52wdate"),
                low_52w = b.ReadLong("low52w"),
                low_52w_date = b.ReadString("low52wdate"),
                high_year = b.ReadLong("highyear"),
                high_year_date = b.ReadString("highyeardate"),
                low_year = b.ReadLong("lowyear"),
                low_year_date = b.ReadString("lowyeardate"),

                reference_index = referenceIndex,
                futures,
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
