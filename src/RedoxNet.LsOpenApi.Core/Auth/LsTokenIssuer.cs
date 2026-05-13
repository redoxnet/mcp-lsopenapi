using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// Issues and caches LS증권 OpenAPI bearer access tokens using the
/// <c>POST /oauth2/token</c> endpoint with <c>grant_type=client_credentials</c>.
/// </summary>
/// <remarks>
/// The issuer applies a two-tier cache: a process-local in-memory snapshot
/// for the hot path, and an <see cref="ITokenStore"/> for cross-process and
/// cross-run reuse. Token issuance is serialized through a per-instance
/// semaphore so concurrent callers do not exhaust the LS-side rate limit.
/// </remarks>
public sealed class LsTokenIssuer : ILsTokenSource, IDisposable
{
    readonly HttpClient _httpClient;
    readonly LsApiOptions _options;
    readonly ITokenStore _tokenStore;
    readonly ILsCredentialsResolver _resolver;
    readonly ILogger<LsTokenIssuer> _logger;
    readonly ConcurrentDictionary<string, LsAccessToken> _memory = new(StringComparer.Ordinal);
    readonly SemaphoreSlim _issueLock = new(1, 1);
    bool _disposed;

    /// <summary>
    /// Creates a new token issuer.
    /// </summary>
    /// <param name="httpClient">Pre-configured HTTP client. Should not have an Authorization header.</param>
    /// <param name="options">Endpoint and refresh-window options.</param>
    /// <param name="tokenStore">Persistent token store.</param>
    /// <param name="resolver">Credentials resolver. Used when <see cref="GetAccessTokenAsync(System.Threading.CancellationToken)"/> is called.</param>
    /// <param name="logger">Optional logger.</param>
    public LsTokenIssuer(
        HttpClient httpClient,
        IOptions<LsApiOptions> options,
        ITokenStore tokenStore,
        ILsCredentialsResolver resolver,
        ILogger<LsTokenIssuer>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _logger = logger ?? NullLogger<LsTokenIssuer>.Instance;
    }

    /// <summary>
    /// Resolves credentials via the configured <see cref="ILsCredentialsResolver"/>
    /// and returns a valid access token.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A cached or freshly issued token.</returns>
    /// <exception cref="LsAuthException">Thrown when credentials are missing or the token endpoint fails.</exception>
    public async Task<LsAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        LsCredentials? credentials = await _resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (credentials is null || !credentials.IsComplete)
            throw new LsAuthException(
                $"LS credentials not configured. Set the {LsCredentials.AppKeyEnvVar} and {LsCredentials.AppSecretKeyEnvVar} environment variables.");

        return await GetAccessTokenAsync(credentials, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns a valid access token for the supplied credentials, issuing a
    /// new one only if the cache miss or refresh window dictates.
    /// </summary>
    /// <param name="credentials">Credentials to authenticate with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A cached or freshly issued token.</returns>
    /// <exception cref="LsAuthException">Thrown when the token endpoint fails.</exception>
    public async Task<LsAccessToken> GetAccessTokenAsync(
        LsCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (!credentials.IsComplete)
            throw new LsAuthException("LS credentials are incomplete (missing app key or secret).");

        string key = TokenCacheKey.Create(credentials);
        TimeSpan refreshWindow = _options.TokenRefreshWindow;

        if (_memory.TryGetValue(key, out LsAccessToken? cached) && !cached.ShouldRefresh(refreshWindow))
            return cached;

        await _issueLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check inside the lock to avoid duplicate issuance under contention.
            if (_memory.TryGetValue(key, out cached) && !cached.ShouldRefresh(refreshWindow))
                return cached;

            LsAccessToken? stored = await _tokenStore.GetAsync(key, cancellationToken).ConfigureAwait(false);
            if (stored is not null && !stored.ShouldRefresh(refreshWindow))
            {
                _memory[key] = stored;
                _logger.LogDebug(
                    "Loaded LS access token from persistent cache (appkey={AppKey}, market={Market}, expires_at={ExpiresAt:u}).",
                    SecretMasker.MaskWithPrefix(credentials.AppKey),
                    credentials.Market.ToCanonical(),
                    stored.ExpiresAtUtc);
                return stored;
            }

            LsAccessToken issued = await IssueAsync(credentials, cancellationToken).ConfigureAwait(false);
            _memory[key] = issued;
            await _tokenStore.SaveAsync(key, issued, cancellationToken).ConfigureAwait(false);
            return issued;
        }
        finally
        {
            _issueLock.Release();
        }
    }

    /// <summary>
    /// Drops the cached token for the supplied credentials so the next call
    /// issues a fresh one. Use after a 401/403 from a downstream TR.
    /// </summary>
    /// <param name="credentials">Credentials whose cache entry should be invalidated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task InvalidateAsync(LsCredentials credentials, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        string key = TokenCacheKey.Create(credentials);
        _memory.TryRemove(key, out _);
        await _tokenStore.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
    }

    async Task<LsAccessToken> IssueAsync(LsCredentials credentials, CancellationToken cancellationToken)
    {
        Uri endpoint = _options.ResolveTokenEndpoint();
        DateTimeOffset issuedAt = DateTimeOffset.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["appkey"] = credentials.AppKey,
                ["appsecretkey"] = credentials.AppSecretKey,
                ["scope"] = "oob",
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new LsAuthException(
                $"Network error while calling LS token endpoint at {endpoint}.",
                innerException: ex);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        string body;
        using (var reader = new StreamReader(stream))
            body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "LS token endpoint returned {Status} (appkey={AppKey}, market={Market}).",
                (int)response.StatusCode,
                SecretMasker.MaskWithPrefix(credentials.AppKey),
                credentials.Market.ToCanonical());
            throw new LsAuthException(
                $"LS token endpoint returned status {(int)response.StatusCode}.",
                statusCode: (int)response.StatusCode,
                responseBody: body);
        }

        TokenResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<TokenResponseDto>(body, LsCoreJson.Wire);
        }
        catch (JsonException ex)
        {
            throw new LsAuthException(
                "Failed to parse LS token response.",
                responseBody: body,
                innerException: ex);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.AccessToken))
            throw new LsAuthException("LS token response is missing access_token.", responseBody: body);

        int expiresIn = dto.ExpiresIn > 0 ? dto.ExpiresIn : 86400;
        var token = new LsAccessToken(
            AccessToken: dto.AccessToken,
            TokenType: string.IsNullOrWhiteSpace(dto.TokenType) ? "Bearer" : dto.TokenType,
            IssuedAtUtc: issuedAt,
            ExpiresAtUtc: issuedAt.AddSeconds(expiresIn),
            Scope: dto.Scope);

        _logger.LogInformation(
            "Issued LS access token (appkey={AppKey}, market={Market}, expires_in={ExpiresIn}s, token={Token}).",
            SecretMasker.MaskWithPrefix(credentials.AppKey),
            credentials.Market.ToCanonical(),
            expiresIn,
            SecretMasker.Mask(token.AccessToken));

        return token;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _issueLock.Dispose();
        _disposed = true;
    }

    sealed class TokenResponseDto
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }
    }
}
