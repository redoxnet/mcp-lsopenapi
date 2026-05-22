using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Charting;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool returning KOSPI / KOSDAQ program-trading (프로그램매매) flow.
/// </summary>
/// <remarks>
/// Two scopes share the tool:
/// <list type="bullet">
///   <item><description><c>market</c> — market-wide intraday series via TR
///   <c>t1662</c> (시간대별 프로그램매매 추이). The arbitrage / non-arbitrage split
///   is the headline; the response carries a scalar summary, deterministic
///   trajectory key-points, and a <c>dataset_id</c> handle to the full series.</description></item>
///   <item><description><c>ranking</c> — per-stock net-buy ranking via TR
///   <c>t1636</c> (종목별 프로그램매매 동향). The response carries the ranked rows
///   plus the normalized footprint metric (<c>mktcap_ratio</c>).</description></item>
///   <item><description><c>stock</c> — one stock's program-trading flow via TR
///   <c>t1637</c> (종목별 프로그램매매 추이) — an intraday cumulative series or a
///   per-day history.</description></item>
/// </list>
/// Either scope ships a Plotly v5 spec under <c>structuredContent.chart</c> when
/// <c>include_chart</c> is set.
/// </remarks>
[McpServerToolType]
public static class GetProgramTradingTool
{
    /// <summary>t1662 / t1633 amounts are LS amount-basis 백만원; ÷100 → 억원.</summary>
    const double MillionWonPerEokwon = 100.0;

    /// <summary>t1636 program amounts are LS amount-basis 천원; ÷100,000 puts them on the 억원 scale.</summary>
    const double ThousandWonPerEokwon = 100_000.0;

    /// <summary>
    /// Returns program-trading flow for one market — intraday series or per-stock ranking.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="scope">Data scope: <c>market</c> (default, t1662) or <c>ranking</c> (t1636).</param>
    /// <param name="market">Target market: <c>kospi</c> (default) or <c>kosdaq</c>.</param>
    /// <param name="day">Market scope only — session: <c>today</c> (default) or <c>yesterday</c>.</param>
    /// <param name="sort">Ranking scope only — sort: <c>net_buy</c> (default), <c>net_sell</c>, or <c>mktcap_weight</c>.</param>
    /// <param name="measure">Ranking scope only — metric: <c>amount</c> (default) or <c>quantity</c>.</param>
    /// <param name="limit">Row cap — ranking: stocks (5–20); daily periods: days (10–120). Default 20.</param>
    /// <param name="shcode">Stock scope only — 6-digit short code (required).</param>
    /// <param name="period">Market &amp; stock scope — <c>intraday</c> (default) or <c>daily</c>.</param>
    /// <param name="name">Stock scope only — optional stock name for the chart title.</param>
    /// <param name="include_chart">If true, ship a Plotly v5 spec as structuredContent for inline rendering.</param>
    /// <param name="chart_view">Market scope only — which chart view to render.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Text content plus an optional <c>structuredContent.chart</c>.</returns>
    [McpServerTool(Name = "ls_get_program_trading")]
    [Description("""
        Returns program-trading (프로그램매매) flow for KOSPI / KOSDAQ via LS TRs. Program trades are basket / arbitrage orders routed through the program channel — a blend of foreign and institutional baskets, not an investor-specific feed.

        scope='market' (default): market-wide program-trading flow — period='intraday' (t1662 ~1-minute 차익/비차익 series + KOSPI200 + futures basis) or period='daily' (t1633 per-day 차익/비차익 history). Applies: market, period, day (intraday), chart_view (intraday).
        scope='ranking': per-stock net-buy ranking via t1636 — which stocks programs are buying / selling right now. Applies: market, sort, measure, limit.
        scope='stock': one stock's program-trading flow via t1637. Applies: shcode (required), period ('intraday' cumulative minute series, or 'daily' per-day history), name, limit (daily day count).

        USE WHEN: the user asks about 프로그램매매 / program trading / 차익·비차익 / basket flow, which stocks programs are accumulating, or program flow for a specific stock.
        AVOID WHEN: the user wants investor-class flow (개인/외국인/기관) — use ls_get_investor_flow.

        Non-arbitrage (비차익) net buying is the directional basket signal (foreign and institutional baskets blended, not 기관-specific); arbitrage (차익) is basis-driven and mechanical. In ranking scope, mktcap_ratio (net buying ÷ market cap) is the normalized footprint — a small-cap with a high ratio is a stronger signal than a large-cap with a big absolute number. sort='mktcap_weight' ranks by that footprint directly.

        Set include_chart=true to ship a Plotly v5 chart as structuredContent so MCP Apps hosts render it inline at zero token cost.
        - market intraday chart_view: flow_overview (default — K200 vs cumulative 전체/비차익/차익), basis_arbitrage (basis vs 차익), intensity_bars (per-minute net bars), gross_flow (per-5-min gross 매수/매도 in 비차익/차익 panels).
        - market daily: a per-day 비차익/차익 stacked-bar chart vs the index (chart_view is ignored).
        - ranking scope: a horizontal bar chart of the top stocks by net buying (chart_view is ignored).
        - stock scope: an intraday cumulative line (price vs program net buying) or a per-day coloured bar chart (chart_view is ignored).

        All program amounts — in the text and the charts alike — are reported in 억원.
        """)]
    public static async Task<CallToolResult> GetProgramTrading(
        LsApiClient apiClient,
        [Description("Data scope: 'market' (default, intraday market-wide via t1662) or 'ranking' (per-stock ranking via t1636).")]
        string scope = "market",
        [Description("Target market: 'kospi' (default) or 'kosdaq'.")]
        string market = "kospi",
        [Description("Market scope only — session: 'today' (default) or 'yesterday'.")]
        string day = "today",
        [Description("Ranking scope only — sort: 'net_buy' (default), 'net_sell', or 'mktcap_weight' (footprint).")]
        string sort = "net_buy",
        [Description("Ranking scope only — metric: 'amount' (default) or 'quantity'.")]
        string measure = "amount",
        [Description("Row cap. Ranking: number of stocks (5–20). Daily periods (market or stock): number of days (10–120). Default 20.")]
        int limit = 20,
        [Description("Stock scope only — 6-digit short code (required when scope='stock'), e.g. '005930'.")]
        string shcode = "",
        [Description("Market & stock scope — 'intraday' (default) or 'daily'. market+daily uses t1633, stock+daily uses t1637 daily.")]
        string period = "intraday",
        [Description("Stock scope only — optional stock name for the chart title, e.g. '삼성전자'.")]
        string name = "",
        [Description("If true, ship a Plotly v5 spec as structuredContent for inline chart rendering on MCP Apps hosts. Default false.")]
        bool include_chart = false,
        [Description("Market scope only — chart view: 'flow_overview' (default), 'basis_arbitrage', 'intensity_bars', or 'gross_flow'.")]
        string chart_view = "flow_overview",
        CancellationToken cancellationToken = default)
    {
        string scopeNorm = (scope ?? "").Trim().ToLowerInvariant();
        if (scopeNorm.Length == 0) scopeNorm = "market";

        string marketNorm = (market ?? "").Trim().ToLowerInvariant();
        string gubun = marketNorm switch
        {
            "" or "kospi" => "0",
            "kosdaq" => "1",
            _ => "",
        };
        if (gubun.Length == 0)
            return McpJson.ErrorResult("market must be 'kospi' or 'kosdaq'.", new { received = market });
        if (marketNorm.Length == 0) marketNorm = "kospi";

        switch (scopeNorm)
        {
            case "market":
            {
                (_, bool isDaily, string? periodError) = ParsePeriod(period);
                if (periodError is not null)
                    return McpJson.ErrorResult(periodError, new { received = period });
                return isDaily
                    ? await GetMarketDailyAsync(
                        apiClient, marketNorm, gubun, limit, include_chart, cancellationToken)
                        .ConfigureAwait(false)
                    : await GetMarketFlowAsync(
                        apiClient, marketNorm, gubun, day, include_chart, chart_view, cancellationToken)
                        .ConfigureAwait(false);
            }
            case "ranking":
                return await GetRankingAsync(
                    apiClient, marketNorm, gubun, sort, measure, limit, include_chart, cancellationToken)
                    .ConfigureAwait(false);
            case "stock":
                return await GetStockFlowAsync(
                    apiClient, shcode, period, name, limit, include_chart, cancellationToken)
                    .ConfigureAwait(false);
            default:
                return McpJson.ErrorResult(
                    "scope must be 'market', 'ranking', or 'stock'.", new { received = scope });
        }
    }

    // ───────────────────────────── market scope (t1662) ─────────────────────────────

    /// <summary>Market-wide intraday program-trading flow via TR t1662.</summary>
    static async Task<CallToolResult> GetMarketFlowAsync(
        LsApiClient apiClient,
        string marketNorm,
        string gubun,
        string day,
        bool includeChart,
        string chartView,
        CancellationToken cancellationToken)
    {
        string dayNorm = (day ?? "").Trim().ToLowerInvariant();
        string gubun3 = dayNorm switch
        {
            "" or "today" => "0",
            "yesterday" => "1",
            _ => "",
        };
        if (gubun3.Length == 0)
            return McpJson.ErrorResult("day must be 'today' or 'yesterday'.", new { received = day });
        if (dayNorm.Length == 0) dayNorm = "today";

        // Validate the chart view up front so an unknown value fails before the
        // TR call instead of after. Ignored entirely when include_chart=false.
        ProgramTradeChartView chartViewEnum = ProgramTradeChartView.FlowOverview;
        string chartViewName = "flow_overview";
        if (includeChart)
        {
            (ProgramTradeChartView view, string canonical, string? viewError) = ParseChartView(chartView);
            if (viewError is not null)
                return McpJson.ErrorResult(viewError, new { received = chartView });
            chartViewEnum = view;
            chartViewName = canonical;
        }

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1662",
                new JsonObject
                {
                    ["gubun"] = gubun,
                    ["gubun1"] = "0",   // 0 = 금액 (amount basis)
                    ["gubun3"] = gubun3,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.ErrorResult("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    market = marketNorm,
                    day = dayNorm,
                });

            // t1662OutBlock is itself the time-series array (no separate header block).
            // LS ships it newest-first; collect, then flip to chronological order.
            var scratch = new List<ProgramRow>();
            JsonElement? block = response.GetBlock("t1662OutBlock");
            if (block is not null && block.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in block.Value.EnumerateArray())
                {
                    string time = r.ReadString("time")?.Trim() ?? "";
                    if (time.Length == 0) continue;
                    scratch.Add(new ProgramRow(
                        time,
                        r.ReadDouble("k200jisu"),
                        IndustryDataCache.ApplySign(r.ReadDouble("change"), r.ReadString("sign")),
                        r.ReadDouble("k200basis"),
                        r.ReadLong("tot3"),
                        r.ReadLong("cha3"),
                        r.ReadLong("bcha3"),
                        r.ReadLong("cha1"),
                        r.ReadLong("cha2"),
                        r.ReadLong("bcha1"),
                        r.ReadLong("bcha2")));
                }
            }

            if (scratch.Count == 0)
                return McpJson.ErrorResult("LS returned no program-trading rows.", new
                {
                    market = marketNorm,
                    day = dayNorm,
                    rsp_cd = response.RspCode,
                    hint = "The market may be pre-open, or t1662 has no data for the requested session.",
                });

            scratch.Reverse();

            // Cumulative → per-minute delta. minute_net[0] folds in the opening auction.
            var minutes = new List<ProgramMinute>(scratch.Count);
            for (int i = 0; i < scratch.Count; i++)
            {
                long minuteNet = i == 0 ? scratch[i].Net : scratch[i].Net - scratch[i - 1].Net;
                ProgramRow s = scratch[i];
                minutes.Add(new ProgramMinute(
                    s.Time, s.K200, s.Change, s.Basis, s.Net, s.Arb, s.NonArb, minuteNet,
                    s.ArbBuy, s.ArbSell, s.NonArbBuy, s.NonArbSell));
            }

            string datasetId = DatasetHandleCache.Add("program_trading", new ProgramTradingDataset(
                marketNorm, dayNorm, "t1662", minutes, DateTimeOffset.UtcNow));

            ProgramMinute last = minutes[^1];
            var summary = new
            {
                market = marketNorm,
                day = dayNorm,
                value_unit = "억원",
                net = last.Net / MillionWonPerEokwon,
                arbitrage = last.Arbitrage / MillionWonPerEokwon,
                non_arbitrage = last.NonArbitrage / MillionWonPerEokwon,
                kospi200 = last.Kospi200,
                kospi200_change = last.Kospi200Change,
                basis = last.Basis,
            };

            var payload = new
            {
                tr_cd = "t1662",
                scope = "market",
                market = marketNorm,
                day = dayNorm,
                dataset_id = datasetId,
                total_minutes = minutes.Count,
                time_range = new { from = FormatTime(minutes[0].Time), to = FormatTime(last.Time) },
                summary,
                key_points = BuildKeyPoints(minutes),
                chart_available = includeChart,
                chart_view = includeChart ? chartViewName : null,
                note = "net / arbitrage / non_arbitrage are program net buying in 억원, cumulative from the session open; minute_net is the per-minute delta. The full minute series is cached under dataset_id. Non-arbitrage (비차익) is the directional basket signal (foreign and institutional baskets blended, not 기관-specific); arbitrage (차익) tracks the basis. Cross-check ls_get_investor_flow for investor-class flow.",
            };

            JsonObject? structured = includeChart
                ? new JsonObject { ["chart"] = BuildFlowChart(chartViewEnum, marketNorm, dayNorm, minutes) }
                : null;

            return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool), structured);
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

    /// <summary>Market-wide daily program-trading history via TR t1633.</summary>
    static async Task<CallToolResult> GetMarketDailyAsync(
        LsApiClient apiClient,
        string marketNorm,
        string gubun,
        int limit,
        bool includeChart,
        CancellationToken cancellationToken)
    {
        int cappedDays = Math.Clamp(limit, 10, 120);
        DateTime today = DateTime.Today;
        // A generous calendar span so the page covers cappedDays trading days.
        string fdate = today.AddDays(-(cappedDays * 2 + 20))
            .ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string tdate = today.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1633",
                new JsonObject
                {
                    ["gubun"] = gubun,
                    ["gubun1"] = "0",    // 0 = 금액 (amount basis)
                    ["gubun2"] = "0",    // 0 = 수치 (per-day values, not cumulative)
                    ["gubun3"] = "1",    // 1 = 일 (daily)
                    ["fdate"] = fdate,
                    ["tdate"] = tdate,
                    ["gubun4"] = "0",
                    ["date"] = "",
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.ErrorResult("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    market = marketNorm,
                    period = "daily",
                });

            var rows = new List<ProgramDailyRow>();
            JsonElement? block = response.GetBlock("t1633OutBlock1");
            if (block is not null && block.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in block.Value.EnumerateArray())
                {
                    string date = r.ReadString("date")?.Trim() ?? "";
                    if (date.Length != 8) continue;
                    rows.Add(new ProgramDailyRow(
                        Date: FormatDate(date),
                        Kospi200: r.ReadDouble("jisu"),
                        Net: r.ReadLong("tot3") / MillionWonPerEokwon,
                        Arbitrage: r.ReadLong("cha3") / MillionWonPerEokwon,
                        NonArbitrage: r.ReadLong("bcha3") / MillionWonPerEokwon));
                }
            }

            if (rows.Count == 0)
                return McpJson.ErrorResult("LS returned no daily program-trading rows.", new
                {
                    market = marketNorm,
                    period = "daily",
                    rsp_cd = response.RspCode,
                });

            rows.Reverse();   // LS ships newest-first → chronological
            if (rows.Count > cappedDays)
                rows = rows.GetRange(rows.Count - cappedDays, cappedDays);

            ProgramDailyRow last = rows[^1];
            var payload = new
            {
                tr_cd = "t1633",
                scope = "market",
                period = "daily",
                market = marketNorm,
                value_unit = "억원",
                count = rows.Count,
                date_range = new { from = rows[0].Date, to = last.Date },
                summary = new
                {
                    net_sum = rows.Sum(x => x.Net),
                    arbitrage_sum = rows.Sum(x => x.Arbitrage),
                    non_arbitrage_sum = rows.Sum(x => x.NonArbitrage),
                    buy_days = rows.Count(x => x.Net > 0),
                    sell_days = rows.Count(x => x.Net < 0),
                    last_kospi200 = last.Kospi200,
                },
                rows = rows.Select(x => new
                {
                    date = x.Date,
                    net = x.Net,
                    arbitrage = x.Arbitrage,
                    non_arbitrage = x.NonArbitrage,
                    kospi200 = x.Kospi200,
                }),
                chart_available = includeChart,
                note = "Each row is one day's program net buying (net = arbitrage + non_arbitrage), in 억원. non_arbitrage (비차익) is the directional basket signal (foreign and institutional baskets blended, not 기관-specific). Cross-check ls_get_investor_flow for investor-class flow.",
            };

            JsonObject? structured = includeChart
                ? new JsonObject { ["chart"] = BuildMarketDailyChart(marketNorm, rows) }
                : null;

            return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool), structured);
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

    /// <summary>Builds the MarketDaily chart envelope from daily t1633 rows.</summary>
    static JsonObject BuildMarketDailyChart(string market, IReadOnlyList<ProgramDailyRow> rows)
    {
        var meta = new ProgramChartMeta(
            market, "today", $"{rows[0].Date} ~ {rows[^1].Date}", "금액 (억원)");

        var points = new List<ProgramTradeChartBuilder.ProgramFlowPoint>(rows.Count);
        foreach (ProgramDailyRow x in rows)
        {
            points.Add(new ProgramTradeChartBuilder.ProgramFlowPoint(
                Time: x.Date,
                Kospi200: x.Kospi200,
                Basis: 0,
                Net: x.Net,
                Arbitrage: x.Arbitrage,
                NonArbitrage: x.NonArbitrage,
                MinuteNet: 0));
        }
        return ProgramTradeChartBuilder.Build(ProgramTradeChartView.MarketDaily, meta, points);
    }

    // ───────────────────────────── ranking scope (t1636) ────────────────────────────

    /// <summary>Per-stock program-trading net-buy ranking via TR t1636.</summary>
    static async Task<CallToolResult> GetRankingAsync(
        LsApiClient apiClient,
        string marketNorm,
        string gubun,
        string sort,
        string measure,
        int limit,
        bool includeChart,
        CancellationToken cancellationToken)
    {
        (string gubun2, string sortLabel, string? sortError) = ParseSort(sort);
        if (sortError is not null)
            return McpJson.ErrorResult(sortError, new { received = sort });

        (string gubun1, bool isAmount, string? measureError) = ParseMeasure(measure);
        if (measureError is not null)
            return McpJson.ErrorResult(measureError, new { received = measure });

        // t1636 returns one ~20-row page; clamp the cap to it.
        int cappedLimit = Math.Clamp(limit, 5, 20);

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1636",
                new JsonObject
                {
                    ["gubun"] = gubun,
                    ["gubun1"] = gubun1,
                    ["gubun2"] = gubun2,
                    ["shcode"] = "",      // empty = market-wide ranking
                    ["cts_idx"] = 0,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.ErrorResult("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    market = marketNorm,
                    sort,
                });

            var rows = new List<ProgramRankRow>();
            JsonElement? block = response.GetBlock("t1636OutBlock1");
            if (block is not null && block.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in block.Value.EnumerateArray())
                {
                    long net = isAmount ? r.ReadLong("svalue") : r.ReadLong("svolume");
                    long buy = isAmount ? r.ReadLong("stksvalue") : r.ReadLong("stksvolume");
                    long sell = isAmount ? r.ReadLong("offervalue") : r.ReadLong("offervolume");
                    rows.Add(new ProgramRankRow(
                        Rank: (int)r.ReadLong("rank"),
                        Name: CleanName(r.ReadString("hname")),
                        Shcode: r.ReadString("shcode")?.Trim() ?? "",
                        Price: r.ReadLong("price"),
                        ChangePct: r.ReadDouble("diff"),
                        Net: net,
                        Buy: buy,
                        Sell: sell,
                        MktCapRatio: r.ReadDouble("mkcap_cmpr_val")));
                }
            }

            if (rows.Count == 0)
                return McpJson.ErrorResult("LS returned no program-trading ranking rows.", new
                {
                    market = marketNorm,
                    sort,
                    rsp_cd = response.RspCode,
                    hint = "The market may be pre-open, or t1636 has no data right now.",
                });

            if (rows.Count > cappedLimit)
                rows = rows.GetRange(0, cappedLimit);

            double divisor = isAmount ? ThousandWonPerEokwon : 1.0;  // 천원 → 억원, or shares as-is
            string valueUnit = isAmount ? "억원" : "주";
            string asOf = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var payload = new
            {
                tr_cd = "t1636",
                scope = "ranking",
                market = marketNorm,
                sort,
                measure,
                value_unit = valueUnit,
                as_of = asOf,
                count = rows.Count,
                rows = rows.Select(r => new
                {
                    rank = r.Rank,
                    name = r.Name,
                    shcode = r.Shcode,
                    price = r.Price,
                    change_pct = r.ChangePct,
                    net = r.Net / divisor,
                    buy = r.Buy / divisor,
                    sell = r.Sell / divisor,
                    mktcap_ratio = r.MktCapRatio,
                }),
                chart_available = includeChart,
                note = $"Rows are LS-sorted (rank-ascending). net/buy/sell are program-trading values in {valueUnit}; mktcap_ratio is net buying as % of market cap — the normalized program-trading footprint. The program channel blends foreign baskets, index arbitrage, and institutional baskets, and misses off-program institutional (direct-order) buying; cross-check ls_get_investor_flow (investors=[\"all\"]) for investor-class flow.",
            };

            JsonObject? structured = includeChart
                ? new JsonObject
                {
                    ["chart"] = BuildRankingChart(marketNorm, sortLabel, isAmount, asOf, rows, divisor),
                }
                : null;

            return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool), structured);
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

    /// <summary>Maps the model-facing <c>sort</c> onto the t1636 <c>gubun2</c> sort code.</summary>
    static (string Gubun2, string Label, string? Error) ParseSort(string? raw)
    {
        string v = (raw ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "" or "net_buy" => ("1", "순매수 상위", null),
            "net_sell" => ("2", "순매도 상위", null),
            "mktcap_weight" or "footprint" => ("0", "시총대비 footprint 상위", null),
            _ => ("", "", $"Unknown sort '{raw}'. Use net_buy, net_sell, or mktcap_weight."),
        };
    }

    /// <summary>
    /// Maps the model-facing <c>measure</c> onto the t1636 <c>gubun1</c> code.
    /// NOTE: t1636's gubun1 is 0=수량 / 1=금액 — the opposite polarity of t1662's gubun1.
    /// </summary>
    static (string Gubun1, bool IsAmount, string? Error) ParseMeasure(string? raw)
    {
        string v = (raw ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "" or "amount" => ("1", true, null),
            "quantity" or "volume" => ("0", false, null),
            _ => ("", false, $"Unknown measure '{raw}'. Use amount or quantity."),
        };
    }

    /// <summary>
    /// Strips the U+FFFD replacement char LS leaves when it truncates an
    /// over-length <c>hname</c> mid-character, then trims surrounding whitespace.
    /// </summary>
    static string CleanName(string? raw) =>
        (raw ?? "").Replace("�", "").Trim();

    /// <summary>
    /// Builds the ranking horizontal-bar chart envelope. Values are converted to
    /// the display unit (억원 for amount; shares as-is for quantity).
    /// </summary>
    static JsonObject BuildRankingChart(
        string market,
        string sortLabel,
        bool isAmount,
        string asOf,
        IReadOnlyList<ProgramRankRow> rows,
        double divisor)
    {
        string measureLabel = isAmount ? "순매수 금액 (억원)" : "순매수 수량 (주)";
        var meta = new ProgramRankingChartMeta(market, sortLabel, measureLabel, asOf);

        var chartRows = new List<ProgramRankingRow>(rows.Count);
        foreach (ProgramRankRow r in rows)
        {
            chartRows.Add(new ProgramRankingRow(
                Rank: r.Rank,
                Name: r.Name,
                Shcode: r.Shcode,
                NetValue: r.Net / divisor,
                MktCapRatio: r.MktCapRatio,
                Diff: r.ChangePct));
        }

        return ProgramRankingChartBuilder.Build(meta, chartRows);
    }

    // ───────────────────────────── stock scope (t1637) ──────────────────────────────

    /// <summary>One stock's program-trading flow via TR t1637 — intraday series or daily history.</summary>
    static async Task<CallToolResult> GetStockFlowAsync(
        LsApiClient apiClient,
        string shcode,
        string period,
        string name,
        int limit,
        bool includeChart,
        CancellationToken cancellationToken)
    {
        string code = (shcode ?? "").Trim();
        if (code.Length == 0)
            return McpJson.ErrorResult("shcode is required for scope='stock'.", new { scope = "stock" });

        (string gubun2, bool isDaily, string? periodError) = ParsePeriod(period);
        if (periodError is not null)
            return McpJson.ErrorResult(periodError, new { received = period });

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1637",
                new JsonObject
                {
                    ["gubun1"] = "1",      // 1 = 금액 (amount basis)
                    ["gubun2"] = gubun2,   // 0 = 시간 (intraday), 1 = 일자 (daily)
                    ["shcode"] = code,
                    ["date"] = "",
                    ["time"] = "",
                    ["cts_idx"] = 9999,    // 9999 = chart query — the full series
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.ErrorResult("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    shcode = code,
                    period = isDaily ? "daily" : "intraday",
                });

            var obs = new List<ProgramStockObs>();
            string sessionDate = "";
            JsonElement? block = response.GetBlock("t1637OutBlock1");
            if (block is not null && block.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in block.Value.EnumerateArray())
                {
                    string rawDate = r.ReadString("date")?.Trim() ?? "";
                    string rawTime = r.ReadString("time")?.Trim() ?? "";
                    if (rawDate.Length == 8) sessionDate = FormatDate(rawDate);
                    obs.Add(new ProgramStockObs(
                        Label: isDaily ? FormatDate(rawDate) : FormatTime(rawTime),
                        Price: r.ReadLong("price"),
                        Net: r.ReadLong("svalue") / ThousandWonPerEokwon,
                        Buy: r.ReadLong("stksvalue") / ThousandWonPerEokwon,
                        Sell: r.ReadLong("offervalue") / ThousandWonPerEokwon,
                        ChangePct: r.ReadDouble("diff")));
                }
            }

            if (obs.Count == 0)
                return McpJson.ErrorResult("LS returned no program-trading rows for this stock.", new
                {
                    shcode = code,
                    period = isDaily ? "daily" : "intraday",
                    rsp_cd = response.RspCode,
                    hint = "Check the short code, or the stock may have no program-trading data.",
                });

            obs.Reverse();   // LS ships newest-first → chronological

            if (isDaily)
            {
                int cappedDays = Math.Clamp(limit, 10, 100);
                if (obs.Count > cappedDays)
                    obs = obs.GetRange(obs.Count - cappedDays, cappedDays);
            }

            string? displayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            return isDaily
                ? BuildStockDailyResult(code, displayName, obs, includeChart)
                : BuildStockIntradayResult(code, displayName, sessionDate, obs, includeChart);
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

    /// <summary>Builds the intraday stock-scope response: summary + key-points + optional chart.</summary>
    static CallToolResult BuildStockIntradayResult(
        string code, string? name, string sessionDate,
        IReadOnlyList<ProgramStockObs> obs, bool includeChart)
    {
        ProgramStockObs last = obs[^1];
        int peak = 0, trough = 0;
        for (int i = 1; i < obs.Count; i++)
        {
            if (obs[i].Net > obs[peak].Net) peak = i;
            if (obs[i].Net < obs[trough].Net) trough = i;
        }

        var labels = new SortedDictionary<int, List<string>>();
        void Mark(int idx, string label)
        {
            if (!labels.TryGetValue(idx, out List<string>? l)) { l = new(); labels[idx] = l; }
            if (!l.Contains(label)) l.Add(label);
        }
        Mark(0, "open");
        Mark(peak, "peak_net");
        Mark(trough, "trough_net");
        Mark(obs.Count - 1, "last");

        var keyPoints = labels.Select(kv => new
        {
            label = string.Join("+", kv.Value),
            time = obs[kv.Key].Label,
            net = obs[kv.Key].Net,
            price = obs[kv.Key].Price,
        });

        var payload = new
        {
            tr_cd = "t1637",
            scope = "stock",
            period = "intraday",
            shcode = code,
            name,
            as_of = sessionDate,
            value_unit = "억원",
            total_minutes = obs.Count,
            time_range = new { from = obs[0].Label, to = last.Label },
            summary = new
            {
                latest_net = last.Net,
                session_high = obs.Max(o => o.Net),
                session_low = obs.Min(o => o.Net),
                last_price = last.Price,
            },
            key_points = keyPoints,
            chart_available = includeChart,
            note = "svalue is cumulative program net buying from the session open, in 억원. Positive = the program channel was a net buyer of this stock; negative = a net seller. The program channel blends foreign baskets, index arbitrage, and institutional baskets, and misses off-program institutional (direct-order) buying; cross-check ls_get_investor_flow (investors=[\"all\"]) for investor-class flow.",
        };

        JsonObject? structured = includeChart
            ? new JsonObject
            {
                ["chart"] = ProgramStockChartBuilder.Build(
                    ProgramStockChartView.IntradayFlow,
                    new ProgramStockChartMeta(code, name ?? "", sessionDate),
                    obs.Select(o => new ProgramStockPoint(o.Label, o.Price, o.Net)).ToList()),
            }
            : null;

        return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool), structured);
    }

    /// <summary>Builds the daily stock-scope response: per-day rows + optional chart.</summary>
    static CallToolResult BuildStockDailyResult(
        string code, string? name,
        IReadOnlyList<ProgramStockObs> obs, bool includeChart)
    {
        ProgramStockObs last = obs[^1];
        var payload = new
        {
            tr_cd = "t1637",
            scope = "stock",
            period = "daily",
            shcode = code,
            name,
            value_unit = "억원",
            count = obs.Count,
            date_range = new { from = obs[0].Label, to = last.Label },
            summary = new
            {
                net_sum = obs.Sum(o => o.Net),
                buy_days = obs.Count(o => o.Net > 0),
                sell_days = obs.Count(o => o.Net < 0),
                last_price = last.Price,
            },
            rows = obs.Select(o => new
            {
                date = o.Label,
                net = o.Net,
                buy = o.Buy,
                sell = o.Sell,
                price = o.Price,
                change_pct = o.ChangePct,
            }),
            chart_available = includeChart,
            note = "Each row is one day's program net buying (net = buy − sell), in 억원; net_sum is the window total. The program channel blends foreign baskets, index arbitrage, and institutional baskets, and misses off-program institutional (direct-order) buying; cross-check ls_get_investor_flow (investors=[\"all\"]) for investor-class flow.",
        };

        JsonObject? structured = includeChart
            ? new JsonObject
            {
                ["chart"] = ProgramStockChartBuilder.Build(
                    ProgramStockChartView.DailyBars,
                    new ProgramStockChartMeta(code, name ?? "", $"최근 {obs.Count}일"),
                    obs.Select(o => new ProgramStockPoint(o.Label, o.Price, o.Net)).ToList()),
            }
            : null;

        return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool), structured);
    }

    /// <summary>Maps the model-facing <c>period</c> onto the t1637 <c>gubun2</c> code.</summary>
    static (string Gubun2, bool IsDaily, string? Error) ParsePeriod(string? raw)
    {
        string v = (raw ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "" or "intraday" or "time" => ("0", false, null),
            "daily" or "day" => ("1", true, null),
            _ => ("0", false, $"Unknown period '{raw}'. Use intraday or daily."),
        };
    }

    /// <summary>Formats a <c>yyyyMMdd</c> date string as <c>yyyy-MM-dd</c>.</summary>
    static string FormatDate(string yyyymmdd) =>
        yyyymmdd.Length == 8
            ? $"{yyyymmdd[..4]}-{yyyymmdd.Substring(4, 2)}-{yyyymmdd.Substring(6, 2)}"
            : yyyymmdd;

    // ───────────────────────────── shared (market scope) ────────────────────────────

    /// <summary>
    /// Maps a model-facing <c>chart_view</c> string onto a
    /// <see cref="ProgramTradeChartView"/>. Returns a non-null error message for
    /// unknown values and for <c>baseline_comparison</c> (deferred to v1.5).
    /// </summary>
    static (ProgramTradeChartView View, string Canonical, string? Error) ParseChartView(string? raw)
    {
        string v = (raw ?? "").Trim().ToLowerInvariant();
        return v switch
        {
            "" or "flow_overview" or "flow" => (ProgramTradeChartView.FlowOverview, "flow_overview", null),
            "basis_arbitrage" or "basis" => (ProgramTradeChartView.BasisArbitrage, "basis_arbitrage", null),
            "intensity_bars" or "intensity" => (ProgramTradeChartView.IntensityBars, "intensity_bars", null),
            "gross_flow" or "gross" => (ProgramTradeChartView.GrossFlow, "gross_flow", null),
            "baseline_comparison" or "baseline" => (ProgramTradeChartView.FlowOverview, "baseline_comparison",
                "chart_view 'baseline_comparison' is not available yet — it needs multi-day intraday history (planned for v1.5)."),
            _ => (ProgramTradeChartView.FlowOverview, v,
                $"Unknown chart_view '{raw}'. Use flow_overview, basis_arbitrage, intensity_bars, or gross_flow."),
        };
    }

    /// <summary>
    /// Builds the market-scope Plotly chart envelope for one view. Amounts are
    /// converted from LS amount-basis (백만원) to 억원 — the chart's axis scale.
    /// </summary>
    static JsonObject BuildFlowChart(
        ProgramTradeChartView view,
        string market,
        string day,
        IReadOnlyList<ProgramMinute> minutes)
    {
        var meta = new ProgramChartMeta(
            market,
            day,
            ResolveSessionDate(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "금액 (억원)");

        var points = new List<ProgramTradeChartBuilder.ProgramFlowPoint>(minutes.Count);
        foreach (ProgramMinute m in minutes)
        {
            points.Add(new ProgramTradeChartBuilder.ProgramFlowPoint(
                Time: FormatTime(m.Time),
                Kospi200: m.Kospi200,
                Basis: m.Basis,
                Net: m.Net / MillionWonPerEokwon,
                Arbitrage: m.Arbitrage / MillionWonPerEokwon,
                NonArbitrage: m.NonArbitrage / MillionWonPerEokwon,
                MinuteNet: m.MinuteNet / MillionWonPerEokwon,
                ArbitrageBuy: m.ArbitrageBuy / MillionWonPerEokwon,
                ArbitrageSell: m.ArbitrageSell / MillionWonPerEokwon,
                NonArbitrageBuy: m.NonArbitrageBuy / MillionWonPerEokwon,
                NonArbitrageSell: m.NonArbitrageSell / MillionWonPerEokwon));
        }

        return ProgramTradeChartBuilder.Build(view, meta, points);
    }

    /// <summary>
    /// Resolves the session's calendar date for the chart's time axis / title.
    /// <c>today</c> → the current date; <c>yesterday</c> → the previous weekday
    /// (a best-effort approximation — exchange holidays are not consulted, which
    /// is why the chart title hedges the label as "Prev session").
    /// </summary>
    static DateOnly ResolveSessionDate(string dayNorm)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        if (!string.Equals(dayNorm, "yesterday", StringComparison.Ordinal))
            return today;

        DateOnly d = today.AddDays(-1);
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            d = d.AddDays(-1);
        return d;
    }

    /// <summary>
    /// Selects the deterministic trajectory anchors: open, the peak per-minute
    /// inflow and outflow, the cumulative trough, the closing auction, and the
    /// last row. Anchors landing on the same minute are merged into one entry.
    /// </summary>
    static List<object> BuildKeyPoints(IReadOnlyList<ProgramMinute> minutes)
    {
        int peakBuy = 0, peakSell = 0, trough = 0;
        for (int i = 1; i < minutes.Count; i++)
        {
            if (minutes[i].MinuteNet > minutes[peakBuy].MinuteNet) peakBuy = i;
            if (minutes[i].MinuteNet < minutes[peakSell].MinuteNet) peakSell = i;
            if (minutes[i].Net < minutes[trough].Net) trough = i;
        }
        int closeAuction = NearestTimeIndex(minutes, "153000");

        var labels = new SortedDictionary<int, List<string>>();
        void Mark(int idx, string label)
        {
            if (idx < 0 || idx >= minutes.Count) return;
            if (!labels.TryGetValue(idx, out List<string>? list))
            {
                list = new List<string>();
                labels[idx] = list;
            }
            if (!list.Contains(label)) list.Add(label);
        }

        Mark(0, "open");
        Mark(peakBuy, "peak_buy");
        Mark(peakSell, "peak_sell");
        Mark(trough, "cumulative_trough");
        Mark(closeAuction, "close_auction");
        Mark(minutes.Count - 1, "last");

        var points = new List<object>(labels.Count);
        foreach ((int idx, List<string> list) in labels)
        {
            ProgramMinute m = minutes[idx];
            points.Add(new
            {
                label = string.Join("+", list),
                time = FormatTime(m.Time),
                net = m.Net / MillionWonPerEokwon,
                arbitrage = m.Arbitrage / MillionWonPerEokwon,
                non_arbitrage = m.NonArbitrage / MillionWonPerEokwon,
                minute_net = m.MinuteNet / MillionWonPerEokwon,
            });
        }
        return points;
    }

    /// <summary>Index of the minute whose <c>HHMMSS</c> time is closest to <paramref name="target"/>.</summary>
    static int NearestTimeIndex(IReadOnlyList<ProgramMinute> minutes, string target)
    {
        int targetSec = ToSeconds(target);
        int best = 0;
        int bestDiff = int.MaxValue;
        for (int i = 0; i < minutes.Count; i++)
        {
            int diff = Math.Abs(ToSeconds(minutes[i].Time) - targetSec);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Parses an <c>HHMMSS</c> (or shorter) time string into seconds-of-day.</summary>
    static int ToSeconds(string hhmmss)
    {
        string t = hhmmss.PadLeft(6, '0');
        return int.TryParse(t[..2], out int h) is false ? 0
            : h * 3600
              + (int.TryParse(t.Substring(2, 2), out int m) ? m : 0) * 60
              + (int.TryParse(t.Substring(4, 2), out int s) ? s : 0);
    }

    /// <summary>Formats an <c>HHMMSS</c> time string as <c>HH:MM</c>.</summary>
    static string FormatTime(string hhmmss)
    {
        string t = hhmmss.PadLeft(6, '0');
        return $"{t[..2]}:{t.Substring(2, 2)}";
    }
}

/// <summary>Raw t1662 row carrier — cumulative nets plus the gross buy / sell legs.</summary>
internal sealed record ProgramRow(
    string Time,
    double K200,
    double Change,
    double Basis,
    long Net,
    long Arb,
    long NonArb,
    long ArbBuy,
    long ArbSell,
    long NonArbBuy,
    long NonArbSell);

/// <summary>One minute of market program-trading flow (cumulative + per-minute delta).</summary>
internal sealed record ProgramMinute(
    string Time,
    double Kospi200,
    double Kospi200Change,
    double Basis,
    long Net,
    long Arbitrage,
    long NonArbitrage,
    long MinuteNet,
    long ArbitrageBuy,
    long ArbitrageSell,
    long NonArbitrageBuy,
    long NonArbitrageSell);

/// <summary>One stock's row in a t1636 program-trading ranking (raw LS units).</summary>
internal sealed record ProgramRankRow(
    int Rank,
    string Name,
    string Shcode,
    long Price,
    double ChangePct,
    long Net,
    long Buy,
    long Sell,
    double MktCapRatio);

/// <summary>One t1637 observation of a stock's program-trading flow (amounts in 억원).</summary>
internal sealed record ProgramStockObs(
    string Label,
    long Price,
    double Net,
    double Buy,
    double Sell,
    double ChangePct);

/// <summary>One day of market-wide program-trading history (TR t1633, amounts in 억원).</summary>
internal sealed record ProgramDailyRow(
    string Date,
    double Kospi200,
    double Net,
    double Arbitrage,
    double NonArbitrage);

/// <summary>Cached full program-trading minute series behind a <c>dataset_id</c> handle.</summary>
internal sealed record ProgramTradingDataset(
    string Market,
    string Day,
    string TrCode,
    IReadOnlyList<ProgramMinute> Minutes,
    DateTimeOffset CreatedAtUtc);
