namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Fetches live quote snapshots for stocks and watched sectors/themes.
/// </summary>
internal interface IQuoteService
{
    /// <summary>Gets stock quotes keyed by six-digit stock code.</summary>
    Task<QuoteBatchResult<StockQuote>> GetStockQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken = default);

    /// <summary>Gets sector/theme quotes keyed by watched sector code.</summary>
    Task<QuoteBatchResult<SectorQuote>> GetSectorQuotesAsync(IReadOnlyCollection<string> sectorCodes, CancellationToken cancellationToken = default);
}

