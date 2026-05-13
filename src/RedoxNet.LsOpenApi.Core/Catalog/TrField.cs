using System.Text.Json.Serialization;

namespace RedoxNet.LsOpenApi.Core.Catalog;

/// <summary>
/// One field (column) inside an LS OpenAPI TR input or output block.
/// </summary>
/// <param name="Name">Raw field name as exposed by LS (e.g. <c>"shcode"</c>).</param>
/// <param name="Type">LS-published field type, such as <c>"char"</c>, <c>"long"</c>, <c>"double"</c>. May be <see langword="null"/> when LS does not publish it.</param>
/// <param name="Description">Korean description scraped from the LS API page.</param>
/// <param name="Required">Whether the field must be supplied (input blocks) / will always be populated (output blocks).</param>
/// <param name="Length">Optional declared length for fixed-width fields.</param>
/// <param name="Example">Optional example value taken from LS docs.</param>
public sealed record TrField(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string? Type = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("required")] bool Required = false,
    [property: JsonPropertyName("length")] int? Length = null,
    [property: JsonPropertyName("example")] string? Example = null);
