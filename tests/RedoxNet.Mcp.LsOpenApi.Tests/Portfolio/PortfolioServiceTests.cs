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
    public async Task EnrichStockMetadataAsync_StoresThemesFromQuoteService()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        var themes = new Dictionary<string, IReadOnlyList<ThemeCatalogRow>>(StringComparer.Ordinal)
        {
            ["005930"] = new[]
            {
                new ThemeCatalogRow("0011", "반도체"),
                new ThemeCatalogRow("0100", "AI"),
            },
        };
        var service = new PortfolioService(repository, new FakeQuoteService(themesPerSymbol: themes));

        await service.EnrichStockMetadataAsync("005930");

        IReadOnlyDictionary<string, IReadOnlyList<StockTheme>> stored =
            await repository.GetStockThemesBatchAsync(new[] { "005930" });
        stored["005930"].Should().HaveCount(2);
        stored["005930"].Select(t => t.ThemeName).Should().BeEquivalentTo(["반도체", "AI"]);
    }

    [Fact]
    public async Task EnrichStockMetadataAsync_LsError_LeavesCacheUnchanged()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        // Seed an existing row so we can detect whether the error path nukes it.
        await repository.ReplaceStockThemesAsync("005930", new[] { new ThemeCatalogRow("0011", "반도체") });
        var service = new PortfolioService(repository, new FakeQuoteService(stockThemesError: "no credentials"));

        await service.EnrichStockMetadataAsync("005930");

        (await repository.GetStockThemesBatchAsync(new[] { "005930" }))["005930"].Should().ContainSingle();
    }

    [Fact]
    public async Task ListHoldingsAsync_EmitsThemesAndFreshnessBlock()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(account.Id, "000660", 5, 100000, null);
        // Pre-populate themes for 005930; leave 000660 un-enriched → "pending".
        await repository.ReplaceStockThemesAsync("005930", new[]
        {
            new ThemeCatalogRow("0011", "반도체"),
            new ThemeCatalogRow("0100", "AI"),
        });
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null);

        result.MetadataFreshness.Should().NotBeNull();
        result.MetadataFreshness!.FullyEnriched.Should().BeFalse();
        result.MetadataFreshness.Pending.Should().ContainKey("themes").WhoseValue.Should().Be(1);
        result.MetadataFreshness.Hint.Should().Contain("테마");

        HoldingWithQuote samsung = result.Accounts[0].Holdings.Single(h => h.Shcode == "005930");
        samsung.Themes.Should().NotBeNull();
        samsung.Themes!.Should().HaveCount(2);
        samsung.ThemesStatus.Should().BeNull("ok status is omitted to save payload tokens");

        HoldingWithQuote hynix = result.Accounts[0].Holdings.Single(h => h.Shcode == "000660");
        hynix.Themes.Should().BeNull();
        hynix.ThemesStatus.Should().Be("pending");
    }

    [Fact]
    public async Task ListHoldingsAsync_AllEnriched_FreshnessFullyEnrichedTrue()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.ReplaceStockThemesAsync("005930", new[] { new ThemeCatalogRow("0011", "반도체") });
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null);

        result.MetadataFreshness!.FullyEnriched.Should().BeTrue();
        result.MetadataFreshness.Pending.Should().BeEmpty();
        result.MetadataFreshness.Hint.Should().BeNull("hint omitted when fully enriched");
    }

    [Fact]
    public async Task ListHoldingsAsync_ThemeCodeFilter_KeepsExactMatches()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null); // Samsung — 반도체 + AI
        await repository.SetHoldingAsync(account.Id, "373220", 3, 380000, null); // LGES — 2차전지
        await repository.SetHoldingAsync(account.Id, "035420", 5, 220000, null); // NAVER — IT/플랫폼
        await repository.ReplaceStockThemesAsync("005930", new[]
        {
            new ThemeCatalogRow("0011", "반도체"),
            new ThemeCatalogRow("0100", "AI"),
        });
        await repository.ReplaceStockThemesAsync("373220", new[] { new ThemeCatalogRow("0064", "2차전지") });
        await repository.ReplaceStockThemesAsync("035420", new[] { new ThemeCatalogRow("0200", "플랫폼") });
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null, themeCode: "0011");

        result.Accounts[0].Holdings.Should().ContainSingle()
            .Which.Shcode.Should().Be("005930");
        result.Filter.Should().NotBeNull();
        result.Filter!.ThemeCode.Should().Be("0011");
        result.Filter.ThemeKeyword.Should().BeNull();
        result.MatchedThemes.Should().ContainSingle().Which.Should().Be("반도체");
    }

    [Fact]
    public async Task ListHoldingsAsync_ThemeKeywordFilter_MatchesLikeAndExposesAllMatches()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(account.Id, "000660", 5, 100000, null);
        await repository.SetHoldingAsync(account.Id, "035420", 5, 220000, null);
        await repository.ReplaceStockThemesAsync("005930", new[]
        {
            new ThemeCatalogRow("0011", "반도체"),
            new ThemeCatalogRow("0100", "AI"),
        });
        await repository.ReplaceStockThemesAsync("000660", new[]
        {
            new ThemeCatalogRow("0011", "반도체"),
            new ThemeCatalogRow("0501", "온디바이스 AI"),
        });
        await repository.ReplaceStockThemesAsync("035420", new[] { new ThemeCatalogRow("0200", "플랫폼") });
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null, themeKeyword: "AI");

        // Both Samsung (AI) and SK Hynix (온디바이스 AI) should match LIKE on "AI"
        result.Accounts[0].Holdings.Should().HaveCount(2);
        result.Accounts[0].Holdings.Select(h => h.Shcode).Should().BeEquivalentTo(["005930", "000660"]);
        result.MatchedThemes.Should().BeEquivalentTo(["AI", "온디바이스 AI"]);
    }

    [Fact]
    public async Task ListHoldingsAsync_ThemeCodeAndKeyword_AndCombine()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(account.Id, "000660", 5, 100000, null);
        // 005930 has both 반도체 AND AI themes.
        await repository.ReplaceStockThemesAsync("005930", new[]
        {
            new ThemeCatalogRow("0011", "반도체"),
            new ThemeCatalogRow("0100", "AI"),
        });
        // 000660 has only 반도체.
        await repository.ReplaceStockThemesAsync("000660", new[] { new ThemeCatalogRow("0011", "반도체") });
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(
            accountIdentifier: null, themeCode: "0011", themeKeyword: "AI");

        result.Accounts[0].Holdings.Should().ContainSingle()
            .Which.Shcode.Should().Be("005930", "AND-combine requires both 반도체 code AND AI keyword");
    }

    [Fact]
    public async Task ListHoldingsAsync_NoFilter_OmitsFilterEcho()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null);

        result.Filter.Should().BeNull();
        result.MatchedThemes.Should().BeNull();
        result.Accounts[0].Holdings.Should().ContainSingle();
    }

    [Fact]
    public async Task ListHoldingsAsync_FilterMatchesNothing_ReturnsEmptyButEchoesFilter()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.ReplaceStockThemesAsync("005930", new[] { new ThemeCatalogRow("0011", "반도체") });
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null, themeCode: "9999");

        result.Accounts[0].Holdings.Should().BeEmpty();
        result.Filter.Should().NotBeNull();
        result.MatchedThemes.Should().BeEmpty();
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
        readonly IReadOnlyDictionary<string, IReadOnlyList<ThemeCatalogRow>> _themesPerSymbol;
        readonly string? _stockThemesError;

        public FakeQuoteService(
            string? stockError = null,
            IReadOnlyDictionary<string, StockQuote?>? quotes = null,
            IReadOnlyDictionary<string, IReadOnlyList<ThemeCatalogRow>>? themesPerSymbol = null,
            string? stockThemesError = null)
        {
            _stockError = stockError;
            _quotes = quotes ?? new Dictionary<string, StockQuote?>();
            _themesPerSymbol = themesPerSymbol ?? new Dictionary<string, IReadOnlyList<ThemeCatalogRow>>();
            _stockThemesError = stockThemesError;
        }

        public Task<QuoteBatchResult<StockQuote>> GetStockQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken = default)
        {
            var result = symbols.Distinct(StringComparer.Ordinal).ToDictionary(
                s => s,
                s => _quotes.TryGetValue(s, out StockQuote? quote) ? quote : null,
                StringComparer.Ordinal);
            return Task.FromResult(new QuoteBatchResult<StockQuote>(result, _stockError));
        }

        public Task<QuoteBatchResult<ThemeQuote>> GetThemeQuotesAsync(IReadOnlyCollection<string> themeCodes, CancellationToken cancellationToken = default)
        {
            var result = themeCodes.Distinct(StringComparer.Ordinal).ToDictionary(
                s => s,
                _ => (ThemeQuote?)null,
                StringComparer.Ordinal);
            return Task.FromResult(new QuoteBatchResult<ThemeQuote>(result, null));
        }

        public Task<ThemeCatalogResult> GetThemeCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ThemeCatalogResult(Array.Empty<ThemeCatalogRow>(), null));

        public Task<StockThemesFetchResult> GetStockThemesAsync(string symbol, CancellationToken cancellationToken = default)
        {
            if (_stockThemesError is not null)
                return Task.FromResult(new StockThemesFetchResult(Array.Empty<ThemeCatalogRow>(), _stockThemesError));
            IReadOnlyList<ThemeCatalogRow> themes = _themesPerSymbol.TryGetValue(symbol, out IReadOnlyList<ThemeCatalogRow>? rows)
                ? rows
                : Array.Empty<ThemeCatalogRow>();
            return Task.FromResult(new StockThemesFetchResult(themes, null));
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
