using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;

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
            // Account mode follows the same LsApiOptions.Market that the API
            // client uses, so the repo and the API endpoint stay in lockstep
            // even when tests override the market via Options.
            LsMarket market = sp.GetRequiredService<IOptions<LsApiOptions>>().Value.Market;
            var repository = new SqlitePortfolioRepository(
                path,
                market.ToCanonical(),
                sp.GetRequiredService<ILogger<SqlitePortfolioRepository>>());
            return repository;
        });
        services.AddSingleton<IQuoteService, LsQuoteService>();
        services.AddSingleton<ILsLiveAccountRepository>(sp =>
        {
            string path = SqlitePortfolioRepository.ResolveDatabasePath();
            return new SqliteLsLiveAccountRepository(
                sp.GetRequiredService<IPortfolioRepository>(),
                path,
                sp.GetRequiredService<ILogger<SqliteLsLiveAccountRepository>>());
        });
        services.AddSingleton<IPortfolioService>(sp => new PortfolioService(
            sp.GetRequiredService<IPortfolioRepository>(),
            sp.GetRequiredService<ILsLiveAccountRepository>(),
            sp.GetRequiredService<IQuoteService>(),
            sp.GetRequiredService<IOptions<LsApiOptions>>().Value.Market,
            sp.GetRequiredService<ILogger<PortfolioService>>()));
        services.AddSingleton(sp => new LsAccountResolver(
            sp.GetRequiredService<ILsLiveAccountRepository>(),
            sp.GetRequiredService<IOptions<LsApiOptions>>().Value.Market));
        return services;
    }
}

