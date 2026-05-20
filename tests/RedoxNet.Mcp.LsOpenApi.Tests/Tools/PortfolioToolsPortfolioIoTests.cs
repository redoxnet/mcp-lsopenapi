using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers the v0.10 <c>ls_portfolio_io</c> domain dispatcher
/// (SPEC-v0.10 §2.6.4): ls_portfolio_export / ls_portfolio_import folded
/// into one action-routed tool. Round-trip math is tested in
/// PortfolioIoTests; here we exercise routing, the import confirm gate,
/// and per-action argument validation.
/// </summary>
public sealed class PortfolioToolsPortfolioIoTests
{
    [Fact]
    public async Task PortfolioIo_Export_WritesVersionedJsonFile()
    {
        await using TestEnvironment env = new();
        await env.Repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        string path = Path.Combine(env.TempDir, "export.json");

        string result = await PortfolioTools.PortfolioIo(env.Service, action: "export", path: path);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("schema_version").GetInt32().Should().Be(1);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task PortfolioIo_ImportMerge_ReimportFindsDuplicates()
    {
        await using TestEnvironment env = new();
        await env.Repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        string path = Path.Combine(env.TempDir, "export.json");
        await PortfolioTools.PortfolioIo(env.Service, action: "export", path: path);

        string result = await PortfolioTools.PortfolioIo(env.Service, action: "import", path: path);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("mode").GetString().Should().Be("merge");
        root.GetProperty("imported").GetProperty("accounts").GetInt32().Should().Be(0, "the account already exists");
    }

    [Fact]
    public async Task PortfolioIo_ImportReplace_WithoutConfirm_IsGated()
    {
        await using TestEnvironment env = new();
        await env.Repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);
        string path = Path.Combine(env.TempDir, "export.json");
        await PortfolioTools.PortfolioIo(env.Service, action: "export", path: path);

        string result = await PortfolioTools.PortfolioIo(
            env.Service, action: "import", path: path, mode: "replace", confirm: false);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Be("RequiresConfirmation");
    }

    [Fact]
    public async Task PortfolioIo_Import_MissingPath_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.PortfolioIo(env.Service, action: "import");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("path");
        root.GetProperty("details").GetProperty("action").GetString().Should().Be("import");
    }

    [Fact]
    public async Task PortfolioIo_UnknownAction_ReturnsValidationErrorWithValidActions()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.PortfolioIo(env.Service, action: "sync");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("unknown action");
        root.GetProperty("details").GetProperty("valid_actions").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(new[] { "export", "import" });
    }

    sealed class TestEnvironment : IAsyncDisposable
    {
        public string TempDir { get; }
        public SqlitePortfolioRepository Repository { get; }
        public PortfolioService Service { get; }

        public TestEnvironment()
        {
            TempDir = Path.Combine(Path.GetTempPath(), "mcp-lsopenapi-pio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);
            Repository = new SqlitePortfolioRepository(Path.Combine(TempDir, "portfolio.db"));
            Service = new PortfolioService(Repository, new NoopQuoteService());
            Repository.InitializeAsync().GetAwaiter().GetResult();
        }

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
            catch { /* best-effort */ }
            return ValueTask.CompletedTask;
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
}
