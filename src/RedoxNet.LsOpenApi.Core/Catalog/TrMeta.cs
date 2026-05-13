using System.Text.Json.Serialization;

namespace RedoxNet.LsOpenApi.Core.Catalog;

/// <summary>
/// Full metadata for a single LS OpenAPI TR.
/// </summary>
/// <param name="TrCode">TR code such as <c>"t1101"</c> or <c>"CSPAQ12300"</c>.</param>
/// <param name="Name">Human-friendly Korean name.</param>
/// <param name="Category">Top-level category (e.g. <c>"주식시세"</c>, <c>"주식차트"</c>).</param>
/// <param name="Path">REST path relative to the LS base URL (e.g. <c>"/stock/market-data"</c>).</param>
/// <param name="Description">Longer Korean description / usage notes.</param>
/// <param name="InBlocks">Input block definitions.</param>
/// <param name="OutBlocks">Output block definitions.</param>
/// <param name="Continuation">Pagination descriptor.</param>
/// <param name="RateLimitPerSec">Per-key rate limit (calls per second). <see langword="null"/> when LS does not publish a value.</param>
public sealed record TrMeta(
    [property: JsonPropertyName("tr_code")] string TrCode,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("in_blocks")] IReadOnlyList<TrBlock> InBlocks,
    [property: JsonPropertyName("out_blocks")] IReadOnlyList<TrBlock> OutBlocks,
    [property: JsonPropertyName("continuation")] TrContinuation Continuation,
    [property: JsonPropertyName("rate_limit_per_sec")] int? RateLimitPerSec = null);
