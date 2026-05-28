using RedoxNet.LsOpenApi.Core.Auth;

namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Resolves which LS broker account number to send to a TR call, applied
/// uniformly by every v1.6 <c>ls_account_*</c> tool.
/// </summary>
/// <remarks>
/// Filters by the active LS_MARKET mode via <see cref="IPortfolioRepository"/>
/// (real-mode repos see only real-mode accounts, virtual-mode repos see only
/// virtual-mode accounts — silent cross-mode leakage is blocked at the
/// SQL layer). The convention matches the existing v0.7 portfolio write
/// path: empty registry → <see cref="RequiresAccountException"/>; multiple
/// candidates with no <c>account</c> argument → <see cref="AmbiguousAccountException"/>.
/// </remarks>
internal sealed class LsAccountResolver
{
    readonly IPortfolioRepository _repository;
    readonly LsMarket _market;

    public LsAccountResolver(IPortfolioRepository repository, LsMarket market)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _market = market;
    }

    /// <summary>Canonical mode label ("real" or "virtual") echoed in tool responses.</summary>
    public string Mode => _market.ToCanonical();

    /// <summary>
    /// Resolves the account that should receive an LS REST call.
    /// </summary>
    /// <param name="identifier">User-supplied account_number or nickname. Null/empty selects the default account.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved account.</returns>
    /// <exception cref="RequiresAccountException">No accounts are registered in the active mode.</exception>
    /// <exception cref="AccountNotFoundException">Identifier did not match any registered account in the active mode.</exception>
    /// <exception cref="AmbiguousAccountException">No identifier supplied, multiple accounts exist, and no default is set.</exception>
    public async Task<Account> ResolveAsync(string? identifier, CancellationToken cancellationToken = default)
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

        // No identifier — prefer the per-mode default. The repository already
        // filters by current mode, so a virtual-mode default never leaks into
        // a real-mode session and vice versa.
        Account? def = await _repository.GetDefaultAccountAsync(cancellationToken).ConfigureAwait(false);
        if (def is not null)
            return def;

        IReadOnlyList<Account> accounts = await _repository.ListAccountsAsync(cancellationToken).ConfigureAwait(false);
        return accounts.Count switch
        {
            0 => throw new RequiresAccountException(
                $"No {Mode} accounts registered. Use ls_account(action=\"upsert\") to register one " +
                $"(LS_MARKET={Mode} is the active mode)."),
            1 => accounts[0],
            _ => throw new AmbiguousAccountException(
                $"Multiple {Mode} accounts exist ({accounts.Count}) and none is marked default. " +
                $"Pass account=<number or nickname> or set a default via ls_account(action=\"upsert\", set_default=true).",
                accounts.Select(SqlitePortfolioRepository.ToAccountInfo).ToList()),
        };
    }
}
