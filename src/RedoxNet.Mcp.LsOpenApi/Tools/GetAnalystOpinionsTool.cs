using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool returning a stock's brokerage (sell-side) investment-opinion and
/// target-price change history via TR t3401.
/// </summary>
[McpServerToolType]
public static class GetAnalystOpinionsTool
{
    /// <summary>Upper bound on opinion rows. One t3401 page ships ~20 most-recent changes.</summary>
    const int MaxCount = 20;

    /// <summary>
    /// Returns the t3401 opinion history wrapped in a normalized envelope.
    /// </summary>
    [McpServerTool(Name = "ls_get_analyst_opinions")]
    [Description("""
        Returns a Korean stock's brokerage (sell-side) investment-opinion history via LS t3401: each entry carries the opinion-day date, 회원사 (broker), the rating before/after the change, the target price before/after, and the stock's close on that day. A current-price snapshot is included.

        This is the authoritative, structured source for Korean 투자의견 / 목표주가 / analyst-consensus change history — LS supplies the official per-broker opinion-change record directly. Prefer this tool before web search for the rating / target-price numbers; use web search only for the narrative around a change (the broker's rationale, the market reaction).

        USE WHEN: the user asks about analyst opinions / 투자의견 / 목표주가 / target price / sell-side coverage / 컨센서스 for a named stock ("삼성전자 투자의견", "SK하이닉스 목표주가 어떻게 바뀌었어?").
        AVOID WHEN: the user wants fundamentals/valuation numbers (use ls_get_stock_info or ls_get_fundamentals_rank).

        Returns the most recent opinion changes (up to 20). Target prices are in 원; opinion labels are LS-supplied strings such as BUY / HOLD. An empty `opinion_from` means a newly initiated rating.
        """)]
    public static async Task<string> GetAnalystOpinions(
        LsApiClient apiClient,
        [Description("6-digit Korean stock code, e.g. 005930.")]
        string shcode,
        [Description("Maximum opinion entries to return, most recent first (1-20). Default 20.")]
        int count = MaxCount,
        CancellationToken cancellationToken = default)
    {
        string trimmed = (shcode ?? "").Trim();
        if (trimmed.Length == 0)
            return McpJson.Error("shcode is required (6-digit Korean stock code, e.g. 005930).");
        if (count < 1 || count > MaxCount)
            return McpJson.Error($"count must be between 1 and {MaxCount}.", new { received = count });

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t3401",
                new JsonObject
                {
                    ["shcode"] = trimmed,
                    ["gubun1"] = " ",
                    ["tradno"] = " ",
                    ["cts_date"] = " ",
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    shcode = trimmed,
                });

            CurrentQuote? current = null;
            if (response.GetBlock("t3401OutBlock") is { } snapshot)
            {
                string? sign = snapshot.ReadString("sign");
                current = new CurrentQuote(
                    Price: snapshot.ReadLong("price"),
                    Change: (long)IndustryDataCache.ApplySign(snapshot.ReadLong("change"), sign),
                    ChangePct: IndustryDataCache.ApplySign(snapshot.ReadDouble("diff"), sign),
                    Volume: snapshot.ReadLong("volume"));
            }

            JsonElement? array = response.GetBlock("t3401OutBlock1");
            var opinions = new List<OpinionEntry>();
            if (array is not null && array.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    if (opinions.Count >= count)
                        break;
                    string date = row.ReadString("date")?.Trim() ?? "";
                    if (date.Length == 0)
                        continue;
                    opinions.Add(new OpinionEntry(
                        Date: date,
                        Broker: NullIfBlank(row.ReadString("tradname")),
                        OpinionFrom: NullIfBlank(row.ReadString("nopn")),
                        OpinionTo: NullIfBlank(row.ReadString("bopn")),
                        TargetFrom: row.ReadLong("boga"),
                        TargetTo: row.ReadLong("noga"),
                        Close: row.ReadLong("close")));
                }
            }

            var payload = new AnalystOpinionsPayload
            {
                Shcode = trimmed,
                Current = current,
                Count = opinions.Count,
                Opinions = opinions,
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

    static string? NullIfBlank(string? raw)
    {
        string trimmed = (raw ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    sealed record AnalystOpinionsPayload
    {
        public string Shcode { get; init; } = "";

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public CurrentQuote? Current { get; init; }

        public int Count { get; init; }
        public IReadOnlyList<OpinionEntry> Opinions { get; init; } = Array.Empty<OpinionEntry>();
    }

    sealed record CurrentQuote(long Price, long Change, double ChangePct, long Volume);

    sealed record OpinionEntry(
        string Date,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Broker,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OpinionFrom,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OpinionTo,
        long TargetFrom,
        long TargetTo,
        long Close);
}
