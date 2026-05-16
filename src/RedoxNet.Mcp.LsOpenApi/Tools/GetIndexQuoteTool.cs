using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns the current snapshot for a Korean market index
/// (KOSPI / KOSPI200 / KOSDAQ / KRX100 / sector indices) via TR t1511.
/// </summary>
[McpServerToolType]
public static class GetIndexQuoteTool
{
    /// <summary>
    /// Returns t1511 wrapped in a normalized envelope: current value,
    /// previous close + change, OHLC w/ timestamps, 52-week and YTD range,
    /// market breadth, and four related auxiliary indices.
    /// </summary>
    [McpServerTool(Name = "ls_get_index_quote")]
    [Description("""
        Returns a real-time snapshot for a single Korean market index: current value, change vs. previous close, session OHLC with timestamps, 52-week and YTD range, market breadth (advancers/decliners/unchanged/limit), and four related auxiliary indices (e.g. for KOSPI 종합 you also get 대형주/중형주/소형주).

        USE WHEN: the user asks "오늘 코스피 어땠어?", "코스닥 지수", "KOSPI 200 현재가" or wants a single-index snapshot.
        AVOID WHEN: the user wants ETFs that track an index — use ls_get_quote with the ETF shcode. For per-sector ranking across many industries use ls_get_industry_indices.

        index_code aliases: kospi→001, kosdaq→301, kospi200→101, krx100→501. Numeric codes (3-char) are passed through.
        """)]
    public static async Task<string> GetIndexQuote(
        LsApiClient apiClient,
        [Description("Index code. Aliases: 'kospi' (001), 'kosdaq' (301), 'kospi200' (101), 'krx100' (501). Or a 3-character LS upcode like '002'.")]
        string index_code = "kospi",
        CancellationToken cancellationToken = default)
    {
        string upcode = NormalizeIndexCode(index_code);
        if (string.IsNullOrEmpty(upcode))
            return McpJson.Error($"index_code '{index_code}' is not recognized. Use one of: kospi, kosdaq, kospi200, krx100, or a 3-character upcode.");

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1511",
                new JsonObject { ["upcode"] = upcode },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                // LS returns no business code for an unknown upcode at the TR
                // layer; missing block in the success case is handled below.
                return McpJson.Error("LS reported a business-level error.", new
                {
                    error_code = "IndexNotFound",
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    requested_code = upcode,
                });
            }

            JsonElement? block = response.GetBlock("t1511OutBlock");
            if (block is null)
                return McpJson.Error("t1511OutBlock was missing from the response.", new { requested_code = upcode });

            JsonElement b = block.Value;
            string? sign = b.ReadString("sign");
            double rawChange = b.ReadDouble("change");
            double rawPct = b.ReadDouble("diffjisu");

            var payload = new IndexQuotePayload
            {
                IndexCode = upcode,
                Name = (b.ReadString("hname") ?? "").Trim(),
                Value = b.ReadDouble("pricejisu"),
                PreviousClose = b.ReadDouble("jniljisu"),
                Change = IndustryDataCache.ApplySign(rawChange, sign),
                ChangePct = IndustryDataCache.ApplySign(rawPct, sign),
                Open = new IndexOhlcPoint(
                    Value: b.ReadDouble("openjisu"),
                    ChangePct: b.ReadDouble("opendiff"),
                    Time: b.ReadString("opentime")),
                High = new IndexOhlcPoint(
                    Value: b.ReadDouble("highjisu"),
                    ChangePct: b.ReadDouble("highdiff"),
                    Time: b.ReadString("hightime")),
                Low = new IndexOhlcPoint(
                    Value: b.ReadDouble("lowjisu"),
                    ChangePct: b.ReadDouble("lowdiff"),
                    Time: b.ReadString("lowtime")),
                Volume = new IndexFlow(
                    Today: b.ReadLong("volume"),
                    Previous: b.ReadLong("jnilvolume"),
                    Change: b.ReadLong("volumechange"),
                    Ratio: b.ReadDouble("volumerate")),
                ValueMillionWon = new IndexFlow(
                    Today: b.ReadLong("value"),
                    Previous: b.ReadLong("jnilvalue"),
                    Change: b.ReadLong("valuechange"),
                    Ratio: b.ReadDouble("valuerate")),
                Range52w = new IndexRange(
                    High: new IndexRangePoint(b.ReadDouble("whjisu"), b.ReadString("whjday"), b.ReadDouble("whchange")),
                    Low: new IndexRangePoint(b.ReadDouble("wljisu"), b.ReadString("wljday"), b.ReadDouble("wlchange"))),
                RangeYtd = new IndexRange(
                    High: new IndexRangePoint(b.ReadDouble("yhjisu"), b.ReadString("yhjday"), b.ReadDouble("yhchange")),
                    Low: new IndexRangePoint(b.ReadDouble("yljisu"), b.ReadString("yljday"), b.ReadDouble("ylchange"))),
                MarketBreadth = new MarketBreadth(
                    Up: b.ReadLong("highjo"),
                    LimitUp: b.ReadLong("upjo"),
                    Unchanged: b.ReadLong("unchgjo"),
                    Down: b.ReadLong("lowjo"),
                    LimitDown: b.ReadLong("downjo")),
                RelatedIndices = ReadRelatedIndices(b),
                Timestamp = SeoulNowIsoString(),
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
    /// Resolves the user-facing index_code (alias or 3-char upcode) to the LS upcode.
    /// Returns null when the input is empty or obviously malformed; an unknown 3-char
    /// upcode is passed through to LS so the testbed can capture friendly errors.
    /// </summary>
    static string NormalizeIndexCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "001";
        string lower = raw.Trim().ToLowerInvariant();
        return lower switch
        {
            "kospi" or "kp" or "코스피" or "종합" => "001",
            "kosdaq" or "kd" or "코스닥" => "301",
            "kospi200" or "kp200" or "kospi_200" or "코스피200" => "101",
            "krx100" or "krx_100" => "501",
            _ when raw.Trim().Length == 3 && raw.Trim().All(char.IsLetterOrDigit) => raw.Trim(),
            _ => "",
        };
    }

    static List<RelatedIndexRow> ReadRelatedIndices(JsonElement b)
    {
        var related = new List<RelatedIndexRow>(4);
        AddIfPresent(related, b, "firstjcode", "firstjname", "firstjisu", "firsign", "firchange", "firdiff");
        AddIfPresent(related, b, "secondjcode", "secondjname", "secondjisu", "secsign", "secchange", "secdiff");
        AddIfPresent(related, b, "thirdjcode", "thirdjname", "thirdjisu", "thrsign", "thrchange", "thrdiff");
        AddIfPresent(related, b, "fourthjcode", "fourthjname", "fourthjisu", "forsign", "forchange", "fordiff");
        return related;
    }

    static void AddIfPresent(
        List<RelatedIndexRow> list,
        JsonElement b,
        string codeKey,
        string nameKey,
        string valueKey,
        string signKey,
        string changeKey,
        string diffKey)
    {
        string? code = b.ReadString(codeKey)?.Trim();
        if (string.IsNullOrEmpty(code))
            return;
        string? sign = b.ReadString(signKey);
        double rawChange = b.ReadDouble(changeKey);
        double rawPct = b.ReadDouble(diffKey);
        list.Add(new RelatedIndexRow(
            Code: code,
            Name: (b.ReadString(nameKey) ?? "").Trim(),
            Value: b.ReadDouble(valueKey),
            Change: IndustryDataCache.ApplySign(rawChange, sign),
            ChangePct: IndustryDataCache.ApplySign(rawPct, sign)));
    }

    internal static string SeoulNowIsoString()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Korea Standard Time" : "Asia/Seoul");
            return TimeZoneInfo.ConvertTime(now, zone).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (TimeZoneNotFoundException)
        {
            return now.ToOffset(TimeSpan.FromHours(9)).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    sealed record IndexQuotePayload
    {
        public string IndexCode { get; init; } = "";
        public string Name { get; init; } = "";
        public double Value { get; init; }
        public double PreviousClose { get; init; }
        public double Change { get; init; }
        public double ChangePct { get; init; }
        public IndexOhlcPoint Open { get; init; } = default!;
        public IndexOhlcPoint High { get; init; } = default!;
        public IndexOhlcPoint Low { get; init; } = default!;
        public IndexFlow Volume { get; init; } = default!;
        public IndexFlow ValueMillionWon { get; init; } = default!;
        // SnakeCaseLower does not insert an underscore between a letter and a digit,
        // so we override the wire name to match the spec envelope (range_52w / range_ytd).
        [JsonPropertyName("range_52w")]
        public IndexRange Range52w { get; init; } = default!;
        [JsonPropertyName("range_ytd")]
        public IndexRange RangeYtd { get; init; } = default!;
        public MarketBreadth MarketBreadth { get; init; } = default!;
        public List<RelatedIndexRow> RelatedIndices { get; init; } = new();
        public string Timestamp { get; init; } = "";
    }

    sealed record IndexOhlcPoint(double Value, double ChangePct, string? Time);
    sealed record IndexFlow(long Today, long Previous, long Change, double Ratio);
    sealed record IndexRangePoint(double Value, string? Date, double Change);
    sealed record IndexRange(IndexRangePoint High, IndexRangePoint Low);
    sealed record MarketBreadth(long Up, long LimitUp, long Unchanged, long Down, long LimitDown);
    sealed record RelatedIndexRow(string Code, string Name, double Value, double Change, double ChangePct);
}
