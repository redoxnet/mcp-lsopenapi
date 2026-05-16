using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.Mcp.LsOpenApi.Portfolio;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns the stocks belonging to one LS curated theme (t1537),
/// plus the theme summary. Header-based continuation paging (tr_cont/tr_cont_key)
/// and keyword resolution against the t1531 catalog cache.
/// </summary>
[McpServerToolType]
internal static class GetThemeStocksTool
{
    /// <summary>Upper bound on returned rows to prevent runaway paging.</summary>
    public const int MaxTopN = 200;
    /// <summary>Safety cap on continuation calls.</summary>
    public const int MaxPages = 10;

    [McpServerTool(Name = "ls_get_theme_stocks")]
    [Description("""
        Returns the stocks inside one LS curated theme (e.g. "AI", "2차전지", "반도체 장비") with the theme's roll-up summary attached. Wraps LS t1537 with header-based continuation paging (tr_cont/tr_cont_key) and resolves a theme_keyword against the cached t1531 catalog.

        USE WHEN: the user asks "AI 테마 종목", "2차전지 테마 종목 비교", "테마 0064 안에 뭐 있어?".
        AVOID WHEN: the user wants industry/sector (KRX 산업분류) — use ls_get_industry_stocks. For market-wide theme rankings, t1531 directly via ls_call_tr.

        theme_code XOR theme_keyword: pass a 4-character tmcode (e.g. '0064') or a Korean keyword. 0 matches → ThemeNotFound; 2+ → AmbiguousTheme with candidates. theme_code wins when both supplied.
        """)]
    public static async Task<string> GetThemeStocks(
        LsApiClient apiClient,
        IQuoteService quoteService,
        [Description("4-character LS theme code (tmcode), e.g. '0064' (2차전지), '0012' (반도체 장비). Mutually exclusive with theme_keyword (theme_code wins on conflict).")]
        string? theme_code = null,
        [Description("Theme name keyword (e.g. '2차전지', 'AI'). Resolved via t1531 catalog LIKE match. 0 matches → ThemeNotFound; 2+ → AmbiguousTheme envelope with candidates.")]
        string? theme_keyword = null,
        [Description("Max stock rows returned (1-200). Default 30.")]
        int top_n = 30,
        CancellationToken cancellationToken = default)
    {
        if (top_n < 1 || top_n > MaxTopN)
            return McpJson.Error($"top_n must be between 1 and {MaxTopN}.");

        // Resolve theme code via explicit input or keyword catalog lookup.
        string? resolvedCode = null;
        ThemeCatalogRow? resolvedRow = null;
        string? matchedVia = null;
        if (!string.IsNullOrWhiteSpace(theme_code))
        {
            string trimmed = theme_code.Trim();
            if (trimmed.Length != 4 || !trimmed.All(char.IsLetterOrDigit))
                return McpJson.Error($"theme_code '{theme_code}' must be a 4-character alphanumeric code.");
            resolvedCode = trimmed.ToUpperInvariant();
            matchedVia = "code";
        }
        else if (!string.IsNullOrWhiteSpace(theme_keyword))
        {
            ThemeCatalogResult catalog = await quoteService.GetThemeCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (catalog.Error is not null)
                return McpJson.Error("Theme catalog lookup failed.", new { reason = catalog.Error });

            string needle = theme_keyword.Trim();
            var matches = catalog.Rows
                .Where(r => r.Name.Contains(needle, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
                return McpJson.Error($"No theme matches keyword '{needle}'.", new
                {
                    error_code = "ThemeNotFound",
                    candidates = catalog.Rows.Take(10).Select(r => new { theme_code = r.Code, theme_name = r.Name }).ToArray(),
                });
            if (matches.Count > 1)
                return McpJson.Error($"Keyword '{needle}' matches {matches.Count} themes; specify theme_code.", new
                {
                    error_code = "AmbiguousTheme",
                    candidates = matches.Select(r => new { theme_code = r.Code, theme_name = r.Name }).ToArray(),
                });

            resolvedRow = matches[0];
            resolvedCode = resolvedRow.Code;
            matchedVia = "keyword";
        }
        else
        {
            return McpJson.Error("Either theme_code or theme_keyword is required.");
        }

        try
        {
            var stocks = new List<ThemeStockRow>(top_n);
            ThemeSummary? themeSummary = null;
            string? continuationKey = null;

            for (int page = 0; page < MaxPages && stocks.Count < top_n; page++)
            {
                LsTrResponse response = await apiClient.CallTrAsync(
                    "t1537",
                    new JsonObject { ["tmcode"] = resolvedCode },
                    continuationKey: continuationKey,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccess)
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        requested_theme_code = resolvedCode,
                    });

                if (themeSummary is null)
                {
                    JsonElement? summary = response.GetBlock("t1537OutBlock");
                    if (summary is not null)
                    {
                        JsonElement s = summary.Value;
                        themeSummary = new ThemeSummary(
                            Code: resolvedCode,
                            Name: (s.ReadString("tmname")?.Trim() ?? resolvedRow?.Name ?? resolvedCode),
                            StockCount: (int)s.ReadLong("tmcnt"),
                            UpCount: (int)s.ReadLong("upcnt"),
                            UpRate: s.ReadDouble("uprate"));
                    }
                }

                JsonElement? array = response.GetBlock("t1537OutBlock1");
                if (array is null || array.Value.ValueKind != JsonValueKind.Array)
                    break;

                int beforeCount = stocks.Count;
                foreach (JsonElement row in array.Value.EnumerateArray())
                {
                    string? shcode = row.ReadString("shcode")?.Trim();
                    if (string.IsNullOrEmpty(shcode))
                        continue;
                    string? sign = row.ReadString("sign");
                    long rawChange = row.ReadLong("change");
                    double rawPct = row.ReadDouble("diff");
                    stocks.Add(new ThemeStockRow(
                        Shcode: shcode,
                        Name: (row.ReadString("hname") ?? "").Trim(),
                        Price: row.ReadLong("price"),
                        Change: (long)IndustryDataCache.ApplySign(rawChange, sign),
                        ChangePct: IndustryDataCache.ApplySign(rawPct, sign),
                        Volume: row.ReadLong("volume"),
                        Value: row.ReadLong("value"),
                        Open: row.ReadLong("open"),
                        High: row.ReadLong("high"),
                        Low: row.ReadLong("low"),
                        MarketCap: row.ReadLong("marketcap")));
                    if (stocks.Count >= top_n)
                        break;
                }

                if (stocks.Count == beforeCount)
                    break;
                if (!response.HasContinuation || string.IsNullOrEmpty(response.ContinuationKey))
                    break;
                continuationKey = response.ContinuationKey;
            }

            var payload = new
            {
                theme = themeSummary ?? new ThemeSummary(resolvedCode, resolvedRow?.Name ?? resolvedCode, 0, 0, 0),
                resolved = matchedVia is null ? null : new
                {
                    theme_code = resolvedCode,
                    theme_name = resolvedRow?.Name ?? themeSummary?.Name,
                    matched_via = matchedVia,
                },
                count = stocks.Count,
                stocks,
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

    sealed record ThemeSummary(
        string Code,
        string Name,
        int StockCount,
        int UpCount,
        double UpRate);

    sealed record ThemeStockRow(
        string Shcode,
        string Name,
        long Price,
        long Change,
        double ChangePct,
        long Volume,
        long Value,
        long Open,
        long High,
        long Low,
        long MarketCap);
}
