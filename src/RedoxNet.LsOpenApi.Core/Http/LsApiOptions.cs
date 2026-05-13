using RedoxNet.LsOpenApi.Core.Auth;

namespace RedoxNet.LsOpenApi.Core.Http;

/// <summary>
/// Connection options for the LS증권 OpenAPI client.
/// </summary>
/// <remarks>
/// Default base URLs are the REST endpoints documented at
/// <see href="https://openapi.ls-sec.co.kr/howto-use"/>. The virtual server
/// shares the same host as real trading; callers can override either URL via
/// <see cref="BaseUrl"/> if LS publishes a different paper-trading endpoint.
/// </remarks>
public sealed class LsApiOptions
{
    /// <summary>Base URL used when <see cref="Market"/> is <see cref="LsMarket.Real"/>.</summary>
    public const string DefaultRealBaseUrl = "https://openapi.ls-sec.co.kr:8080";

    /// <summary>Base URL used when <see cref="Market"/> is <see cref="LsMarket.Virtual"/>.</summary>
    public const string DefaultVirtualBaseUrl = "https://openapi.ls-sec.co.kr:8080";

    /// <summary>Path of the OAuth2 token endpoint relative to <see cref="BaseUrl"/>.</summary>
    public const string TokenEndpointPath = "/oauth2/token";

    /// <summary>Target market. Determines the default <see cref="BaseUrl"/> when not set explicitly.</summary>
    public LsMarket Market { get; set; } = LsMarket.Virtual;

    /// <summary>
    /// REST base URL. When <see langword="null"/>, the default for the current
    /// <see cref="Market"/> is used.
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// Pre-expiry refresh window. When a cached token's remaining lifetime is
    /// at or below this value, a new token is issued. Defaults to 5 minutes
    /// per the v1.0 spec.
    /// </summary>
    public TimeSpan TokenRefreshWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Overall HTTP timeout for non-token requests. Applied to the
    /// shared <see cref="HttpClient"/>.
    /// </summary>
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resolves the effective base URL, applying the per-market default when
    /// <see cref="BaseUrl"/> is unset.
    /// </summary>
    /// <returns>Effective base URL.</returns>
    public Uri ResolveBaseUrl()
    {
        if (BaseUrl is not null)
            return BaseUrl;

        string raw = Market == LsMarket.Real ? DefaultRealBaseUrl : DefaultVirtualBaseUrl;
        return new Uri(raw);
    }

    /// <summary>
    /// Builds the absolute URI of the OAuth2 token endpoint.
    /// </summary>
    /// <returns>Token endpoint URI.</returns>
    public Uri ResolveTokenEndpoint() => new(ResolveBaseUrl(), TokenEndpointPath);
}
