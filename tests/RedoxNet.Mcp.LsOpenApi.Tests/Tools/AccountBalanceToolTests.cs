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
/// Pins <see cref="AccountInquiryTools.Balance"/> (TR CSPAQ12200) against
/// the LS docs fixture. v1.6 correction: CSPAQ22200 is a v2 of CSPAQ12200,
/// not a virtual variant — both work with both real and virtual appkey
/// pairs and return whatever account the appkey is tied to. We always send
/// CSPAQ12200 (the v1, richer field set); the appkey determines the
/// account. See <c>docs/LS-API-QUIRKS.md</c> §4.2b.
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

    [Fact]
    public async Task Balance_RealMode_HitsCSPAQ12200_AndCarriesValuationFields()
    {
        await using LiveAccountScratch scratch = new(LsMarket.Real, scope: "bal");
        await scratch.SeedLiveAccount("12345-01", nickname: "주식");

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
    public async Task Balance_VirtualMode_StillUsesCSPAQ12200()
    {
        // v1.6 correction: virtual mode does NOT route to CSPAQ22200 — that
        // was a misunderstanding of the LS docs. The appkey pair determines
        // which account answers; the TR code is the same.
        await using LiveAccountScratch scratch = new(LsMarket.Virtual, scope: "bal-v");
        await scratch.SeedLiveAccount("999-01", nickname: "모의");

        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var (client, _) = TestClientFactory.Create(async (req, _) =>
        {
            capturedRequest = req;
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            // Reuse the real-shaped fixture; the field set is what CSPAQ12200
            // always returns regardless of which appkey reached it.
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(RealResponse.Replace("CSPAQ12200", "CSPAQ12200"), System.Text.Encoding.UTF8, "application/json"),
            };
        });

        string result = await AccountInquiryTools.Balance(client, scratch.Resolver);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        capturedRequest!.Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("CSPAQ12200");
        capturedBody.Should().Contain("\"CSPAQ12200InBlock1\"");

        root.GetProperty("_meta").GetProperty("tr_code").GetString().Should().Be("CSPAQ12200");
        root.GetProperty("_meta").GetProperty("account_used").GetProperty("mode").GetString().Should().Be("virtual");
        // Valuation fields are present in the payload (always populated; zero
        // is a real value, not a missing field).
        JsonElement balance = root.GetProperty("balance");
        balance.TryGetProperty("evaluation_amount", out _).Should().BeTrue();
        balance.TryGetProperty("pnl_pct", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Balance_SurfacesBusinessLevelErrorWithTrCode()
    {
        await using LiveAccountScratch scratch = new(LsMarket.Real, scope: "bal");
        await scratch.SeedLiveAccount("12345-01", nickname: "주식");

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

}
