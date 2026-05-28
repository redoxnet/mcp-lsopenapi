using FluentAssertions;
using Microsoft.Data.Sqlite;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Portfolio;

/// <summary>
/// Direct coverage of <see cref="SqliteLsLiveAccountRepository"/> — the
/// v1.6 schema-split store for LS broker accounts. Pins the conflict
/// semantics on (account_no, mode), the COALESCE label preservation on
/// re-upsert, mode isolation, and the nickname read/clear roundtrip.
/// </summary>
public sealed class SqliteLsLiveAccountRepositoryTests
{
    [Fact]
    public async Task UpsertDiscoveredAsync_CreatesRowOnFirstCall()
    {
        await using Scratch s = new();

        LsLiveAccount row = await s.Repo.UpsertDiscoveredAsync("20856195501", "real");

        row.AccountNo.Should().Be("20856195501");
        row.Mode.Should().Be("real");
        row.Nickname.Should().BeNull();
        row.DiscoveredAt.Should().NotBeNullOrWhiteSpace();
        row.LastSeenAt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task UpsertDiscoveredAsync_IsIdempotent_PreservesId()
    {
        await using Scratch s = new();
        LsLiveAccount first = await s.Repo.UpsertDiscoveredAsync("20856195501", "real");
        LsLiveAccount second = await s.Repo.UpsertDiscoveredAsync("20856195501", "real");

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task UpsertDiscoveredAsync_DoesNotOverwriteUserNickname()
    {
        // The auto-discovery path passes null for nickname so a manually
        // set nickname survives every subsequent ls_account_* call.
        await using Scratch s = new();
        await s.Repo.UpsertDiscoveredAsync("20856195501", "real");
        await s.Repo.SetNicknameAsync("20856195501", "real", "내 LS");

        await s.Repo.UpsertDiscoveredAsync("20856195501", "real");

        LsLiveAccount? row = await s.Repo.GetByModeAsync("real");
        row.Should().NotBeNull();
        row!.Nickname.Should().Be("내 LS");
    }

    [Fact]
    public async Task UpsertDiscoveredAsync_PreservesLabelsWhenSubsequentCallOmitsThem()
    {
        // CSPAQ12200 supplies BrnNm / AcntNm; a follow-up t0424 call does
        // not. The repository must keep the cached labels (COALESCE).
        await using Scratch s = new();
        await s.Repo.UpsertDiscoveredAsync("20856195501", "real", branchName: "다이렉트206", accountName: "김종현");

        await s.Repo.UpsertDiscoveredAsync("20856195501", "real");

        LsLiveAccount? row = await s.Repo.GetByModeAsync("real");
        row!.BranchName.Should().Be("다이렉트206");
        row.AccountName.Should().Be("김종현");
    }

    [Fact]
    public async Task GetByModeAsync_ReturnsNullWhenNoRowsForMode()
    {
        await using Scratch s = new();
        await s.Repo.UpsertDiscoveredAsync("R-01", "real");

        (await s.Repo.GetByModeAsync("virtual")).Should().BeNull();
    }

    [Fact]
    public async Task UpsertDiscoveredAsync_SeparatesRealAndVirtual()
    {
        // Schema is UNIQUE(account_no, mode) — same number CAN coexist
        // across modes, even though LS issues distinct numbers in
        // practice. Pin the schema so future virtual-mode work has the
        // isolation behaviour it expects.
        await using Scratch s = new();
        LsLiveAccount real = await s.Repo.UpsertDiscoveredAsync("X-01", "real");
        LsLiveAccount virt = await s.Repo.UpsertDiscoveredAsync("X-01", "virtual");

        real.Id.Should().NotBe(virt.Id);
        (await s.Repo.GetByModeAsync("real"))!.Mode.Should().Be("real");
        (await s.Repo.GetByModeAsync("virtual"))!.Mode.Should().Be("virtual");
    }

    [Fact]
    public async Task SetNicknameAsync_ReturnsNullForUnknownAccount()
    {
        await using Scratch s = new();

        LsLiveAccount? result = await s.Repo.SetNicknameAsync("does-not-exist", "real", "label");

        result.Should().BeNull();
    }

    [Fact]
    public async Task SetNicknameAsync_ClearsLabelWhenPassedBlank()
    {
        await using Scratch s = new();
        await s.Repo.UpsertDiscoveredAsync("20856195501", "real");
        await s.Repo.SetNicknameAsync("20856195501", "real", "내 LS");

        LsLiveAccount? cleared = await s.Repo.SetNicknameAsync("20856195501", "real", "  ");

        cleared.Should().NotBeNull();
        cleared!.Nickname.Should().BeNull();
    }

    [Fact]
    public async Task MigrationV7_BackfillsPreExistingBrokerLsRowFromPaperTable()
    {
        // v1.6 dev shipped auto-discovery that wrote broker='LS' rows into
        // the paper-portfolio `accounts` table. Migration v7 moves those
        // rows to `ls_accounts` and removes the originals so paper / live
        // stay physically separate going forward. Migrations apply only
        // when their version is greater than the current `_schema_version`,
        // so the test simulates pre-v7 state by stopping migrations at v6
        // (writing the broker='LS' row directly via SQL) then opening a
        // fresh repository pair to let v7 land.
        await using Scratch s = new();
        await s.PaperRepo.InitializeAsync();
        // After InitializeAsync the migrations table has v7 logged. Roll
        // it back by deleting the v7 marker and dropping ls_accounts so
        // the next init reapplies v7's backfill INSERT against a paper
        // row we plant directly.
        await using (var conn = new SqliteConnection($"Data Source={s.DbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM _schema_version WHERE version = 7;
                DROP TABLE IF EXISTS ls_accounts;
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('12345-67890', 'LS-real-12345', 'LS', 'real', 0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Fresh repo pair on the same file → migrations replay from v7
        // forward, backfilling our planted row into ls_accounts.
        var freshPaper = new SqlitePortfolioRepository(s.DbPath, "real");
        var freshLive = new SqliteLsLiveAccountRepository(freshPaper, s.DbPath);
        await freshLive.InitializeAsync();

        LsLiveAccount? migrated = await freshLive.GetByModeAsync("real");
        migrated.Should().NotBeNull();
        migrated!.AccountNo.Should().Be("12345-67890");
        migrated.Nickname.Should().Be("LS-real-12345");

        // And the source row in `accounts` is gone — paper table is paper-only.
        IReadOnlyList<Account> paperAfter = await freshPaper.ListAccountsAsync();
        paperAfter.Should().NotContain(a => a.Broker == "LS");
    }

    sealed class Scratch : IAsyncDisposable
    {
        readonly string _directory;
        public Scratch()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-lsopenapi-live-repo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            DbPath = System.IO.Path.Combine(_directory, "portfolio.db");
            PaperRepo = new SqlitePortfolioRepository(DbPath, "real");
            Repo = new SqliteLsLiveAccountRepository(PaperRepo, DbPath);
        }

        public string DbPath { get; }
        public SqlitePortfolioRepository PaperRepo { get; }
        public SqliteLsLiveAccountRepository Repo { get; }

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
}
