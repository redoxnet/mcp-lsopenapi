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
/// Per <see href="../../docs/ADR-001-credential-management.md">ADR-001</see>
/// the only accepted source is process environment variables
/// (<c>LS_APPKEY</c>, <c>LS_APPSECRETKEY</c>, optional <c>LS_MARKET</c>).
/// There is no resolution chain — MCP elicitation and CLI arguments are
/// intentionally **not** implementation paths. The MCP spec explicitly
/// forbids elicitation for sensitive information, and CLI args leak
/// through <c>ps</c>/Task Manager and shell history. The interface
/// exists for test substitution (injecting a fake env source);
/// production composition stays env-var-only via
/// <see cref="EnvironmentLsCredentialsResolver"/>.
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
/// The only <see cref="ILsCredentialsResolver"/> registered in production
/// (see <c>ServiceCollectionExtensions.AddRedoxNetLsOpenApi</c>). Per
/// ADR-001 there is no fallback chain — elicitation / CLI args / chat
/// are not implementation paths. Do not introduce a composing resolver
/// that adds interactive fallbacks without revising ADR-001 first.
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
