using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.LsOpenApi.Core.Time;

namespace RedoxNet.LsOpenApi.Core;

/// <summary>
/// DI helpers for registering the LS OpenAPI Core SDK.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Named HttpClient used internally for the OAuth2 token endpoint.</summary>
    public const string TokenHttpClientName = "RedoxNet.LsOpenApi.TokenIssuer";

    /// <summary>Named HttpClient used internally for TR calls.</summary>
    public const string ApiHttpClientName = "RedoxNet.LsOpenApi.ApiClient";

    /// <summary>
    /// Registers the LS Core SDK with sensible defaults:
    /// <list type="bullet">
    ///   <item><description><see cref="EnvironmentLsCredentialsResolver"/> for credential resolution.</description></item>
    ///   <item><description><see cref="LsTokenCache"/> backed by the user's local data folder.</description></item>
    ///   <item><description><see cref="TrCatalog.Default"/> for the embedded TR catalog.</description></item>
    ///   <item><description>A singleton <see cref="LsTokenIssuer"/> exposed via <see cref="ILsTokenSource"/>.</description></item>
    ///   <item><description>A singleton <see cref="LsApiClient"/>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Optional callback to override <see cref="LsApiOptions"/>.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddLsOpenApiCore(
        this IServiceCollection services,
        Action<LsApiOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configureOptions is not null)
            services.Configure(configureOptions);
        else
            services.AddOptions<LsApiOptions>();

        services.TryAddSingleton<ILsCredentialsResolver, EnvironmentLsCredentialsResolver>();
        services.TryAddSingleton<ITokenStore>(_ => new LsTokenCache());
        services.TryAddSingleton(_ => TrCatalog.Default);
        services.TryAddSingleton<TrRateLimiter>();
        services.TryAddSingleton<ITradingCalendar, WeekendOnlyCalendar>();

        services.AddHttpClient(TokenHttpClientName, (sp, client) =>
        {
            LsApiOptions opt = sp.GetRequiredService<IOptions<LsApiOptions>>().Value;
            client.Timeout = opt.HttpTimeout;
        });
        services.AddHttpClient(ApiHttpClientName, (sp, client) =>
        {
            LsApiOptions opt = sp.GetRequiredService<IOptions<LsApiOptions>>().Value;
            client.Timeout = opt.HttpTimeout;
        });

        services.TryAddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new LsTokenIssuer(
                factory.CreateClient(TokenHttpClientName),
                sp.GetRequiredService<IOptions<LsApiOptions>>(),
                sp.GetRequiredService<ITokenStore>(),
                sp.GetRequiredService<ILsCredentialsResolver>(),
                sp.GetService(typeof(Microsoft.Extensions.Logging.ILogger<LsTokenIssuer>)) as Microsoft.Extensions.Logging.ILogger<LsTokenIssuer>);
        });
        services.TryAddSingleton<ILsTokenSource>(sp => sp.GetRequiredService<LsTokenIssuer>());

        services.TryAddSingleton(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new LsApiClient(
                factory.CreateClient(ApiHttpClientName),
                sp.GetRequiredService<IOptions<LsApiOptions>>(),
                sp.GetRequiredService<ILsTokenSource>(),
                catalog: sp.GetRequiredService<TrCatalog>(),
                rateLimiter: sp.GetRequiredService<TrRateLimiter>(),
                logger: sp.GetService(typeof(Microsoft.Extensions.Logging.ILogger<LsApiClient>)) as Microsoft.Extensions.Logging.ILogger<LsApiClient>);
        });

        return services;
    }

    /// <summary>
    /// Applies LS-related environment variables to <see cref="LsApiOptions"/>:
    /// <c>LS_MARKET</c> and (optionally) <c>LS_BASEURL</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection ConfigureLsOptionsFromEnvironment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.PostConfigure<LsApiOptions>(opt =>
        {
            string? marketRaw = Environment.GetEnvironmentVariable(LsCredentials.MarketEnvVar);
            if (!string.IsNullOrWhiteSpace(marketRaw))
                opt.Market = LsMarketExtensions.Parse(marketRaw);

            string? baseUrlRaw = Environment.GetEnvironmentVariable("LS_BASEURL");
            if (!string.IsNullOrWhiteSpace(baseUrlRaw) && Uri.TryCreate(baseUrlRaw, UriKind.Absolute, out Uri? parsed))
                opt.BaseUrl = parsed;
        });

        return services;
    }
}
