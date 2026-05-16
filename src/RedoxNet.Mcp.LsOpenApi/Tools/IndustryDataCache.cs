using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// Process-local 60s cache for KRX industry catalog (t8424) and the
/// t8424 + t1511 fanout used by <c>ls_get_industry_indices</c>.
/// </summary>
/// <remarks>
/// Separating the catalog from the indices lets keyword resolution
/// (<c>industry_keyword</c>) stay cheap (one t8424 call), while the
/// fanout (N × t1511) only runs when the user actually asks for indices.
/// Both caches key on the normalized <c>market</c> string
/// (kospi / kosdaq / all) so users switching <c>top_n</c> don't pay the
/// fanout twice within the TTL window.
/// </remarks>
internal sealed class IndustryDataCache
{
    static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    readonly LsApiClient _apiClient;
    readonly ILogger<IndustryDataCache> _logger;
    readonly ConcurrentDictionary<string, CatalogEntry> _catalogByMarket = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, IndicesEntry> _indicesByMarket = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _catalogLock = new(1, 1);
    readonly SemaphoreSlim _indicesLock = new(1, 1);

    sealed record CatalogEntry(IReadOnlyList<IndustryCatalogRow> Rows, DateTimeOffset FetchedAt, string? Error);
    sealed record IndicesEntry(IReadOnlyList<IndustryIndexRow> Rows, DateTimeOffset FetchedAt, string? Error);

    public IndustryDataCache(LsApiClient apiClient, ILogger<IndustryDataCache>? logger = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _logger = logger ?? NullLogger<IndustryDataCache>.Instance;
    }

    /// <summary>
    /// Returns the catalog (upcode + name) for the requested market. Cached 60s.
    /// </summary>
    public async Task<IndustryCatalogResult> GetCatalogAsync(string market, CancellationToken cancellationToken)
    {
        string normalized = NormalizeMarket(market);
        if (_catalogByMarket.TryGetValue(normalized, out CatalogEntry? cached) && IsFresh(cached.FetchedAt))
            return new IndustryCatalogResult(cached.Rows, cached.Error);

        await _catalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_catalogByMarket.TryGetValue(normalized, out cached) && IsFresh(cached.FetchedAt))
                return new IndustryCatalogResult(cached.Rows, cached.Error);

            CatalogEntry entry = await FetchCatalogAsync(normalized, cancellationToken).ConfigureAwait(false);
            _catalogByMarket[normalized] = entry;
            return new IndustryCatalogResult(entry.Rows, entry.Error);
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <summary>
    /// Returns the index snapshot (upcode + name + value + change + change_pct)
    /// for every upcode in the market's catalog, sorted by change_pct desc.
    /// Cached 60s; cold-call cost is N × t1511 (rate-limited).
    /// </summary>
    public async Task<IndustryIndicesResult> GetIndicesAsync(string market, CancellationToken cancellationToken)
    {
        string normalized = NormalizeMarket(market);
        if (_indicesByMarket.TryGetValue(normalized, out IndicesEntry? cached) && IsFresh(cached.FetchedAt))
            return new IndustryIndicesResult(cached.Rows, cached.Error);

        await _indicesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_indicesByMarket.TryGetValue(normalized, out cached) && IsFresh(cached.FetchedAt))
                return new IndustryIndicesResult(cached.Rows, cached.Error);

            IndicesEntry entry = await FetchIndicesAsync(normalized, cancellationToken).ConfigureAwait(false);
            _indicesByMarket[normalized] = entry;
            return new IndustryIndicesResult(entry.Rows, entry.Error);
        }
        finally
        {
            _indicesLock.Release();
        }
    }

    async Task<CatalogEntry> FetchCatalogAsync(string normalizedMarket, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            LsTrResponse response = await _apiClient.CallTrAsync(
                "t8424",
                new JsonObject { ["gubun1"] = MarketToGubun1(normalizedMarket) },
                cancellationToken: ct).ConfigureAwait(false);

            if (!response.IsSuccess)
                return new CatalogEntry(Array.Empty<IndustryCatalogRow>(), now,
                    $"LS reported a business-level error ({response.RspCode}: {response.RspMessage}).");

            JsonElement? block = response.GetBlock("t8424OutBlock");
            if (block is null || block.Value.ValueKind != JsonValueKind.Array)
                return new CatalogEntry(Array.Empty<IndustryCatalogRow>(), now,
                    "t8424OutBlock array was missing from the response.");

            var rows = new List<IndustryCatalogRow>();
            foreach (JsonElement row in block.Value.EnumerateArray())
            {
                string? upcode = row.ReadString("upcode")?.Trim();
                string? name = row.ReadString("hname")?.Trim();
                if (string.IsNullOrEmpty(upcode))
                    continue;
                rows.Add(new IndustryCatalogRow(upcode, name ?? upcode));
            }
            return new CatalogEntry(rows, now, null);
        }
        catch (LsAuthException ex)
        {
            return new CatalogEntry(Array.Empty<IndustryCatalogRow>(), now, $"Authentication failed: {ex.Message}");
        }
        catch (LsTrException ex)
        {
            return new CatalogEntry(Array.Empty<IndustryCatalogRow>(), now, $"TR call failed: {ex.Message}");
        }
    }

    async Task<IndicesEntry> FetchIndicesAsync(string normalizedMarket, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CatalogEntry catalogEntry = _catalogByMarket.TryGetValue(normalizedMarket, out CatalogEntry? existing) && IsFresh(existing.FetchedAt)
            ? existing
            : await FetchCatalogAsync(normalizedMarket, ct).ConfigureAwait(false);
        _catalogByMarket[normalizedMarket] = catalogEntry;

        if (catalogEntry.Error is not null)
            return new IndicesEntry(Array.Empty<IndustryIndexRow>(), now, catalogEntry.Error);
        if (catalogEntry.Rows.Count == 0)
            return new IndicesEntry(Array.Empty<IndustryIndexRow>(), now, null);

        // Serial fanout: t1511 rate_limit_per_sec=1 (catalog default). The
        // rate limiter on LsApiClient throttles us regardless of parallelism,
        // so a sequential loop produces the same wall-clock as parallel tasks
        // without burning thread-pool slots.
        var rows = new List<IndustryIndexRow>(catalogEntry.Rows.Count);
        string? firstError = null;
        foreach (IndustryCatalogRow catalogRow in catalogEntry.Rows)
        {
            try
            {
                LsTrResponse response = await _apiClient.CallTrAsync(
                    "t1511",
                    new JsonObject { ["upcode"] = catalogRow.Upcode },
                    cancellationToken: ct).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    firstError ??= $"t1511 for {catalogRow.Upcode}: {response.RspCode} {response.RspMessage}";
                    continue;
                }

                JsonElement? block = response.GetBlock("t1511OutBlock");
                if (block is null) continue;
                JsonElement b = block.Value;

                string? sign = b.ReadString("sign");
                double rawChange = b.ReadDouble("change");
                double rawPct = b.ReadDouble("diffjisu");
                rows.Add(new IndustryIndexRow(
                    Upcode: catalogRow.Upcode,
                    Name: (b.ReadString("hname")?.Trim() ?? catalogRow.Name),
                    Value: b.ReadDouble("pricejisu"),
                    Change: ApplySign(rawChange, sign),
                    ChangePct: ApplySign(rawPct, sign)));
            }
            catch (LsAuthException ex)
            {
                return new IndicesEntry(rows, now, $"Authentication failed: {ex.Message}");
            }
            catch (LsTrException ex)
            {
                firstError ??= $"t1511 for {catalogRow.Upcode}: {ex.Message}";
            }
        }

        rows.Sort((a, b) => b.ChangePct.CompareTo(a.ChangePct));
        return new IndicesEntry(rows, now, firstError);
    }

    /// <summary>
    /// Applies the LS sign code to an unsigned change/percent value.
    /// </summary>
    internal static double ApplySign(double value, string? sign) => sign switch
    {
        "4" or "5" => -Math.Abs(value),
        "1" or "2" => Math.Abs(value),
        "3" => 0,
        _ => value,
    };

    static string NormalizeMarket(string market) =>
        market?.Trim().ToLowerInvariant() switch
        {
            "" or null or "all" or "전체" => "all",
            "kospi" or "kp" or "1" or "코스피" => "kospi",
            "kosdaq" or "kd" or "2" or "코스닥" => "kosdaq",
            _ => market!.Trim().ToLowerInvariant(),
        };

    /// <summary>
    /// Maps the normalized market to LS t8424 gubun1.
    /// Per spec §10 Q2 the LS-side semantics are not documented; the values
    /// here are working assumptions to be verified against the testbed.
    /// </summary>
    static string MarketToGubun1(string normalizedMarket) => normalizedMarket switch
    {
        "kospi" => "1",
        "kosdaq" => "2",
        _ => "",
    };

    static bool IsFresh(DateTimeOffset fetchedAt) =>
        DateTimeOffset.UtcNow - fetchedAt < CacheTtl;
}

/// <summary>One row of the t8424 catalog cache (upcode + name).</summary>
internal sealed record IndustryCatalogRow(string Upcode, string Name);

/// <summary>Result returned by <see cref="IndustryDataCache.GetCatalogAsync"/>.</summary>
internal sealed record IndustryCatalogResult(IReadOnlyList<IndustryCatalogRow> Rows, string? Error);

/// <summary>One row of the t8424 + t1511 fanout cache.</summary>
internal sealed record IndustryIndexRow(
    string Upcode,
    string Name,
    double Value,
    double Change,
    double ChangePct);

/// <summary>Result returned by <see cref="IndustryDataCache.GetIndicesAsync"/>.</summary>
internal sealed record IndustryIndicesResult(IReadOnlyList<IndustryIndexRow> Rows, string? Error);
