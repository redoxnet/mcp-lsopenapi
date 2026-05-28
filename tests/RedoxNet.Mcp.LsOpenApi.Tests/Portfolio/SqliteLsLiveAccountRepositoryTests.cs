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
    public async Task Migrations_ProduceCleanV8EndState()
    {
        // v1.6 release-prep migrations (v6→v7→v8) cascade on first init.
        // v6 added accounts.mode; v7 moved auto-discovered broker='LS'
        // rows from `accounts` to the new `ls_accounts` table; v8 dropped
        // the now-dead mode column and re-keyed the nickname uniqueness
        // back to a single-column index. Pinning the end-state ensures
        // a fresh install lands on the v8 schema directly without
        // intermediate breakage.
        await using Scratch s = new();
        await s.PaperRepo.InitializeAsync();

        // accounts.mode column is gone (v8 ALTER DROP COLUMN).
        await using var conn = new SqliteConnection($"Data Source={s.DbPath}");
        await conn.OpenAsync();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "SELECT name FROM pragma_table_info('accounts');";
        using var reader = await pragma.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync())
            columns.Add(reader.GetString(0));
        columns.Should().NotContain("mode", "v8 migration drops the dead mode column from paper portfolios");
        columns.Should().Contain(["account_no", "nickname", "broker", "is_default"]);

        // ls_accounts table exists, mode-keyed.
        await reader.DisposeAsync();
        using var lsPragma = conn.CreateCommand();
        lsPragma.CommandText = "SELECT name FROM pragma_table_info('ls_accounts');";
        using var lsReader = await lsPragma.ExecuteReaderAsync();
        var lsColumns = new List<string>();
        while (await lsReader.ReadAsync())
            lsColumns.Add(lsReader.GetString(0));
        lsColumns.Should().Contain(["account_no", "mode", "nickname"], "live registry stays mode-keyed");
    }

    sealed class Scratch : IAsyncDisposable
    {
        readonly string _directory;
        public Scratch()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-lsopenapi-live-repo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            DbPath = System.IO.Path.Combine(_directory, "portfolio.db");
            PaperRepo = new SqlitePortfolioRepository(DbPath);
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
