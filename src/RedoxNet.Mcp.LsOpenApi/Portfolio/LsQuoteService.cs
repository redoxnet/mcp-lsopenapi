using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.Mcp.LsOpenApi.Tools;

namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Retrieves LS quote data used to enrich portfolio, watchlist, and theme results.
/// </summary>
internal sealed class LsQuoteService : IQuoteService
{
    readonly LsApiClient _apiClient;
    readonly SemaphoreSlim _themeCacheLock = new(1, 1);
    ThemeCacheEntry? _themeCache;
    static readonly TimeSpan ThemeCacheTtl = TimeSpan.FromSeconds(60);

    sealed record ThemeCacheEntry(
        IReadOnlyDictionary<string, ThemeQuote> Quotes,
        IReadOnlyList<ThemeCatalogRow> Catalog,
        DateTimeOffset FetchedAt,
        string? Error);

    /// <summary>
    /// Initializes a new instance of the <see cref="LsQuoteService"/> class.
    /// </summary>
    public LsQuoteService(LsApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    /// <inheritdoc />
    public async Task<QuoteBatchResult<StockQuote>> GetStockQuotesAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default)
    {
        string[] normalized = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (string symbol in normalized)
        {
            if (symbol.Length != 6 || !symbol.All(char.IsLetterOrDigit))
                return new QuoteBatchResult<StockQuote>(EmptyQuotes(normalized), $"Symbol '{symbol}' is not a valid 6-character stock/ETF code.");
        }

        if (normalized.Length == 0)
            return new QuoteBatchResult<StockQuote>(new Dictionary<string, StockQuote?>(), null);

        var quotes = normalized.ToDictionary(s => s, _ => (StockQuote?)null, StringComparer.Ordinal);
        try
        {
            foreach (string[] batch in normalized.Chunk(GetMultiQuoteTool.MaxStocks))
            {
                var inBlock = new JsonObject
                {
                    ["qrycnt"] = batch.Length,
                    ["shcode"] = string.Concat(batch),
                };

                LsTrResponse response = await _apiClient.CallTrAsync("t8407", inBlock, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    return new QuoteBatchResult<StockQuote>(quotes,
                        $"LS reported a business-level error ({response.RspCode}: {response.RspMessage}).");
                }

                JsonElement? block = response.GetBlock("t8407OutBlock1");
                if (block is null || block.Value.ValueKind != JsonValueKind.Array)
                    return new QuoteBatchResult<StockQuote>(quotes, "t8407OutBlock1 array was missing from the response.");

                foreach (JsonElement row in block.Value.EnumerateArray())
                {
                    string? shcode = row.ReadString("shcode");
                    if (shcode is null || !quotes.ContainsKey(shcode))
                        continue;

                    long rawChange = row.ReadLong("change");
                    long signedChange = ApplySign(rawChange, row.ReadString("sign"));
                    quotes[shcode] = new StockQuote(
                        Price: row.ReadLong("price"),
                        Change: signedChange,
                        ChangePct: row.ReadDouble("diff"),
                        Open: row.ReadLong("open"),
                        High: row.ReadLong("high"),
                        Low: row.ReadLong("low"),
                        Volume: row.ReadLong("volume"),
                        Timestamp: SeoulNow())
                    {
                        Name = row.ReadString("hname"),
                    };
                }
            }

            return new QuoteBatchResult<StockQuote>(quotes, null);
        }
        catch (LsAuthException ex)
        {
            return new QuoteBatchResult<StockQuote>(quotes, $"Authentication failed: {ex.Message}");
        }
        catch (LsTrException ex)
        {
            return new QuoteBatchResult<StockQuote>(quotes, $"TR call failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<QuoteBatchResult<ThemeQuote>> GetThemeQuotesAsync(
        IReadOnlyCollection<string> themeCodes,
        CancellationToken cancellationToken = default)
    {
        string[] normalized = themeCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var quotes = normalized.ToDictionary(c => c, _ => (ThemeQuote?)null, StringComparer.Ordinal);
        if (normalized.Length == 0)
            return new QuoteBatchResult<ThemeQuote>(quotes, null);

        ThemeCacheEntry entry = await GetAllThemeQuotesAsync(cancellationToken).ConfigureAwait(false);
        foreach (string code in normalized)
        {
            if (entry.Quotes.TryGetValue(code, out ThemeQuote? quote))
                quotes[code] = quote;
        }

        return new QuoteBatchResult<ThemeQuote>(quotes, entry.Error);
    }

    /// <inheritdoc />
    public async Task<ThemeCatalogResult> GetThemeCatalogAsync(CancellationToken cancellationToken = default)
    {
        ThemeCacheEntry entry = await GetAllThemeQuotesAsync(cancellationToken).ConfigureAwait(false);
        return new ThemeCatalogResult(entry.Catalog, entry.Error);
    }

    /// <summary>
    /// Gets and short-term caches the full t1531 theme quote table.
    /// </summary>
    async Task<ThemeCacheEntry> GetAllThemeQuotesAsync(CancellationToken cancellationToken)
    {
        ThemeCacheEntry? snapshot = _themeCache;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (snapshot is not null && now - snapshot.FetchedAt < ThemeCacheTtl)
            return snapshot;

        await _themeCacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            snapshot = _themeCache;
            now = DateTimeOffset.UtcNow;
            if (snapshot is not null && now - snapshot.FetchedAt < ThemeCacheTtl)
                return snapshot;

            var fetched = new Dictionary<string, ThemeQuote>(StringComparer.Ordinal);
            var catalog = new List<ThemeCatalogRow>();
            string? error = null;
            try
            {
                LsTrResponse response = await _apiClient.CallTrAsync(
                    "t1531",
                    new JsonObject { ["tmname"] = "", ["tmcode"] = "" },
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccess)
                {
                    error = $"LS reported a business-level error ({response.RspCode}: {response.RspMessage}).";
                }
                else
                {
                    JsonElement? block = response.GetBlock("t1531OutBlock");
                    if (block is null || block.Value.ValueKind != JsonValueKind.Array)
                    {
                        error = "t1531OutBlock array was missing from the response.";
                    }
                    else
                    {
                        string timestamp = SeoulNow();
                        foreach (JsonElement row in block.Value.EnumerateArray())
                        {
                            string? tmcode = row.ReadString("tmcode")?.Trim().ToUpperInvariant();
                            if (string.IsNullOrEmpty(tmcode))
                                continue;
                            string tmname = (row.ReadString("tmname") ?? tmcode).Trim();

                            fetched[tmcode] = new ThemeQuote(
                                IndexValue: null,
                                Change: null,
                                ChangePct: row.ReadDouble("avgdiff"),
                                Timestamp: timestamp);
                            catalog.Add(new ThemeCatalogRow(tmcode, tmname));
                        }
                    }
                }
            }
            catch (LsAuthException ex)
            {
                error = $"Authentication failed: {ex.Message}";
            }
            catch (LsTrException ex)
            {
                error = $"TR call failed: {ex.Message}";
            }

            var entry = new ThemeCacheEntry(fetched, catalog, now, error);
            _themeCache = entry;
            return entry;
        }
        finally
        {
            _themeCacheLock.Release();
        }
    }

    /// <summary>
    /// Builds an empty stock quote dictionary for a failed batch.
    /// </summary>
    static Dictionary<string, StockQuote?> EmptyQuotes(IEnumerable<string> symbols) =>
        symbols.ToDictionary(s => s, _ => (StockQuote?)null, StringComparer.Ordinal);

    /// <summary>
    /// Applies the LS sign code to an unsigned change value.
    /// </summary>
    static long ApplySign(long change, string? sign) => sign switch
    {
        "4" or "5" => -Math.Abs(change),
        "1" or "2" => Math.Abs(change),
        "3" => 0,
        _ => change,
    };

    /// <summary>
    /// Returns the current timestamp in Korea Standard Time.
    /// </summary>
    static string SeoulNow()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        try
        {
            TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Korea Standard Time" : "Asia/Seoul");
            return TimeZoneInfo.ConvertTime(now, zone).ToString("O", CultureInfo.InvariantCulture);
        }
        catch (TimeZoneNotFoundException)
        {
            return now.ToOffset(TimeSpan.FromHours(9)).ToString("O", CultureInfo.InvariantCulture);
        }
    }
}

