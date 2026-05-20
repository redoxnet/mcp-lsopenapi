using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.LsOpenApi.Core.Charting;

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
    /// <param name="date">Optional PDF basis date in <c>yyyyMMdd</c>; defaults to today (KST).</param>
    /// <param name="top_n">Cap on the constituents returned, from the top of the LS-sorted list. Defaults to 20; pass -1 for the full list.</param>
    /// <param name="include_chart">If true, ship a Plotly treemap spec + side-panel as structuredContent so MCP Apps (SEP-1865) hosts render the composition inline. Default false.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Text content with the ETF summary and the holdings array, plus an optional <c>structuredContent.chart</c>/<c>panel</c> pair for inline rendering.</returns>
    [McpServerTool(Name = "ls_get_etf_holdings")]
    [Description("""
        Returns the PDF (portfolio deposit file / 구성종목) of a Korean ETF: each holding's short code, name, weight (%), price, change, market value, and a summary block (ETF price, NAV, constituent count, total AUM, cash portion).

        USE WHEN: the user asks "이 ETF 안에 뭐 들어있어?", "KODEX 2차전지 비중 1위 종목", "TIGER 미국S&P500 구성종목", "ETF 보유종목", "ETF holdings/composition/PDF".
        AVOID WHEN: the user wants the ETF's price/NAV/괴리율 only — `ls_get_etf_info` is lighter; for a regular stock use `ls_get_stock_info`.

        The basis date defaults to today (KST). LS publishes PDF per trading day, so on weekends or before today's PDF is posted, pass an explicit `date` (yyyyMMdd) of a recent trading day. Holdings come back sorted by valuation amount (LS InBlock `sgb=1`); the alternative `sgb=2` (share count) is accessible via `ls_call_tr` if needed.

        `top_n` caps the constituent array — it defaults to 20, since the largest holdings carry most of an ETF's weight. Pass `top_n=-1` for the full list; an ETF with 200+ holdings (KODEX 200 ≈ 201) returned in full would blow past inline token budgets. The summary block (`holdings_count`, AUM, NAV, etc.) always reflects the full ETF regardless — only the `holdings[]` array is capped, and `holdings_truncated` flags when it was.

        Set include_chart=true for inline composition rendering on MCP Apps hosts (Claude Desktop, Claude.ai, ChatGPT, Goose, VS Code). A Plotly treemap + top-10 side panel ships as structuredContent (not in the model's text context — zero token cost) and the host's iframe renders it via ui://lsopenapi/plotly. The panel surfaces a concentration badge (분산형 / 균형형 / 집중형 / 초집중형 by top-5 cumulative weight), the top-10 cumulative weights, and notes for single-name concentrations ≥30% and cash buffers.

        Note units: `total_assets` is in 억원 (100M KRW), per-holding `value` is in 백만원 (1M KRW), all other monetary fields are raw 원 (KRW).
        """)]
    public static async Task<CallToolResult> GetEtfHoldings(
        LsApiClient apiClient,
        [Description("6-digit Korean short code of an ETF, e.g. '069500'.")]
        string shcode,
        [Description("Optional PDF basis date in 'yyyyMMdd' format. Defaults to today (KST). Use an explicit weekday date if today's PDF hasn't been posted yet.")]
        string? date = null,
        [Description("Cap on the holdings array. Default 20 — the largest holdings carry most of an ETF's weight. Pass -1 for the full list.")]
        int top_n = 20,
        [Description("If true, ship a Plotly treemap + side-panel as structuredContent so MCP Apps hosts render the composition inline. Default false.")]
        bool include_chart = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.ErrorResult("shcode is required.");
        if (top_n == 0 || top_n < -1)
            return McpJson.ErrorResult("top_n must be a positive integer, or -1 for the full list.");

        // LS's t1904 InBlock requires three fields: shcode, date (yyyyMMdd), and
        // sgb (정렬기준 — 1=평가금액, 2=증권수). Omitting any of date or sgb
        // causes LS to respond with rsp_cd=00000 and an empty OutBlock list,
        // which masquerades as "no data for this shcode" — the 2026-05-13 E2E
        // session burned ~30 minutes on that mis-diagnosis before the docs
        // were located.
        string effectiveDate = !string.IsNullOrWhiteSpace(date)
            ? date!
            : DateTime.UtcNow.AddHours(9).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1904",
                new JsonObject
                {
                    ["shcode"] = shcode,
                    ["date"] = effectiveDate,
                    ["sgb"] = "1",
                },
                cancellationToken: cancellationToken);

            if (!response.IsSuccess)
                return McpJson.ErrorResult("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                });

            JsonElement? summaryBlock = response.GetBlock("t1904OutBlock");
            JsonElement? holdingsBlock = response.GetBlock("t1904OutBlock1");

            if (summaryBlock is null)
            {
                string lsMessage = response.RspMessage ?? "";
                string headline = lsMessage.Contains("해당자료가 없습니다")
                    ? $"LS reports '해당자료가 없습니다' (no PDF data available) for shcode {shcode}. This is common on the LS virtual server, which has limited PDF coverage even for liquid ETFs (e.g. KODEX 200 069500). Try the same call against LS_MARKET=real, or check the ETF issuer's official PDF page (samsungfund.com / tigeretf.com / etc.)."
                    : !string.IsNullOrWhiteSpace(lsMessage)
                        ? $"LS returned no PDF block. LS message: '{lsMessage}'. Inspect the raw response with ls_call_tr (tr_cd='t1904')."
                        : "LS returned success (rsp_cd=00000) but no t1904OutBlock and no rsp_msg detail. Inspect the raw response with ls_call_tr (tr_cd='t1904').";
                return McpJson.ErrorResult(
                    headline,
                    new { shcode, rsp_cd = response.RspCode, rsp_msg = response.RspMessage });
            }

            JsonElement s = summaryBlock.Value;

            var holdings = new List<object>();
            // top_n = -1 is the opt-in "full list" sentinel (consistent with
            // themes_limit on ls_holdings_list); a null cap means no truncation.
            int? cap = top_n < 0 ? null : top_n;
            int rowsInResponse = holdingsBlock is { ValueKind: JsonValueKind.Array }
                ? holdingsBlock.Value.GetArrayLength()
                : 0;
            if (holdingsBlock is { ValueKind: JsonValueKind.Array })
            {
                foreach (JsonElement row in holdingsBlock.Value.EnumerateArray())
                {
                    if (cap is int limit && holdings.Count >= limit)
                        break;
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

            long totalMarketValue = s.ReadLong("tot_sigatval");
            long cashKrw = s.ReadLong("cash");

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
                total_market_value = totalMarketValue,
                total_valuation = s.ReadLong("tot_pval"),
                cash = cashKrw,

                holdings_returned = holdings.Count,
                holdings_truncated = holdings.Count < rowsInResponse,
                chart_available = include_chart && holdings.Count > 0,
                holdings,
            };
            string textJson = JsonSerializer.Serialize(payload, McpJson.Tool);

            JsonObject? structured = null;
            if (include_chart && holdings.Count > 0)
                structured = BuildHoldingsChartContent(shcode, holdings, totalMarketValue, cashKrw);

            return McpJson.OkResult(textJson, structured);
        }
        catch (LsAuthException ex)
        {
            return McpJson.ErrorResult("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.ErrorResult("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
    }

    /// <summary>
    /// Converts the anonymous-typed <paramref name="holdings"/> rows the tool
    /// emits in its text payload into the typed view
    /// <see cref="EtfHoldingsChartBuilder.EtfHoldingView"/> expects, then
    /// invokes the builder. Cash percentage is derived from the summary
    /// block: <c>cash / total_market_value × 100</c>.
    /// </summary>
    static JsonObject? BuildHoldingsChartContent(
        string shcode,
        IReadOnlyList<object> holdings,
        long totalMarketValue,
        long cashKrw)
    {
        var views = new List<EtfHoldingsChartBuilder.EtfHoldingView>(holdings.Count);
        foreach (object row in holdings)
        {
            // The text-payload rows are anonymous objects with snake_case
            // property names *in the serialized form*; in-memory they still
            // carry the C# field names (`name`, `weight_percent`, `value`,
            // `shcode`). Round-trip via JsonElement avoids reflection bets.
            JsonElement el = JsonSerializer.SerializeToElement(row, McpJson.Tool);
            string? hShcode = el.TryGetProperty("shcode", out JsonElement sc) ? sc.GetString() : null;
            if (string.IsNullOrEmpty(hShcode)) continue;
            string? name = el.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            double weight = el.TryGetProperty("weight_percent", out JsonElement w)
                ? w.GetDouble()
                : 0.0;
            long marketValue = el.TryGetProperty("value", out JsonElement v)
                ? v.GetInt64()
                : 0L;
            views.Add(new EtfHoldingsChartBuilder.EtfHoldingView(hShcode, name, weight, marketValue));
        }

        double? cashPct = (cashKrw > 0 && totalMarketValue > 0)
            ? (double)cashKrw / totalMarketValue * 100.0
            : (double?)null;

        return EtfHoldingsChartBuilder.Build(shcode, views, cashPct);
    }
}
