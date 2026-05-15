using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Registers portfolio services in the MCP host service collection.
/// </summary>
internal static class PortfolioServiceCollectionExtensions
{
    /// <summary>
    /// Adds the SQLite portfolio repository, quote service, and portfolio service.
    /// </summary>
    public static IServiceCollection AddPortfolio(this IServiceCollection services)
    {
        services.AddSingleton<IPortfolioRepository>(sp =>
        {
            string path = SqlitePortfolioRepository.ResolveDatabasePath();
            var repository = new SqlitePortfolioRepository(
                path,
                sp.GetRequiredService<ILogger<SqlitePortfolioRepository>>());
            return repository;
        });
        services.AddSingleton<IQuoteService, LsQuoteService>();
        services.AddSingleton<IPortfolioService, PortfolioService>();
        return services;
    }
}

