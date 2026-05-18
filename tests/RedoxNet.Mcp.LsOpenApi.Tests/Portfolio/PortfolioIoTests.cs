using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Portfolio;

public sealed class PortfolioIoTests
{
    [Fact]
    public async Task ExportPortfolioAsync_SerializesAllDomainsWithCorrectCounts()
    {
        await using TestEnvironment env = new();
        Account account = await env.Repository.UpsertAccountAsync("AAA", "한투", "한국투자", setDefault: false);
        await env.Repository.SetHoldingAsync(account.Id, "005930", 10, 70000, "core");
        await env.Repository.SetHoldingAsync(account.Id, "000660", 5, 100000, null);
        await env.Repository.CreateGroupAsync("semis", "반도체");
        await env.Repository.AddWatchlistItemAsync("005930", "semis", "watch", CancellationToken.None);
        await env.Repository.WatchThemeAsync("0064", "2차전지", "watch this", CancellationToken.None);

        string outputPath = Path.Combine(env.TempDir, "export.json");
        PortfolioExportResult result = await env.Service.ExportPortfolioAsync(outputPath);

        result.Path.Should().Be(outputPath);
        result.SchemaVersion.Should().Be(1);
        result.Counts.Accounts.Should().Be(1);
        result.Counts.Holdings.Should().Be(2);
        result.Counts.WatchlistGroups.Should().Be(2, "the seeded 'default' group counts as an exported row");
        result.Counts.WatchlistItems.Should().Be(1);
        result.Counts.WatchedThemes.Should().Be(1);
        result.SizeBytes.Should().BeGreaterThan(0);
        File.Exists(outputPath).Should().BeTrue();

        // Inspect the persisted JSON shape.
        string raw = await File.ReadAllTextAsync(outputPath);
        JsonElement root = JsonDocument.Parse(raw).RootElement;
        root.GetProperty("schema_version").GetInt32().Should().Be(1);
        root.GetProperty("accounts").GetArrayLength().Should().Be(1);
        JsonElement firstAccount = root.GetProperty("accounts")[0];
        firstAccount.GetProperty("account_number").GetString().Should().Be("AAA");
        firstAccount.GetProperty("nickname").GetString().Should().Be("한투");
        firstAccount.GetProperty("holdings").GetArrayLength().Should().Be(2);
        root.GetProperty("watched_themes")[0].GetProperty("theme_code").GetString().Should().Be("0064");
    }

    [Fact]
    public async Task ImportPortfolioAsync_RoundTripsAllDomains()
    {
        // First DB: seed + export
        await using TestEnvironment source = new();
        Account account = await source.Repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        await source.Repository.SetHoldingAsync(account.Id, "005930", 10, 70000, "core");
        await source.Repository.CreateGroupAsync("semis", null);
        await source.Repository.AddWatchlistItemAsync("005930", "semis", null, CancellationToken.None);
        await source.Repository.WatchThemeAsync("0064", "2차전지", null, CancellationToken.None);
        string exportPath = Path.Combine(source.TempDir, "export.json");
        await source.Service.ExportPortfolioAsync(exportPath);

        // Second DB: empty target → import (merge)
        await using TestEnvironment target = new();
        PortfolioImportResult result = await target.Service.ImportPortfolioAsync(exportPath, "merge", confirm: false);

        result.Mode.Should().Be("merge");
        result.SchemaVersion.Should().Be(1);
        result.Imported.Accounts.Should().Be(1);
        result.Imported.Holdings.Should().Be(1);
        result.Imported.WatchedThemes.Should().Be(1);
        result.AutoBackupPath.Should().BeNull("merge mode does not snapshot");

        (await target.Repository.ListAccountSummariesAsync()).Should().ContainSingle(a => a.AccountNumber == "AAA");
        (await target.Repository.ListThemesAsync()).Should().ContainSingle(t => t.ThemeCode == "0064");
    }

    [Fact]
    public async Task ImportPortfolioAsync_MergeMode_DuplicatesAppearInSkipped()
    {
        await using TestEnvironment env = new();
        Account account = await env.Repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        await env.Repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        await env.Repository.WatchThemeAsync("0064", "2차전지", null, CancellationToken.None);
        string path = Path.Combine(env.TempDir, "export.json");
        await env.Service.ExportPortfolioAsync(path);

        // Re-import into the SAME db → every row is a duplicate.
        PortfolioImportResult result = await env.Service.ImportPortfolioAsync(path, "merge", confirm: false);

        result.Imported.Accounts.Should().Be(0);
        result.Imported.WatchedThemes.Should().Be(0);
        result.Skipped.Accounts.Should().ContainSingle().Which.Reason.Should().Be("duplicate_account_number");
        result.Skipped.WatchedThemes.Should().ContainSingle().Which.Reason.Should().Be("duplicate_theme_code");
    }

    [Fact]
    public async Task ImportPortfolioAsync_ReplaceWithoutConfirm_Throws()
    {
        await using TestEnvironment source = new();
        Account account = await source.Repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        await source.Repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);
        string path = Path.Combine(source.TempDir, "export.json");
        await source.Service.ExportPortfolioAsync(path);

        await using TestEnvironment target = new();
        Func<Task> act = () => target.Service.ImportPortfolioAsync(path, "replace", confirm: false);

        await act.Should().ThrowAsync<ImportReplaceRequiresConfirmationException>();
    }

    [Fact]
    public async Task ImportPortfolioAsync_ReplaceWithConfirm_WipesAndAutoBackupsExistingState()
    {
        // Source: seed + export
        await using TestEnvironment source = new();
        Account srcAccount = await source.Repository.UpsertAccountAsync("AAA", "import-target", null, setDefault: false);
        await source.Repository.SetHoldingAsync(srcAccount.Id, "005930", 10, 70000, null);
        string exportPath = Path.Combine(source.TempDir, "export.json");
        await source.Service.ExportPortfolioAsync(exportPath);

        // Target: has DIFFERENT pre-existing data that replace should wipe.
        await using TestEnvironment target = new();
        Account oldAccount = await target.Repository.UpsertAccountAsync("OLD", "pre-existing", null, setDefault: false);
        await target.Repository.SetHoldingAsync(oldAccount.Id, "035420", 7, 200000, null);

        PortfolioImportResult result = await target.Service.ImportPortfolioAsync(exportPath, "replace", confirm: true);

        result.Mode.Should().Be("replace");
        result.AutoBackupPath.Should().NotBeNull();
        File.Exists(result.AutoBackupPath!).Should().BeTrue("replace mode writes a before-import snapshot");

        // The pre-existing OLD account must be gone; only AAA from the file survives.
        IReadOnlyList<AccountSummary> after = await target.Repository.ListAccountSummariesAsync();
        after.Should().ContainSingle().Which.AccountNumber.Should().Be("AAA");
    }

    [Fact]
    public async Task ImportPortfolioAsync_UnsupportedSchemaVersion_Throws()
    {
        await using TestEnvironment env = new();
        string path = Path.Combine(env.TempDir, "bad-schema.json");
        await File.WriteAllTextAsync(path, """
            {
              "schema_version": 99,
              "exported_at": "2026-05-15T12:34:56+09:00",
              "exporter_version": "future",
              "accounts": [],
              "watchlist_groups": [],
              "watched_themes": []
            }
            """);

        Func<Task> act = () => env.Service.ImportPortfolioAsync(path, "merge", confirm: false);

        ImportSchemaMismatchException ex = (await act.Should().ThrowAsync<ImportSchemaMismatchException>()).Subject.First();
        ex.FileSchemaVersion.Should().Be(99);
        ex.SupportedSchemaVersion.Should().Be(1);
    }

    [Fact]
    public async Task ImportPortfolioAsync_MissingFile_ThrowsValidationError()
    {
        await using TestEnvironment env = new();
        Func<Task> act = () => env.Service.ImportPortfolioAsync(
            Path.Combine(env.TempDir, "does-not-exist.json"), "merge", confirm: false);

        await act.Should().ThrowAsync<PortfolioValidationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task ExportPortfolioAsync_DefaultPath_WritesUnderResolvedExportsDir()
    {
        // Override the DB path so the default exports dir lives under our temp dir.
        await using TestEnvironment env = new();
        string? previous = Environment.GetEnvironmentVariable(SqlitePortfolioRepository.DatabasePathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(SqlitePortfolioRepository.DatabasePathEnvVar, env.DbPath);

            PortfolioExportResult result = await env.Service.ExportPortfolioAsync(path: null);

            result.Path.Should().StartWith(Path.Combine(env.TempDir, "exports"));
            File.Exists(result.Path).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(SqlitePortfolioRepository.DatabasePathEnvVar, previous);
        }
    }

    sealed class TestEnvironment : IAsyncDisposable
    {
        public string TempDir { get; }
        public string DbPath { get; }
        public SqlitePortfolioRepository Repository { get; }
        public PortfolioService Service { get; }

        public TestEnvironment()
        {
            TempDir = Path.Combine(Path.GetTempPath(), "mcp-lsopenapi-io-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);
            DbPath = Path.Combine(TempDir, "portfolio.db");
            Repository = new SqlitePortfolioRepository(DbPath);
            Service = new PortfolioService(Repository, new NoopQuoteService());
            // Eagerly initialize to keep test setup synchronous.
            Repository.InitializeAsync().GetAwaiter().GetResult();
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(TempDir))
                    Directory.Delete(TempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; SQLite WAL handles may linger briefly on Windows.
            }
            return ValueTask.CompletedTask;
        }
    }

    sealed class NoopQuoteService : IQuoteService
    {
        public Task<QuoteBatchResult<StockQuote>> GetStockQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuoteBatchResult<StockQuote>(new Dictionary<string, StockQuote?>(), null));

        public Task<QuoteBatchResult<ThemeQuote>> GetThemeQuotesAsync(IReadOnlyCollection<string> themeCodes, CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuoteBatchResult<ThemeQuote>(new Dictionary<string, ThemeQuote?>(), null));

        public Task<ThemeCatalogResult> GetThemeCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ThemeCatalogResult(Array.Empty<ThemeCatalogRow>(), null));

        public Task<StockThemesFetchResult> GetStockThemesAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StockThemesFetchResult(Array.Empty<ThemeCatalogRow>(), null));
        public Task<StockIndustryFetchResult> GetStockIndustryAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StockIndustryFetchResult(null, null, null));
    }
}
