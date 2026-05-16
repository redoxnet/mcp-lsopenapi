using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns the stocks belonging to a Korean industry/sector
/// (t1516), plus the industry's own index summary. Supports body-based
/// continuation paging (last shcode echoed) and resolves
/// <c>industry_keyword</c> against the cached t8424 catalog.
/// </summary>
[McpServerToolType]
internal static class GetIndustryStocksTool
{
    /// <summary>Upper bound on returned rows to prevent runaway paging.</summary>
    public const int MaxTopN = 200;
    /// <summary>Safety cap on continuation calls to t1516.</summary>
    public const int MaxPages = 10;

    /// <summary>
    /// Returns t1516OutBlock (industry index summary) + t1516OutBlock1 (per-stock rows).
    /// </summary>
    [McpServerTool(Name = "ls_get_industry_stocks")]
    [Description("""
        Returns the stocks inside a Korean industry/sector with the industry index summary attached. Wraps LS t1516 with body-based continuation paging (last shcode echoed) and resolves an industry_keyword against the cached t8424 catalog.

        USE WHEN: the user asks "전기전자 업종 종목", "반도체 업종 비교", "코스피 운수창고 종목들" — i.e. peer-comparison across one industry.
        AVOID WHEN: the user wants market-wide industry rankings (use ls_get_industry_indices) or a single stock detail (use ls_get_quote).

        upcode XOR industry_keyword: pass either a 3-char LS upcode (e.g. '013' 전기전자) or a Korean keyword that resolves against the catalog (LIKE match). Multiple matches return an AmbiguousIndustry envelope with candidates. When both are supplied, upcode wins.
        market: t1516 gubun. '1'=코스피업종 (default), '2'=코스닥업종, '3'=섹터지수.
        """)]
    public static async Task<string> GetIndustryStocks(
        LsApiClient apiClient,
        IndustryDataCache cache,
        [Description("3-character LS upcode (e.g. '001' KOSPI종합, '013' 전기전자). Mutually exclusive with industry_keyword (upcode wins on conflict).")]
        string? upcode = null,
        [Description("Industry name keyword (e.g. '반도체', '전기전자'). Resolved via t8424 catalog LIKE match. 0 matches → IndustryNotFound; 2+ → AmbiguousIndustry envelope with candidates.")]
        string? industry_keyword = null,
        [Description("t1516 gubun. '1'=코스피업종 (default), '2'=코스닥업종, '3'=섹터지수.")]
        string market = "1",
        [Description("Max stock rows returned (1-200). Default 30.")]
        int top_n = 30,
        CancellationToken cancellationToken = default)
    {
        if (top_n < 1 || top_n > MaxTopN)
            return McpJson.Error($"top_n must be between 1 and {MaxTopN}.");

        string gubun = (market ?? "").Trim() switch
        {
            "" or "1" or "kospi" => "1",
            "2" or "kosdaq" => "2",
            "3" or "sector" => "3",
            _ => "",
        };
        if (gubun.Length == 0)
            return McpJson.Error($"market '{market}' is not recognized. Use 1 (kospi), 2 (kosdaq), or 3 (sector).");

        // Resolve upcode: explicit wins over keyword. Keyword may produce
        // 0/1/N matches — model the three branches per §4.1.1.
        string? resolvedUpcode = null;
        IndustryCatalogRow? resolvedRow = null;
        string? matchedVia = null;
        if (!string.IsNullOrWhiteSpace(upcode))
        {
            string trimmed = upcode.Trim();
            if (trimmed.Length != 3 || !trimmed.All(char.IsLetterOrDigit))
                return McpJson.Error($"upcode '{upcode}' must be a 3-character alphanumeric code.");
            resolvedUpcode = trimmed;
            matchedVia = "upcode";
        }
        else if (!string.IsNullOrWhiteSpace(industry_keyword))
        {
            string catalogMarket = gubun switch { "1" => "kospi", "2" => "kosdaq", _ => "all" };
            IndustryCatalogResult catalog = await cache.GetCatalogAsync(catalogMarket, cancellationToken).ConfigureAwait(false);
            if (catalog.Error is not null)
                return McpJson.Error("Catalog lookup failed.", new { reason = catalog.Error });

            string needle = industry_keyword.Trim();
            var matches = catalog.Rows
                .Where(r => r.Name.Contains(needle, StringComparison.Ordinal))
                .ToList();

            if (matches.Count == 0)
                return McpJson.Error($"No industry matches keyword '{needle}'.", new
                {
                    error_code = "IndustryNotFound",
                    candidates = catalog.Rows.Take(10).Select(r => new { upcode = r.Upcode, name = r.Name }).ToArray(),
                });
            if (matches.Count > 1)
                return McpJson.Error($"Keyword '{needle}' matches {matches.Count} industries; specify upcode.", new
                {
                    error_code = "AmbiguousIndustry",
                    candidates = matches.Select(r => new { upcode = r.Upcode, name = r.Name }).ToArray(),
                });

            resolvedRow = matches[0];
            resolvedUpcode = resolvedRow.Upcode;
            matchedVia = "keyword";
        }
        else
        {
            return McpJson.Error("Either upcode or industry_keyword is required.");
        }

        try
        {
            var stocks = new List<IndustryStockRow>(top_n);
            string shcodeCursor = "";
            IndexSummary? industrySummary = null;
            string? lastShcode = null;

            for (int page = 0; page < MaxPages && stocks.Count < top_n; page++)
            {
                LsTrResponse response = await apiClient.CallTrAsync(
                    "t1516",
                    new JsonObject
                    {
                        ["upcode"] = resolvedUpcode,
                        ["gubun"] = gubun,
                        ["shcode"] = shcodeCursor,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccess)
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        requested_upcode = resolvedUpcode,
                    });

                if (industrySummary is null)
                {
                    JsonElement? summary = response.GetBlock("t1516OutBlock");
                    if (summary is not null)
                    {
                        JsonElement s = summary.Value;
                        string? sign = s.ReadString("sign");
                        industrySummary = new IndexSummary(
                            Upcode: resolvedUpcode,
                            Name: resolvedRow?.Name,
                            Value: s.ReadDouble("pricejisu"),
                            Change: IndustryDataCache.ApplySign(s.ReadDouble("change"), sign),
                            ChangePct: IndustryDataCache.ApplySign(s.ReadDouble("jdiff"), sign));
                    }
                }

                JsonElement? array = response.GetBlock("t1516OutBlock1");
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
                    stocks.Add(new IndustryStockRow(
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
                        MarketCap: row.ReadLong("total"),
                        Per: row.ReadDouble("perx"),
                        ForeignNetBuy: row.ReadLong("frgsvolume"),
                        InstitutionNetBuy: row.ReadLong("orgsvolume")));
                    lastShcode = shcode;
                    if (stocks.Count >= top_n)
                        break;
                }

                if (stocks.Count == beforeCount)
                    break; // No progress on this page.

                // Continuation cursor — prefer the OutBlock shcode echo when
                // present, fall back to the last row's shcode otherwise.
                string? nextCursor = response.GetBlock("t1516OutBlock")?.ReadString("shcode")?.Trim();
                if (string.IsNullOrEmpty(nextCursor) || string.Equals(nextCursor, shcodeCursor, StringComparison.Ordinal))
                {
                    if (string.IsNullOrEmpty(lastShcode) || string.Equals(lastShcode, shcodeCursor, StringComparison.Ordinal))
                        break;
                    nextCursor = lastShcode;
                }
                shcodeCursor = nextCursor!;
            }

            var payload = new
            {
                industry = industrySummary ?? new IndexSummary(resolvedUpcode, resolvedRow?.Name, 0, 0, 0),
                resolved = matchedVia is null ? null : new
                {
                    upcode = resolvedUpcode,
                    name = resolvedRow?.Name,
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

    sealed record IndexSummary(
        string Upcode,
        string? Name,
        double Value,
        double Change,
        double ChangePct);

    sealed record IndustryStockRow(
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
        long MarketCap,
        double Per,
        long ForeignNetBuy,
        long InstitutionNetBuy);
}
