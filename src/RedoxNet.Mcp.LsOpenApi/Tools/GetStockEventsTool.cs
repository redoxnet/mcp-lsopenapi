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
/// MCP wrapper for t3202 — 종목별 증시일정 (corporate action / shareholder
/// meeting calendar for a single stock).
/// </summary>
[McpServerToolType]
public static class GetStockEventsTool
{
    // LS ships event-type codes via upgu (업무구분). The labels below mirror
    // the LS guide; alias map below accepts English / Korean inputs and pins
    // each one to the same two-char upgu code.
    static readonly IReadOnlyDictionary<string, string> UpguLabel = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["01"] = "유상증자",
        ["02"] = "무상증자",
        ["03"] = "배당",
        ["04"] = "감자",
        ["05"] = "합병/분할",
        ["06"] = "매수청구",
        ["07"] = "실권주",
        ["08"] = "액면교체",
        ["09"] = "주주총회",
        ["10"] = "상호변경",
        ["11"] = "국내CB전환",
        ["12"] = "해외CB전환",
        ["13"] = "해외BW행사",
        ["14"] = "스톡옵션행사",
    };

    // English (snake_case) keys mirroring the LS upgu values.
    static readonly IReadOnlyDictionary<string, string> UpguEnglish = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["01"] = "rights_issue",
        ["02"] = "bonus_issue",
        ["03"] = "dividend",
        ["04"] = "capital_reduction",
        ["05"] = "merger_split",
        ["06"] = "buyback_request",
        ["07"] = "forfeited_shares",
        ["08"] = "par_value_change",
        ["09"] = "shareholder_meeting",
        ["10"] = "name_change",
        ["11"] = "domestic_cb_conversion",
        ["12"] = "overseas_cb_conversion",
        ["13"] = "overseas_bw_exercise",
        ["14"] = "stock_option_exercise",
    };

    [McpServerTool(Name = "ls_get_stock_events")]
    [Description("""
        Returns the corporate-action / shareholder-meeting calendar for a single Korean stock via t3202 (종목별 증시일정). Events include dividends, AGMs, rights issues, bonus issues, capital changes, mergers/splits, stock-option exercises, CB conversions, etc.

        USE WHEN: the user asks "다음 주총 언제", "삼성전자 배당 일정", "내 보유 종목 다음 이벤트", or any forward-looking corporate-action question for a specific symbol. The wrapper is read-only and one TR call.
        AVOID WHEN: the user wants market-wide screening (this is single-symbol only) or wants real-time disclosure feeds (t3202 covers scheduled actions, not breaking 공시).

        Filtering:
        - `from` / `to` clip the returned events to a [YYYYMMDD, YYYYMMDD] window. Events with `recdt = "00000000"` (TBD / undated) are kept regardless of the window so the model still surfaces "scheduled, date TBD" entries.
        - `kinds` accepts English snake_case (dividend, shareholder_meeting, rights_issue, …), Korean labels (배당, 주주총회, …), or raw two-char upgu codes ("03", "09").

        Each event row carries the LS upgu code plus normalized English / Korean labels so the model can render whichever fits the question's language.
        """)]
    public static async Task<string> GetStockEvents(
        LsApiClient apiClient,
        [Description("6-digit Korean stock code, e.g. '005930' for Samsung Electronics.")]
        string shcode,
        [Description("Optional inclusive start date YYYYMMDD. Events with recdt='00000000' (TBD) are always kept.")]
        string? from = null,
        [Description("Optional inclusive end date YYYYMMDD.")]
        string? to = null,
        [Description("Optional event-kind filter. List of English snake_case names, Korean labels, or raw upgu codes (e.g. ['dividend','주주총회','01']).")]
        string[]? kinds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.Error("shcode is required.");

        string trimmedShcode = shcode.Trim();
        string? fromYmd = NormalizeOptionalDate(from);
        string? toYmd = NormalizeOptionalDate(to);
        if (from is not null && fromYmd is null)
            return McpJson.Error("from must be a YYYYMMDD date.", new { received = from });
        if (to is not null && toYmd is null)
            return McpJson.Error("to must be a YYYYMMDD date.", new { received = to });
        if (fromYmd is not null && toYmd is not null && string.Compare(fromYmd, toYmd, StringComparison.Ordinal) > 0)
            return McpJson.Error("from must be <= to.", new { from = fromYmd, to = toYmd });

        HashSet<string>? wantedCodes = null;
        if (kinds is { Length: > 0 })
        {
            wantedCodes = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in kinds)
            {
                string? code = ResolveUpguCode(raw);
                if (code is null)
                    return McpJson.Error($"kind '{raw}' is not recognized. See the tool description for accepted forms.");
                wantedCodes.Add(code);
            }
        }

        try
        {
            // t3202's InBlock.date is a single anchor, not a range. The wrapper
            // sends an empty value (LS treats it as "no anchor — return the full
            // schedule") and filters from/to client-side, so the from/to envelope
            // stays consistent regardless of how LS interprets the anchor.
            LsTrResponse response = await apiClient.CallTrAsync(
                "t3202",
                new JsonObject
                {
                    ["shcode"] = trimmedShcode,
                    ["date"] = " ",
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    shcode = trimmedShcode,
                });

            var events = new List<StockEventRow>();
            JsonElement? array = response.GetBlock("t3202OutBlock");
            if (array is not null && array.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    string recdt = row.ReadString("recdt")?.Trim() ?? "";
                    string upgu = row.ReadString("upgu")?.Trim() ?? "";
                    bool isTbd = recdt == "00000000" || string.IsNullOrEmpty(recdt);

                    if (!isTbd)
                    {
                        if (fromYmd is not null && string.Compare(recdt, fromYmd, StringComparison.Ordinal) < 0)
                            continue;
                        if (toYmd is not null && string.Compare(recdt, toYmd, StringComparison.Ordinal) > 0)
                            continue;
                    }

                    if (wantedCodes is not null && !wantedCodes.Contains(upgu))
                        continue;

                    UpguLabel.TryGetValue(upgu, out string? koreanLabel);
                    UpguEnglish.TryGetValue(upgu, out string? englishKind);

                    events.Add(new StockEventRow(
                        Date: isTbd ? null : recdt,
                        DateTbd: isTbd,
                        UpguCode: upgu,
                        Kind: englishKind ?? upgu,
                        KoreanLabel: row.ReadString("upunm")?.Trim() is { Length: > 0 } upunm ? upunm : koreanLabel,
                        IssuerNumber: row.ReadString("custno")?.Trim(),
                        IssuerName: row.ReadString("custnm")?.Trim(),
                        TableId: row.ReadString("tableid")?.Trim()));
                }
            }

            // Sort: dated events ascending by date first, then TBD entries last
            // — most "next event" questions want the soonest concrete date.
            events.Sort((a, b) =>
            {
                if (a.DateTbd != b.DateTbd) return a.DateTbd ? 1 : -1;
                return string.Compare(a.Date ?? "", b.Date ?? "", StringComparison.Ordinal);
            });

            var payload = new StockEventsPayload
            {
                Shcode = trimmedShcode,
                From = fromYmd,
                To = toYmd,
                Count = events.Count,
                Events = events,
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

    static string? ResolveUpguCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string trimmed = raw.Trim();
        // Raw two-char upgu code passthrough.
        if (trimmed.Length == 2 && UpguLabel.ContainsKey(trimmed))
            return trimmed;
        string lower = trimmed.ToLowerInvariant();
        foreach (KeyValuePair<string, string> pair in UpguEnglish)
        {
            if (string.Equals(pair.Value, lower, StringComparison.Ordinal))
                return pair.Key;
        }
        // Alternate English aliases not in the canonical map.
        switch (lower)
        {
            case "agm": return "09";
            case "shareholder_meeting" or "annual_general_meeting": return "09";
            case "rights": return "01";
            case "bonus": return "02";
            case "merger": case "split": return "05";
            case "stock_option": return "14";
        }
        foreach (KeyValuePair<string, string> pair in UpguLabel)
        {
            if (string.Equals(pair.Value, trimmed, StringComparison.Ordinal))
                return pair.Key;
        }
        return null;
    }

    static string? NormalizeOptionalDate(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed.Length == 8 && DateTime.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return trimmed;
        return null;
    }

    sealed record StockEventsPayload
    {
        public string Shcode { get; init; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? From { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? To { get; init; }

        public int Count { get; init; }
        public IReadOnlyList<StockEventRow> Events { get; init; } = Array.Empty<StockEventRow>();
    }

    sealed record StockEventRow(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Date,
        bool DateTbd,
        string UpguCode,
        string Kind,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? KoreanLabel,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? IssuerNumber,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? IssuerName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? TableId);
}
