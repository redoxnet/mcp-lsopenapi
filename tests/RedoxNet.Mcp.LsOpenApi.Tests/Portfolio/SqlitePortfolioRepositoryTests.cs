using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Portfolio;

public sealed class SqlitePortfolioRepositoryTests
{
    [Fact]
    public async Task InitializeAsync_StartsWithZeroAccountsAndDefaultWatchlistGroup()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);

        await repository.InitializeAsync();

        IReadOnlyList<AccountSummary> accounts = await repository.ListAccountSummariesAsync();
        accounts.Should().BeEmpty();

        IReadOnlyList<WatchlistGroupSummary> groups = await repository.ListGroupsAsync();
        groups.Should().ContainSingle(g => g.Name == "default" && g.ItemCount == 0);
    }

    [Fact]
    public async Task UpsertAccountAsync_FirstCallAutoPromotesToDefault()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        Account hantoo = await repository.UpsertAccountAsync("12345-01", "한투", "한국투자", setDefault: false);
        Account kb = await repository.UpsertAccountAsync("67890-22", "KB ISA", "KB증권", setDefault: false);

        hantoo.IsDefault.Should().BeTrue("first account auto-promotes to default");
        kb.IsDefault.Should().BeFalse("second account does not displace existing default unless setDefault=true");

        IReadOnlyList<AccountSummary> summaries = await repository.ListAccountSummariesAsync();
        summaries.Should().HaveCount(2);
        summaries.Should().ContainSingle(a => a.AccountNumber == "12345-01" && a.IsDefault);
    }

    [Fact]
    public async Task UpsertAccountAsync_SetDefaultTrueSwitchesDefault()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        await repository.UpsertAccountAsync("12345-01", "한투", null, setDefault: false);
        Account kb = await repository.UpsertAccountAsync("67890-22", "KB", null, setDefault: true);

        kb.IsDefault.Should().BeTrue();
        Account? def = await repository.GetDefaultAccountAsync();
        def!.AccountNo.Should().Be("67890-22");
    }

    [Fact]
    public async Task GetAccountByIdentifierAsync_ResolvesByAccountNumberOrNickname()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("12345-01", "한투", null, setDefault: false);

        Account? byNumber = await repository.GetAccountByIdentifierAsync("12345-01");
        Account? byNickname = await repository.GetAccountByIdentifierAsync("한투");
        Account? missing = await repository.GetAccountByIdentifierAsync("nope");

        byNumber!.Nickname.Should().Be("한투");
        byNickname!.AccountNo.Should().Be("12345-01");
        missing.Should().BeNull();
    }

    [Fact]
    public async Task RemoveAccountAsync_PromotesNextAccountAsDefault()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account a = await repository.UpsertAccountAsync("AAA", "first", null, setDefault: false);
        Account b = await repository.UpsertAccountAsync("BBB", "second", null, setDefault: false);
        await repository.UpsertAccountAsync("CCC", "third", null, setDefault: false);

        a.IsDefault.Should().BeTrue("first inserted auto-promotes");
        RemoveAccountResult result = await repository.RemoveAccountAsync(a, confirm: false);

        result.Removed.Should().BeTrue();
        result.CascadedHoldings.Should().Be(0);
        result.NewDefault.Should().NotBeNull();
        result.NewDefault!.AccountNumber.Should().Be("BBB", "id ASC succession");

        Account? def = await repository.GetDefaultAccountAsync();
        def!.AccountNo.Should().Be("BBB");
    }

    [Fact]
    public async Task RemoveAccountAsync_RefusesWithoutConfirmWhenHoldingsExist()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("12345-01", "한투", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);

        RemoveAccountResult result = await repository.RemoveAccountAsync(account, confirm: false);

        result.Removed.Should().BeFalse();
        result.CascadedHoldings.Should().Be(1);
        (await repository.ListAccountSummariesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task SellHoldingAsync_RemovesRowWhenReachingZero()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);

        Holding? remaining = await repository.SellHoldingAsync(account.Id, "005930", 10);

        remaining.Should().BeNull();
        (await repository.ListHoldingsAsync(account.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task BuyHoldingAsync_MergesUsingWeightedAverage()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);

        Holding first = await repository.BuyHoldingAsync(account.Id, "005930", 10, 70000);
        Holding second = await repository.BuyHoldingAsync(account.Id, "005930", 5, 80000);

        first.Quantity.Should().Be(10);
        first.AvgPrice.Should().Be(70000);
        second.Quantity.Should().Be(15);
        second.AvgPrice.Should().BeApproximately((10 * 70000 + 5 * 80000) / 15.0, 1e-6);
    }

    [Fact]
    public async Task ApplyCorporateActionAsync_AppliesSplitMath()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 2500000, null);

        Holding? after = await repository.ApplyCorporateActionAsync(account.Id, "005930", qtyMultiplier: 50, priceMultiplier: 1.0 / 50);

        after!.Quantity.Should().Be(500);
        after.AvgPrice.Should().BeApproximately(50000, 1e-3);
    }

    [Fact]
    public async Task ApplyCorporateActionAsync_RejectsNonDivisibleReverseSplit()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 7, 50000, null);

        Func<Task> act = () => repository.ApplyCorporateActionAsync(account.Id, "005930", qtyMultiplier: 1.0 / 3, priceMultiplier: 3);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        Holding? unchanged = await repository.GetHoldingAsync(account.Id, "005930");
        unchanged!.Quantity.Should().Be(7);
    }

    [Fact]
    public async Task FindAccountsHoldingAsync_ReturnsBothAccounts()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account a = await repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        Account b = await repository.UpsertAccountAsync("BBB", "KB", null, setDefault: false);
        await repository.SetHoldingAsync(a.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(b.Id, "005930", 5, 80000, null);

        IReadOnlyList<Account> holders = await repository.FindAccountsHoldingAsync("005930");

        holders.Should().HaveCount(2);
        holders.Select(h => h.AccountNo).Should().BeEquivalentTo(["AAA", "BBB"]);
    }

    [Fact]
    public async Task RenameGroupAsync_RejectsExistingTarget()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.CreateGroupAsync("semis", null);
        await repository.CreateGroupAsync("bio", null);

        Func<Task> act = () => repository.RenameGroupAsync("semis", "bio");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RenameGroupAsync_RenamesSuccessfully()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.CreateGroupAsync("semis", "반도체");

        RenameGroupResult result = await repository.RenameGroupAsync("semis", "semiconductors");

        result.NewName.Should().Be("semiconductors");
        (await repository.ListGroupsAsync()).Should().Contain(g => g.Name == "semiconductors");
    }

    [Fact]
    public async Task RenameBrokerAsync_UpdatesAllMatching()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("AAA", "한투-주식", "한투", setDefault: false);
        await repository.UpsertAccountAsync("BBB", "한투-ISA", "한투", setDefault: false);
        await repository.UpsertAccountAsync("CCC", "KB", "KB증권", setDefault: false);

        RenameBrokerResult result = await repository.RenameBrokerAsync("한투", "한국투자증권");

        result.AccountsAffected.Should().Be(2);
        (await repository.ListAccountsAsync()).Where(a => a.Broker == "한국투자증권").Should().HaveCount(2);
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
    public async Task SetHoldingAsync_AcceptsEtfCodeWithLetter()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);

        Holding holding = await repository.SetHoldingAsync(account.Id, "0117v0", 40, 32530, null);

        holding.Symbol.Should().Be("0117V0");
        holding.Quantity.Should().Be(40);
    }

    [Fact]
    public async Task InitializeAsync_AllowsConcurrentCalls()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => repository.InitializeAsync()));

        (await repository.ListAccountSummariesAsync()).Should().BeEmpty();
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
