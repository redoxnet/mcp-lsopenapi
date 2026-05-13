namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// LS증권 OpenAPI credentials. Wraps the <c>AppKey</c>/<c>AppSecretKey</c> pair
/// and the target <see cref="LsMarket"/>.
/// </summary>
/// <remarks>
/// Treat this type as a secret. Do not log it; use <see cref="SecretMasker"/>
/// when the values must appear in diagnostics.
/// </remarks>
/// <param name="AppKey">The LS OpenAPI app key. Required.</param>
/// <param name="AppSecretKey">The LS OpenAPI app secret key. Required.</param>
/// <param name="Market">The target environment.</param>
public sealed record LsCredentials(
    string AppKey,
    string AppSecretKey,
    LsMarket Market)
{
    /// <summary>
    /// Environment variable holding the LS app key.
    /// </summary>
    public const string AppKeyEnvVar = "LS_APPKEY";

    /// <summary>
    /// Environment variable holding the LS app secret key.
    /// </summary>
    public const string AppSecretKeyEnvVar = "LS_APPSECRETKEY";

    /// <summary>
    /// Environment variable holding the LS market mode (<c>"real"</c> or <c>"virtual"</c>).
    /// </summary>
    public const string MarketEnvVar = "LS_MARKET";

    /// <summary>
    /// Returns <see langword="true"/> when both secrets are non-empty.
    /// </summary>
    public bool IsComplete => !string.IsNullOrWhiteSpace(AppKey)
                              && !string.IsNullOrWhiteSpace(AppSecretKey);
}

/// <summary>
/// Resolves <see cref="LsCredentials"/> from the configured sources.
/// </summary>
/// <remarks>
/// Implementations follow the ADR-001 resolution order:
/// <list type="number">
///   <item><description>Environment variables (<c>LS_APPKEY</c>, <c>LS_APPSECRETKEY</c>, <c>LS_MARKET</c>).</description></item>
///   <item><description>MCP elicitation (when running as MCP server and creds are missing).</description></item>
///   <item><description>CLI args (debug only).</description></item>
/// </list>
/// </remarks>
public interface ILsCredentialsResolver
{
    /// <summary>
    /// Resolves credentials, blocking only as long as needed to walk the
    /// resolution chain.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved credentials, or <see langword="null"/> if none are configured.</returns>
    Task<LsCredentials?> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads <see cref="LsCredentials"/> from process environment variables.
/// </summary>
/// <remarks>
/// This is the first link in the ADR-001 resolution chain. It is intentionally
/// trivial — higher layers (e.g. MCP server with elicitation) compose this
/// resolver with interactive fallbacks.
/// </remarks>
public sealed class EnvironmentLsCredentialsResolver : ILsCredentialsResolver
{
    readonly Func<string, string?> _readEnv;

    /// <summary>
    /// Creates a resolver that reads from the real process environment.
    /// </summary>
    public EnvironmentLsCredentialsResolver()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>
    /// Creates a resolver that reads from the supplied delegate. Used by tests.
    /// </summary>
    /// <param name="readEnv">Delegate that returns the value of an env var, or <see langword="null"/> when unset.</param>
    public EnvironmentLsCredentialsResolver(Func<string, string?> readEnv)
    {
        _readEnv = readEnv ?? throw new ArgumentNullException(nameof(readEnv));
    }

    /// <inheritdoc />
    public Task<LsCredentials?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        string? appKey = _readEnv(LsCredentials.AppKeyEnvVar);
        string? appSecret = _readEnv(LsCredentials.AppSecretKeyEnvVar);
        string? marketRaw = _readEnv(LsCredentials.MarketEnvVar);

        if (string.IsNullOrWhiteSpace(appKey) || string.IsNullOrWhiteSpace(appSecret))
            return Task.FromResult<LsCredentials?>(null);

        var market = LsMarketExtensions.Parse(marketRaw);
        return Task.FromResult<LsCredentials?>(new LsCredentials(appKey, appSecret, market));
    }
}
