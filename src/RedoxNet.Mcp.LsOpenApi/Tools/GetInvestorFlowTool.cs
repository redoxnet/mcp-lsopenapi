using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP wrapper for investor-type flow data — t1601 (intraday market-wide
/// 매매주체별 매매수량) when no shcode is given, t1702 (일별주체별 종목투자동향)
/// when a shcode is supplied.
/// </summary>
[McpServerToolType]
public static class GetInvestorFlowTool
{
    /// <summary>Max trading-day rows the daily-mode response will surface from t1702.</summary>
    const int MaxDailyCount = 200;

    /// <summary>Default daily-mode window when neither fromdt nor count is provided.</summary>
    const int DefaultDailyCount = 30;

    // LS labels twelve investor types with two-digit suffixes; the order below
    // matches t1601's "투자자별 종합" section and t1702's per-day breakdown.
    static readonly IReadOnlyList<InvestorKind> Investors = new InvestorKind[]
    {
        new("individual",            "개인",     "08", "08"),
        new("foreign",               "외국인",   "17", "16"), // t1601: code 17 (registered); t1702: combined 16
        new("institution_total",     "기관계",   "18", "18"),
        new("securities",            "증권",     "01", "01"),
        new("investment_trust",      "투신",     "03", "03"),
        new("bank",                  "은행",     "04", "04"),
        new("insurance",             "보험",     "02", "02"),
        new("merchant_bank",         "종금",     "05", "05"),
        new("pension_fund",          "기금",     "06", "06"),
        new("national",              "국가",     "11", "11"),
        new("etc",                   "기타",     "07", "07"),
        new("private_equity",        "사모펀드", "00", "00"),
    };

    /// <summary>Default investor subset surfaced when caller does not pin a list.</summary>
    /// <remarks>
    /// Most questions ("외인 매수?" / "기관 흐름") only need the three macro categories.
    /// Drilling into 증권 / 투신 / 은행 / 보험 / 종금 / 기금 / 국가 / 기타 / 사모펀드 is
    /// the exception, so default 3-of-12 cuts ~75% of token weight while keeping the
    /// answers the model writes verbatim. Pass `investors=["all"]` (or any explicit
    /// subset) to expand.
    /// </remarks>
    static readonly string[] DefaultInvestors = { "foreign", "institution_total", "individual" };

    [McpServerTool(Name = "ls_get_investor_flow")]
    [Description("""
        Returns investor-type flow in one of two modes. Up to 12 LS-tracked investor categories are available (개인 / 외국인 / 기관계 / 증권 / 투신 / 은행 / 보험 / 종금 / 기금 / 국가 / 기타 / 사모펀드), but the wrapper surfaces only the three macro categories (`foreign`, `institution_total`, `individual`) by default to keep responses compact. Pass `investors=["all"]` or an explicit subset to expand.

        - INTRADAY MARKET-WIDE MODE (`shcode` 미지정): wraps t1601. Returns one snapshot per market segment LS ships (KOSPI / KOSDAQ / 선물 / 옵션 등). LS does not label the segments in the wire response, so the wrapper surfaces them as `segments[].block_index = 1..6` with each segment's selected investors as a `{kind: {buy, sell, net, change}}` map. Use this for *"지금 외인 / 기관 누가 사고 있어?"* style market commentary.
        - SINGLE-STOCK DAILY MODE (`shcode` 지정): wraps t1702. Returns a daily time series of investor-type flow for the named stock plus a `summary` block (period totals + per-investor largest buy/sell day). `fromdt` / `todt` clip the range; `direction` picks net / buy / sell (LS-side msmdgb); `metric` picks 수량 / 금액 / 단가; `cumulative=true` reports running totals instead of per-day deltas.

        AVOID WHEN: the user wants top-N stocks by foreign / institution net buying — that's a separate ranking TR (t1471/t1717) not in v0.7. AVOID using daily mode without a shcode (will error).

        Units: t1601 magnitudes are 천주 (unit=volume) or 백만원 (unit=value). t1702 magnitudes follow `metric` — 천주 / 백만원 / 원 per share.
        """)]
    public static async Task<string> GetInvestorFlow(
        LsApiClient apiClient,
        [Description("6-digit Korean stock code (optional). When supplied, switches to single-stock daily mode (t1702).")]
        string? shcode = null,
        [Description("Intraday mode: 'volume' (수량, default) or 'value' (금액). Maps to t1601.gubun1/2/4.")]
        string unit = "volume",
        [Description("Daily mode: start date YYYYMMDD. Default = todt - count business days (server clips).")]
        string? fromdt = null,
        [Description("Daily mode: end date YYYYMMDD. Default = today (LS server time).")]
        string? todt = null,
        [Description("Daily mode: how many trading days to return when fromdt is omitted. 1-200, default 30.")]
        int count = DefaultDailyCount,
        [Description("Daily mode: 'volume' (수량, default), 'value' (금액), or 'price' (단가). Maps to t1702.volvalgb.")]
        string metric = "volume",
        [Description("Daily mode: 'net' (순매수, default), 'buy' (매수), or 'sell' (매도). Maps to t1702.msmdgb.")]
        string direction = "net",
        [Description("Daily mode: false (per-day, default) or true (누적). Maps to t1702.gubun.")]
        bool cumulative = false,
        [Description("Exchange filter: 'unified' (통합 — default), 'krx', or 'nxt'.")]
        string exchange = "unified",
        [Description("Investor categories to surface. Default is the three macro categories ['foreign','institution_total','individual']. Pass ['all'] to include all 12, or list specific kinds: securities, investment_trust, bank, insurance, merchant_bank, pension_fund, national, etc, private_equity.")]
        string[]? investors = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveExchange(exchange, out string exchgubun, out string normalizedExchange))
            return McpJson.Error($"exchange '{exchange}' is not recognized. Use 'unified', 'krx', or 'nxt'.");

        if (!TryResolveInvestors(investors, out IReadOnlyList<InvestorKind> selectedInvestors, out string? investorsError))
            return McpJson.Error(investorsError!);

        if (string.IsNullOrWhiteSpace(shcode))
            return await GetIntradayMarket(apiClient, unit, exchgubun, normalizedExchange, selectedInvestors, cancellationToken).ConfigureAwait(false);

        string trimmed = shcode.Trim();
        return await GetDailyStock(apiClient, trimmed, fromdt, todt, count, metric, direction, cumulative, exchgubun, normalizedExchange, selectedInvestors, cancellationToken).ConfigureAwait(false);
    }

    static bool TryResolveInvestors(string[]? raw, out IReadOnlyList<InvestorKind> selected, out string? error)
    {
        if (raw is null || raw.Length == 0)
        {
            selected = DefaultInvestors
                .Select(code => Investors.First(k => k.Code == code))
                .ToList();
            error = null;
            return true;
        }

        // Special token "all" expands to the full 12.
        if (raw.Any(s => string.Equals(s?.Trim(), "all", StringComparison.OrdinalIgnoreCase)))
        {
            selected = Investors;
            error = null;
            return true;
        }

        var picked = new List<InvestorKind>(raw.Length);
        foreach (string item in raw)
        {
            string trimmed = (item ?? "").Trim().ToLowerInvariant();
            InvestorKind? kind = Investors.FirstOrDefault(k => k.Code == trimmed)
                                ?? Investors.FirstOrDefault(k => string.Equals(k.Korean, item?.Trim(), StringComparison.Ordinal));
            if (kind is null)
            {
                selected = Array.Empty<InvestorKind>();
                error = $"investor '{item}' is not recognized. See the tool description for the supported set.";
                return false;
            }
            if (!picked.Contains(kind))
                picked.Add(kind);
        }
        selected = picked;
        error = null;
        return true;
    }

    static async Task<string> GetIntradayMarket(
        LsApiClient apiClient,
        string unit,
        string exchgubun,
        string normalizedExchange,
        IReadOnlyList<InvestorKind> investors,
        CancellationToken cancellationToken)
    {
        if (!TryResolveIntradayUnit(unit, out string unitCode, out string normalizedUnit))
            return McpJson.Error($"unit '{unit}' is not recognized. Use 'volume' or 'value'.");

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1601",
                new JsonObject
                {
                    // The same code applies to 주식 (gubun1) / 옵션 (gubun2) / 선물 (gubun4)
                    // segments so the response's six OutBlocks share one unit interpretation.
                    ["gubun1"] = unitCode,
                    ["gubun2"] = unitCode,
                    ["gubun3"] = " ",
                    ["gubun4"] = unitCode,
                    ["exchgubun"] = exchgubun,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    mode = "intraday",
                });

            var segments = new List<IntradaySegment>(6);
            for (int i = 1; i <= 6; i++)
            {
                JsonElement? block = response.GetBlock($"t1601OutBlock{i}");
                if (block is null)
                    continue;
                segments.Add(new IntradaySegment(
                    BlockIndex: i,
                    Investors: ReadIntradayInvestors(block.Value, investors)));
            }

            var payload = new InvestorFlowPayload
            {
                Mode = "intraday",
                Unit = normalizedUnit,
                Exchange = normalizedExchange,
                InvestorsShown = investors.Select(k => k.Code).ToArray(),
                Segments = segments,
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

    static async Task<string> GetDailyStock(
        LsApiClient apiClient,
        string shcode,
        string? fromdt,
        string? todt,
        int count,
        string metric,
        string direction,
        bool cumulative,
        string exchgubun,
        string normalizedExchange,
        IReadOnlyList<InvestorKind> investors,
        CancellationToken cancellationToken)
    {
        if (!TryResolveDailyMetric(metric, out string volvalgb, out string normalizedMetric))
            return McpJson.Error($"metric '{metric}' is not recognized. Use 'volume', 'value', or 'price'.");
        if (!TryResolveDirection(direction, out string msmdgb, out string normalizedDirection))
            return McpJson.Error($"direction '{direction}' is not recognized. Use 'net', 'buy', or 'sell'.");
        if (count < 1 || count > MaxDailyCount)
            return McpJson.Error($"count must be between 1 and {MaxDailyCount}.", new { received = count });

        string normalizedTo = NormalizeDateOrToday(todt);
        string normalizedFrom = NormalizeDateOrDefault(fromdt, normalizedTo, count);
        if (string.Compare(normalizedFrom, normalizedTo, StringComparison.Ordinal) > 0)
            return McpJson.Error("fromdt must be <= todt.", new { fromdt = normalizedFrom, todt = normalizedTo });

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1702",
                new JsonObject
                {
                    ["shcode"] = shcode,
                    ["fromdt"] = normalizedFrom,
                    ["todt"] = normalizedTo,
                    ["volvalgb"] = volvalgb,
                    ["msmdgb"] = msmdgb,
                    ["gubun"] = cumulative ? "1" : "0",
                    ["exchgubun"] = exchgubun,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    shcode,
                    fromdt = normalizedFrom,
                    todt = normalizedTo,
                });

            JsonElement? array = response.GetBlock("t1702OutBlock1");
            var series = new List<DailyFlowPoint>();
            if (array is not null && array.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    string date = row.ReadString("date")?.Trim() ?? "";
                    if (string.IsNullOrEmpty(date))
                        continue;
                    if (series.Count >= count)
                        break;
                    series.Add(ReadDailyFlowPoint(row, investors));
                }
            }

            var summary = BuildDailySummary(series, investors);

            var payload = new InvestorFlowPayload
            {
                Mode = "daily",
                Shcode = shcode,
                Fromdt = normalizedFrom,
                Todt = normalizedTo,
                Metric = normalizedMetric,
                Direction = normalizedDirection,
                Cumulative = cumulative,
                Exchange = normalizedExchange,
                InvestorsShown = investors.Select(k => k.Code).ToArray(),
                Summary = summary,
                TimeSeries = series,
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

    static DailySummary BuildDailySummary(IReadOnlyList<DailyFlowPoint> series, IReadOnlyList<InvestorKind> investors)
    {
        // Period totals + per-investor largest single-day buy/sell. Today's row
        // can have all-zero flows when LS hasn't settled it yet — we count those
        // days in trading_days but they contribute 0 to the totals (no-op).
        var totals = new Dictionary<string, long>(investors.Count);
        var biggestBuy = new Dictionary<string, FlowExtreme>(investors.Count);
        var biggestSell = new Dictionary<string, FlowExtreme>(investors.Count);

        foreach (InvestorKind kind in investors)
        {
            totals[kind.Code] = 0;
        }

        foreach (DailyFlowPoint point in series)
        {
            foreach (InvestorKind kind in investors)
            {
                if (!point.Flows.TryGetValue(kind.Code, out long value))
                    continue;
                totals[kind.Code] += value;

                if (value > 0)
                {
                    if (!biggestBuy.TryGetValue(kind.Code, out FlowExtreme? prevBuy) || value > prevBuy.Value)
                        biggestBuy[kind.Code] = new FlowExtreme(point.Date, value);
                }
                else if (value < 0)
                {
                    if (!biggestSell.TryGetValue(kind.Code, out FlowExtreme? prevSell) || value < prevSell.Value)
                        biggestSell[kind.Code] = new FlowExtreme(point.Date, value);
                }
            }
        }

        return new DailySummary(
            TradingDays: series.Count,
            PeriodTotals: totals,
            LargestBuyByInvestor: biggestBuy,
            LargestSellByInvestor: biggestSell);
    }

    static IReadOnlyDictionary<string, IntradayInvestorRow> ReadIntradayInvestors(JsonElement block, IReadOnlyList<InvestorKind> investors)
    {
        var rows = new Dictionary<string, IntradayInvestorRow>(investors.Count);
        foreach (InvestorKind kind in investors)
        {
            // t1601 uses code 17 for foreigners (registered only) — no aggregate.
            string suffix = kind.T1601Suffix;
            rows[kind.Code] = new IntradayInvestorRow(
                Buy: block.ReadLong($"ms_{suffix}"),
                Sell: block.ReadLong($"md_{suffix}"),
                Net: block.ReadLong($"svolume_{suffix}"),
                Change: block.ReadDouble($"rate_{suffix}"));
        }
        return rows;
    }

    static DailyFlowPoint ReadDailyFlowPoint(JsonElement row, IReadOnlyList<InvestorKind> investors)
    {
        // The change/diff magnitudes ship pre-signed on t1702 modern responses;
        // we keep change_pct as the single delta signal (sign + change kept off
        // the wire — caller can derive from change_pct).
        var flows = new Dictionary<string, long>(investors.Count);
        foreach (InvestorKind kind in investors)
        {
            string field = $"tjj00{kind.T1702Suffix.PadLeft(2, '0')}";
            flows[kind.Code] = row.ReadLong(field);
        }
        return new DailyFlowPoint(
            Date: row.ReadString("date")?.Trim() ?? "",
            Close: row.ReadDouble("close"),
            ChangePct: row.ReadDouble("diff"),
            Volume: row.ReadLong("volume"),
            Value: row.ReadLong("value"),
            Flows: flows);
    }

    static bool TryResolveIntradayUnit(string? raw, out string code, out string normalized)
    {
        string lower = (raw ?? "volume").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "volume" or "수량" or "qty" or "quantity":
                code = "1"; normalized = "volume"; return true;
            case "value" or "금액" or "amount":
                code = "2"; normalized = "value"; return true;
            default:
                code = ""; normalized = ""; return false;
        }
    }

    static bool TryResolveDailyMetric(string? raw, out string code, out string normalized)
    {
        string lower = (raw ?? "volume").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "value" or "금액" or "amount":
                code = "0"; normalized = "value"; return true;
            case "volume" or "수량" or "qty" or "quantity":
                code = "1"; normalized = "volume"; return true;
            case "price" or "단가":
                code = "2"; normalized = "price"; return true;
            default:
                code = ""; normalized = ""; return false;
        }
    }

    static bool TryResolveDirection(string? raw, out string code, out string normalized)
    {
        string lower = (raw ?? "net").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "net" or "순매수" or "순":
                code = "0"; normalized = "net"; return true;
            case "buy" or "매수":
                code = "1"; normalized = "buy"; return true;
            case "sell" or "매도":
                code = "2"; normalized = "sell"; return true;
            default:
                code = ""; normalized = ""; return false;
        }
    }

    static bool TryResolveExchange(string? raw, out string code, out string normalized)
    {
        string lower = (raw ?? "unified").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "unified" or "u" or "통합":
                code = "U"; normalized = "unified"; return true;
            case "krx" or "k":
                code = "K"; normalized = "krx"; return true;
            case "nxt" or "n":
                code = "N"; normalized = "nxt"; return true;
            default:
                code = ""; normalized = ""; return false;
        }
    }

    static string NormalizeDateOrToday(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        if (TryParseYmd(trimmed, out _))
            return trimmed;
        return DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    static string NormalizeDateOrDefault(string? raw, string anchorYmd, int days)
    {
        string trimmed = (raw ?? "").Trim();
        if (TryParseYmd(trimmed, out _))
            return trimmed;
        if (!TryParseYmd(anchorYmd, out DateTime anchor))
            anchor = DateTime.Now.Date;
        // Pad the window because t1702 returns trading days only — N calendar
        // days ≈ N × 5/7 trading days, plus a buffer so weekends/holidays don't
        // truncate the requested count.
        int padded = (int)Math.Ceiling(Math.Max(1, days) * 1.6) + 3;
        return anchor.AddDays(-padded).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
    }

    static bool TryParseYmd(string raw, out DateTime parsed)
    {
        if (raw.Length == 8 && DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            return true;
        parsed = default;
        return false;
    }

    sealed record InvestorKind(string Code, string Korean, string T1601Suffix, string T1702Suffix);

    sealed record InvestorFlowPayload
    {
        public string Mode { get; init; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Unit { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Shcode { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Fromdt { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Todt { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Metric { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Direction { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Cumulative { get; init; }

        public string Exchange { get; init; } = "";

        public IReadOnlyList<string> InvestorsShown { get; init; } = Array.Empty<string>();

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DailySummary? Summary { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<IntradaySegment>? Segments { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<DailyFlowPoint>? TimeSeries { get; init; }
    }

    sealed record IntradaySegment(int BlockIndex, IReadOnlyDictionary<string, IntradayInvestorRow> Investors);

    sealed record IntradayInvestorRow(long Buy, long Sell, long Net, double Change);

    sealed record DailyFlowPoint(
        string Date,
        double Close,
        double ChangePct,
        long Volume,
        long Value,
        IReadOnlyDictionary<string, long> Flows);

    sealed record DailySummary(
        int TradingDays,
        IReadOnlyDictionary<string, long> PeriodTotals,
        IReadOnlyDictionary<string, FlowExtreme> LargestBuyByInvestor,
        IReadOnlyDictionary<string, FlowExtreme> LargestSellByInvestor);

    sealed record FlowExtreme(string Date, long Value);
}
