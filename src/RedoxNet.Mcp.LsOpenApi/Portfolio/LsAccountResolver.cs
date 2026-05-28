using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedoxNet.LsOpenApi.Core.Auth;

namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Provides the <c>_meta.account_used</c> echo for v1.6 <c>ls_account_*</c>
/// tools. The account is NOT a routing handle — LS account-inquiry and
/// trading TRs do not accept <c>AcntNo</c> in their request InBlock; the
/// authenticated session (via <c>LS_APPKEY</c> / <c>LS_APPSECRETKEY</c>)
/// resolves to a single subaccount server-side and only echoes the
/// resolved number back in the response. So the resolver's job is purely
/// label persistence: read the echo'd AcntNo, upsert into the
/// <c>ls_accounts</c> store keyed by (account_no, mode), and surface the
/// resulting row for friendly display.
/// <para>
/// Earlier dev iterations of v1.6 conflated this with the paper-portfolio
/// <c>accounts</c> table and routed identifier lookups through it, which
/// let an unrelated paper-portfolio default shadow the LS label and
/// confuse the model about which account answered. The split into
/// <see cref="ILsLiveAccountRepository"/> removes that class of bug
/// entirely. See [[ls-api-quirks-doc]] §4.2e.
/// </para>
/// </summary>
internal sealed class LsAccountResolver
{
    readonly ILsLiveAccountRepository _repository;
    readonly LsMarket _market;
    readonly ILogger<LsAccountResolver> _logger;

    public LsAccountResolver(
        ILsLiveAccountRepository repository,
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
    /// Returns the registered live row for the active mode, or
    /// <see langword="null"/> when discovery has not happened yet (cold
    /// start). Callers use this for the <c>_meta.account_used</c> echo.
    /// </summary>
    public Task<LsLiveAccount?> GetRegisteredAsync(CancellationToken cancellationToken = default) =>
        _repository.GetByModeAsync(Mode, cancellationToken);

    /// <summary>
    /// Upserts a broker-discovered live account row so subsequent calls
    /// can echo a stable label. Fire-and-forget by intent — any failure
    /// is logged at Debug and swallowed so the response path still
    /// returns the user-visible data. Returns the upserted row on
    /// success, or <see langword="null"/> when the upsert failed or
    /// <paramref name="accountNo"/> was blank.
    /// </summary>
    public async Task<LsLiveAccount?> RecordDiscoveredAsync(
        string? accountNo,
        string? branchName = null,
        string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountNo))
            return null;
        try
        {
            return await _repository
                .UpsertDiscoveredAsync(accountNo!, Mode, branchName, accountName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Auto-discovery upsert failed for {AccountNumber} (mode={Mode}); proceeding with response-only echo.", accountNo, Mode);
            return null;
        }
    }

    /// <summary>
    /// Builds the synchronous echo shape used in <c>_meta.account_used</c>
    /// for success and error responses. <paramref name="registered"/>
    /// wins when present; otherwise <paramref name="discoveredAcntNo"/>
    /// (when present) surfaces as a synthetic <c>discovered=true</c>
    /// shape so the model can explain "this is the broker-reported
    /// account number". On full cold-start both are null and the echo
    /// shape carries only the mode plus <c>discovered=false</c>.
    /// </summary>
    public LsLiveAccountInfo BuildEcho(LsLiveAccount? registered, string? discoveredAcntNo = null)
    {
        if (registered is not null)
            return SqliteLsLiveAccountRepository.ToInfo(registered);
        string? trimmed = string.IsNullOrWhiteSpace(discoveredAcntNo) ? null : discoveredAcntNo.Trim();
        return new LsLiveAccountInfo(
            AccountNumber: trimmed,
            Nickname: null,
            Mode: Mode,
            Discovered: !string.IsNullOrEmpty(trimmed));
    }
}
