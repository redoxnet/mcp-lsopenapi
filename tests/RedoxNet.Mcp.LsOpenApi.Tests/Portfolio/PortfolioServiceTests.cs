using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Portfolio;

public sealed class PortfolioServiceTests
{
    [Fact]
    public async Task ListWatchlistAsync_ReturnsMetadataWhenQuoteServiceFails()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.AddWatchlistItemAsync("005930", "default", "watch", CancellationToken.None);
        var service = new PortfolioService(repository, new FakeQuoteService("no credentials"));

        WatchlistListResult result = await service.ListWatchlistAsync("default");

        result.QuoteError.Should().Be("no credentials");
        WatchlistGroupItems group = result.Groups.Should().ContainSingle().Subject;
        group.Items.Should().ContainSingle();
        group.Items[0].Shcode.Should().Be("005930");
        group.Items[0].Quote.Should().BeNull();
    }

    [Fact]
    public async Task ListHoldingsAsync_GroupsByAccountAndAggregatesTotals()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account hantoo = await repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        Account kb = await repository.UpsertAccountAsync("BBB", "KB", null, setDefault: false);
        await repository.SetHoldingAsync(hantoo.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(kb.Id, "005930", 5, 80000, null);
        var quotes = new Dictionary<string, StockQuote?>
        {
            ["005930"] = new StockQuote(75000, 1000, 1.35, 74000, 76000, 73500, 1000, "2026-05-15T09:00:00+09:00")
            {
                Name = "삼성전자",
            },
        };
        var service = new PortfolioService(repository, new FakeQuoteService(quotes: quotes));

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null);

        result.Accounts.Should().HaveCount(2);
        result.TotalSummary.CostBasis.Should().Be(10 * 70000 + 5 * 80000);
        result.TotalSummary.MarketValue.Should().Be((10 + 5) * 75000);
        result.TotalSummary.Pnl.Should().Be(result.TotalSummary.MarketValue - result.TotalSummary.CostBasis);

        AccountHoldings hantooGroup = result.Accounts.Single(a => a.AccountNumber == "AAA");
        hantooGroup.Holdings.Should().ContainSingle();
        hantooGroup.Summary.CostBasis.Should().Be(700000);
    }

    [Fact]
    public async Task SetHoldingAsync_RejectsZeroQuantity()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        var service = new PortfolioService(repository, new FakeQuoteService());

        Func<Task> act = () => service.SetHoldingAsync("005930", 0, 70000, null, null);

        await act.Should().ThrowAsync<PortfolioValidationException>();
    }

    [Fact]
    public async Task SetHoldingAsync_NoAccountsRaisesRequiresAccount()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        var service = new PortfolioService(repository, new FakeQuoteService());

        Func<Task> act = () => service.SetHoldingAsync("005930", 10, 70000, null, null);

        await act.Should().ThrowAsync<RequiresAccountException>();
    }

    [Fact]
    public async Task SetHoldingAsync_MultipleAccountsWithoutTargetRaisesAmbiguous()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.UpsertAccountAsync("BBB", "other", null, setDefault: false);
        var service = new PortfolioService(repository, new FakeQuoteService());

        Func<Task> act = () => service.SetHoldingAsync("005930", 10, 70000, null, null);

        var ex = await act.Should().ThrowAsync<AmbiguousAccountException>();
        ex.Which.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task BuyHoldingAsync_AppliesWeightedAverageMergeWithEcho()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        var service = new PortfolioService(repository, new FakeQuoteService());

        await service.BuyHoldingAsync("005930", 10, 70000, accountIdentifier: null);
        HoldingWriteResult second = await service.BuyHoldingAsync("005930", 5, 80000, accountIdentifier: null);

        second.Quantity.Should().Be(15);
        second.AvgPrice.Should().BeApproximately((10 * 70000 + 5 * 80000) / 15.0, 1e-6);
        second.AppliedTo.AccountNumber.Should().Be("AAA");
    }

    [Fact]
    public async Task SellHoldingAsync_InsufficientQuantityCarriesAppliedTo()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        var service = new PortfolioService(repository, new FakeQuoteService());
        await service.SetHoldingAsync("005930", 10, 70000, null, null);

        Func<Task> act = () => service.SellHoldingAsync("005930", 15, accountIdentifier: null);

        var ex = await act.Should().ThrowAsync<InsufficientQuantityException>();
        ex.Which.CurrentQuantity.Should().Be(10);
        ex.Which.RequestedQuantity.Should().Be(15);
        ex.Which.AppliedTo.Nickname.Should().Be("main");
    }

    [Fact]
    public async Task SellHoldingAsync_AmbiguousSymbolRaisesAmbiguousAccount()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account a = await repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        Account b = await repository.UpsertAccountAsync("BBB", "KB", null, setDefault: false);
        await repository.SetHoldingAsync(a.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(b.Id, "005930", 5, 80000, null);
        var service = new PortfolioService(repository, new FakeQuoteService());

        Func<Task> act = () => service.SellHoldingAsync("005930", 1, accountIdentifier: null);

        var ex = await act.Should().ThrowAsync<AmbiguousAccountException>();
        ex.Which.Candidates.Should().HaveCount(2);
    }

    [Fact]
    public async Task SplitHoldingAsync_AppliesToAllAccountsByDefault()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account a = await repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        Account b = await repository.UpsertAccountAsync("BBB", "KB", null, setDefault: false);
        await repository.SetHoldingAsync(a.Id, "005930", 10, 2500000, null);
        await repository.SetHoldingAsync(b.Id, "005930", 4, 2520000, null);
        var service = new PortfolioService(repository, new FakeQuoteService());

        CorporateActionResult result = await service.SplitHoldingAsync("005930", 50, accountIdentifier: null);

        result.AppliedTo.Should().HaveCount(2);
        result.AppliedTo.All(r => r.After.Quantity == r.Before.Quantity * 50).Should().BeTrue();
    }

    [Fact]
    public async Task ReverseSplitHoldingAsync_RejectsNonDivisibleQuantity()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 7, 50000, null);
        var service = new PortfolioService(repository, new FakeQuoteService());

        Func<Task> act = () => service.ReverseSplitHoldingAsync("005930", 3, accountIdentifier: null);

        await act.Should().ThrowAsync<PortfolioValidationException>();
    }

    [Fact]
    public async Task RemoveAccountAsync_WithoutConfirmRaisesRequiresConfirmation()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        var service = new PortfolioService(repository, new FakeQuoteService());

        Func<Task> act = () => service.RemoveAccountAsync("한투", confirm: false);

        var ex = await act.Should().ThrowAsync<RequiresConfirmationException>();
        ex.Which.HoldingCount.Should().Be(1);
    }

    [Fact]
    public async Task RemoveAccountAsync_NotFoundCarriesCandidates()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("AAA", "real", null, setDefault: false);
        var service = new PortfolioService(repository, new FakeQuoteService());

        Func<Task> act = () => service.RemoveAccountAsync("ghost", confirm: false);

        var ex = await act.Should().ThrowAsync<AccountNotFoundException>();
        ex.Which.Candidates.Should().ContainSingle(c => c.Nickname == "real");
    }

    [Fact]
    public async Task UpsertAccountAsync_RejectsNicknameCollisionAcrossAccountNumbers()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        var service = new PortfolioService(repository, new FakeQuoteService());

        Func<Task> act = () => service.UpsertAccountAsync("BBB", "한투", null, setDefault: false);

        await act.Should().ThrowAsync<PortfolioValidationException>();
    }

    sealed class FakeQuoteService : IQuoteService
    {
        readonly string? _stockError;
        readonly IReadOnlyDictionary<string, StockQuote?> _quotes;

        public FakeQuoteService(string? stockError = null, IReadOnlyDictionary<string, StockQuote?>? quotes = null)
        {
            _stockError = stockError;
            _quotes = quotes ?? new Dictionary<string, StockQuote?>();
        }

        public Task<QuoteBatchResult<StockQuote>> GetStockQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken = default)
        {
            var result = symbols.Distinct(StringComparer.Ordinal).ToDictionary(
                s => s,
                s => _quotes.TryGetValue(s, out StockQuote? quote) ? quote : null,
                StringComparer.Ordinal);
            return Task.FromResult(new QuoteBatchResult<StockQuote>(result, _stockError));
        }

        public Task<QuoteBatchResult<SectorQuote>> GetSectorQuotesAsync(IReadOnlyCollection<string> sectorCodes, CancellationToken cancellationToken = default)
        {
            var result = sectorCodes.Distinct(StringComparer.Ordinal).ToDictionary(
                s => s,
                _ => (SectorQuote?)null,
                StringComparer.Ordinal);
            return Task.FromResult(new QuoteBatchResult<SectorQuote>(result, null));
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
            }
            return ValueTask.CompletedTask;
        }
    }
}
