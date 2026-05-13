using System.Text.Json.Serialization;

namespace RedoxNet.LsOpenApi.Core.Catalog;

/// <summary>
/// Continuation (pagination) descriptor for a TR.
/// </summary>
/// <remarks>
/// LS uses two distinct continuation styles depending on the TR vintage:
/// <list type="bullet">
///   <item><description><b>Header-based</b> (newer, CSPAQ-style): the server returns <c>tr_cont: Y</c> + <c>tr_cont_key: ...</c> response headers and the caller echoes them back on the next call. <see cref="KeyFields"/> is empty/null for these TRs.</description></item>
///   <item><description><b>Body-based</b> (legacy stock TRs like <c>t8410</c>, <c>t8412</c>, <c>t1301</c>): one or more named fields inside <c>{TrCode}OutBlock</c> carry the cursor. The caller must copy them into the next request's InBlock to fetch the next page. <see cref="KeyFields"/> lists those field names.</description></item>
/// </list>
/// </remarks>
/// <param name="Supported">Whether this TR supports pagination at all.</param>
/// <param name="KeyFields">For body-based pagination, the names of the cursor fields inside <c>{TrCode}OutBlock</c> that must be echoed back to fetch the next page. <see langword="null"/> or empty for header-based TRs.</param>
public sealed record TrContinuation(
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("key_fields")] IReadOnlyList<string>? KeyFields = null);
