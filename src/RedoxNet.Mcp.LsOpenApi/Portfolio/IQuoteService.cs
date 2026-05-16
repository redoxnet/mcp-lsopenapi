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
}

