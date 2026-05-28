using FluentAssertions;
using Microsoft.Data.Sqlite;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Portfolio;

/// <summary>
/// Pins the v1.6 (schema-split) resolver behavior. LS account-inquiry TRs
/// do not accept <c>AcntNo</c> in their request — the appkey-bound LS
/// subaccount is resolved server-side and only echoed in the response.
/// So the resolver's job is purely label persistence into a registry
/// that is physically separate from the paper-portfolio <c>accounts</c>
/// table. Tests below pin that separation plus the discovery roundtrip.
/// </summary>
public sealed class LsAccountResolverTests
{
    [Fact]
    public async Task GetRegisteredAsync_ReturnsNullWhenLiveRegistryEmpty()
    {
        await using ResolverScratch db = new();
        ILsLiveAccountRepository live = NewLive(db);

        var resolver = new LsAccountResolver(live, LsMarket.Virtual);
        LsLiveAccount? row = await resolver.GetRegisteredAsync();

        row.Should().BeNull();
        resolver.Mode.Should().Be("virtual");
    }

    [Fact]
    public async Task GetRegisteredAsync_IgnoresPaperAccountsInTheSameDatabase()
    {
        // Sanity: paper-portfolio rows must not leak into ls_account_*
        // labelling. The E2E bug we ship-blocked v1.6 on was the paper
        // default 유안타-001 shadowing the LS broker echo. Schema-split
        // makes the leak structurally impossible — paper accounts live
        // in `accounts`, live in `ls_accounts`, and the resolver only
        // reads the latter.
        await using ResolverScratch db = new();
        var paper = new SqlitePortfolioRepository(db.Path, "real");
        await paper.InitializeAsync();
        await paper.UpsertAccountAsync("유안타-001", "유안타", "유안타증권", setDefault: true);
        await paper.UpsertAccountAsync("카카오페이-001", "카카오페이", "카카오페이증권", setDefault: false);

        ILsLiveAccountRepository live = NewLive(db);
        var resolver = new LsAccountResolver(live, LsMarket.Real);

        LsLiveAccount? row = await resolver.GetRegisteredAsync();

        row.Should().BeNull();
    }

    [Fact]
    public async Task RecordDiscoveredAsync_UpsertsAndIsIdempotent()
    {
        await using ResolverScratch db = new();
        ILsLiveAccountRepository live = NewLive(db);
        var resolver = new LsAccountResolver(live, LsMarket.Virtual);

        LsLiveAccount? first = await resolver.RecordDiscoveredAsync("99988877766");
        first.Should().NotBeNull();
        first!.AccountNo.Should().Be("99988877766");
        first.Mode.Should().Be("virtual");
        first.Nickname.Should().BeNull();

        // Idempotent: calling again refreshes last_seen_at but returns
        // the same row identity.
        LsLiveAccount? second = await resolver.RecordDiscoveredAsync("99988877766");
        second.Should().NotBeNull();
        second!.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task RecordDiscoveredAsync_StoresBranchAndAccountNameWhenSupplied()
    {
        // CSPAQ12200 already extracts BrnNm / AcntNm for the balance
        // payload; the resolver caches them in ls_accounts so a later
        // call without those fields still gets the friendly labels.
        await using ResolverScratch db = new();
        ILsLiveAccountRepository live = NewLive(db);
        var resolver = new LsAccountResolver(live, LsMarket.Real);

        LsLiveAccount? row = await resolver.RecordDiscoveredAsync(
            "20856195501", branchName: "다이렉트206", accountName: "김종현");

        row.Should().NotBeNull();
        row!.BranchName.Should().Be("다이렉트206");
        row.AccountName.Should().Be("김종현");

        // A second call without labels should NOT erase the cached ones.
        LsLiveAccount? second = await resolver.RecordDiscoveredAsync("20856195501");
        second!.BranchName.Should().Be("다이렉트206");
        second.AccountName.Should().Be("김종현");
    }

    [Fact]
    public async Task RecordDiscoveredAsync_ReturnsNullOnBlankAcntNo()
    {
        await using ResolverScratch db = new();
        ILsLiveAccountRepository live = NewLive(db);
        var resolver = new LsAccountResolver(live, LsMarket.Real);

        (await resolver.RecordDiscoveredAsync(null)).Should().BeNull();
        (await resolver.RecordDiscoveredAsync("")).Should().BeNull();
        (await resolver.RecordDiscoveredAsync("   ")).Should().BeNull();
    }

    [Fact]
    public async Task BuildEcho_PrefersRegisteredRow()
    {
        await using ResolverScratch db = new();
        ILsLiveAccountRepository live = NewLive(db);
        var resolver = new LsAccountResolver(live, LsMarket.Real);
        LsLiveAccount? recorded = await resolver.RecordDiscoveredAsync("20856195501", branchName: "다이렉트206");

        LsLiveAccountInfo echo = resolver.BuildEcho(recorded, discoveredAcntNo: "ignored-precedence-test");

        echo.AccountNumber.Should().Be("20856195501");
        echo.Discovered.Should().BeTrue();
        echo.BranchName.Should().Be("다이렉트206");
        echo.Mode.Should().Be("real");
    }

    [Fact]
    public async Task BuildEcho_FallsBackToDiscoveredAcntNoWhenUnregistered()
    {
        await using ResolverScratch db = new();
        ILsLiveAccountRepository live = NewLive(db);
        var resolver = new LsAccountResolver(live, LsMarket.Real);

        LsLiveAccountInfo echo = resolver.BuildEcho(registered: null, discoveredAcntNo: "20856195501");

        echo.AccountNumber.Should().Be("20856195501");
        echo.Nickname.Should().BeNull();
        echo.Mode.Should().Be("real");
        echo.Discovered.Should().BeTrue();
    }

    [Fact]
    public async Task BuildEcho_ColdStartReturnsSyntheticWithNullNumber()
    {
        await using ResolverScratch db = new();
        ILsLiveAccountRepository live = NewLive(db);
        var resolver = new LsAccountResolver(live, LsMarket.Virtual);

        LsLiveAccountInfo echo = resolver.BuildEcho(registered: null);

        echo.AccountNumber.Should().BeNull();
        echo.Discovered.Should().BeFalse();
        echo.Mode.Should().Be("virtual");
    }

    [Fact]
    public async Task GetRegisteredAsync_DoesNotLeakAcrossModes()
    {
        await using ResolverScratch db = new();
        var paper = new SqlitePortfolioRepository(db.Path, "real");
        await paper.InitializeAsync();
        ILsLiveAccountRepository live = new SqliteLsLiveAccountRepository(paper, db.Path);

        var realResolver = new LsAccountResolver(live, LsMarket.Real);
        var virtualResolver = new LsAccountResolver(live, LsMarket.Virtual);
        await realResolver.RecordDiscoveredAsync("R-01");
        await virtualResolver.RecordDiscoveredAsync("V-01");

        (await realResolver.GetRegisteredAsync())!.AccountNo.Should().Be("R-01");
        (await virtualResolver.GetRegisteredAsync())!.AccountNo.Should().Be("V-01");
    }

    static ILsLiveAccountRepository NewLive(ResolverScratch db)
    {
        var paper = new SqlitePortfolioRepository(db.Path, "real");
        return new SqliteLsLiveAccountRepository(paper, db.Path);
    }

    sealed class ResolverScratch : IAsyncDisposable
    {
        readonly string _directory;

        public ResolverScratch()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-lsopenapi-res-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "portfolio.db");
        }

        public string Path { get; }

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
