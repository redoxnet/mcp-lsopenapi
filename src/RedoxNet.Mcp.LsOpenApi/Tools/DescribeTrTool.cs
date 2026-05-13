using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Catalog;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that returns the full schema and usage hints for a single TR.
/// </summary>
[McpServerToolType]
public static class DescribeTrTool
{
    /// <summary>
    /// Returns the complete <see cref="TrMeta"/> for one TR code.
    /// </summary>
    /// <param name="catalog">Injected TR catalog.</param>
    /// <param name="tr_cd">TR code from <c>ls_search_tr</c>, e.g. <c>"t1101"</c>.</param>
    /// <returns>JSON describing in/out blocks, continuation support, rate limit.</returns>
    [McpServerTool(Name = "ls_describe_tr")]
    [Description("""
        Returns the full schema and usage hints for a specific LS TR code.

        USE WHEN: a TR code is known and the input/output field list is required before calling ls_call_tr.
        AVOID WHEN: a dedicated semantic tool exists for the operation — prefer ls_get_quote / ls_get_chart / ls_search_stock / etc., which already handle the TR plumbing.
        """)]
    public static string DescribeTr(
        TrCatalog catalog,
        [Description("TR code, e.g. 't1101', 't8410', 't8430'. Case-insensitive.")]
        string tr_cd)
    {
        if (string.IsNullOrWhiteSpace(tr_cd))
            return McpJson.Error("tr_cd is required.");

        TrMeta? meta = catalog.Find(tr_cd);
        if (meta is null)
            return McpJson.Error(
                $"TR '{tr_cd}' is not in the catalog.",
                new { hint = "Use ls_search_tr to discover available TR codes." });

        var payload = new
        {
            tr_cd = meta.TrCode,
            name = meta.Name,
            category = meta.Category,
            path = meta.Path,
            description = meta.Description,
            in_blocks = meta.InBlocks.Select(BlockPayload),
            out_blocks = meta.OutBlocks.Select(BlockPayload),
            continuation = new
            {
                supported = meta.Continuation.Supported,
                key_fields = meta.Continuation.KeyFields,
            },
            rate_limit_per_sec = meta.RateLimitPerSec,
        };
        return JsonSerializer.Serialize(payload, McpJson.Tool);
    }

    /// <summary>
    /// Projects a <see cref="TrBlock"/> into an anonymous object whose shape
    /// matches the public JSON contract of <c>ls_describe_tr</c>.
    /// </summary>
    /// <param name="block">Catalog block (InBlock or OutBlock).</param>
    /// <returns>An anonymous object ready for serialization.</returns>
    static object BlockPayload(TrBlock block) => new
    {
        name = block.Name,
        is_array = block.IsArray,
        fields = block.Fields.Select(f => new
        {
            name = f.Name,
            type = f.Type,
            description = f.Description,
            required = f.Required,
            length = f.Length,
            example = f.Example,
        }),
    };
}
