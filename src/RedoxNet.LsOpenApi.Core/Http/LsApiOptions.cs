using RedoxNet.LsOpenApi.Core.Auth;

namespace RedoxNet.LsOpenApi.Core.Http;

/// <summary>
/// Connection options for the LS증권 OpenAPI client.
/// </summary>
/// <remarks>
/// LS's REST endpoint is the same for real and virtual (모의투자) modes —
/// both flow through <c>https://openapi.ls-sec.co.kr:8080</c>. The mode is
/// determined by the appkey/appsecretkey pair itself: LS issues a separate
/// pair for the virtual account, and a token issued from a given pair is
/// tied to that account. See <c>docs/LS-API-QUIRKS.md</c> §4.2d and
/// programgarden's <c>URLS.LS_URL</c> for confirmation. <see cref="Market"/>
/// here is informational (portfolio.db namespacing); the WSS endpoint is
/// the only place real/virtual actually diverge, and this project does not
/// use WSS.
/// </remarks>
public sealed class LsApiOptions
{
    /// <summary>
    /// Base URL used when <see cref="Market"/> is <see cref="LsMarket.Real"/>.
    /// Intentionally identical to <see cref="DefaultVirtualBaseUrl"/> — see the
    /// type-level remarks for why the LS REST endpoint doesn't split by mode.
    /// </summary>
    public const string DefaultRealBaseUrl = "https://openapi.ls-sec.co.kr:8080";

    /// <summary>
    /// Base URL used when <see cref="Market"/> is <see cref="LsMarket.Virtual"/>.
    /// Intentionally identical to <see cref="DefaultRealBaseUrl"/> — the mode
    /// is the appkey pair, not the URL. Documented as a separate constant so
    /// a future LS change (if they ever split the REST endpoint) is a
    /// one-line edit.
    /// </summary>
    public const string DefaultVirtualBaseUrl = "https://openapi.ls-sec.co.kr:8080";

    /// <summary>Path of the OAuth2 token endpoint relative to <see cref="BaseUrl"/>.</summary>
    public const string TokenEndpointPath = "/oauth2/token";

    /// <summary>Target market. Determines the default <see cref="BaseUrl"/> when not set explicitly.</summary>
    public LsMarket Market { get; set; } = LsMarket.Real;

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
