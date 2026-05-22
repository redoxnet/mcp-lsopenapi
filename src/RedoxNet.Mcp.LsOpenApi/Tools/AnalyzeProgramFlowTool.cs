using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Analysis;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// Analysis-Layer MCP tool — classifies a stock's program-trading footprint
/// into a deterministic verdict via <see cref="ProgramFootprintAnalyzer"/>.
/// </summary>
/// <remarks>
/// Sits one tier above the Data Layer: it pulls the stock's intraday + daily
/// program-trading flow (TR <c>t1637</c>), runs the deterministic analyzer, and
/// returns a regime verdict plus plain-language evidence. The Interpretation
/// Layer (the LLM) narrates that evidence — no LLM logic lives here.
/// Program-trading flow is a channel, not an investor class — the verdict
/// describes the program tape, not 기관 / 외국인 flow (cross-check
/// <c>ls_get_investor_flow</c> for that).
/// </remarks>
[McpServerToolType]
public static class AnalyzeProgramFlowTool
{
    /// <summary>t1637 program amounts are LS amount-basis 천원; ÷100,000 → 억원.</summary>
    const double ThousandWonPerEokwon = 100_000.0;

    /// <summary>
    /// Classifies a stock's program-trading footprint.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="shcode">6-digit Korean short code.</param>
    /// <param name="name">Optional stock name for the report.</param>
    /// <param name="window">Daily look-back window in trading days (10–60).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A footprint verdict with a deterministic regime and evidence.</returns>
    [McpServerTool(Name = "ls_analyze_program_flow")]
    [Description("""
        Analysis Layer — classifies a Korean stock's program-trading footprint into a deterministic verdict.

        USE WHEN: the user asks what the program-trading channel is doing for a stock — whether program flows are accumulating, distributing, or churning beyond the raw numbers.
        AVOID WHEN: the user just wants the raw program-trading series or a chart — use ls_get_program_trading.

        IMPORTANT — Program-trading flow is a channel, not an investor class: it blends foreign baskets, index arbitrage, and institutional baskets, and misses off-program institutional (direct-order) buying. Do not read this verdict as whether 기관 / 외국인 are accumulating; cross-check ls_get_investor_flow (investors=["all"]) for investor-class flow.

        Pulls the stock's intraday and daily program-trading flow (TR t1637) and computes a regime — accumulation / distribution / churn / neutral — with a 0–1 direction_confidence and these signals: window/today net, buy- and sell-day counts, consecutive-day streak, churn_ratio (one-directional vs two-way), intensity (today vs the window's average day), intraday pace (steady TWAP-like vs bursty), open/close loading, and price_coupling (cumulative net vs price correlation). The evidence[] array is plain-language findings ready to narrate to the user.

        window sets the daily look-back (10–60 trading days, default 20).
        """)]
    public static async Task<CallToolResult> AnalyzeProgramFlow(
        LsApiClient apiClient,
        [Description("6-digit Korean short code, e.g. '005930'.")]
        string shcode,
        [Description("Optional stock name for the report, e.g. '삼성전자'.")]
        string name = "",
        [Description("Daily look-back window in trading days (10–60). Default 20.")]
        int window = 20,
        CancellationToken cancellationToken = default)
    {
        string code = (shcode ?? "").Trim();
        if (code.Length == 0)
            return McpJson.ErrorResult("shcode is required.");

        int windowDays = Math.Clamp(window, 10, 60);

        try
        {
            // 1) Intraday cumulative series (gubun2=0 시간).
            LsTrResponse intradayResp = await apiClient.CallTrAsync(
                "t1637", T1637InBlock(code, "0"), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!intradayResp.IsSuccess)
                return McpJson.ErrorResult("LS reported a business-level error (intraday).", new
                {
                    rsp_cd = intradayResp.RspCode,
                    rsp_msg = intradayResp.RspMessage,
                    shcode = code,
                });

            var intraday = new List<ProgramFlowMinute>();
            string sessionDate = "";
            JsonElement? intradayBlock = intradayResp.GetBlock("t1637OutBlock1");
            if (intradayBlock is not null && intradayBlock.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in intradayBlock.Value.EnumerateArray())
                {
                    string rawDate = r.ReadString("date")?.Trim() ?? "";
                    string rawTime = r.ReadString("time")?.Trim() ?? "";
                    if (rawDate.Length == 8) sessionDate = FormatDate(rawDate);
                    intraday.Add(new ProgramFlowMinute(
                        Time: FormatTime(rawTime),
                        CumulativeNet: r.ReadLong("svalue") / ThousandWonPerEokwon,
                        Price: r.ReadLong("price")));
                }
            }
            intraday.Reverse();   // LS ships newest-first → chronological

            // 2) Daily series (gubun2=1 일자).
            LsTrResponse dailyResp = await apiClient.CallTrAsync(
                "t1637", T1637InBlock(code, "1"), cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (!dailyResp.IsSuccess)
                return McpJson.ErrorResult("LS reported a business-level error (daily).", new
                {
                    rsp_cd = dailyResp.RspCode,
                    rsp_msg = dailyResp.RspMessage,
                    shcode = code,
                });

            var daily = new List<ProgramFlowDay>();
            JsonElement? dailyBlock = dailyResp.GetBlock("t1637OutBlock1");
            if (dailyBlock is not null && dailyBlock.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement r in dailyBlock.Value.EnumerateArray())
                {
                    string rawDate = r.ReadString("date")?.Trim() ?? "";
                    if (rawDate.Length != 8) continue;
                    daily.Add(new ProgramFlowDay(
                        Date: FormatDate(rawDate),
                        Net: r.ReadLong("svalue") / ThousandWonPerEokwon,
                        GrossBuy: r.ReadLong("stksvalue") / ThousandWonPerEokwon,
                        GrossSell: r.ReadLong("offervalue") / ThousandWonPerEokwon));
                }
            }
            daily.Reverse();
            if (daily.Count > windowDays)
                daily = daily.GetRange(daily.Count - windowDays, windowDays);

            if (daily.Count == 0)
                return McpJson.ErrorResult("LS returned no daily program-trading rows for this stock.", new
                {
                    shcode = code,
                    hint = "Check the short code, or the stock may have no program-trading data.",
                });

            ProgramFootprintReport report = ProgramFootprintAnalyzer.Analyze(daily, intraday);

            string? displayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();

            var payload = new
            {
                tool = "analyze_program_flow",
                tr_cd = "t1637",
                shcode = code,
                name = displayName,
                as_of = sessionDate,
                window_days = daily.Count,
                intraday_minutes = intraday.Count,
                regime = report.Regime,
                direction_confidence = report.DirectionConfidence,
                signals = report.Signals,
                evidence = report.Evidence,
                note = "Analysis Layer — deterministic program-trading footprint verdict from the program channel (TR t1637); regime is accumulation / distribution / churn / neutral. Narrate the evidence for the user. Program-trading flow is a channel, not an investor class: it blends foreign baskets, index arbitrage, and institutional baskets, and misses off-program institutional (direct-order) buying. Do not read this verdict as whether 기관 / 외국인 are accumulating; cross-check ls_get_investor_flow (investors=[\"all\"]) for investor-class flow.",
            };

            return McpJson.OkResult(JsonSerializer.Serialize(payload, McpJson.Tool));
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

    /// <summary>Builds a t1637 InBlock for a chart-mode query (full series).</summary>
    static JsonObject T1637InBlock(string shcode, string gubun2) => new()
    {
        ["gubun1"] = "1",          // 1 = 금액 (amount basis)
        ["gubun2"] = gubun2,       // 0 = 시간 (intraday), 1 = 일자 (daily)
        ["shcode"] = shcode,
        ["date"] = "",
        ["time"] = "",
        ["cts_idx"] = 9999,        // 9999 = chart query — the full series
    };

    /// <summary>Formats an <c>HHMMSS</c> time string as <c>HH:mm</c>.</summary>
    static string FormatTime(string hhmmss)
    {
        string t = hhmmss.PadLeft(6, '0');
        return $"{t[..2]}:{t.Substring(2, 2)}";
    }

    /// <summary>Formats a <c>yyyyMMdd</c> date string as <c>yyyy-MM-dd</c>.</summary>
    static string FormatDate(string yyyymmdd) =>
        yyyymmdd.Length == 8
            ? $"{yyyymmdd[..4]}-{yyyymmdd.Substring(4, 2)}-{yyyymmdd.Substring(6, 2)}"
            : yyyymmdd;
}
