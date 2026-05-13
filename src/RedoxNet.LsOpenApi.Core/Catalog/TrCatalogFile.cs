using System.Text.Json.Serialization;

namespace RedoxNet.LsOpenApi.Core.Catalog;

/// <summary>
/// On-disk shape of the embedded <c>TrCatalog.json</c> resource.
/// </summary>
/// <param name="Version">Catalog version string (e.g. semver of the catalog itself).</param>
/// <param name="GeneratedAtUtc">UTC timestamp when the catalog was generated.</param>
/// <param name="Source">Where the catalog was generated from (URL of the LS API service, or <c>"manual"</c>).</param>
/// <param name="Trs">All TR entries.</param>
public sealed record TrCatalogFile(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("generated_at_utc")] DateTimeOffset GeneratedAtUtc,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("trs")] IReadOnlyList<TrMeta> Trs);
