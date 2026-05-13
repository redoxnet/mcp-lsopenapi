using System.Security.Cryptography;
using System.Text;

namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// Computes the cache key used by <see cref="ITokenStore"/> implementations.
/// </summary>
/// <remarks>
/// The key never contains the raw app key. Instead it is the lowercase hex
/// SHA-256 of the app key combined with the canonical market name, so two
/// processes that share a token cache cannot accidentally cross-contaminate
/// real/virtual tokens.
/// </remarks>
public static class TokenCacheKey
{
    /// <summary>
    /// Builds the canonical cache key for the given credentials.
    /// </summary>
    /// <param name="appKey">The raw LS app key. Required.</param>
    /// <param name="market">The target market.</param>
    /// <returns>A short, stable, secret-free string suitable for use as a primary key.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="appKey"/> is null or empty.</exception>
    public static string Create(string appKey, LsMarket market)
    {
        if (string.IsNullOrEmpty(appKey))
            throw new ArgumentException("App key must not be empty.", nameof(appKey));

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(appKey), hash);
        return $"{Convert.ToHexString(hash).ToLowerInvariant()}:{market.ToCanonical()}";
    }

    /// <summary>
    /// Builds the canonical cache key for the given credentials.
    /// </summary>
    /// <param name="credentials">The credentials providing app key and market.</param>
    /// <returns>The cache key.</returns>
    public static string Create(LsCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return Create(credentials.AppKey, credentials.Market);
    }
}
