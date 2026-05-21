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
/// (kospi / kosdaq / all) so users switching <c>limit</c> don't pay the
/// fanout twice within the TTL window.
/// </remarks>
internal sealed class IndustryDataCache
{
    static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    /// <summary>Delay before retrying a t8424 leg that came back empty.</summary>
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

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
        // "all" is deliberately NOT t8424 gubun1="" — that returns the full
        // 250+ index zoo (KP200 / F-K200 leveraged & inverse sector indices,
        // composites like "KQ150 L KP200 0.5 S"), which are index products,
        // not 업종. Merge the two real industry catalogs instead; this reuses
        // the gubun1 "1"/"2" paths the kospi/kosdaq markets already trust.
        if (normalizedMarket == "all")
            return await FetchMergedCatalogAsync(ct).ConfigureAwait(false);

        return await FetchCatalogForGubunAsync(MarketToGubun1(normalizedMarket), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the "all" catalog as KOSPI ∪ KOSDAQ. One t8424 leg occasionally
    /// returns empty on the back-to-back call (a transient hiccup — see
    /// docs/LS-API-QUIRKS.md §2.1); the empty leg is retried once, and any
    /// remaining gap is surfaced in the error rather than silently dropped.
    /// </summary>
    async Task<CatalogEntry> FetchMergedCatalogAsync(CancellationToken ct)
    {
        CatalogEntry kospi = await FetchCatalogForGubunAsync("1", ct).ConfigureAwait(false);
        CatalogEntry kosdaq = await FetchCatalogForGubunAsync("2", ct).ConfigureAwait(false);

        if (kospi.Rows.Count == 0 ^ kosdaq.Rows.Count == 0)
        {
            await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            if (kospi.Rows.Count == 0)
                kospi = await FetchCatalogForGubunAsync("1", ct).ConfigureAwait(false);
            else
                kosdaq = await FetchCatalogForGubunAsync("2", ct).ConfigureAwait(false);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<IndustryCatalogRow>(kospi.Rows.Count + kosdaq.Rows.Count);
        foreach (IndustryCatalogRow row in kospi.Rows.Concat(kosdaq.Rows))
            if (seen.Add(row.Upcode))
                merged.Add(row);

        // A single-market outage still yields a useful partial board — keep
        // the rows we got, but surface the gap so the caller (and the model)
        // can say the board is incomplete instead of presenting it as whole.
        string? error = (kospi.Rows.Count == 0, kosdaq.Rows.Count == 0) switch
        {
            (true, true) => $"Industry catalog unavailable (kospi: {kospi.Error ?? "empty"}, kosdaq: {kosdaq.Error ?? "empty"}).",
            (true, false) => $"KOSPI industry catalog unavailable ({kospi.Error ?? "empty response"}); board shows KOSDAQ only.",
            (false, true) => $"KOSDAQ industry catalog unavailable ({kosdaq.Error ?? "empty response"}); board shows KOSPI only.",
            _ => null,
        };
        if (error is not null)
            _logger.LogWarning("t8424 merge incomplete: {Error}", error);

        return new CatalogEntry(merged, DateTimeOffset.UtcNow, error);
    }

    async Task<CatalogEntry> FetchCatalogForGubunAsync(string gubun1, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            LsTrResponse response = await _apiClient.CallTrAsync(
                "t8424",
                new JsonObject { ["gubun1"] = gubun1 },
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
                if (string.IsNullOrEmpty(upcode))
                    continue;
                // t8424 hname is a fixed-width 20-byte field — space-padded
                // between characters for short names, truncated mid-character
                // for long ones. CompactName strips the padding and any
                // U+FFFD left by a mid-character cut.
                string name = GetIndexQuoteTool.CompactName(row.ReadString("hname"));
                if (name.Length == 0)
                    name = upcode;
                // gubun1 "1"/"2" return real 업종 alongside LS index products
                // (KP200/KP50 GICS sector indices, KOSPI/KOSDAQ composites,
                // F- futures indices). Drop the products — the board ranks
                // industries, not composite/derived index values.
                if (IsDerivedIndexName(name))
                    continue;
                rows.Add(new IndustryCatalogRow(upcode, name));
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

        // Bail only when the catalog is genuinely empty. A partial catalog
        // (one market down) still produces a useful board — its error is
        // seeded into firstError below so it reaches partial_error.
        if (catalogEntry.Rows.Count == 0)
            return new IndicesEntry(Array.Empty<IndustryIndexRow>(), now, catalogEntry.Error);

        // Serial fanout: t1511 rate_limit_per_sec=10 (confirmed from LS API
        // 업종 시세 guide page). The rate limiter on LsApiClient throttles us
        // regardless of parallelism, so a sequential loop produces the same
        // wall-clock as parallel tasks without burning thread-pool slots
        // (KOSPI ~25 upcodes / 10 TPS ≈ 2.5s cold-cache cost).
        var rows = new List<IndustryIndexRow>(catalogEntry.Rows.Count);
        string? firstError = catalogEntry.Error;
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
                double rawPct = b.ReadDouble("diffjisu");
                double value = b.ReadDouble("pricejisu");
                // Derive the absolute change from value + percent instead of
                // trusting t1511's 'change' field: LS occasionally computes
                // 'change' against a frozen base (observed on 전기전자/013 —
                // an index-rebase remnant), leaving it inconsistent with
                // pricejisu/diffjisu. value and diffjisu stay mutually
                // consistent, so change = value × pct / (100 + pct).
                double pctMagnitude = Math.Abs(rawPct);
                double rawChange = Math.Round(value * pctMagnitude / (100 + pctMagnitude), 2);
                // t1511 hname carries the same fixed-width padding/truncation
                // as t8424; CompactName normalizes it. Fall back to the
                // catalog name when t1511 omits hname.
                string indexName = GetIndexQuoteTool.CompactName(b.ReadString("hname"));
                rows.Add(new IndustryIndexRow(
                    Upcode: catalogRow.Upcode,
                    Name: indexName.Length == 0 ? catalogRow.Name : indexName,
                    Value: value,
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
    /// Maps a single concrete market to LS t8424 gubun1. "all" never reaches
    /// here — <see cref="FetchCatalogAsync"/> handles it by merging the "1"
    /// and "2" catalogs. The LS-side gubun1 semantics are undocumented; "1"
    /// (kospi) / "2" (kosdaq) are the assumptions the default kospi path has
    /// always relied on.
    /// </summary>
    static string MarketToGubun1(string normalizedMarket) => normalizedMarket switch
    {
        "kospi" => "1",
        "kosdaq" => "2",
        _ => "",
    };

    /// <summary>
    /// True for an LS index <em>product</em> that t8424's 업종 catalog also
    /// carries but that is not a real industry — KP-family GICS sector
    /// indices (KP50 / KP100 / KP200), KOSPI / KOSDAQ market-cap composites,
    /// KRX cross-market indices, F- futures-linked indices, and the VKOSPI
    /// volatility index. Real 업종 are plain Korean sector names; every
    /// product carries one of these Latin index-family prefixes (confirmed
    /// against the live t8424 "all" catalog). Korean industries that merely
    /// start with Latin letters — e.g. "IT서비스" — are deliberately kept.
    /// </summary>
    static bool IsDerivedIndexName(string name) =>
        DerivedIndexPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    static readonly string[] DerivedIndexPrefixes =
        ["KP", "KOSPI", "KOSDAQ", "KRX", "F-", "VKOSPI"];

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
