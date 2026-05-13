namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// Supplies valid LS access tokens to consumers (e.g. <c>LsApiClient</c>).
/// </summary>
/// <remarks>
/// This is the minimal contract <c>LsApiClient</c> depends on. The default
/// implementation is <see cref="LsTokenIssuer"/>, which handles caching and
/// refresh. Tests can substitute a trivial fake without setting up an
/// HTTP client.
/// </remarks>
public interface ILsTokenSource
{
    /// <summary>
    /// Returns a valid access token, issuing or refreshing one as needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The token.</returns>
    Task<LsAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
