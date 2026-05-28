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
    public async Task Accounts_AreFilteredByConfiguredMode()
    {
        await using TestDatabase db = new();
        var realRepository = new SqlitePortfolioRepository(db.Path, "real");
        var virtualRepository = new SqlitePortfolioRepository(db.Path, "virtual");
        await realRepository.InitializeAsync();

        await realRepository.UpsertAccountAsync("REAL-01", "주식", null, setDefault: false);
        await virtualRepository.UpsertAccountAsync("VIRT-01", "모의", null, setDefault: false);

        IReadOnlyList<AccountSummary> realAccounts = await realRepository.ListAccountSummariesAsync();
        IReadOnlyList<AccountSummary> virtualAccounts = await virtualRepository.ListAccountSummariesAsync();

        realAccounts.Should().ContainSingle(a => a.AccountNumber == "REAL-01" && a.Mode == "real" && a.IsDefault);
        virtualAccounts.Should().ContainSingle(a => a.AccountNumber == "VIRT-01" && a.Mode == "virtual" && a.IsDefault);
        (await realRepository.GetAccountByIdentifierAsync("모의")).Should().BeNull();
        (await virtualRepository.GetAccountByIdentifierAsync("주식")).Should().BeNull();
    }

    [Fact]
    public async Task Accounts_DefaultIsIndependentPerMode()
    {
        await using TestDatabase db = new();
        var realRepository = new SqlitePortfolioRepository(db.Path, "real");
        var virtualRepository = new SqlitePortfolioRepository(db.Path, "virtual");
        await realRepository.InitializeAsync();

        await realRepository.UpsertAccountAsync("REAL-01", "real-main", null, setDefault: false);
        await realRepository.UpsertAccountAsync("REAL-02", "real-sub", null, setDefault: true);
        await virtualRepository.UpsertAccountAsync("VIRT-01", "virtual-main", null, setDefault: false);

        (await realRepository.GetDefaultAccountAsync())!.AccountNo.Should().Be("REAL-02");
        (await virtualRepository.GetDefaultAccountAsync())!.AccountNo.Should().Be("VIRT-01");
    }

    [Fact]
    public async Task Holdings_AreFilteredByConfiguredMode()
    {
        await using TestDatabase db = new();
        var realRepository = new SqlitePortfolioRepository(db.Path, "real");
        var virtualRepository = new SqlitePortfolioRepository(db.Path, "virtual");
        await realRepository.InitializeAsync();

        Account real = await realRepository.UpsertAccountAsync("REAL-01", "real-main", null, setDefault: false);
        Account virtualAccount = await virtualRepository.UpsertAccountAsync("VIRT-01", "virtual-main", null, setDefault: false);
        await realRepository.SetHoldingAsync(real.Id, "005930", 3, 70000, null);
        await virtualRepository.SetHoldingAsync(virtualAccount.Id, "000660", 5, 120000, null);

        (await realRepository.ListAllHoldingsAsync()).Should().ContainSingle(h => h.Symbol == "005930");
        (await virtualRepository.ListAllHoldingsAsync()).Should().ContainSingle(h => h.Symbol == "000660");
        (await realRepository.FindAccountsHoldingAsync("000660")).Should().BeEmpty();
        (await virtualRepository.FindAccountsHoldingAsync("005930")).Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertAccountAsync_RejectsCrossModeAccountNoCollision()
    {
        // Migration v6 keeps account_no UNIQUE column-level (table-rebuild avoided);
        // cross-mode upserts must surface as explicit errors, not silent mode flips.
        await using TestDatabase db = new();
        var realRepository = new SqlitePortfolioRepository(db.Path, "real");
        var virtualRepository = new SqlitePortfolioRepository(db.Path, "virtual");
        await realRepository.InitializeAsync();

        await realRepository.UpsertAccountAsync("SHARED-01", "주식", null, setDefault: false);

        Func<Task> act = () => virtualRepository.UpsertAccountAsync("SHARED-01", "모의", null, setDefault: false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*mode='real'*");

        // Original row stays real-mode and visible in real mode only.
        Account? stillReal = await realRepository.GetAccountByIdentifierAsync("SHARED-01");
        stillReal!.Mode.Should().Be("real");
        (await virtualRepository.ListAccountsAsync()).Should().BeEmpty();
    }

    [Theory]
    [InlineData("virtual", "virtual")]
    [InlineData("paper", "virtual")]
    [InlineData("mock", "virtual")]
    [InlineData("sandbox", "virtual")]
    [InlineData("test", "virtual")]
    [InlineData("real", "real")]
    [InlineData("prod", "real")]
    [InlineData("production", "real")]
    [InlineData("live", "real")]
    [InlineData("", "real")]
    [InlineData(null, "real")]
    [InlineData("garbage", "real")]
    public void NormalizeAccountMode_AcceptsLsMarketAliases(string? input, string expected)
    {
        // Repository delegates to LsMarketExtensions.Parse so the credentials
        // resolver and the account repository agree on the same canonical mode.
        SqlitePortfolioRepository.NormalizeAccountMode(input).Should().Be(expected);
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
        // v0.7 stores avg_price as fractional won (×10000); 1_100_000 / 15 rounds to 73333.3333.
        second.AvgPrice.Should().BeApproximately((10 * 70000 + 5 * 80000) / 15.0, 1.0 / Holding.AvgPriceScale);
    }

    [Fact]
    public async Task ApplyCorporateActionAsync_AppliesSplitMath()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 2500000, null);

        Holding? after = await repository.ApplyCorporateActionAsync(account.Id, "005930", qtyNum: 50, qtyDen: 1);

        after!.Quantity.Should().Be(500);
        after.AvgPrice.Should().Be(50000);
    }

    [Fact]
    public async Task ApplyCorporateActionAsync_RejectsNonDivisibleReverseSplit()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 7, 50000, null);

        Func<Task> act = () => repository.ApplyCorporateActionAsync(account.Id, "005930", qtyNum: 1, qtyDen: 3);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        Holding? unchanged = await repository.GetHoldingAsync(account.Id, "005930");
        unchanged!.Quantity.Should().Be(7);
    }

    [Fact]
    public async Task CorporateAction_SplitReverseSplitRoundTrip_ExactInIntegerStorage()
    {
        // v0.7 B1 fix: v0.6 stored avg_price as REAL so 1_003_502 → split(10) →
        // reverse_split(10) drifted by 1e-10. v0.7 stores fractional won as INTEGER
        // and uses rational math, so identical round-trips are exact.
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 100, 1_003_502, null);

        Holding? afterSplit = await repository.ApplyCorporateActionAsync(account.Id, "005930", qtyNum: 10, qtyDen: 1);
        afterSplit!.Quantity.Should().Be(1000);
        afterSplit.AvgPrice.Should().Be(100_350.2);

        Holding? afterReverse = await repository.ApplyCorporateActionAsync(account.Id, "005930", qtyNum: 1, qtyDen: 10);
        afterReverse!.Quantity.Should().Be(100);
        afterReverse.AvgPrice.Should().Be(1_003_502, "integer fractional-won storage round-trips split↔reverse_split exactly");
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
    public async Task ReplaceStockThemesAsync_ReplacesPriorRowsAtomically()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        // First write: 3 themes.
        await repository.ReplaceStockThemesAsync("005930", new[]
        {
            new ThemeCatalogRow("0011", "반도체"),
            new ThemeCatalogRow("0100", "AI"),
            new ThemeCatalogRow("0042", "스마트폰"),
        });
        IReadOnlyDictionary<string, IReadOnlyList<StockTheme>> after1 =
            await repository.GetStockThemesBatchAsync(new[] { "005930" });
        after1["005930"].Should().HaveCount(3);

        // Second write: only 2 (one removed, one new).
        await repository.ReplaceStockThemesAsync("005930", new[]
        {
            new ThemeCatalogRow("0100", "AI"),
            new ThemeCatalogRow("0500", "온디바이스 AI"),
        });
        IReadOnlyDictionary<string, IReadOnlyList<StockTheme>> after2 =
            await repository.GetStockThemesBatchAsync(new[] { "005930" });
        after2["005930"].Should().HaveCount(2, "REPLACE keeps the cache in sync with the latest fetch — stale memberships disappear");
        after2["005930"].Select(t => t.ThemeCode).Should().BeEquivalentTo(["0100", "0500"]);
    }

    [Fact]
    public async Task ReplaceStockThemesAsync_EmptyArray_KeepsSymbolInBatchResultWithEmptyList()
    {
        // v0.7 B2: ETFs return [] from t1532. A sentinel row records the fetch
        // so themesMap.ContainsKey is true (no perpetual re-dispatch) while the
        // projection layer still surfaces the symbol with an empty theme list.
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        await repository.ReplaceStockThemesAsync("069500", Array.Empty<ThemeCatalogRow>());

        IReadOnlyDictionary<string, IReadOnlyList<StockTheme>> result =
            await repository.GetStockThemesBatchAsync(new[] { "069500" });

        result.Should().ContainKey("069500", "sentinel row keeps the symbol in the cache hit set");
        result["069500"].Should().BeEmpty("sentinel is stripped from the returned list — caller sees an empty membership set");
    }

    [Fact]
    public async Task UpsertStockIndustryAsync_PersistsLabelsAndFetchedAt()
    {
        // v0.7 A1: writing the industry triple should produce a cache row that
        // batch reads can find. The normalised label is the one filters match
        // against; the raw label survives for audit.
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        await repository.UpsertStockIndustryAsync("005930", "FICS 반도체 및 관련장비", "반도체 및 관련장비");

        IReadOnlyDictionary<string, StockIndustryRow> batch =
            await repository.GetStockIndustriesBatchAsync(new[] { "005930" });
        batch.Should().ContainKey("005930");
        batch["005930"].IndustryRaw.Should().Be("FICS 반도체 및 관련장비");
        batch["005930"].Industry.Should().Be("반도체 및 관련장비");
        batch["005930"].IndustryFetchedAt.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UpsertStockIndustryAsync_EmptyResult_StillSetsFetchedAtSoRetriesStop()
    {
        // ETF / SPAC: t3320 reports rsp_cd=00000 with an empty OutBlock. We
        // persist a fetched-but-empty row so the read path counts this as
        // cache hit (no industry, no pending) instead of dispatching forever.
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();

        await repository.UpsertStockIndustryAsync("069500", null, null);

        IReadOnlyDictionary<string, StockIndustryRow> batch =
            await repository.GetStockIndustriesBatchAsync(new[] { "069500" });
        batch.Should().ContainKey("069500", "fetched-but-empty rows must remain visible to the batch reader so callers stop re-dispatching");
        batch["069500"].Industry.Should().BeNull();
        batch["069500"].IndustryRaw.Should().BeNull();
        batch["069500"].IndustryFetchedAt.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetStockIndustriesBatchAsync_OmitsSymbolsWithNeverFetchedRecords()
    {
        // Default placeholder rows (created by EnsureStockAsync on holdings
        // write) have industry_fetched_at NULL until enrichment lands. They
        // should NOT appear in the batch result — callers treat absent as
        // "pending".
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);

        IReadOnlyDictionary<string, StockIndustryRow> batch =
            await repository.GetStockIndustriesBatchAsync(new[] { "005930" });

        batch.Should().BeEmpty("symbols whose industry has never been fetched are absent from the dictionary");
    }

    [Fact]
    public async Task ReplaceStockThemesAsync_EmptyAfterReal_SwitchesSymbolToSentinel()
    {
        // Defensive: a real-themes fetch followed by an empty refetch (rare —
        // e.g. theme catalog re-organisation) should leave the symbol cached
        // with an empty list, not the stale memberships.
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.ReplaceStockThemesAsync("005930", new[] { new ThemeCatalogRow("0011", "반도체") });

        await repository.ReplaceStockThemesAsync("005930", Array.Empty<ThemeCatalogRow>());

        IReadOnlyDictionary<string, IReadOnlyList<StockTheme>> result =
            await repository.GetStockThemesBatchAsync(new[] { "005930" });
        result.Should().ContainKey("005930");
        result["005930"].Should().BeEmpty();
    }

    [Fact]
    public async Task GetStockThemesBatchAsync_GroupsBySymbolAndOmitsAbsentSymbols()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.ReplaceStockThemesAsync("005930", new[] { new ThemeCatalogRow("0011", "반도체") });
        await repository.ReplaceStockThemesAsync("000660", new[]
        {
            new ThemeCatalogRow("0011", "반도체"),
            new ThemeCatalogRow("0100", "AI"),
        });

        IReadOnlyDictionary<string, IReadOnlyList<StockTheme>> result =
            await repository.GetStockThemesBatchAsync(new[] { "005930", "000660", "035420" });

        result.Should().HaveCount(2, "035420 has no theme rows yet — absent from result dict");
        result["005930"].Should().ContainSingle();
        result["000660"].Should().HaveCount(2);
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
