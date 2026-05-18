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
        // v0.7 stores avg_price as fractional won (×10000); 1_100_000 / 15 rounds to 73333.3333.
        second.AvgPrice.Should().BeApproximately((10 * 70000 + 5 * 80000) / 15.0, 1.0 / Holding.AvgPriceScale);
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
        await repository.UpsertStockIndustryAsync("005930", "FICS 반도체 및 관련장비", "반도체 및 관련장비");
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null);

        result.MetadataFreshness!.FullyEnriched.Should().BeTrue();
        result.MetadataFreshness.Pending.Should().BeEmpty();
        result.MetadataFreshness.Hint.Should().BeNull("hint omitted when fully enriched");
    }

    [Fact]
    public async Task ListHoldingsAsync_EtfAfterEmptyEnrichment_OmitsThemesAndStaysFullyEnriched()
    {
        // v0.7 B2 + A1: ETFs return [] from t1532 (themes) and an empty
        // upgubunnm from t3320 (industry). Both write paths record a
        // "fetched-but-empty" marker so themesMap.ContainsKey and
        // industriesMap.ContainsKey are true, ListHoldingsAsync treats the
        // ETF as cache hit with no memberships, and neither freshness key
        // appears in Pending. Before the fix this row stayed perpetually
        // "pending" on the themes side, re-firing enrichment past the cooldown.
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "069500", 50, 32000, null); // KODEX 200 ETF
        await repository.ReplaceStockThemesAsync("069500", Array.Empty<ThemeCatalogRow>());
        await repository.UpsertStockIndustryAsync("069500", null, null);
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null);

        result.MetadataFreshness!.FullyEnriched.Should().BeTrue("both sentinel rows mark enrichment as completed");
        result.MetadataFreshness.Pending.Should().NotContainKey("themes");
        result.MetadataFreshness.Pending.Should().NotContainKey("industry");
        HoldingWithQuote etf = result.Accounts[0].Holdings.Single(h => h.Shcode == "069500");
        etf.Themes.Should().BeNull("theme-less symbols omit the themes array");
        etf.ThemesStatus.Should().BeNull("theme-less symbols do not report pending after enrichment");
        etf.Industry.Should().BeNull("industry-less symbols omit the industry label");
        etf.IndustryStatus.Should().BeNull("industry-less symbols do not report pending after enrichment");
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
    public async Task ListHoldingsAsync_IndustryFilter_CaseInsensitiveSubstringMatch_AndEchoesMatchedIndustries()
    {
        // v0.7 A1: industry filter matches case-insensitive substring against
        // the normalised label (FICS prefix stripped). 005930 + 000660 both
        // sit in "반도체 및 관련장비"; 035420 is "인터넷" — only the first two
        // survive industry="반도체".
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(account.Id, "000660", 5, 100000, null);
        await repository.SetHoldingAsync(account.Id, "035420", 5, 220000, null);
        await repository.UpsertStockIndustryAsync("005930", "FICS 반도체 및 관련장비", "반도체 및 관련장비");
        await repository.UpsertStockIndustryAsync("000660", "FICS 반도체 및 관련장비", "반도체 및 관련장비");
        await repository.UpsertStockIndustryAsync("035420", "FICS 인터넷", "인터넷");
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null, industry: "반도체");

        result.Accounts[0].Holdings.Should().HaveCount(2);
        result.Accounts[0].Holdings.Select(h => h.Shcode).Should().BeEquivalentTo(["005930", "000660"]);
        result.Filter.Should().NotBeNull();
        result.Filter!.Industry.Should().Be("반도체");
        result.MatchedIndustries.Should().BeEquivalentTo(["반도체 및 관련장비"]);
        // 005930 row exposes the normalised + raw labels so the model can quote either.
        HoldingWithQuote samsung = result.Accounts[0].Holdings.Single(h => h.Shcode == "005930");
        samsung.Industry.Should().Be("반도체 및 관련장비");
        samsung.IndustryRaw.Should().Be("FICS 반도체 및 관련장비");
        samsung.IndustryStatus.Should().BeNull();
    }

    [Fact]
    public async Task ListHoldingsAsync_IndustryFilter_ExcludesEtfWithEmptyIndustryRecord()
    {
        // ETF / SPAC enrichment writes a fetched-but-empty row (industry NULL,
        // industry_fetched_at populated). The industry filter must drop these
        // rows — they have no label to match — without re-firing enrichment.
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(account.Id, "069500", 50, 32000, null);
        await repository.UpsertStockIndustryAsync("005930", "FICS 반도체 및 관련장비", "반도체 및 관련장비");
        await repository.UpsertStockIndustryAsync("069500", null, null);
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null, industry: "반도체");

        result.Accounts[0].Holdings.Should().ContainSingle()
            .Which.Shcode.Should().Be("005930");
        result.MetadataFreshness!.Pending.Should().NotContainKey("industry",
            "ETF with a fetched-but-empty row is treated as enriched, not pending");
    }

    [Fact]
    public async Task ListHoldingsAsync_IndustryFilter_PendingHoldingExcludedAndCounted()
    {
        // A holding whose industry has never been fetched can't satisfy the
        // filter — it's excluded from the result *and* contributes to
        // MetadataFreshness so the model can explain the gap.
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(account.Id, "000660", 5, 100000, null);
        await repository.UpsertStockIndustryAsync("005930", "FICS 반도체 및 관련장비", "반도체 및 관련장비");
        // 000660 deliberately left without an industry row.
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(accountIdentifier: null, industry: "반도체");

        result.Accounts[0].Holdings.Should().ContainSingle()
            .Which.Shcode.Should().Be("005930");
        result.MetadataFreshness!.FullyEnriched.Should().BeFalse();
        result.MetadataFreshness.Pending.Should().ContainKey("themes",
            "themes is still pending for both rows since FakeQuoteService didn't seed any");
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

    // ---------- v0.6.0 hot-fix coverage (dedup + Fix A + Fix B) ----------

    [Fact]
    public async Task FireAndForgetEnrich_RepeatedFiresForSameSymbol_DispatchesOnlyOneFetch()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        var quoteService = new FakeQuoteService
        {
            // Block the in-flight fetch so subsequent fires see _enrichInFlight populated.
            StockThemesGate = new TaskCompletionSource(),
        };
        var service = new PortfolioService(repository, quoteService);

        // Fire 5 times in rapid succession — only the first should dispatch.
        for (int i = 0; i < 5; i++)
            service.FireAndForgetEnrich("005930");

        // Give the first Task.Run a moment to enter EnrichStockMetadataAsync.
        // We can't await the in-flight set yet because the gate holds it open.
        await Task.Delay(50);

        quoteService.GetStockThemesCallCount("005930").Should().Be(1,
            "in-flight dedup must collapse simultaneous fires for the same symbol");

        // Release the gate and let the single dispatched task finish cleanly.
        quoteService.StockThemesGate!.SetResult();
        await service.WaitForPendingEnrichmentsAsync();
    }

    [Fact]
    public async Task FireAndForgetEnrich_AfterCompletion_CooldownSkipsRefire()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        var quoteService = new FakeQuoteService();
        var service = new PortfolioService(repository, quoteService);

        service.FireAndForgetEnrich("005930");
        await service.WaitForPendingEnrichmentsAsync();
        int afterFirst = quoteService.GetStockThemesCallCount("005930");

        // Immediately re-fire — well within the 60s cooldown window.
        service.FireAndForgetEnrich("005930");
        await service.WaitForPendingEnrichmentsAsync();

        afterFirst.Should().Be(1);
        quoteService.GetStockThemesCallCount("005930").Should().Be(1,
            "the cooldown must skip a refire that lands within EnrichCooldown of the previous completion");
    }

    [Fact]
    public async Task ImportPortfolioAsync_FiresEnrichmentForImportedSymbols()
    {
        // Phase 1: build an export file from a populated DB.
        await using TestDatabase source = new();
        var sourceRepo = new SqlitePortfolioRepository(source.Path);
        await sourceRepo.InitializeAsync();
        Account acc = await sourceRepo.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await sourceRepo.SetHoldingAsync(acc.Id, "005930", 10, 70000, null);
        await sourceRepo.SetHoldingAsync(acc.Id, "000660", 5, 100000, null);
        var sourceService = new PortfolioService(sourceRepo, new FakeQuoteService());
        string exportPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source.Path)!, "export.json");
        await sourceService.ExportPortfolioAsync(exportPath);

        // Phase 2: import into a fresh DB with a counting FakeQuoteService.
        await using TestDatabase target = new();
        var targetRepo = new SqlitePortfolioRepository(target.Path);
        await targetRepo.InitializeAsync();
        var targetQuotes = new FakeQuoteService(themesPerSymbol: new Dictionary<string, IReadOnlyList<ThemeCatalogRow>>(StringComparer.Ordinal)
        {
            ["005930"] = new[] { new ThemeCatalogRow("0011", "반도체") },
            ["000660"] = new[] { new ThemeCatalogRow("0011", "반도체") },
        });
        var targetService = new PortfolioService(targetRepo, targetQuotes);

        await targetService.ImportPortfolioAsync(exportPath, "merge", confirm: false);
        await targetService.WaitForPendingEnrichmentsAsync();

        targetQuotes.GetStockThemesCallCount("005930").Should().Be(1,
            "import must dispatch fire-and-forget enrichment for each imported holding symbol");
        targetQuotes.GetStockThemesCallCount("000660").Should().Be(1);

        // Verify the cache actually got populated.
        var cached = await targetRepo.GetStockThemesBatchAsync(new[] { "005930", "000660" });
        cached.Should().ContainKeys("005930", "000660");
    }

    [Fact]
    public async Task ListHoldingsAsync_CacheMissOnUnenrichedHoldings_DispatchesLazyEnrichment()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        // Insert holdings directly via the repo — no FireAndForgetEnrich
        // dispatch from the service. Simulates pre-existing DB state.
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(account.Id, "000660", 5, 100000, null);
        var quoteService = new FakeQuoteService(themesPerSymbol: new Dictionary<string, IReadOnlyList<ThemeCatalogRow>>(StringComparer.Ordinal)
        {
            ["005930"] = new[] { new ThemeCatalogRow("0011", "반도체") },
            ["000660"] = new[] { new ThemeCatalogRow("0011", "반도체") },
        });
        var service = new PortfolioService(repository, quoteService);
        // Reset the counter that SetHoldingAsync did NOT touch (this repo
        // doesn't fire-and-forget; the assertion is that *listing* triggers).
        quoteService.TotalGetStockThemesCalls.Should().Be(0, "sanity: no dispatches before listing");

        await service.ListHoldingsAsync(accountIdentifier: null);
        await service.WaitForPendingEnrichmentsAsync();

        quoteService.GetStockThemesCallCount("005930").Should().Be(1,
            "list must dispatch enrichment for each holding missing from the stock_themes cache");
        quoteService.GetStockThemesCallCount("000660").Should().Be(1);
    }

    [Fact]
    public async Task ListHoldingsAsync_ThemeFilterWithEmptyCache_ReportsNotFullyEnriched()
    {
        // Regression for Test_v0.6.0 finding: metadata_freshness was reporting
        // fully_enriched=true when every holding was filtered out for cache
        // miss, contradicting the empty matched_themes alongside it.
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await repository.SetHoldingAsync(account.Id, "000660", 5, 100000, null);
        // No ReplaceStockThemesAsync — stock_themes is empty for both symbols.
        var service = new PortfolioService(repository, new FakeQuoteService());

        HoldingListResult result = await service.ListHoldingsAsync(
            accountIdentifier: null, themeKeyword: "반도체");

        result.Accounts[0].Holdings.Should().BeEmpty("filter drops every cache-miss holding");
        result.MatchedThemes.Should().BeEmpty();
        result.MetadataFreshness.Should().NotBeNull();
        result.MetadataFreshness!.FullyEnriched.Should().BeFalse(
            "two holdings are missing the cache; freshness must NOT claim fully enriched");
        result.MetadataFreshness.Pending.Should().ContainKey("themes")
            .WhoseValue.Should().Be(2, "both cache misses should count toward pending");
    }

    sealed class FakeQuoteService : IQuoteService
    {
        readonly string? _stockError;
        readonly IReadOnlyDictionary<string, StockQuote?> _quotes;
        readonly IReadOnlyDictionary<string, IReadOnlyList<ThemeCatalogRow>> _themesPerSymbol;
        readonly string? _stockThemesError;
        readonly IReadOnlyDictionary<string, string?> _industryPerSymbol;

        readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _getStockThemesCalls = new(StringComparer.Ordinal);
        readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _getStockIndustryCalls = new(StringComparer.Ordinal);
        /// <summary>Optional gate — when set, GetStockThemesAsync blocks until completed. Used by dedup tests.</summary>
        public TaskCompletionSource? StockThemesGate { get; set; }

        public FakeQuoteService(
            string? stockError = null,
            IReadOnlyDictionary<string, StockQuote?>? quotes = null,
            IReadOnlyDictionary<string, IReadOnlyList<ThemeCatalogRow>>? themesPerSymbol = null,
            string? stockThemesError = null,
            IReadOnlyDictionary<string, string?>? industryPerSymbol = null)
        {
            _stockError = stockError;
            _quotes = quotes ?? new Dictionary<string, StockQuote?>();
            _themesPerSymbol = themesPerSymbol ?? new Dictionary<string, IReadOnlyList<ThemeCatalogRow>>();
            _stockThemesError = stockThemesError;
            _industryPerSymbol = industryPerSymbol ?? new Dictionary<string, string?>();
        }

        public int GetStockThemesCallCount(string symbol) =>
            _getStockThemesCalls.TryGetValue(symbol, out int n) ? n : 0;
        public int TotalGetStockThemesCalls => _getStockThemesCalls.Values.Sum();
        public int GetStockIndustryCallCount(string symbol) =>
            _getStockIndustryCalls.TryGetValue(symbol, out int n) ? n : 0;

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

        public async Task<StockThemesFetchResult> GetStockThemesAsync(string symbol, CancellationToken cancellationToken = default)
        {
            _getStockThemesCalls.AddOrUpdate(symbol, 1, (_, v) => v + 1);
            if (StockThemesGate is { } gate)
                await gate.Task.ConfigureAwait(false);
            if (_stockThemesError is not null)
                return new StockThemesFetchResult(Array.Empty<ThemeCatalogRow>(), _stockThemesError);
            IReadOnlyList<ThemeCatalogRow> themes = _themesPerSymbol.TryGetValue(symbol, out IReadOnlyList<ThemeCatalogRow>? rows)
                ? rows
                : Array.Empty<ThemeCatalogRow>();
            return new StockThemesFetchResult(themes, null);
        }

        public Task<StockIndustryFetchResult> GetStockIndustryAsync(string symbol, CancellationToken cancellationToken = default)
        {
            _getStockIndustryCalls.AddOrUpdate(symbol, 1, (_, v) => v + 1);
            // The dictionary maps shcode → t3320 upgubunnm raw value. Null entry
            // models an ETF/SPAC response (rsp_cd=00000 but empty profile); a
            // missing key behaves identically — both yield (null, null, null).
            if (!_industryPerSymbol.TryGetValue(symbol, out string? raw) || string.IsNullOrEmpty(raw))
                return Task.FromResult(new StockIndustryFetchResult(null, null, null));
            string normalized = LsQuoteService.NormalizeFicsIndustry(raw);
            return Task.FromResult(new StockIndustryFetchResult(raw, normalized, null));
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
