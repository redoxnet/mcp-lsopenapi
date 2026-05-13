using System.Text.Json.Nodes;

namespace RedoxNet.LsOpenApi.Core.Http;

/// <summary>
/// Strongly-typed request descriptor for an LS OpenAPI TR call.
/// </summary>
/// <remarks>
/// Built by callers via the constructor or by helpers on <see cref="LsApiClient"/>.
/// </remarks>
public sealed class LsTrRequest
{
    /// <summary>TR code, e.g. <c>"t1101"</c>.</summary>
    public string TrCode { get; }

    /// <summary>The input block payload — typically a single object keyed by <c>{trcode}InBlock</c>.</summary>
    public JsonObject Body { get; }

    /// <summary>Continuation key for paginated TRs. <see langword="null"/> for the first page.</summary>
    public string? ContinuationKey { get; }

    /// <summary>
    /// Creates a TR request whose body is the supplied raw JSON object.
    /// </summary>
    /// <param name="trCode">TR code.</param>
    /// <param name="body">Full request body (LS expects <c>{ "{trcode}InBlock": { ... } }</c> shape).</param>
    /// <param name="continuationKey">Continuation key from a prior response, when paging.</param>
    public LsTrRequest(string trCode, JsonObject body, string? continuationKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trCode);
        ArgumentNullException.ThrowIfNull(body);
        TrCode = trCode;
        Body = body;
        ContinuationKey = continuationKey;
    }

    /// <summary>
    /// Convenience builder: wraps <paramref name="inBlock"/> inside the canonical
    /// <c>{ "{trcode}InBlock": { ... } }</c> body shape.
    /// </summary>
    /// <param name="trCode">TR code.</param>
    /// <param name="inBlock">Just the input block fields (without the wrapping key).</param>
    /// <param name="continuationKey">Optional continuation key.</param>
    /// <returns>A ready-to-send TR request.</returns>
    public static LsTrRequest FromInBlock(string trCode, JsonObject inBlock, string? continuationKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trCode);
        ArgumentNullException.ThrowIfNull(inBlock);

        var body = new JsonObject
        {
            [$"{trCode}InBlock"] = inBlock.DeepClone(),
        };
        return new LsTrRequest(trCode, body, continuationKey);
    }
}
