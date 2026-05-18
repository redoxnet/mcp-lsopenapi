using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP wrapper that returns the union of LS's two market-warning screens:
/// t1404 (관리/불성실공시/투자유의/투자환기) and t1405 (투자경고/매매정지/
/// 정리매매/투자주의/투자위험/위험예고/단기과열지정/이상급등/상장주식수부족).
/// </summary>
[McpServerToolType]
public static class GetMarketWarningsTool
{
    /// <summary>Defensive cap so one call cannot fan out to dozens of TR requests.</summary>
    /// <remarks>
    /// v0.7 E2E showed real KRX screens fit in 1 page; the 6-page cap from the
    /// pre-dedup era was load-bearing only because LS was looping the same
    /// screen. With the per-kind <c>seen</c> dedup in <see cref="FetchKindAsync"/>
    /// the loop already breaks early, so 3 is enough for the legitimate "screen
    /// genuinely spans multiple pages" case while keeping fan-out tight.
    /// </remarks>
    const int MaxPagesPerKind = 3;

    /// <summary>Default warning set when the caller does not specify <c>kinds</c>.</summary>
    /// <remarks>
    /// Just <c>관리</c> (t1404 jongchk=1). v0.7 E2E showed the model commonly
    /// calls this tool with no kinds for natural questions like "오늘 관리종목" —
    /// the previous 5-kind default fanned out to 5 TR calls and ~500 rows when
    /// the user wanted exactly one screen. Models that want comprehensive
    /// surveillance coverage pass an explicit list (the description points at
    /// the multi-kind pattern), so default-narrow is the safer floor.
    /// </remarks>
    static readonly string[] DefaultKindCodes = new[]
    {
        "t1404:1", // 관리
    };

    sealed record KindSpec(string Tr, string Jongchk, string Kind, string KoreanLabel);

    // Map: every accepted alias → KindSpec. Key is lower-invariant trimmed input.
    static readonly IReadOnlyList<KindSpec> AllKinds = new KindSpec[]
    {
        new("t1404", "1", "designated_admin",         "관리종목"),
        new("t1404", "2", "untrustworthy_disclosure", "불성실공시"),
        new("t1404", "3", "investment_caution",       "투자유의"),
        new("t1404", "4", "investment_warning_call",  "투자환기"),
        new("t1405", "1", "investment_alert",         "투자경고"),
        new("t1405", "2", "trading_halt",             "매매정지"),
        // 정리매매 = the 7-day liquidation window before delisting. "Liquidation
        // trading" is the standard English finance term; v0.7 E2E showed models
        // reaching for it first. The legacy "delisting_trade" name is kept as an
        // input alias in ResolveKind() for back-compat.
        new("t1405", "3", "liquidation_trading",      "정리매매"),
        new("t1405", "4", "investment_attention",     "투자주의"),
        new("t1405", "5", "investment_risk",          "투자위험"),
        new("t1405", "6", "risk_forecast",            "위험예고"),
        new("t1405", "7", "short_term_overheating",   "단기과열"),
        new("t1405", "8", "abnormal_surge",           "이상급등"),
        new("t1405", "9", "shares_outstanding_shortage", "상장주식수부족"),
    };

    [McpServerTool(Name = "ls_get_market_warnings")]
    [Description("""
        Returns Korean-market warning / surveillance designations. Wraps t1404 + t1405 behind a single tool so the model can answer *"내 보유 중 관리종목 있어?"*, *"매매정지 종목"*, or *"단기과열 지정 종목"* in one call.

        Two source TRs underpin it:
        - **t1404** — 관리 / 불성실공시 / 투자유의 / 투자환기.
        - **t1405** — 투자경고 / 매매정지 / 정리매매 / 투자주의 / 투자위험 / 위험예고 / 단기과열지정 / 이상급등 / 상장주식수부족.

        USE WHEN: the user is screening for KRX-side risk flags, or asking "are any of my holdings on a watchlist". AVOID WHEN: the user wants disclosure feed (공시) or news — those belong to other TRs.

        kinds: list of English snake_case ('designated_admin', 'trading_halt', 'short_term_overheating', 'liquidation_trading', …), Korean labels ('관리종목', '매매정지', '단기과열', '정리매매', …), or LS jongchk codes prefixed with the source TR ('t1404:1', 't1405:7'). When omitted, only 관리 (designated_admin) is queried — pass an explicit list (e.g. ['관리', '매매정지', '단기과열']) for a wider surveillance sweep.

        shcodes: optional whitelist (typically the user's holdings). When supplied, only matching rows pass through; otherwise the full market scope is returned.

        market: 'all' (default) / 'kospi' / 'kosdaq'.

        Each row carries the source TR and the LS jongchk code so the model can cite the exact designation type.
        """)]
    public static async Task<string> GetMarketWarnings(
        LsApiClient apiClient,
        [Description("Optional list of warning kinds. English snake_case, Korean label, or 'trCode:jongchk' (e.g. 't1405:7') accepted. When omitted, only 관리 (designated_admin) is queried — pass an explicit list for wider sweep.")]
        string[]? kinds = null,
        [Description("Optional subset of 6-digit shcodes to keep. Useful for filtering against a user's holdings list.")]
        string[]? shcodes = null,
        [Description("Market scope: 'all' (default), 'kospi', or 'kosdaq'.")]
        string market = "all",
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveMarket(market, out string gubun, out string normalizedMarket))
            return McpJson.Error($"market '{market}' is not recognized. Use 'all', 'kospi', or 'kosdaq'.");

        List<KindSpec> selectedKinds;
        if (kinds is null || kinds.Length == 0)
        {
            selectedKinds = DefaultKindCodes.Select(ResolveKindCode).ToList()!;
        }
        else
        {
            selectedKinds = new List<KindSpec>(kinds.Length);
            foreach (string raw in kinds)
            {
                KindSpec? resolved = ResolveKind(raw);
                if (resolved is null)
                    return McpJson.Error($"kind '{raw}' is not recognized. See the tool description for accepted forms.");
                selectedKinds.Add(resolved);
            }
        }

        HashSet<string>? shcodeFilter = null;
        if (shcodes is { Length: > 0 })
        {
            shcodeFilter = new HashSet<string>(StringComparer.Ordinal);
            foreach (string s in shcodes)
            {
                if (string.IsNullOrWhiteSpace(s))
                    continue;
                shcodeFilter.Add(s.Trim());
            }
        }

        var rows = new List<WarningRow>();
        try
        {
            foreach (KindSpec kind in selectedKinds)
            {
                await FetchKindAsync(apiClient, kind, gubun, shcodeFilter, rows, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("Authentication failed.", new { reason = ex.Message });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
        catch (LsBusinessErrorException ex)
        {
            return McpJson.Error("LS reported a business-level error.", new
            {
                rsp_cd = ex.RspCode,
                rsp_msg = ex.RspMessage,
                source_tr = ex.SourceTr,
                kind = ex.Kind,
            });
        }

        // Sort by kind then shcode for stable presentation; the model commonly
        // wants "all 관리 종목 first, then 매매정지" groupings.
        rows.Sort((a, b) =>
        {
            int byKind = string.Compare(a.Kind, b.Kind, StringComparison.Ordinal);
            if (byKind != 0) return byKind;
            return string.Compare(a.Shcode, b.Shcode, StringComparison.Ordinal);
        });

        var payload = new WarningsPayload
        {
            Market = normalizedMarket,
            QueriedKinds = selectedKinds.Select(k => k.Kind).ToArray(),
            Count = rows.Count,
            Rows = rows,
        };
        return JsonSerializer.Serialize(payload, McpJson.Tool);
    }

    static async Task FetchKindAsync(
        LsApiClient apiClient,
        KindSpec kind,
        string gubun,
        HashSet<string>? shcodeFilter,
        List<WarningRow> sink,
        CancellationToken cancellationToken)
    {
        string ctsShcode = " ";
        // Per-kind seen-shcode set so we can detect "LS echoes cursor without
        // advancing" — observed live in v0.7 E2E: when the entire kind's data
        // fits in one page, LS keeps echoing the last shcode as cts_shcode and
        // returning the same rows. Without dedup the loop fanned out 6× and
        // shipped ~600 duplicate rows for a 100-row screen.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int page = 0; page < MaxPagesPerKind; page++)
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                kind.Tr,
                new JsonObject
                {
                    ["gubun"] = gubun,
                    ["jongchk"] = kind.Jongchk,
                    ["cts_shcode"] = ctsShcode,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                throw new LsBusinessErrorException(kind.Tr, kind.Kind, response.RspCode, response.RspMessage);

            int newRowsThisPage = 0;
            JsonElement? array = response.GetBlock($"{kind.Tr}OutBlock1");
            if (array is not null && array.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    string shcode = row.ReadString("shcode")?.Trim() ?? "";
                    if (string.IsNullOrEmpty(shcode))
                        continue;
                    if (!seen.Add(shcode))
                        continue; // Already returned on a prior page — LS is echoing the same screen.
                    newRowsThisPage++;
                    if (shcodeFilter is not null && !shcodeFilter.Contains(shcode))
                        continue;
                    sink.Add(BuildRow(kind, shcode, row));
                }
            }

            string? nextCts = response.GetBlock($"{kind.Tr}OutBlock")?.ReadString("cts_shcode")?.Trim();
            if (string.IsNullOrEmpty(nextCts))
                break;
            // Cursor must advance OR new rows must arrive. If neither, LS is
            // serving the same screen — abort to avoid the 6× duplication.
            if (string.Equals(nextCts, ctsShcode, StringComparison.Ordinal) || newRowsThisPage == 0)
                break;
            ctsShcode = nextCts;
        }
    }

    static WarningRow BuildRow(KindSpec kind, string shcode, JsonElement row)
    {
        string? since = row.ReadString("date")?.Trim();
        string? until = row.ReadString("edate")?.Trim();
        // t1404 ships a `reason` code; t1405 does not. Surface as nullable.
        string? reason = row.ReadString("reason")?.Trim();
        return new WarningRow(
            SourceTr: kind.Tr,
            Jongchk: kind.Jongchk,
            Kind: kind.Kind,
            KoreanLabel: kind.KoreanLabel,
            Shcode: shcode,
            Name: row.ReadString("hname")?.Trim(),
            Price: row.ReadLong("price"),
            Sign: row.ReadString("sign"),
            Change: row.ReadLong("change"),
            ChangePct: row.ReadDouble("diff"),
            Volume: row.ReadLong("volume"),
            Since: string.IsNullOrEmpty(since) ? null : since,
            Until: string.IsNullOrEmpty(until) ? null : until,
            ReasonCode: string.IsNullOrEmpty(reason) ? null : reason);
    }

    static KindSpec? ResolveKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string trimmed = raw.Trim();
        // Source-prefixed form like "t1404:1".
        if (trimmed.Contains(':'))
            return ResolveKindCode(trimmed);
        string lower = trimmed.ToLowerInvariant();
        foreach (KindSpec spec in AllKinds)
        {
            if (string.Equals(spec.Kind, lower, StringComparison.Ordinal))
                return spec;
            if (string.Equals(spec.KoreanLabel, trimmed, StringComparison.Ordinal))
                return spec;
        }
        // Korean aliases without the "지정" suffix LS sometimes appends, plus
        // legacy English names from earlier v0.7-prep builds.
        switch (lower)
        {
            case "delisting_trade": case "delisting": return AllKinds.First(k => k.Kind == "liquidation_trading");
        }
        switch (trimmed)
        {
            case "관리": return AllKinds.First(k => k.Kind == "designated_admin");
            case "단기과열": case "단기과열지정": return AllKinds.First(k => k.Kind == "short_term_overheating");
            case "정지": case "거래정지": return AllKinds.First(k => k.Kind == "trading_halt");
        }
        return null;
    }

    static KindSpec? ResolveKindCode(string raw)
    {
        string[] parts = raw.Split(':', 2);
        if (parts.Length != 2)
            return null;
        string tr = parts[0].Trim();
        string jongchk = parts[1].Trim();
        return AllKinds.FirstOrDefault(k => k.Tr == tr && k.Jongchk == jongchk);
    }

    static bool TryResolveMarket(string? raw, out string code, out string normalized)
    {
        string lower = (raw ?? "all").Trim().ToLowerInvariant();
        switch (lower)
        {
            case "" or "all" or "전체":
                code = "0"; normalized = "all"; return true;
            case "kospi" or "코스피":
                code = "1"; normalized = "kospi"; return true;
            case "kosdaq" or "코스닥":
                code = "2"; normalized = "kosdaq"; return true;
            default:
                code = ""; normalized = ""; return false;
        }
    }

    sealed class LsBusinessErrorException(string sourceTr, string kind, string? rspCode, string? rspMessage) : Exception
    {
        public string SourceTr { get; } = sourceTr;
        public string Kind { get; } = kind;
        public string? RspCode { get; } = rspCode;
        public string? RspMessage { get; } = rspMessage;
    }

    sealed record WarningsPayload
    {
        public string Market { get; init; } = "";
        public IReadOnlyList<string> QueriedKinds { get; init; } = Array.Empty<string>();
        public int Count { get; init; }
        public IReadOnlyList<WarningRow> Rows { get; init; } = Array.Empty<WarningRow>();
    }

    sealed record WarningRow(
        string SourceTr,
        string Jongchk,
        string Kind,
        string KoreanLabel,
        string Shcode,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Name,
        long Price,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Sign,
        long Change,
        double ChangePct,
        long Volume,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Since,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Until,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ReasonCode);
}
