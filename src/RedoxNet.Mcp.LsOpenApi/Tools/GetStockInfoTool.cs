using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns a company profile + fundamentals snapshot for a
/// Korean stock (TR <c>t1102</c>), projected to a caller-selected set of
/// sections.
/// </summary>
/// <remarks>
/// v0.9 response-shape SPEC §4.3 (pattern A): the ~160-field t1102 dump is
/// split into sections; the default returns only <c>snapshot</c> +
/// <c>fundamentals</c>. Field mapping is pinned to the official t1102 spec —
/// note LS's counter-intuitive broker suffix convention (<c>d*</c> = 매도 /
/// sell, <c>s*</c> = 매수 / buy). t1102 itself carries no foreign-investor
/// data; the opt-in <c>foreign</c> section (v0.10, SPEC-v0.10 §3) is sourced
/// from a secondary <c>t1716</c> call — foreign holding <em>level</em>, not flow.
/// </remarks>
[McpServerToolType]
public static class GetStockInfoTool
{
    /// <summary>All section names, in canonical emit order.</summary>
    static readonly string[] AllowedSections =
        { "snapshot", "fundamentals", "periods", "brokers", "flags", "foreign" };

    /// <summary>Narrow default — the sections the common question needs.</summary>
    static readonly string[] DefaultSections = { "snapshot", "fundamentals" };

    /// <summary>
    /// Returns the t1102 company-info snapshot, limited to the selected sections.
    /// </summary>
    [McpServerTool(Name = "ls_get_stock_info")]
    [Description("""
        Returns a company profile + fundamentals snapshot for a Korean stock, split into selectable sections.

        USE WHEN: the user wants company/financial context ("삼성전자 어떤 회사야?", "PER 얼마야?", "52주 신고가 근처야?"). Pairs well with ls_get_quote for the level-2 book.
        AVOID WHEN: only the latest price + 10-level order book are needed — use ls_get_quote. For foreign / institutional *flow* (일별 순매수, "외국인 매수세 어때?") use ls_get_investor_flow — the "foreign" section here is the holding *level* (지분율 / 소진율), not the daily flow.

        sections (default ["snapshot","fundamentals"]) — request only what the question needs:
        - "snapshot": 현재가 / 등락 / 거래량 / OHLC / 상하한가 / 회전율.
        - "fundamentals": PER / PBR / EPS, 분기 재무(직전 분기 latest + 그 전 분기 previous — t1102는 당기 진행분을 주지 않음) + 전년대비 성장률, 시가총액·상장주식수·자본금.
        - "periods": 52주 / 연중(YTD) 고저 범위.
        - "brokers": 매수 / 매도 상위 5 거래원.
        - "flags": SPAC / 단기과열 / 저유동성 / 배분 구분 플래그 + 공시 텍스트.
        - "foreign": 외국인 보유주식수 / 지분율 / 소진율 — 보유 잔량(level)이지 일별 흐름이 아님. t1716 별도 호출이라 이 섹션을 고를 때만 추가 API 호출 1회 발생.

        Identity fields (shcode, name, market, currency, listing_date, par_value, trade_unit) are always returned. Unselected sections are omitted; sections_shown echoes what was returned.
        """)]
    public static async Task<string> GetStockInfo(
        LsApiClient apiClient,
        [Description("6-digit Korean short code, e.g. '005930'.")]
        string shcode,
        [Description("""Sections to return — any of "snapshot", "fundamentals", "periods", "brokers", "flags", "foreign". Omit for the default ["snapshot","fundamentals"].""")]
        string[]? sections = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.Error("shcode is required.");

        SectionSelection selection = ResponseShape.ParseSections(sections, AllowedSections, DefaultSections);
        if (selection.Unknown.Count > 0)
            return McpJson.Error(
                $"Unknown section(s): {string.Join(", ", selection.Unknown)}.",
                new { allowed = AllowedSections });

        var want = new HashSet<string>(selection.Selected, StringComparer.OrdinalIgnoreCase);

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1102",
                new JsonObject { ["shcode"] = shcode },
                cancellationToken: cancellationToken).ConfigureAwait(false);

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

            // Identity header — always emitted; tiny and identifies the security.
            var result = new Dictionary<string, object?>
            {
                ["shcode"] = shcode,
                ["name"] = b.ReadString("hname"),
                ["market"] = b.ReadString("janginfo"),
                ["currency"] = b.ReadString("tonghwa"),
                ["listing_date"] = b.ReadString("listdate"),
                ["par_value"] = b.ReadLong("parprice"),
                ["trade_unit"] = b.ReadString("memedan"),
                ["sections_shown"] = selection.Selected,
            };

            if (want.Contains("snapshot"))
                result["snapshot"] = new
                {
                    price = b.ReadLong("price"),
                    sign = b.ReadString("sign"),
                    change = b.ReadLong("change"),
                    change_percent = b.ReadDouble("diff"),
                    volume = b.ReadLong("volume"),
                    volume_diff = b.ReadLong("volumediff"),
                    yesterday_volume = b.ReadLong("jnilvolume"),
                    yesterday_same_time_volume = b.ReadLong("jvolume"),
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
                    turnover_ratio_percent = b.ReadDouble("vol"),
                };

            if (want.Contains("fundamentals"))
                result["fundamentals"] = new
                {
                    per = b.ReadDouble("per"),
                    expected_per = b.ReadDouble("t_per"),
                    pbr = b.ReadDouble("pbrx"),
                    // t1102 reports the two most recently *settled* periods —
                    // there is no current-quarter-in-progress data.
                    latest_period_label = b.ReadString("name"),
                    previous_period_label = b.ReadString("name2"),
                    settlement_month = b.ReadString("gsmm"),
                    sales_latest = b.ReadLong("bfsales"),
                    sales_previous = b.ReadLong("bfsales2"),
                    operating_income_latest = b.ReadLong("bfoperatingincome"),
                    operating_income_previous = b.ReadLong("bfoperatingincome2"),
                    ordinary_income_latest = b.ReadLong("bfordinaryincome"),
                    ordinary_income_previous = b.ReadLong("bfordinaryincome2"),
                    net_income_latest = b.ReadLong("bfnetincome"),
                    net_income_previous = b.ReadLong("bfnetincome2"),
                    eps_latest = b.ReadDouble("bfeps"),
                    eps_previous = b.ReadDouble("bfeps2"),
                    // *rt fields are year-over-year (전년대비) growth percentages.
                    growth_percent = new
                    {
                        sales = b.ReadDouble("salert"),
                        operating_income = b.ReadDouble("opert"),
                        ordinary_income = b.ReadDouble("ordrt"),
                        net_income = b.ReadDouble("netrt"),
                        eps = b.ReadDouble("epsrt"),
                    },
                    capital = new
                    {
                        market_cap_in_100m_won = b.ReadLong("total"),
                        shares_in_thousands = b.ReadLong("listing"),
                        capital_in_100m_won = b.ReadLong("capital"),
                        margin_ratio_percent = b.ReadLong("jkrate"),
                        issue_price = b.ReadLong("issueprice"),
                        target_price = b.ReadLong("target"),
                    },
                };

            if (want.Contains("periods"))
                result["periods"] = new
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
                };

            // LS broker suffix convention is counter-intuitive: 'd*' fields are
            // 매도 (sell), 's*' fields are 매수 (buy). offerno* names the sell-side
            // broker, bidno* the buy-side.
            if (want.Contains("brokers"))
                result["brokers"] = new
                {
                    sellers = Enumerable.Range(1, 5).Select(i => new
                    {
                        rank = i,
                        code = b.ReadString($"offernocd{i}"),
                        name = b.ReadString($"offerno{i}"),
                        avg_price = b.ReadLong($"davg{i}"),
                        volume = b.ReadLong($"dvol{i}"),
                        value = b.ReadLong($"dval{i}"),
                        change = b.ReadLong($"dcha{i}"),
                        change_percent = b.ReadDouble($"ddiff{i}"),
                    }).ToList(),
                    buyers = Enumerable.Range(1, 5).Select(i => new
                    {
                        rank = i,
                        code = b.ReadString($"bidnocd{i}"),
                        name = b.ReadString($"bidno{i}"),
                        avg_price = b.ReadLong($"savg{i}"),
                        volume = b.ReadLong($"svol{i}"),
                        value = b.ReadLong($"sval{i}"),
                        change = b.ReadLong($"scha{i}"),
                        change_percent = b.ReadDouble($"sdiff{i}"),
                    }).ToList(),
                };

            if (want.Contains("flags"))
                result["flags"] = new
                {
                    is_spac = string.Equals(b.ReadString("spac_gubun"), "Y", StringComparison.OrdinalIgnoreCase),
                    abnormal_rise = b.ReadString("abnormal_rise_gu"),
                    low_liquidity = b.ReadString("low_lqdt_gu"),
                    allocation_class = b.ReadString("alloc_gubun"),
                    allocation_text = b.ReadString("alloc_text"),
                    short_term = b.ReadString("shterm_text"),
                    type_text = b.ReadString("ty_text"),
                    lending_text = b.ReadString("lend_text"),
                    info = new[]
                    {
                        b.ReadString("info1"), b.ReadString("info2"), b.ReadString("info3"),
                        b.ReadString("info4"), b.ReadString("info5"),
                    },
                };

            // Opt-in foreign section: a secondary t1716 call (SPEC-v0.10 §3).
            // Only fires when explicitly selected, so the default path stays 1 TR.
            if (want.Contains("foreign"))
                result["foreign"] = await BuildForeignSectionAsync(
                    apiClient, shcode, b.ReadLong("listing"), cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Serialize(result, McpJson.Tool);
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
    /// Builds the <c>foreign</c> section from a secondary <c>t1716</c> call —
    /// foreign holding <em>level</em> (보유주식수 / 지분율 / 소진율), not daily
    /// flow. Sourced separately because t1102 carries no foreign-investor data.
    /// </summary>
    /// <remarks>
    /// Resilient by design: a t1716 business error, empty result, or auth /
    /// transport failure degrades to a section carrying a <c>note</c> rather
    /// than failing the whole stock_info call — the t1102 sections already
    /// succeeded by the time this runs.
    /// </remarks>
    static async Task<object> BuildForeignSectionAsync(
        LsApiClient apiClient,
        string shcode,
        long listingThousands,
        CancellationToken cancellationToken)
    {
        // A ~3-week window clears long holidays so the latest trading day's
        // holding level is in range; t1716 returns newest-first, row [0] = latest.
        string todt = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string fromdt = DateTime.Now.AddDays(-20).ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1716",
                new JsonObject
                {
                    ["shcode"] = shcode,
                    ["gubun"] = "0",       // per-day; we only need the latest row
                    ["fromdt"] = fromdt,
                    ["todt"] = todt,
                    ["prapp"] = 0,
                    ["prgubun"] = "0",     // no program-trading adjustment
                    ["orggubun"] = "0",
                    ["frggubun"] = "0",
                    ["exchgubun"] = "U",   // unified KRX + NXT
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return new { note = $"t1716 foreign data unavailable (rsp_cd={response.RspCode}: {response.RspMessage})." };

            JsonElement? array = response.GetBlock("t1716OutBlock");
            if (array is null || array.Value.ValueKind != JsonValueKind.Array || array.Value.GetArrayLength() == 0)
                return new { note = "No t1716 foreign-holdings data for this stock (신규 상장 / 거래정지 / ETF 등 가능)." };

            JsonElement latest = array.Value[0];
            long heldShares = latest.ReadLong("fsc_listing");

            // fsc_sjrate ships ×100-scaled ("5251.00" = 52.51%). listing is in
            // thousands of shares, so total shares = listingThousands × 1000.
            return new
            {
                as_of = latest.ReadString("date"),
                held_shares = heldShares,
                ownership_percent = listingThousands > 0
                    ? Math.Round(heldShares / (listingThousands * 1000.0) * 100.0, 2)
                    : (double?)null,
                exhaustion_rate_percent = Math.Round(latest.ReadDouble("fsc_sjrate") / 100.0, 2),
            };
        }
        catch (LsAuthException ex)
        {
            return new { note = $"t1716 foreign data unavailable: authentication failed ({ex.Message})." };
        }
        catch (LsTrException ex)
        {
            return new { note = $"t1716 foreign data unavailable: {ex.Message}" };
        }
    }
}
