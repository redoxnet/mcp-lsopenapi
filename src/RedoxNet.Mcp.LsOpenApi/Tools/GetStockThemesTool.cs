using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns every LS curated theme a given stock belongs to (t1532).
/// Empty arrays are valid responses — not every stock is themed.
/// </summary>
[McpServerToolType]
public static class GetStockThemesTool
{
    [McpServerTool(Name = "ls_get_stock_themes")]
    [Description("""
        Returns every LS curated theme (tmcode) that a Korean stock belongs to, with the theme's recent average percent change. Wraps LS t1532. An empty array is a valid response — many stocks aren't pinned to any theme.

        USE WHEN: the user asks "삼성전자는 어떤 테마야?", "이 종목 테마 묶음", or before calling ls_get_theme_stocks to discover the actual tmcode.
        AVOID WHEN: the user wants the KRX industry/sector classification — that's stocks.krx_sector / t1102, not LS themes.
        """)]
    public static async Task<string> GetStockThemes(
        LsApiClient apiClient,
        [Description("6-character Korean short code, e.g. '005930' for Samsung Electronics.")]
        string shcode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shcode))
            return McpJson.Error("shcode is required.");
        string normalized = shcode.Trim().ToUpperInvariant();
        if (normalized.Length != 6 || !normalized.All(char.IsLetterOrDigit))
            return McpJson.Error($"shcode '{shcode}' is not a valid 6-character stock/ETF code.");

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t1532",
                new JsonObject { ["shcode"] = normalized },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                });

            JsonElement? block = response.GetBlock("t1532OutBlock");
            var themes = new List<ThemeMembershipRow>();
            if (block is not null && block.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in block.Value.EnumerateArray())
                {
                    string? code = row.ReadString("tmcode")?.Trim();
                    if (string.IsNullOrEmpty(code))
                        continue;
                    themes.Add(new ThemeMembershipRow(
                        ThemeCode: code,
                        ThemeName: (row.ReadString("tmname") ?? "").Trim(),
                        AvgChangePct: row.ReadDouble("avgdiff")));
                }
            }

            var payload = new
            {
                shcode = normalized,
                count = themes.Count,
                themes,
                timestamp = GetIndexQuoteTool.SeoulNowIsoString(),
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

    sealed record ThemeMembershipRow(string ThemeCode, string ThemeName, double AvgChangePct);
}
