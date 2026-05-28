using Microsoft.Data.Sqlite;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.Mcp.LsOpenApi.Portfolio;

namespace RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;

/// <summary>
/// Temp-directory SQLite store + a wired-up <see cref="LsAccountResolver"/>
/// for tool tests that exercise the v1.6 (schema-split) live-account
/// surface. Seeding goes through <c>RecordDiscoveredAsync</c> so the row
/// lands in <c>ls_accounts</c>, not the paper-portfolio table.
/// </summary>
internal sealed class LiveAccountScratch : IAsyncDisposable
{
    readonly string _directory;
    readonly SqlitePortfolioRepository _paperRepo;
    readonly SqliteLsLiveAccountRepository _liveRepo;

    public LiveAccountScratch(string mode = "real", LsMarket market = LsMarket.Real, string? scope = null)
    {
        _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"mcp-lsopenapi-{scope ?? "live"}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        DbPath = System.IO.Path.Combine(_directory, "portfolio.db");
        _paperRepo = new SqlitePortfolioRepository(DbPath, mode);
        _liveRepo = new SqliteLsLiveAccountRepository(_paperRepo, DbPath);
        Resolver = new LsAccountResolver(_liveRepo, market);
    }

    public string DbPath { get; }
    public LsAccountResolver Resolver { get; }
    public ILsLiveAccountRepository LiveRepo => _liveRepo;
    public SqlitePortfolioRepository PaperRepo => _paperRepo;

    /// <summary>
    /// Primes the live registry with one row so <see cref="LsAccountResolver.GetRegisteredAsync"/>
    /// returns it on the next call. Optional <paramref name="nickname"/>
    /// is applied via <see cref="ILsLiveAccountRepository.SetNicknameAsync"/>
    /// because <c>RecordDiscoveredAsync</c> only writes nicknames when
    /// the row is upserted with non-null label fields.
    /// </summary>
    public async Task SeedLiveAccount(string accountNo, string? nickname = null, string? branchName = null, string? accountName = null)
    {
        await _paperRepo.InitializeAsync();
        await Resolver.RecordDiscoveredAsync(accountNo, branchName, accountName);
        if (!string.IsNullOrWhiteSpace(nickname))
            await _liveRepo.SetNicknameAsync(accountNo, Resolver.Mode, nickname);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; SQLite WAL handles may linger on Windows.
        }
        return ValueTask.CompletedTask;
    }
}
