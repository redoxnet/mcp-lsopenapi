using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedoxNet.LsOpenApi.Core.Auth;

namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Resolves which LS broker account a v1.6 <c>ls_account_*</c> tool should
/// label its response with.
/// </summary>
/// <remarks>
/// LS account-inquiry TRs do not take <c>account_number</c> as input — the
/// access token tells LS which account to return. So the resolver's role is
/// purely about *labelling*: pick the right nickname / mode tag for the
/// <c>_meta.account_used</c> echo, and (when the user pre-registered an
/// account via <c>ls_account(action="upsert")</c>) verify the identifier
/// makes sense. When the registry is empty, the resolver returns
/// <see langword="null"/> so the caller can fall back to auto-discovery from
/// the TR response's <c>AcntNo</c> field.
/// <para>
/// The convention matches the v0.7 portfolio write path: when an explicit
/// identifier is given but does not resolve, <see cref="AccountNotFoundException"/>;
/// when the registry has multiple accounts and no default,
/// <see cref="AmbiguousAccountException"/>. The empty-registry case is NOT
/// an error in v1.6 — the broker tells us which account the appkey owns.
/// </para>
/// </remarks>
internal sealed class LsAccountResolver
{
    readonly IPortfolioRepository _repository;
    readonly LsMarket _market;
    readonly ILogger<LsAccountResolver> _logger;

    public LsAccountResolver(
        IPortfolioRepository repository,
        LsMarket market,
        ILogger<LsAccountResolver>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _market = market;
        _logger = logger ?? NullLogger<LsAccountResolver>.Instance;
    }

    /// <summary>Canonical mode label ("real" or "virtual") echoed in tool responses.</summary>
    public string Mode => _market.ToCanonical();

    /// <summary>
    /// Resolves the labelling account for a TR call.
    /// </summary>
    /// <param name="identifier">User-supplied account_number or nickname. Null/empty falls back to default → single-row → null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The labelling account, or <see langword="null"/> when no account is registered in the active mode (caller should auto-discover from the TR response).</returns>
    /// <exception cref="AccountNotFoundException">Explicit identifier given but no registered account matched.</exception>
    /// <exception cref="AmbiguousAccountException">No identifier given, multiple accounts exist, and none is the default.</exception>
    public async Task<Account?> ResolveAsync(string? identifier, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            Account? found = await _repository.GetAccountByIdentifierAsync(identifier!, cancellationToken).ConfigureAwait(false);
            if (found is not null)
                return found;

            IReadOnlyList<Account> known = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
            throw new AccountNotFoundException(
                identifier!,
                known.Select(SqlitePortfolioRepository.ToAccountInfo).ToList());
        }

        Account? def = await _repository.GetDefaultAccountAsync(cancellationToken).ConfigureAwait(false);
        if (def is not null)
            return def;

        IReadOnlyList<Account> accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        return accounts.Count switch
        {
            0 => null,
            1 => accounts[0],
            _ => throw new AmbiguousAccountException(
                $"Multiple {Mode} accounts exist ({accounts.Count}) and none is marked default. " +
                $"Pass account=<number or nickname> or set a default via ls_account(action=\"upsert\", set_default=true).",
                accounts.Select(SqlitePortfolioRepository.ToAccountInfo).ToList()),
        };
    }

    /// <summary>
    /// Records a broker-discovered account in portfolio.db so subsequent
    /// calls in the active mode can resolve it by nickname or set a custom
    /// label via <c>ls_account(action="upsert")</c>. Idempotent — calling
    /// twice with the same account_no is a no-op (the existing row stays).
    /// </summary>
    /// <remarks>
    /// Fire-and-forget by intent: a discovery failure must never block the
    /// model-facing response. Errors are logged at Debug and swallowed.
    /// </remarks>
    public async Task<Account?> RecordDiscoveredAsync(string accountNumber, string? defaultNickname, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountNumber))
            return null;
        try
        {
            string nickname = string.IsNullOrWhiteSpace(defaultNickname)
                ? $"LS-{Mode}-{accountNumber.TrimStart('A').Trim()}"
                : defaultNickname.Trim();
            return await _repository.UpsertAccountAsync(accountNumber.Trim(), nickname, "LS", setDefault: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-discovery upsert failed for {AccountNumber} (mode={Mode}); proceeding with response-only echo.", accountNumber, Mode);
            return null;
        }
    }
}
