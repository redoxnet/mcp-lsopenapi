using System.Text.Json.Serialization;

namespace RedoxNet.LsOpenApi.Core.Catalog;

/// <summary>
/// An input or output block declared by an LS OpenAPI TR.
/// </summary>
/// <param name="Name">Block name (e.g. <c>"t1101InBlock"</c>, <c>"t1101OutBlock1"</c>).</param>
/// <param name="IsArray">Whether the block represents an array of rows (multi-row output) rather than a single record.</param>
/// <param name="Fields">Ordered list of fields in the block.</param>
public sealed record TrBlock(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_array")] bool IsArray,
    [property: JsonPropertyName("fields")] IReadOnlyList<TrField> Fields);
