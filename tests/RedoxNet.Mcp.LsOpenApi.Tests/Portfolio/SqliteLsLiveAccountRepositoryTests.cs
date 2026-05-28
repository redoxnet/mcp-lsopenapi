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

    // ===== P0/P1/P2 regression tests =====
    //
    // Each test simulates the pre-v7-or-v8 schema state on a freshly
    // initialised DB by re-creating the v6 column / dropped indexes
    // and rolling back the _schema_version marker. A second repo
    // instance then re-applies v7+v8 (or just v8) so the migration
    // behavior under realistic pre-state can be pinned.

    /// <summary>
    /// P0: a pre-v1.6 paper portfolio that happens to carry the v1
    /// DEFAULT broker='LS' and owns user-entered holdings must survive
    /// migration v7. The holdings ON DELETE CASCADE FK would silently
    /// destroy that data if v7 used broker='LS' alone as a discriminator.
    /// </summary>
    [Fact]
    public async Task MigrationV7_PreservesPaperPortfolioWithBrokerLsAndHoldings()
    {
        await using Scratch s = new();
        await s.PaperRepo.InitializeAsync();
        await ResetToV6(s.DbPath);

        // Plant a paper portfolio with broker='LS' and real holdings.
        await using (var conn = new SqliteConnection($"Data Source={s.DbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO stocks(symbol, name, market) VALUES ('005930', '삼성전자', 'kospi');
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('PAPER-LS-01', '내 LS증권', 'LS', 'real', 1);
                INSERT INTO holdings(account_id, symbol, quantity, avg_price)
                VALUES ((SELECT id FROM accounts WHERE account_no = 'PAPER-LS-01'),
                        '005930', 10, 700000000);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        // Re-init: v7 + v8 fire. The holdings-guard predicate must
        // protect the planted paper row from the broker='LS' DELETE.
        var freshPaper = new SqlitePortfolioRepository(s.DbPath);
        await freshPaper.InitializeAsync();

        IReadOnlyList<Account> paperAfter = await freshPaper.ListAccountsAsync();
        paperAfter.Should().ContainSingle(a => a.AccountNo == "PAPER-LS-01" && a.Broker == "LS");
        IReadOnlyList<Holding> holdingsAfter = await freshPaper.ListAllHoldingsAsync();
        holdingsAfter.Should().ContainSingle(h => h.Symbol == "005930" && h.Quantity == 10);

        // The empty broker='LS' would have been moved; this case has none.
        var freshLive = new SqliteLsLiveAccountRepository(freshPaper, s.DbPath);
        (await freshLive.ListAllAsync()).Should().BeEmpty(
            "broker='LS' rows with holdings are paper portfolios and never get moved to ls_accounts");
    }

    /// <summary>
    /// P0 follow-up: pre-v1.6 paper account registration accepted
    /// `broker=null` which the C# layer resolved to "LS" (the v1
    /// schema DEFAULT). A user who registered an empty paper account
    /// before v1.6 — e.g. "내 한투" with no broker label and no
    /// holdings yet — carries broker='LS' purely by inheritance.
    /// The holdings-only guard does NOT save them. The
    /// nickname-pattern guard does: the row's friendly name doesn't
    /// match `LS-{mode}-{acntno}`, so the row stays in `accounts`.
    /// </summary>
    [Fact]
    public async Task MigrationV7_PreservesEmptyPaperRowWithBrokerLsAndNonPatternNickname()
    {
        await using Scratch s = new();
        await s.PaperRepo.InitializeAsync();
        await ResetToV6(s.DbPath);

        await using (var conn = new SqliteConnection($"Data Source={s.DbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            // Three pre-v1.6 paper rows, all with broker='LS' by null-default
            // inheritance, all with zero holdings, none matching the auto-
            // discovery nickname template. All must survive v7.
            cmd.CommandText = """
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('HANTOO-01', '내 한투', 'LS', 'real', 1);
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('KB-99', 'KB 연금', 'LS', 'real', 0);
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('MIRAE-7', 'LS-something-non-pattern', 'LS', 'real', 0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var freshPaper = new SqlitePortfolioRepository(s.DbPath);
        await freshPaper.InitializeAsync();
        var freshLive = new SqliteLsLiveAccountRepository(freshPaper, s.DbPath);

        IReadOnlyList<Account> paperAfter = await freshPaper.ListAccountsAsync();
        paperAfter.Should().HaveCount(3,
            "broker='LS' alone does not flag a row as ghost — strict nickname pattern AND zero holdings AND broker='LS' must all hold");
        paperAfter.Select(a => a.AccountNo).Should().BeEquivalentTo(["HANTOO-01", "KB-99", "MIRAE-7"]);
        (await freshLive.ListAllAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// P0 companion: a broker='LS' row with NO holdings AND a nickname
    /// that EXACTLY matches the v1.6-dev auto-discovery template
    /// (`LS-{mode}-{acntno}`) IS the ghost auto-discovery shape and
    /// migrates to ls_accounts.
    /// </summary>
    [Fact]
    public async Task MigrationV7_MovesEmptyBrokerLsGhostRowsToLiveRegistry()
    {
        await using Scratch s = new();
        await s.PaperRepo.InitializeAsync();
        await ResetToV6(s.DbPath);

        await using (var conn = new SqliteConnection($"Data Source={s.DbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('20856195501', 'LS-real-20856195501', 'LS', 'real', 0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var freshPaper = new SqlitePortfolioRepository(s.DbPath);
        await freshPaper.InitializeAsync();
        var freshLive = new SqliteLsLiveAccountRepository(freshPaper, s.DbPath);

        (await freshPaper.ListAccountsAsync()).Should().BeEmpty("ghost broker='LS' rows leave `accounts`");
        LsLiveAccount? moved = await freshLive.GetByModeAsync("real");
        moved.Should().NotBeNull();
        moved!.AccountNo.Should().Be("20856195501");
    }

    /// <summary>
    /// P1: a v6 state with one is_default=1 row per mode must collapse to
    /// a single canonical default after v8 — the schema-level partial
    /// UNIQUE wouldn't even let the index be created otherwise.
    /// </summary>
    [Fact]
    public async Task MigrationV8_CollapsesCrossModeDefaultDuplicatesToOne()
    {
        await using Scratch s = new();
        await s.PaperRepo.InitializeAsync();
        await ResetToV6(s.DbPath);

        await using (var conn = new SqliteConnection($"Data Source={s.DbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('REAL-01', '주식', '유안타증권', 'real', 1);
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('VIRT-01', '모의', '유안타증권', 'virtual', 1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var freshPaper = new SqlitePortfolioRepository(s.DbPath);
        await freshPaper.InitializeAsync();

        IReadOnlyList<Account> all = await freshPaper.ListAccountsAsync();
        all.Should().HaveCount(2);
        all.Count(a => a.IsDefault).Should().Be(1, "v8 collapses to a single canonical default");
        all.Single(a => a.IsDefault).AccountNo.Should().Be("REAL-01", "lowest-id wins the consolidation");
    }

    /// <summary>
    /// P2: v8 nickname disambig adds "(mode#id)" — the id makes the
    /// suffix guaranteed unique even if a row already happens to use
    /// the "(mode)" pattern alone.
    /// </summary>
    [Fact]
    public async Task MigrationV8_DisambiguatesNicknamesEvenWhenSuffixCollides()
    {
        await using Scratch s = new();
        await s.PaperRepo.InitializeAsync();
        await ResetToV6(s.DbPath);

        // Three rows that would conflict under a naive "(mode)" suffix:
        //  - id=1: "주식" / real
        //  - id=2: "주식" / virtual          → naive rename to "주식 (virtual)"
        //  - id=3: "주식 (virtual)" / real    → pre-existing, would collide
        await using (var conn = new SqliteConnection($"Data Source={s.DbPath}"))
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('A-01', '주식', '한투', 'real', 0);
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('A-02', '주식', '한투', 'virtual', 0);
                INSERT INTO accounts(account_no, nickname, broker, mode, is_default)
                VALUES ('A-03', '주식 (virtual)', '한투', 'real', 0);
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        // The migration must succeed (the UNIQUE INDEX on nickname creates
        // without conflict because id-based suffixes are guaranteed unique).
        var freshPaper = new SqlitePortfolioRepository(s.DbPath);
        await freshPaper.InitializeAsync();

        IReadOnlyList<Account> all = await freshPaper.ListAccountsAsync();
        all.Should().HaveCount(3);
        all.Select(a => a.Nickname).Distinct().Should().HaveCount(3, "all nicknames must be unique");
    }

    static async Task ResetToV6(string dbPath)
    {
        // Rolls a freshly v8-initialised DB back to a v6-shaped state so
        // tests can plant pre-v7/v8 data and observe migration behavior.
        // Recreates the dropped mode column + v6 indexes; drops ls_accounts;
        // deletes the v7+v8 schema_version markers so the next repo init
        // re-applies them.
        await using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DROP INDEX IF EXISTS idx_accounts_nickname;
            DROP INDEX IF EXISTS idx_accounts_default;
            DROP INDEX IF EXISTS idx_accounts_one_default;
            DROP TABLE IF EXISTS ls_accounts;
            ALTER TABLE accounts ADD COLUMN mode TEXT NOT NULL DEFAULT 'real';
            CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_nickname_mode ON accounts(nickname, mode);
            CREATE INDEX IF NOT EXISTS idx_accounts_mode_default ON accounts(mode, is_default, id);
            DELETE FROM _schema_version WHERE version >= 7;
            """;
        await cmd.ExecuteNonQueryAsync();
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
