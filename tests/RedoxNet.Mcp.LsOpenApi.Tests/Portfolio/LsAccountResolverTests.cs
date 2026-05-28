using FluentAssertions;
using Microsoft.Data.Sqlite;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Portfolio;

/// <summary>
/// Pins the resolver behavior every v1.6 <c>ls_account_*</c> tool relies on
/// — mode-bounded account selection with the v0.7 portfolio default /
/// ambiguous / not-found envelope pattern.
/// </summary>
public sealed class LsAccountResolverTests
{
    [Fact]
    public async Task ResolveAsync_PicksDefaultWhenIdentifierOmitted()
    {
        await using ResolverScratch db = new();
        var repository = new SqlitePortfolioRepository(db.Path, "real");
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("12345-01", "한투", null, setDefault: false);
        await repository.UpsertAccountAsync("67890-22", "ISA", null, setDefault: true);

        var resolver = new LsAccountResolver(repository, LsMarket.Real);
        Account picked = await resolver.ResolveAsync(null);

        picked.AccountNo.Should().Be("67890-22");
        picked.IsDefault.Should().BeTrue();
        picked.Mode.Should().Be("real");
        resolver.Mode.Should().Be("real");
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullOnEmptyRegistry()
    {
        // v1.6 correction: an empty registry is NOT an error. LS account-
        // inquiry TRs do not take account_number as input — the appkey's
        // token tells LS which account to return — so the wrapper can call
        // the TR and read AcntNo from the response. The resolver returns
        // null and the calling tool auto-discovers from the broker response.
        await using ResolverScratch db = new();
        var repository = new SqlitePortfolioRepository(db.Path, "virtual");
        await repository.InitializeAsync();

        var resolver = new LsAccountResolver(repository, LsMarket.Virtual);
        Account? resolved = await resolver.ResolveAsync(null);

        resolved.Should().BeNull();
    }

    [Fact]
    public async Task RecordDiscoveredAsync_UpsertsAndIsIdempotent()
    {
        await using ResolverScratch db = new();
        var repository = new SqlitePortfolioRepository(db.Path, "virtual");
        await repository.InitializeAsync();
        var resolver = new LsAccountResolver(repository, LsMarket.Virtual);

        Account? first = await resolver.RecordDiscoveredAsync("99988877766", defaultNickname: null);
        first.Should().NotBeNull();
        first!.AccountNo.Should().Be("99988877766");
        first.Mode.Should().Be("virtual");
        first.Nickname.Should().Contain("99988877766");

        // Idempotent: calling again does not throw and returns the same row.
        Account? second = await resolver.RecordDiscoveredAsync("99988877766", defaultNickname: null);
        second.Should().NotBeNull();
        second!.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task ResolveAsync_RaisesAmbiguousAccountWhenNoDefaultAndMany()
    {
        // UpsertAccountAsync auto-promotes the first row to default. Demote
        // it directly via SQL to reach the AmbiguousAccount branch.
        await using ResolverScratch db = new();
        var repository = new SqlitePortfolioRepository(db.Path, "real");
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("A-01", "first", null, setDefault: false);
        await repository.UpsertAccountAsync("A-02", "second", null, setDefault: false);

        using (var conn = new SqliteConnection($"Data Source={db.Path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE accounts SET is_default = 0;";
            cmd.ExecuteNonQuery();
        }

        var resolver = new LsAccountResolver(repository, LsMarket.Real);
        Func<Task> act = () => resolver.ResolveAsync(null);

        var ex = (await act.Should().ThrowAsync<AmbiguousAccountException>()).Which;
        ex.Code.Should().Be("AmbiguousAccount");
        ex.Candidates.Should().HaveCount(2);
        ex.Candidates.Select(c => c.AccountNumber).Should().BeEquivalentTo(["A-01", "A-02"]);
        ex.Candidates.Should().OnlyContain(c => c.Mode == "real");
    }

    [Fact]
    public async Task ResolveAsync_LooksUpByAccountNumberAndNickname()
    {
        await using ResolverScratch db = new();
        var repository = new SqlitePortfolioRepository(db.Path, "real");
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("99-77", "주식", null, setDefault: false);

        var resolver = new LsAccountResolver(repository, LsMarket.Real);

        (await resolver.ResolveAsync("99-77")).Nickname.Should().Be("주식");
        (await resolver.ResolveAsync("주식")).AccountNo.Should().Be("99-77");
    }

    [Fact]
    public async Task ResolveAsync_RaisesAccountNotFoundOnUnknownIdentifier()
    {
        await using ResolverScratch db = new();
        var repository = new SqlitePortfolioRepository(db.Path, "real");
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("99-77", "주식", null, setDefault: false);

        var resolver = new LsAccountResolver(repository, LsMarket.Real);

        var ex = await Assert.ThrowsAsync<AccountNotFoundException>(
            () => resolver.ResolveAsync("나의비밀계좌"));
        ex.Identifier.Should().Be("나의비밀계좌");
        ex.Candidates.Should().ContainSingle(c => c.AccountNumber == "99-77");
    }

    [Fact]
    public async Task ResolveAsync_DoesNotLeakAccountsAcrossModes()
    {
        await using ResolverScratch db = new();
        var realRepository = new SqlitePortfolioRepository(db.Path, "real");
        var virtualRepository = new SqlitePortfolioRepository(db.Path, "virtual");
        await realRepository.InitializeAsync();
        await realRepository.UpsertAccountAsync("R-01", "real-acct", null, setDefault: true);
        await virtualRepository.UpsertAccountAsync("V-01", "virt-acct", null, setDefault: true);

        var realResolver = new LsAccountResolver(realRepository, LsMarket.Real);
        var virtualResolver = new LsAccountResolver(virtualRepository, LsMarket.Virtual);

        (await realResolver.ResolveAsync(null)).AccountNo.Should().Be("R-01");
        (await virtualResolver.ResolveAsync(null)).AccountNo.Should().Be("V-01");

        await Assert.ThrowsAsync<AccountNotFoundException>(() => realResolver.ResolveAsync("V-01"));
        await Assert.ThrowsAsync<AccountNotFoundException>(() => virtualResolver.ResolveAsync("R-01"));
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
