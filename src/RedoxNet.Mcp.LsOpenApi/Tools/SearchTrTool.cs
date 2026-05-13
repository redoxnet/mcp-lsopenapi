using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Catalog;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that searches the embedded LS TR catalog by keyword.
/// </summary>
[McpServerToolType]
public static class SearchTrTool
{
    /// <summary>
    /// Returns matching TR entries, ranked best-first.
    /// </summary>
    /// <param name="catalog">Injected TR catalog.</param>
    /// <param name="keyword">Korean or English keyword. Examples: 현재가, balance, 차트, ohlcv.</param>
    /// <param name="limit">Max results, default 10, max 50.</param>
    /// <returns>JSON array of <c>{ tr_cd, name, category, path, description, in_block_brief }</c>.</returns>
    [McpServerTool(Name = "ls_search_tr")]
    [Description("""
        Searches the embedded LS OpenAPI TR catalog by Korean or English keyword. Matches against TR code, name, category, description, and field descriptions.

        USE WHEN: the user wants to discover which TR provides a given piece of LS data (e.g. "어떤 TR이 분봉을 주지?", "balance TR 찾아줘").
        AVOID WHEN: the TR code is already known — call ls_describe_tr or the semantic tools (ls_get_quote, ls_get_chart, ...) directly.
        """)]
    public static string SearchTr(
        TrCatalog catalog,
        [Description("Korean or English keyword. Examples: '현재가', '차트', 'balance', 'ohlcv'.")]
        string keyword,
        [Description("Max results (1–50). Default 10.")]
        int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return McpJson.Error("keyword is required.");

        int cappedLimit = Math.Clamp(limit, 1, 50);
        IReadOnlyList<TrMeta> matches = catalog.Search(keyword, cappedLimit);

        var payload = new
        {
            keyword,
            limit = cappedLimit,
            count = matches.Count,
            results = matches.Select(tr => new
            {
                tr_cd = tr.TrCode,
                name = tr.Name,
                category = tr.Category,
                path = tr.Path,
                description = tr.Description,
                in_block_brief = BriefInBlock(tr),
                continuation_supported = tr.Continuation.Supported,
            }),
        };
        return JsonSerializer.Serialize(payload, McpJson.Tool);
    }

    /// <summary>
    /// Renders a one-line summary of a TR's first InBlock for the search hit
    /// preview — just the required field names, comma-separated.
    /// </summary>
    /// <param name="tr">TR metadata.</param>
    /// <returns>A short string like <c>"shcode, gubun"</c>, or <c>"(none)"</c>.</returns>
    static string BriefInBlock(TrMeta tr)
    {
        if (tr.InBlocks.Count == 0)
            return "(none)";

        TrBlock first = tr.InBlocks[0];
        IEnumerable<string> required = first.Fields
            .Where(f => f.Required)
            .Select(f => f.Name);
        string list = string.Join(", ", required);
        return string.IsNullOrEmpty(list) ? "(no required fields)" : list;
    }
}
