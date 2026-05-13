using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tool that invokes an arbitrary LS TR with a caller-supplied input block.
/// </summary>
[McpServerToolType]
public static class CallTrTool
{
    /// <summary>
    /// Invokes the given TR and returns the raw response payload.
    /// </summary>
    /// <param name="apiClient">Injected LS API client.</param>
    /// <param name="catalog">Injected TR catalog (for input-shape validation messages).</param>
    /// <param name="tr_cd">TR code, e.g. <c>"t1101"</c>.</param>
    /// <param name="in_block">JSON object matching the TR's InBlock schema. Just the inner block, not the <c>{trcode}InBlock</c> wrapper.</param>
    /// <param name="cont_key">Optional continuation key from a prior response.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>JSON containing tr_cd, status, response code/message, raw response body, and continuation info.</returns>
    [McpServerTool(Name = "ls_call_tr")]
    [Description("""
        Invokes any LS OpenAPI TR with a caller-supplied input block and returns the raw JSON response.

        USE WHEN: a specific TR is needed that is not exposed as a dedicated semantic tool (e.g. an LS-specific report TR, or a TR newly published by LS but not yet wrapped here).
        AVOID WHEN: a dedicated semantic tool exists — prefer ls_get_quote, ls_get_chart, ls_search_stock, etc. They return cleaner, indicator-enriched payloads and handle TR plumbing for you.

        The 'in_block' argument is the inner block payload only; ls_call_tr wraps it as { "{tr_cd}InBlock": in_block } before sending.
        """)]
    public static async Task<string> CallTr(
        LsApiClient apiClient,
        TrCatalog catalog,
        [Description("TR code, e.g. 't1101'. Use ls_search_tr / ls_describe_tr to discover.")]
        string tr_cd,
        [Description("JSON object matching the TR's InBlock schema. Send just the inner block.")]
        JsonElement in_block,
        [Description("Optional continuation key copied from a prior response's tr_cont_key.")]
        string? cont_key = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tr_cd))
            return McpJson.Error("tr_cd is required.");

        TrMeta? meta = catalog.Find(tr_cd);
        if (meta is null)
            return McpJson.Error(
                $"TR '{tr_cd}' is not in the catalog.",
                new { hint = "Use ls_search_tr to discover available TR codes." });

        if (in_block.ValueKind != JsonValueKind.Object)
            return McpJson.Error("in_block must be a JSON object.");

        JsonObject inBlockObject;
        try
        {
            inBlockObject = JsonNode.Parse(in_block.GetRawText())?.AsObject()
                ?? throw new InvalidOperationException("in_block parsed as null.");
        }
        catch (Exception ex)
        {
            return McpJson.Error("Failed to read in_block.", new { reason = ex.Message });
        }

        try
        {
            LsTrResponse response = await apiClient.CallTrAsync(meta.TrCode, inBlockObject, cont_key, cancellationToken);

            var payload = new
            {
                tr_cd = response.TrCode,
                status = response.StatusCode,
                rsp_cd = response.RspCode,
                rsp_msg = response.RspMessage,
                is_success = response.IsSuccess,
                out_block_names = response.OutBlockNames,
                continuation = new
                {
                    has_more = response.HasContinuation,
                    // Header-based TRs (newer CSPAQ-style): single 'key' value to
                    // pass back as the cont_key parameter to ls_call_tr.
                    key = response.ContinuationKey,
                    // Body-based TRs (legacy stock TRs t8410/t8412/t1301):
                    // copy each (field, value) entry into the next call's
                    // in_block to fetch the next page.
                    keys = response.ContinuationKeys,
                },
                body = JsonDocument.Parse(response.RawBody).RootElement,
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("Authentication failed.", new { reason = ex.Message, status = ex.StatusCode });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new
            {
                tr_cd = ex.TrCode,
                status = ex.StatusCode,
                reason = ex.Message,
                response_body_preview = ex.ResponseBody?.Length > 512 ? ex.ResponseBody[..512] + "…" : ex.ResponseBody,
            });
        }
    }
}
