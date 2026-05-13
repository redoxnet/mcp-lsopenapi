namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// An LS증권 OpenAPI access token issued via <c>POST /oauth2/token</c>
/// (<c>grant_type=client_credentials</c>).
/// </summary>
/// <remarks>
/// The token is a bearer credential. Treat it as secret; never log the
/// <see cref="AccessToken"/> value without masking it first.
/// </remarks>
/// <param name="AccessToken">The bearer token string returned by LS.</param>
/// <param name="TokenType">Always <c>"Bearer"</c> for LS OpenAPI.</param>
/// <param name="IssuedAtUtc">UTC timestamp when the token was issued (local clock).</param>
/// <param name="ExpiresAtUtc">UTC timestamp when the token expires (derived from <c>expires_in</c>).</param>
/// <param name="Scope">Scope returned by the token endpoint, if any.</param>
public sealed record LsAccessToken(
    string AccessToken,
    string TokenType,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? Scope = null)
{
    /// <summary>
    /// Returns <see langword="true"/> when the token is within
    /// <paramref name="refreshWindow"/> of its expiry, indicating that a fresh
    /// token should be issued before the next request.
    /// </summary>
    /// <param name="refreshWindow">Pre-expiry refresh window, e.g. 5 minutes.</param>
    /// <param name="nowUtc">Current UTC time. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <returns><see langword="true"/> when the token should be refreshed.</returns>
    public bool ShouldRefresh(TimeSpan refreshWindow, DateTimeOffset? nowUtc = null)
    {
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        return ExpiresAtUtc - now <= refreshWindow;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the token is past its expiry time.
    /// </summary>
    /// <param name="nowUtc">Current UTC time. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <returns><see langword="true"/> when expired.</returns>
    public bool IsExpired(DateTimeOffset? nowUtc = null)
    {
        DateTimeOffset now = nowUtc ?? DateTimeOffset.UtcNow;
        return now >= ExpiresAtUtc;
    }
}
