namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// Persists and retrieves <see cref="LsAccessToken"/> values keyed by an
/// opaque cache key (typically <c>SHA256(appkey) + market</c>).
/// </summary>
/// <remarks>
/// Implementations must protect token data at rest (file ACLs, encrypted DB,
/// etc.). The interface is deliberately narrow — no transaction or query
/// surface — so it can be backed by SQLite, an in-memory map, or any
/// key/value store.
/// </remarks>
public interface ITokenStore
{
    /// <summary>
    /// Retrieves the token cached under <paramref name="key"/>, or
    /// <see langword="null"/> when no entry exists.
    /// </summary>
    /// <param name="key">Cache key. Caller-defined; usually computed via <see cref="TokenCacheKey"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached token, or <see langword="null"/>.</returns>
    Task<LsAccessToken?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves <paramref name="token"/> under <paramref name="key"/>, overwriting
    /// any previously stored token at the same key.
    /// </summary>
    /// <param name="key">Cache key.</param>
    /// <param name="token">The token to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync(string key, LsAccessToken token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the entry under <paramref name="key"/>. No-op if missing.
    /// </summary>
    /// <param name="key">Cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
