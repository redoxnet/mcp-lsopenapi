namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Fetches live quote snapshots for stocks and watched LS themes.
/// </summary>
internal interface IQuoteService
{
    /// <summary>Gets stock quotes keyed by six-digit stock code.</summary>
    Task<QuoteBatchResult<StockQuote>> GetStockQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken = default);

    /// <summary>Gets LS theme quotes keyed by tmcode.</summary>
    Task<QuoteBatchResult<ThemeQuote>> GetThemeQuotesAsync(IReadOnlyCollection<string> themeCodes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the t1531 theme catalog (tmcode + tmname) for keyword resolution.
    /// Shares the same 60s cache as <see cref="GetThemeQuotesAsync"/> so a single
    /// t1531 fetch serves both quote enrichment and tool-side name lookups.
    /// </summary>
    Task<ThemeCatalogResult> GetThemeCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches every LS theme a single stock belongs to via t1532. Used by the
    /// fire-and-forget enrichment path to populate the stock_themes cache after
    /// portfolio writes; no in-process caching since it's keyed per-symbol and
    /// only fires once on each write.
    /// </summary>
    Task<StockThemesFetchResult> GetStockThemesAsync(string symbol, CancellationToken cancellationToken = default);
}

