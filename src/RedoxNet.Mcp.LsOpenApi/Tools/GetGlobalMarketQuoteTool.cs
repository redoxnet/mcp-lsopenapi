using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns a one-shot overseas index / FX / futures snapshot via TR t3521.
/// </summary>
[McpServerToolType]
public static class GetGlobalMarketQuoteTool
{
    static readonly IReadOnlyDictionary<string, string> SymbolAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["dow"] = "DJI@DJI",
        ["dji"] = "DJI@DJI",
        ["다우"] = "DJI@DJI",
        ["nasdaq"] = "NAS@IXIC",
        ["ixic"] = "NAS@IXIC",
        ["나스닥"] = "NAS@IXIC",
        ["sp500"] = "SPI@SPX",
        ["s&p500"] = "SPI@SPX",
        ["snp500"] = "SPI@SPX",
        ["soxx"] = "USI@SOXX",
        ["philadelphia_semiconductor"] = "USI@SOXX",
        ["필라델피아반도체"] = "USI@SOXX",
        ["nikkei"] = "NII@NI225",
        ["니케이"] = "NII@NI225",
        ["hangseng"] = "HSI@HSI",
        ["항셍"] = "HSI@HSI",
        ["usdkrw"] = "USDKRWSMBS",
        ["usd_krw"] = "USDKRWSMBS",
        ["원달러"] = "USDKRWSMBS",
        ["달러원"] = "USDKRWSMBS",
        ["usdjpy"] = "USDJPYCOMP",
        ["eurusd"] = "EURUSDCOMP",
        ["jpykrw"] = "JPYKRWCOMP",
        ["usdcny"] = "USDCNYCOMP",
        ["wti"] = "NYM@CL",
        ["gold"] = "COM@GC",
        ["금"] = "COM@GC",
    };

    /// <summary>
    /// Returns t3521 wrapped as a compact semantic envelope.
    /// </summary>
    [McpServerTool(Name = "ls_get_global_market_quote")]
    [Description("""
        Returns a one-shot quote for major overseas indices, FX rates, or overseas futures via LS t3521.

        USE WHEN: the user asks for US/global index snapshots ("나스닥", "S&P 500", "필라델피아 반도체"), FX rates ("원달러 환율", "USD/KRW"), or simple commodity/futures snapshots ("WTI", "금").
        AVOID WHEN: the user wants Korean KOSPI/KOSDAQ indices — use ls_get_index_quote. For historical/minute/tick overseas series use ls_call_tr with t3518 until a dedicated wrapper ships.

        Common symbol aliases: nasdaq→NAS@IXIC, sp500→SPI@SPX, dow→DJI@DJI, soxx→USI@SOXX, usdkrw→USDKRWSMBS, wti→NYM@CL, gold→COM@GC. Raw LS symbols are accepted.
        """)]
    public static async Task<string> GetGlobalMarketQuote(
        LsApiClient apiClient,
        [Description("Asset kind: index/S, fx/R, futures/F. Default index.")]
        string kind = "index",
        [Description("Alias or raw LS symbol. Examples: nasdaq, sp500, dow, soxx, usdkrw, wti, gold, or NAS@IXIC.")]
        string symbol = "nasdaq",
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeKind(kind, out string kindCode, out string kindName))
            return McpJson.Error("kind must be one of: index/S, fx/R, futures/F.");

        string normalizedSymbol = NormalizeSymbol(symbol);
        if (string.IsNullOrWhiteSpace(normalizedSymbol))
            return McpJson.Error("symbol is required. Use an alias like nasdaq/usdkrw or a raw LS symbol like NAS@IXIC.");

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(
                "t3521",
                new JsonObject
                {
                    ["kind"] = kindCode,
                    ["symbol"] = normalizedSymbol,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccess)
            {
                return McpJson.Error("LS reported a business-level error.", new
                {
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    kind = kindCode,
                    symbol = normalizedSymbol,
                });
            }

            JsonElement? block = response.GetBlock("t3521OutBlock");
            if (block is null)
                return McpJson.Error("t3521OutBlock was missing from the response.", new { kind = kindCode, symbol = normalizedSymbol });

            JsonElement b = block.Value;
            string? sign = b.ReadString("sign");
            double rawChange = b.ReadDouble("change");
            double rawPct = b.ReadDouble("diff");

            var payload = new
            {
                kind = kindName,
                kind_code = kindCode,
                symbol = b.ReadString("symbol")?.Trim() is { Length: > 0 } echoedSymbol ? echoedSymbol : normalizedSymbol,
                name = b.ReadString("hname"),
                date = b.ReadString("date"),
                close = b.ReadDouble("close"),
                sign,
                change = IndustryDataCache.ApplySign(rawChange, sign),
                change_pct = IndustryDataCache.ApplySign(rawPct, sign),
                source_tr = "t3521",
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

    internal static string NormalizeSymbol(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        string key = raw.Trim().Replace("/", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        return SymbolAliases.TryGetValue(key, out string? symbol)
            ? symbol
            : raw.Trim();
    }

    internal static bool TryNormalizeKind(string? raw, out string code, out string name)
    {
        string key = raw?.Trim().ToLowerInvariant().Replace("-", "_") ?? "";
        (code, name) = key switch
        {
            "" or "s" or "index" or "indices" or "overseas_index" or "global_index" or "지수" or "해외지수" => ("S", "index"),
            "r" or "fx" or "forex" or "currency" or "exchange_rate" or "환율" => ("R", "fx"),
            "f" or "future" or "futures" or "commodity" or "선물" or "해외선물" => ("F", "futures"),
            _ => ("", ""),
        };

        return code.Length > 0;
    }
}
