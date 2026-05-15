using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Portfolio;

public sealed class SqlitePortfolioRepositoryTests
{
    [Fact]
    public async Task InitializeAsync_SeedsDefaultGroupAndAccount()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);

        await repository.InitializeAsync();

        IReadOnlyList<WatchlistGroupSummary> groups = await repository.ListGroupsAsync();
        groups.Should().ContainSingle(g => g.Name == "default" && g.ItemCount == 0);

        Account account = await repository.GetDefaultAccountAsync();
        account.AccountNo.Should().Be("UNSET");
        account.Nickname.Should().Be("기본 계좌");
        account.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task WatchlistItems_AreGroupedAndCascadeOnGroupDelete()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        await repository.CreateGroupAsync("semis", "반도체", CancellationToken.None);
        WatchlistItem item = await repository.AddWatchlistItemAsync("005930", "semis", "core", CancellationToken.None);

        item.Symbol.Should().Be("005930");
        item.GroupName.Should().Be("semis");
        item.Name.Should().Be("005930");

        IReadOnlyList<WatchlistItem> semis = await repository.ListWatchlistAsync("semis");
        semis.Should().ContainSingle(x => x.Symbol == "005930" && x.Notes == "core");

        DeleteGroupResult deleted = await repository.DeleteGroupAsync("semis");
        deleted.Deleted.Should().BeTrue();
        deleted.CascadedItems.Should().Be(1);
        (await repository.ListWatchlistAsync("semis")).Should().BeEmpty();
    }

    [Fact]
    public async Task Holdings_UseDefaultAccountAndCanBeUpdated()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.GetDefaultAccountAsync();

        await repository.UpsertHoldingAsync(account.Id, "005930", 10, 70000, "first", CancellationToken.None);
        Holding updated = await repository.UpsertHoldingAsync(account.Id, "005930", 12, 71000, "updated", CancellationToken.None);

        updated.Quantity.Should().Be(12);
        updated.AvgPrice.Should().Be(71000);
        IReadOnlyList<Holding> holdings = await repository.ListHoldingsAsync(account.Id);
        holdings.Should().ContainSingle(h => h.Symbol == "005930" && h.Notes == "updated");
    }

    [Fact]
    public async Task InitializeAsync_AllowsConcurrentCalls()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repository.InitializeAsync()));

        IReadOnlyList<WatchlistGroupSummary> groups = await repository.ListGroupsAsync();
        groups.Should().ContainSingle(g => g.Name == "default");
    }

    [Fact]
    public async Task AddWatchlistItemAsync_UpdatesNoteOnDuplicateSymbolInGroup()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        await repository.AddWatchlistItemAsync("005930", "default", "first", CancellationToken.None);
        await repository.AddWatchlistItemAsync("005930", "default", "second", CancellationToken.None);

        IReadOnlyList<WatchlistItem> items = await repository.ListWatchlistAsync("default");
        items.Should().ContainSingle(i => i.Symbol == "005930" && i.Notes == "second");
    }

    [Fact]
    public async Task AddWatchlistItemAsync_ThrowsForMissingGroup()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        Func<Task> act = () => repository.AddWatchlistItemAsync("005930", "missing", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*missing*");
    }

    [Fact]
    public async Task AddWatchlistItemAsync_AcceptsEtfCodeWithLetter()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        WatchlistItem upper = await repository.AddWatchlistItemAsync("0117V0", "default", null, CancellationToken.None);
        WatchlistItem lower = await repository.AddWatchlistItemAsync("0117v0", "default", "lowercase", CancellationToken.None);

        upper.Symbol.Should().Be("0117V0");
        lower.Symbol.Should().Be("0117V0");
        IReadOnlyList<WatchlistItem> items = await repository.ListWatchlistAsync("default");
        items.Should().ContainSingle(i => i.Symbol == "0117V0" && i.Notes == "lowercase");
    }

    [Fact]
    public async Task UpsertHoldingAsync_AcceptsEtfCodeWithLetter()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.GetDefaultAccountAsync();

        Holding holding = await repository.UpsertHoldingAsync(account.Id, "0117v0", 40, 32530, null, CancellationToken.None);

        holding.Symbol.Should().Be("0117V0");
        holding.Quantity.Should().Be(40);
    }

    [Fact]
    public void ResolveDatabasePath_PrefersEnvironmentOverride()
    {
        string? previous = Environment.GetEnvironmentVariable(SqlitePortfolioRepository.DatabasePathEnvVar);
        string overridePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "portfolio-override.db");
        try
        {
            Environment.SetEnvironmentVariable(SqlitePortfolioRepository.DatabasePathEnvVar, overridePath);

            SqlitePortfolioRepository.ResolveDatabasePath().Should().Be(overridePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SqlitePortfolioRepository.DatabasePathEnvVar, previous);
        }
    }

    sealed class TestDatabase : IAsyncDisposable
    {
        readonly string _directory;

        public TestDatabase()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-lsopenapi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "portfolio.db");
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; SQLite WAL handles may linger briefly on Windows.
            }
            return ValueTask.CompletedTask;
        }
    }
}

