using System.Reflection;
using System.Text.Json;

namespace RedoxNet.LsOpenApi.Core.Catalog;

/// <summary>
/// In-memory accessor over the embedded LS OpenAPI TR catalog.
/// </summary>
/// <remarks>
/// The default instance, <see cref="Default"/>, lazily loads the embedded
/// <c>TrCatalog.json</c> shipped with <c>RedoxNet.LsOpenApi.Core</c>. Tests
/// and tooling can construct alternative instances via
/// <see cref="FromFile"/> or <see cref="FromContent"/>.
/// </remarks>
public sealed class TrCatalog
{
    /// <summary>Embedded resource name of the built-in catalog JSON.</summary>
    public const string EmbeddedResourceName = "RedoxNet.LsOpenApi.Core.Catalog.TrCatalog.json";

    static readonly Lazy<TrCatalog> _default = new(LoadEmbedded, isThreadSafe: true);

    readonly Dictionary<string, TrMeta> _byCode;

    /// <summary>
    /// Process-wide default catalog. The first access loads the embedded JSON.
    /// </summary>
    public static TrCatalog Default => _default.Value;

    /// <summary>Catalog version string from the loaded file.</summary>
    public string Version { get; }

    /// <summary>UTC timestamp when the catalog file was generated.</summary>
    public DateTimeOffset GeneratedAtUtc { get; }

    /// <summary>Source URL (or <c>"manual"</c>) of the catalog file.</summary>
    public string Source { get; }

    /// <summary>All TR entries.</summary>
    public IReadOnlyList<TrMeta> All { get; }

    TrCatalog(TrCatalogFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Version = file.Version;
        GeneratedAtUtc = file.GeneratedAtUtc;
        Source = file.Source;
        All = file.Trs;
        _byCode = file.Trs.ToDictionary(t => t.TrCode, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the TR with the given code, or <see langword="null"/> when unknown.
    /// </summary>
    /// <param name="trCode">TR code (case-insensitive).</param>
    /// <returns>The matching TR or <see langword="null"/>.</returns>
    public TrMeta? Find(string trCode)
    {
        if (string.IsNullOrWhiteSpace(trCode))
            return null;
        _byCode.TryGetValue(trCode, out TrMeta? meta);
        return meta;
    }

    /// <summary>
    /// Returns the TR with the given code, throwing if unknown.
    /// </summary>
    /// <param name="trCode">TR code (case-insensitive).</param>
    /// <returns>The matching TR.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when no entry matches.</exception>
    public TrMeta Get(string trCode)
        => Find(trCode) ?? throw new KeyNotFoundException($"TR '{trCode}' is not in the catalog.");

    /// <summary>
    /// Returns TRs whose code, name, category, description, or field
    /// descriptions contain <paramref name="keyword"/> (case-insensitive).
    /// </summary>
    /// <param name="keyword">Search keyword (Korean or English).</param>
    /// <param name="limit">Maximum number of entries to return.</param>
    /// <returns>Ranked search results (best matches first).</returns>
    public IReadOnlyList<TrMeta> Search(string keyword, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return Array.Empty<TrMeta>();

        if (limit <= 0)
            return Array.Empty<TrMeta>();

        string needle = keyword.Trim();

        // Rank entries: exact-code match > name contains > category contains > description / field contains.
        var ranked = new List<(int score, TrMeta meta)>();
        foreach (TrMeta tr in All)
        {
            int score = ScoreMatch(tr, needle);
            if (score > 0)
                ranked.Add((score, tr));
        }

        return ranked
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.meta.TrCode, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(t => t.meta)
            .ToList();
    }

    static int ScoreMatch(TrMeta tr, string needle)
    {
        int score = 0;

        if (string.Equals(tr.TrCode, needle, StringComparison.OrdinalIgnoreCase))
            score += 1000;
        else if (Contains(tr.TrCode, needle))
            score += 100;

        if (Contains(tr.Name, needle))
            score += 50;

        if (Contains(tr.Category, needle))
            score += 25;

        if (Contains(tr.Description, needle))
            score += 10;

        // Field descriptions and names are weaker signals.
        foreach (TrBlock block in tr.InBlocks.Concat(tr.OutBlocks))
        {
            foreach (TrField field in block.Fields)
            {
                if (Contains(field.Name, needle) || Contains(field.Description, needle))
                {
                    score += 1;
                    break;
                }
            }
            if (score > 0 && (score % 1) == 0)
                continue;
        }

        return score;
    }

    static bool Contains(string? haystack, string needle)
        => !string.IsNullOrEmpty(haystack)
           && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Loads a catalog from a JSON file on disk. Used by tests and tooling.
    /// </summary>
    /// <param name="path">Absolute path to a catalog JSON file.</param>
    /// <returns>The parsed catalog.</returns>
    public static TrCatalog FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        return FromStream(stream);
    }

    /// <summary>
    /// Parses catalog JSON from an in-memory string. Used by tests.
    /// </summary>
    /// <param name="content">Catalog JSON content.</param>
    /// <returns>The parsed catalog.</returns>
    public static TrCatalog FromContent(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var file = JsonSerializer.Deserialize<TrCatalogFile>(content, LsCoreJson.Wire)
                   ?? throw new InvalidOperationException("Catalog JSON parsed as null.");
        return new TrCatalog(file);
    }

    /// <summary>
    /// Parses catalog JSON from a stream.
    /// </summary>
    /// <param name="stream">Stream containing UTF-8 catalog JSON.</param>
    /// <returns>The parsed catalog.</returns>
    public static TrCatalog FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var file = JsonSerializer.Deserialize<TrCatalogFile>(stream, LsCoreJson.Wire)
                   ?? throw new InvalidOperationException("Catalog JSON parsed as null.");
        return new TrCatalog(file);
    }

    static TrCatalog LoadEmbedded()
    {
        Assembly assembly = typeof(TrCatalog).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null)
            throw new InvalidOperationException(
                $"Embedded catalog resource '{EmbeddedResourceName}' was not found in {assembly.FullName}. " +
                "Run the catalog builder to generate it, then ensure the .csproj includes it as <EmbeddedResource>.");

        return FromStream(stream);
    }
}
