using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RedoxNet.LsOpenApi.Core.Auth;

namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// SQLite-backed <see cref="ILsLiveAccountRepository"/>. Shares the same
/// database file as <see cref="SqlitePortfolioRepository"/> — migrations
/// (including v7 which creates <c>ls_accounts</c>) are owned by the
/// portfolio repository, so this class delegates init to it before any
/// query runs. The two repositories are intentionally separate classes
/// to make the paper / live surfaces impossible to cross-query at the
/// SQL layer.
/// </summary>
internal sealed class SqliteLsLiveAccountRepository : ILsLiveAccountRepository
{
    readonly IPortfolioRepository _migrations;
    readonly string _connectionString;
    readonly ILogger<SqliteLsLiveAccountRepository> _logger;

    const string SelectSql = """
        SELECT id AS Id, account_no AS AccountNo, mode AS Mode,
               nickname AS Nickname, branch_name AS BranchName,
               account_name AS AccountName,
               discovered_at AS DiscoveredAt, last_seen_at AS LastSeenAt
        FROM ls_accounts
        """;

    public SqliteLsLiveAccountRepository(
        IPortfolioRepository migrations,
        string databasePath,
        ILogger<SqliteLsLiveAccountRepository>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _migrations = migrations;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        _logger = logger ?? NullLogger<SqliteLsLiveAccountRepository>.Instance;
    }

    /// <inheritdoc />
    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _migrations.InitializeAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<LsLiveAccount?> GetByModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string normalized = NormalizeMode(mode);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<LsLiveAccount>(new CommandDefinition(
            SelectSql + " WHERE mode = @Mode ORDER BY id ASC LIMIT 1;",
            new { Mode = normalized }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LsLiveAccount>> ListByModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string normalized = NormalizeMode(mode);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<LsLiveAccount> rows = await connection.QueryAsync<LsLiveAccount>(new CommandDefinition(
            SelectSql + " WHERE mode = @Mode ORDER BY id ASC;",
            new { Mode = normalized }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LsLiveAccount>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // 'real' rows first, then 'virtual'; stable within each by id.
        IEnumerable<LsLiveAccount> rows = await connection.QueryAsync<LsLiveAccount>(new CommandDefinition(
            SelectSql + " ORDER BY CASE mode WHEN 'real' THEN 0 ELSE 1 END, id ASC;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<LsLiveAccount> UpsertDiscoveredAsync(
        string accountNo, string mode,
        string? branchName = null, string? accountName = null,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string normalizedAccountNo = TrimRequired(accountNo, nameof(accountNo));
        string normalizedMode = NormalizeMode(mode);
        string? normalizedBranch = NullIfWhiteSpace(branchName);
        string? normalizedName = NullIfWhiteSpace(accountName);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ls_accounts(account_no, mode, nickname, branch_name, account_name, discovered_at, last_seen_at)
            VALUES (@AccountNo, @Mode, NULL, @BranchName, @AccountName, datetime('now'), datetime('now'))
            ON CONFLICT(account_no, mode) DO UPDATE SET
                last_seen_at = datetime('now'),
                branch_name  = COALESCE(excluded.branch_name, branch_name),
                account_name = COALESCE(excluded.account_name, account_name);
            """,
            new { AccountNo = normalizedAccountNo, Mode = normalizedMode, BranchName = normalizedBranch, AccountName = normalizedName },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        LsLiveAccount? row = await connection.QuerySingleOrDefaultAsync<LsLiveAccount>(new CommandDefinition(
            SelectSql + " WHERE account_no = @AccountNo AND mode = @Mode;",
            new { AccountNo = normalizedAccountNo, Mode = normalizedMode }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row ?? throw new InvalidOperationException("Upsert of ls_accounts row succeeded but the row could not be read back.");
    }

    /// <inheritdoc />
    public async Task<LsLiveAccount?> SetNicknameAsync(string accountNo, string mode, string? nickname, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        string normalizedAccountNo = TrimRequired(accountNo, nameof(accountNo));
        string normalizedMode = NormalizeMode(mode);
        string? normalizedNickname = NullIfWhiteSpace(nickname);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE ls_accounts SET nickname = @Nickname, last_seen_at = datetime('now') WHERE account_no = @AccountNo AND mode = @Mode;",
            new { AccountNo = normalizedAccountNo, Mode = normalizedMode, Nickname = normalizedNickname },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected == 0)
            return null;
        return await connection.QuerySingleOrDefaultAsync<LsLiveAccount>(new CommandDefinition(
            SelectSql + " WHERE account_no = @AccountNo AND mode = @Mode;",
            new { AccountNo = normalizedAccountNo, Mode = normalizedMode }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    static string TrimRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }

    static string NormalizeMode(string mode) =>
        LsMarketExtensions.Parse(mode).ToCanonical();

    static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Converts a stored row to the MCP-facing echo shape used in
    /// <c>_meta.account_used</c>.
    /// </summary>
    internal static LsLiveAccountInfo ToInfo(LsLiveAccount account) =>
        new(account.AccountNo, account.Nickname, account.Mode, Discovered: true, account.BranchName, account.AccountName);
}
