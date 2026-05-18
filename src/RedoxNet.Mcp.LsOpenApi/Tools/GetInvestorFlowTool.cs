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

    [McpServerTool(Name = "ls_get_investor_flow")]
    [Description("""
        Returns investor-type flow (개인 / 외국인 / 기관 / 증권 / 투신 / 은행 / 보험 / 종금 / 기금 / 국가 / 기타 / 사모펀드) in one of two modes.

        - INTRADAY MARKET-WIDE MODE (`shcode` 미지정): wraps t1601. Returns one snapshot per market segment LS ships (KOSPI / KOSDAQ / 선물 / 옵션 등). LS does not label the segments in the wire response, so the wrapper surfaces them as `segments[].block_index = 1..6` with the raw OutBlock contents intact. Use this for *"지금 외인 / 기관 누가 사고 있어?"* style market commentary.
        - SINGLE-STOCK DAILY MODE (`shcode` 지정): wraps t1702. Returns a daily time series of investor-type flow for the named stock. `fromdt` / `todt` clip the range; `direction` picks net / buy / sell (LS-side msmdgb); `metric` picks 수량 / 금액 / 단가; `cumulative=true` reports running totals instead of per-day deltas.

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
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveExchange(exchange, out string exchgubun, out string normalizedExchange))
            return McpJson.Error($"exchange '{exchange}' is not recognized. Use 'unified', 'krx', or 'nxt'.");

        if (string.IsNullOrWhiteSpace(shcode))
            return await GetIntradayMarket(apiClient, unit, exchgubun, normalizedExchange, cancellationToken).ConfigureAwait(false);

        string trimmed = shcode.Trim();
        return await GetDailyStock(apiClient, trimmed, fromdt, todt, count, metric, direction, cumulative, exchgubun, normalizedExchange, cancellationToken).ConfigureAwait(false);
    }

    static async Task<string> GetIntradayMarket(
        LsApiClient apiClient,
        string unit,
        string exchgubun,
        string normalizedExchange,
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
                    Investors: ReadIntradayInvestors(block.Value)));
            }

            var payload = new InvestorFlowPayload
            {
                Mode = "intraday",
                Unit = normalizedUnit,
                Exchange = normalizedExchange,
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
                    series.Add(ReadDailyFlowPoint(row));
                }
            }

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

    static IReadOnlyList<IntradayInvestorRow> ReadIntradayInvestors(JsonElement block)
    {
        var rows = new List<IntradayInvestorRow>(Investors.Count);
        foreach (InvestorKind kind in Investors)
        {
            // t1601 uses code 17 for foreigners (registered only) — no aggregate.
            string suffix = kind.T1601Suffix;
            rows.Add(new IntradayInvestorRow(
                Kind: kind.Code,
                KoreanLabel: kind.Korean,
                Buy: block.ReadLong($"ms_{suffix}"),
                Sell: block.ReadLong($"md_{suffix}"),
                Net: block.ReadLong($"svolume_{suffix}"),
                Change: block.ReadDouble($"rate_{suffix}")));
        }
        return rows;
    }

    static DailyFlowPoint ReadDailyFlowPoint(JsonElement row)
    {
        // LS encodes change sign as a separate "sign" code (2/5 = up/down etc).
        // The change/diff magnitudes are unsigned in some legacy responses, but
        // t1702 in modern wire format ships them already signed; pass through as-is.
        var flows = new List<DailyInvestorFlow>(Investors.Count);
        foreach (InvestorKind kind in Investors)
        {
            string field = $"tjj00{kind.T1702Suffix.PadLeft(2, '0')}";
            flows.Add(new DailyInvestorFlow(
                Kind: kind.Code,
                KoreanLabel: kind.Korean,
                Value: row.ReadLong(field)));
        }
        return new DailyFlowPoint(
            Date: row.ReadString("date")?.Trim() ?? "",
            Close: row.ReadDouble("close"),
            Sign: row.ReadString("sign"),
            Change: row.ReadDouble("change"),
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

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<IntradaySegment>? Segments { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<DailyFlowPoint>? TimeSeries { get; init; }
    }

    sealed record IntradaySegment(int BlockIndex, IReadOnlyList<IntradayInvestorRow> Investors);

    sealed record IntradayInvestorRow(
        string Kind,
        string KoreanLabel,
        long Buy,
        long Sell,
        long Net,
        double Change);

    sealed record DailyFlowPoint(
        string Date,
        double Close,
        string? Sign,
        double Change,
        double ChangePct,
        long Volume,
        long Value,
        IReadOnlyList<DailyInvestorFlow> Flows);

    sealed record DailyInvestorFlow(string Kind, string KoreanLabel, long Value);
}
