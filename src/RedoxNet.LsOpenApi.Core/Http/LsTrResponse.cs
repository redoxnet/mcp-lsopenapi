using System.Text.Json;

namespace RedoxNet.LsOpenApi.Core.Http;

/// <summary>
/// Response from a successful LS TR HTTP call.
/// </summary>
/// <remarks>
/// LS returns one JSON object that contains zero or more named output blocks
/// (e.g. <c>"t1101OutBlock"</c>, <c>"t1101OutBlock1"</c>) plus the protocol
/// envelope <c>rsp_cd</c> / <c>rsp_msg</c>. A non-success <c>rsp_cd</c> is
/// returned with HTTP 200; callers should check <see cref="IsSuccess"/>.
/// </remarks>
public sealed class LsTrResponse
{
    /// <summary>The LS success code (<c>"00000"</c>).</summary>
    public const string SuccessCode = "00000";

    /// <summary>TR code that was invoked.</summary>
    public string TrCode { get; }

    /// <summary>HTTP status code returned by LS.</summary>
    public int StatusCode { get; }

    /// <summary>Raw response body verbatim from LS.</summary>
    public string RawBody { get; }

    /// <summary>Parsed JSON root.</summary>
    public JsonElement Root { get; }

    /// <summary>LS response code (<c>rsp_cd</c>). <see langword="null"/> when missing.</summary>
    public string? RspCode { get; }

    /// <summary>LS response message (<c>rsp_msg</c>). <see langword="null"/> when missing.</summary>
    public string? RspMessage { get; }

    /// <summary>True when LS signals that more pages of data are available, via header or body.</summary>
    public bool HasContinuation { get; }

    /// <summary>
    /// Continuation cursor for header-based TRs (<c>tr_cont_key</c>), or
    /// <see langword="null"/> for body-based TRs (see <see cref="ContinuationKeys"/>).
    /// </summary>
    public string? ContinuationKey { get; }

    /// <summary>
    /// For body-based TRs, the cursor field name → value map that must be
    /// copied into the next request's InBlock (e.g.
    /// <c>{ "cts_date": "20240906", "cts_time": "111200" }</c> for <c>t8412</c>).
    /// Empty for header-based TRs and for responses that have no more pages.
    /// </summary>
    public IReadOnlyDictionary<string, string> ContinuationKeys { get; }

    /// <summary>Output block names present in the body (excluding <c>rsp_cd</c>/<c>rsp_msg</c>).</summary>
    public IReadOnlyList<string> OutBlockNames { get; }

    /// <summary>
    /// Creates a parsed response wrapper. Internal: produced by <see cref="LsApiClient"/>.
    /// </summary>
    internal LsTrResponse(
        string trCode,
        int statusCode,
        string rawBody,
        JsonElement root,
        bool hasContinuation,
        string? continuationKey,
        IReadOnlyDictionary<string, string>? continuationKeys = null)
    {
        TrCode = trCode;
        StatusCode = statusCode;
        RawBody = rawBody;
        Root = root;
        HasContinuation = hasContinuation;
        ContinuationKey = continuationKey;
        ContinuationKeys = continuationKeys ?? new Dictionary<string, string>(0);

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("rsp_cd", out JsonElement rspCd) && rspCd.ValueKind == JsonValueKind.String)
                RspCode = rspCd.GetString();
            if (root.TryGetProperty("rsp_msg", out JsonElement rspMsg) && rspMsg.ValueKind == JsonValueKind.String)
                RspMessage = rspMsg.GetString();

            var blocks = new List<string>();
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                if (prop.Name is "rsp_cd" or "rsp_msg")
                    continue;
                blocks.Add(prop.Name);
            }
            OutBlockNames = blocks;
        }
        else
        {
            OutBlockNames = Array.Empty<string>();
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when LS reports business-level success.
    /// </summary>
    public bool IsSuccess => RspCode == SuccessCode;

    /// <summary>
    /// Retrieves a named output block as a <see cref="JsonElement"/>.
    /// </summary>
    /// <param name="blockName">Output block name, e.g. <c>"t1101OutBlock"</c>.</param>
    /// <returns>The block, or <see langword="null"/> when missing.</returns>
    public JsonElement? GetBlock(string blockName)
    {
        if (Root.ValueKind != JsonValueKind.Object)
            return null;
        return Root.TryGetProperty(blockName, out JsonElement block) ? block : null;
    }
}
