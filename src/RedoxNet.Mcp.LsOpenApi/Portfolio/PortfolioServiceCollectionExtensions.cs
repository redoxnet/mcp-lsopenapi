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
            // Paper portfolios are LS-mode agnostic (v8 migration): the
            // user-curated multi-broker book is visible regardless of
            // which LS appkey pair is loaded. Mode-keyed routing is now
            // a live-registry concern, see SqliteLsLiveAccountRepository.
            string path = SqlitePortfolioRepository.ResolveDatabasePath();
            return new SqlitePortfolioRepository(
                path,
                sp.GetRequiredService<ILogger<SqlitePortfolioRepository>>());
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

