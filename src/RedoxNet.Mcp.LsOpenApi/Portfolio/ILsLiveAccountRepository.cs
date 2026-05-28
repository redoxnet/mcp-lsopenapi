namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Persistence for live LS broker account rows (v1.6 schema-split).
/// Distinct from <see cref="IPortfolioRepository"/>, which now owns the
/// paper-portfolio surface only — see <see cref="LsLiveAccount"/> for the
/// row shape and rationale.
/// </summary>
internal interface ILsLiveAccountRepository
{
    /// <summary>Applies the shared portfolio migrations (idempotent — defers to the SQLite repo).</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the live account row for the given mode, or null when the
    /// row has not been discovered yet. When multiple rows somehow exist
    /// for the same mode (defensive; LS REST TRs route per-appkey to one
    /// subaccount in practice), the lowest <c>id</c> wins.
    /// </summary>
    Task<LsLiveAccount?> GetByModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every live account row for the given mode. Returns an empty
    /// list when none are registered. The defensive multi-row case stays
    /// here so future LS 모계좌-style expansions can surface without a
    /// schema migration.
    /// </summary>
    Task<IReadOnlyList<LsLiveAccount>> ListByModeAsync(string mode, CancellationToken cancellationToken = default);

    /// <summary>Lists every live account row across both modes, real first.</summary>
    Task<IReadOnlyList<LsLiveAccount>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts a discovered live account row. Idempotent — on conflict
    /// (account_no, mode) the existing row's <c>last_seen_at</c> is
    /// refreshed plus any non-null label fields are updated; the
    /// <c>nickname</c> is only overwritten when a non-null value is
    /// supplied so prior user-set nicknames survive auto-discovery.
    /// </summary>
    Task<LsLiveAccount> UpsertDiscoveredAsync(
        string accountNo, string mode,
        string? branchName = null, string? accountName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets or clears the user-supplied nickname on an existing live
    /// account row. Returns the updated row, or null when no row matches.
    /// </summary>
    Task<LsLiveAccount?> SetNicknameAsync(string accountNo, string mode, string? nickname, CancellationToken cancellationToken = default);
}
