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
/// Pins <see cref="AccountInquiryTools.Balance"/> (TR CSPAQ12200 / CSPAQ22200)
/// against the LS docs fixtures. The dual-TR routing — real → 12200, virtual
/// → 22200 — is the core behaviour to keep stable, plus the unique-to-real
/// valuation fields (PnlPct, InvestmentOriginal, EvaluationAmount) must be
/// omitted from JSON in virtual mode rather than zeroed.
/// </summary>
public sealed class AccountBalanceToolTests
{
    const string RealResponse = """
        {
          "rsp_cd": "00136",
          "rsp_msg": "조회가 완료되었습니다.",
          "CSPAQ12200OutBlock1": { "RecCnt": 1, "AcntNo": "12345-01", "BalCreTp": "0" },
          "CSPAQ12200OutBlock2": {
            "BrnNm": "다이렉트203", "AcntNm": "엘에스",
            "MnyOrdAbleAmt": 307, "MnyoutAbleAmt": 307,
            "SeOrdAbleAmt": 306, "KdqOrdAbleAmt": 306,
            "BalEvalAmt": 227989450, "RcvblAmt": 0, "DpsastTotamt": 227989757,
            "PnlRat": "1031.979979", "InvstOrgAmt": 0, "InvstPlAmt": 227989757,
            "Dps": 307, "SubstAmt": 142982800,
            "D1Dps": 307, "D2Dps": 307,
            "SubstOrdAbleAmt": 142982800,
            "MgnRat100pctOrdAbleAmt": 306, "MgnRat50ordAbleAmt": 306, "MgnRat35ordAbleAmt": 306,
            "D1PrsmptWthdwAbleAmt": 307, "D2PrsmptWthdwAbleAmt": 307,
            "MloanAmt": 0, "CrdtPldgOrdAmt": 0, "CrdtOrdAbleAmt": 0
          }
        }
        """;

    const string VirtualResponse = """
        {
          "rsp_cd": "00136",
          "rsp_msg": "조회가 완료되었습니다.",
          "CSPAQ22200OutBlock1": { "RecCnt": 1, "AcntNo": "999-01", "BalCreTp": "0" },
          "CSPAQ22200OutBlock2": {
            "BrnNm": "모의지점", "AcntNm": "모의계좌",
            "MnyOrdAbleAmt": 50000000, "SubstOrdAbleAmt": 0,
            "SeOrdAbleAmt": 50000000, "KdqOrdAbleAmt": 50000000,
            "CrdtPldgOrdAmt": 0,
            "MgnRat100pctOrdAbleAmt": 50000000, "MgnRat50ordAbleAmt": 50000000, "MgnRat35ordAbleAmt": 50000000,
            "CrdtOrdAbleAmt": 0,
            "Dps": 50000000, "SubstAmt": 0,
            "D1Dps": 50000000, "D2Dps": 50000000,
            "RcvblAmt": 0, "MloanAmt": 0
          }
        }
        """;

    [Fact]
    public async Task Balance_RealMode_HitsCSPAQ12200_AndCarriesValuationFields()
    {
        await using AccountScratch scratch = new("real", LsMarket.Real);
        await scratch.SeedDefaultAccount("12345-01", "주식");

        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var (client, _) = TestClientFactory.Create(async (req, _) =>
        {
            capturedRequest = req;
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(RealResponse, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        string result = await AccountInquiryTools.Balance(client, scratch.Resolver);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        // tr_cd header decides the route on LS's side, but we send it via the
        // header — capture the header to confirm we routed CSPAQ12200.
        capturedRequest!.Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("CSPAQ12200");
        capturedBody.Should().Contain("\"CSPAQ12200InBlock1\"");
        capturedBody.Should().Contain("\"BalCreTp\":\"0\"");

        JsonElement balance = root.GetProperty("balance");
        balance.GetProperty("branch_name").GetString().Should().Be("다이렉트203");
        balance.GetProperty("deposit").GetInt64().Should().Be(307);
        balance.GetProperty("cash_orderable_amount").GetInt64().Should().Be(307);
        balance.GetProperty("substitute_amount").GetInt64().Should().Be(142982800);
        balance.GetProperty("evaluation_amount").GetInt64().Should().Be(227989450);
        balance.GetProperty("deposited_asset_total").GetInt64().Should().Be(227989757);
        balance.GetProperty("withdrawable_amount").GetInt64().Should().Be(307);
        balance.GetProperty("pnl_pct").GetDouble().Should().BeApproximately(1031.98, 0.01);
        balance.GetProperty("investment_pnl").GetInt64().Should().Be(227989757);

        JsonElement meta = root.GetProperty("_meta");
        meta.GetProperty("tr_code").GetString().Should().Be("CSPAQ12200");
        meta.GetProperty("source").GetString().Should().Be("live");
        meta.GetProperty("account_used").GetProperty("mode").GetString().Should().Be("real");
    }

    [Fact]
    public async Task Balance_VirtualMode_HitsCSPAQ22200_AndOmitsRealOnlyFields()
    {
        await using AccountScratch scratch = new("virtual", LsMarket.Virtual);
        await scratch.SeedDefaultAccount("999-01", "모의");

        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var (client, _) = TestClientFactory.Create(async (req, _) =>
        {
            capturedRequest = req;
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(VirtualResponse, System.Text.Encoding.UTF8, "application/json"),
            };
        });

        string result = await AccountInquiryTools.Balance(client, scratch.Resolver);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        capturedRequest!.Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("CSPAQ22200");
        capturedBody.Should().Contain("\"CSPAQ22200InBlock1\"");

        JsonElement balance = root.GetProperty("balance");
        balance.GetProperty("deposit").GetInt64().Should().Be(50000000);
        balance.GetProperty("d2_deposit").GetInt64().Should().Be(50000000);
        balance.GetProperty("kospi_orderable_amount").GetInt64().Should().Be(50000000);

        // Real-mode-only fields must not appear at all (not zero).
        balance.TryGetProperty("evaluation_amount", out _).Should().BeFalse();
        balance.TryGetProperty("withdrawable_amount", out _).Should().BeFalse();
        balance.TryGetProperty("pnl_pct", out _).Should().BeFalse();
        balance.TryGetProperty("investment_original", out _).Should().BeFalse();

        root.GetProperty("_meta").GetProperty("tr_code").GetString().Should().Be("CSPAQ22200");
        root.GetProperty("_meta").GetProperty("account_used").GetProperty("mode").GetString().Should().Be("virtual");
    }

    [Fact]
    public async Task Balance_SurfacesBusinessLevelErrorWithTrCode()
    {
        await using AccountScratch scratch = new("real", LsMarket.Real);
        await scratch.SeedDefaultAccount("12345-01", "주식");

        const string errorBody = """{"rsp_cd":"IGW00012","rsp_msg":"계좌가 존재하지 않습니다."}""";
        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(errorBody, System.Text.Encoding.UTF8, "application/json"),
            }));

        string result = await AccountInquiryTools.Balance(client, scratch.Resolver);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        JsonElement details = root.GetProperty("details");
        details.GetProperty("tr_code").GetString().Should().Be("CSPAQ12200");
        details.GetProperty("rsp_cd").GetString().Should().Be("IGW00012");
    }

    sealed class AccountScratch : IAsyncDisposable
    {
        readonly string _directory;
        readonly SqlitePortfolioRepository _repository;

        public AccountScratch(string mode, LsMarket market)
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-lsopenapi-bal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            DbPath = System.IO.Path.Combine(_directory, "portfolio.db");
            _repository = new SqlitePortfolioRepository(DbPath, mode);
            Resolver = new LsAccountResolver(_repository, market);
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
