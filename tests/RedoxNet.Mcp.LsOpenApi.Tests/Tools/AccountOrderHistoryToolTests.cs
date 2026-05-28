using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins the v1.6 single-page auto-discovery behavior of
/// <see cref="AccountInquiryTools.OrderHistory"/>. The second E2E
/// (<c>todo/E2E-v1.6-2nd_claude_codex.txt</c>) surfaced a latent v1.6-dev
/// bug: the AcntNo read in the paginated tools (CSPAQ13700, CDPCQ04700,
/// FOCCQ33600) lived after the break-on-no-continuation check, so
/// single-page responses (the common case for narrow date filters)
/// shipped <c>account_used.discovered = false</c> even though
/// <c>OutBlock1.AcntNo</c> was present. Fix moves the read to the first
/// successful page, unconditional on pagination.
/// </summary>
public sealed class AccountOrderHistoryToolTests
{
    const string SinglePageResponse = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회가 완료되었습니다.",
          "CSPAQ13700OutBlock1": {
            "RecCnt": 1, "AcntNo": "20856195501", "InptPwd": "********",
            "OrdMktCode": "00", "BnsTpCode": "0", "IsuNo": "",
            "ExecYn": "0", "OrdDt": "20260528", "SrtOrdNo2": 0,
            "BkseqTpCode": "1", "OrdPtnCode": "00"
          },
          "CSPAQ13700OutBlock2": {
            "RecCnt": 1, "SellExecAmt": 0, "BuyExecAmt": 301000,
            "SellExecQty": 0, "BuyExecQty": 1,
            "SellOrdQty": 0, "BuyOrdQty": 1
          },
          "CSPAQ13700OutBlock3": [
            {
              "OrdNo": 11028, "OrgOrdNo": 0, "OrdDt": "20260528",
              "IsuNo": "A005930", "IsuNm": "삼성전자",
              "BnsTpNm": "매수", "OrdPtnNm": "(KSE)현금매수",
              "OrdTrxPtnNm": "", "MrcTpNm": "정상",
              "OrdQty": 1, "OrdPrc": 301000,
              "ExecQty": 1, "ExecPrc": 301000, "AllExecQty": 1,
              "OrdTime": "11063192", "LastExecTime": "11160180",
              "OrdprcPtnNm": "지정가", "CommdaNm": "투혼(iOS)"
            }
          ]
        }
        """;

    [Fact]
    public async Task OrderHistory_SinglePageResponse_TriggersAutoDiscoveryFromOutBlock1()
    {
        // Regression for E2E-v1.6-2nd_claude_codex.txt: single-page
        // CSPAQ13700 responses must auto-discover the AcntNo. Prior to
        // the fix the read sat after the pagination-break, so this exact
        // shape (the common case) shipped discovered=false.
        await using LiveAccountScratch scratch = new(scope: "oh");

        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SinglePageResponse, System.Text.Encoding.UTF8, "application/json"),
            }));

        string result = await AccountInquiryTools.OrderHistory(client, scratch.Resolver, order_date: "2026-05-28");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(1);
        JsonElement used = root.GetProperty("_meta").GetProperty("account_used");
        used.GetProperty("account_number").GetString().Should().Be("20856195501");
        used.GetProperty("discovered").GetBoolean().Should().BeTrue();
        used.GetProperty("mode").GetString().Should().Be("real");

        // And the row landed in the live registry, so a follow-up call
        // sees it without going through the response path again.
        LsLiveAccount? row = await scratch.LiveRepo.GetByModeAsync("real");
        row.Should().NotBeNull();
        row!.AccountNo.Should().Be("20856195501");
    }
}
