using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="AccountInquiryTools.Holdings"/> (TR t0424) against a
/// real-shaped sample response. The fixture is adapted from the LS docs
/// example in <c>todo/[주식] 계좌_t0424.txt</c> so we exercise the actual
/// wire format — including the numeric-string <c>sunikrt</c> / <c>janrt</c>
/// quirk and the 한투 / KOSPI / KOSDAQ category mapping.
/// </summary>
public sealed class AccountHoldingsToolTests
{
    const string SampleT0424Response = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회가 완료되었습니다.",
          "t0424OutBlock": {
            "dtsunik": 0,
            "cts_expcode": "",
            "mamt": 120013,
            "sunamt1": 80000000,
            "tappamt": 150283,
            "sunamt": 80030265,
            "tdtsunik": 30270
          },
          "t0424OutBlock1": [
            {
              "sininter": 0,
              "fee": 30,
              "mamt": 120000,
              "sinamt": 0,
              "mpmd": 0,
              "mdposqt": 2,
              "jsat": 0,
              "janqty": 2,
              "loandt": "",
              "sysprocseq": 4,
              "price": 75300,
              "janrt": "100.00",
              "jdat": 0,
              "jpms": 0,
              "hname": "삼성전자",
              "appamt": 150283,
              "sunikrt": "25.22",
              "jonggb": "3",
              "msat": 2,
              "tax": 300,
              "pamt": 60000,
              "jpmd": 0,
              "marketgb": "1",
              "jangb": "",
              "dtsunik": 30270,
              "expcode": "005930",
              "mdat": 0,
              "mpms": 60000,
              "lastdt": ""
            }
          ]
        }
        """;

    [Fact]
    public async Task Holdings_ParsesSampleResponse_AndAttachesMeta()
    {
        await using AccountScratch scratch = new();
        await scratch.SeedDefaultAccount("12345-01", "주식");

        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleT0424Response, System.Text.Encoding.UTF8, "application/json"),
            }));

        string result = await AccountInquiryTools.Holdings(client, scratch.Resolver);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("estimated_net_assets").GetInt64().Should().Be(80030265);
        summary.GetProperty("purchase_amount").GetInt64().Should().Be(120013);
        summary.GetProperty("total_evaluation").GetInt64().Should().Be(150283);
        summary.GetProperty("total_evaluation_pnl").GetInt64().Should().Be(30270);
        summary.GetProperty("estimated_d2_deposit").GetInt64().Should().Be(80000000);

        root.GetProperty("count").GetInt32().Should().Be(1);
        JsonElement row = root.GetProperty("holdings")[0];
        row.GetProperty("symbol").GetString().Should().Be("005930");
        row.GetProperty("name").GetString().Should().Be("삼성전자");
        row.GetProperty("quantity").GetInt64().Should().Be(2);
        row.GetProperty("sellable_quantity").GetInt64().Should().Be(2);
        row.GetProperty("average_price").GetInt64().Should().Be(60000);
        row.GetProperty("current_price").GetInt64().Should().Be(75300);
        row.GetProperty("evaluation_amount").GetInt64().Should().Be(150283);
        row.GetProperty("evaluation_pnl").GetInt64().Should().Be(30270);
        // sunikrt arrived as the string "25.22" — defensive reader must parse it.
        row.GetProperty("evaluation_pnl_pct").GetDouble().Should().BeApproximately(25.22, 0.001);
        row.GetProperty("holding_weight").GetDouble().Should().BeApproximately(100.0, 0.001);
        row.GetProperty("market_category").GetString().Should().Be("stock");
        row.GetProperty("symbol_category").GetString().Should().Be("kospi");

        JsonElement meta = root.GetProperty("_meta");
        meta.GetProperty("tr_code").GetString().Should().Be("t0424");
        meta.GetProperty("source").GetString().Should().Be("live");
        meta.GetProperty("data_as_of").GetString().Should().NotBeNullOrEmpty();
        JsonElement accountUsed = meta.GetProperty("account_used");
        accountUsed.GetProperty("account_number").GetString().Should().Be("12345-01");
        accountUsed.GetProperty("nickname").GetString().Should().Be("주식");
        accountUsed.GetProperty("mode").GetString().Should().Be("real");
    }

    [Fact]
    public async Task Holdings_ReturnsRequiresAccountWhenNoAccountsRegistered()
    {
        await using AccountScratch scratch = new();
        // No accounts seeded — empty registry.

        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleT0424Response, System.Text.Encoding.UTF8, "application/json"),
            }));

        string result = await AccountInquiryTools.Holdings(client, scratch.Resolver);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("No real accounts registered");
        root.GetProperty("details").GetProperty("error_code").GetString().Should().Be("RequiresAccount");
    }

    [Fact]
    public async Task Holdings_SurfacesLsBusinessLevelError()
    {
        await using AccountScratch scratch = new();
        await scratch.SeedDefaultAccount("12345-01", "주식");

        const string errorBody = """
            {
              "rsp_cd": "IGW00012",
              "rsp_msg": "계좌가 존재하지 않습니다."
            }
            """;
        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(errorBody, System.Text.Encoding.UTF8, "application/json"),
            }));

        string result = await AccountInquiryTools.Holdings(client, scratch.Resolver);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level error");
        JsonElement details = root.GetProperty("details");
        details.GetProperty("tr_code").GetString().Should().Be("t0424");
        details.GetProperty("rsp_cd").GetString().Should().Be("IGW00012");
        details.GetProperty("account_used").GetProperty("account_number").GetString().Should().Be("12345-01");
    }

    sealed class AccountScratch : IAsyncDisposable
    {
        readonly string _directory;
        readonly SqlitePortfolioRepository _repository;

        public AccountScratch()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-lsopenapi-h-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            DbPath = System.IO.Path.Combine(_directory, "portfolio.db");
            _repository = new SqlitePortfolioRepository(DbPath, "real");
            Resolver = new LsAccountResolver(_repository, LsMarket.Real);
        }

        public string DbPath { get; }
        public LsAccountResolver Resolver { get; }

        public async Task SeedDefaultAccount(string accountNumber, string nickname)
        {
            await _repository.InitializeAsync();
            await _repository.UpsertAccountAsync(accountNumber, nickname, null, setDefault: true);
        }

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
                // Best-effort cleanup.
            }
            return ValueTask.CompletedTask;
        }
    }
}
