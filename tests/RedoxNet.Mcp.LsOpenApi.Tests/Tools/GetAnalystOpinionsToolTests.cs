using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetAnalystOpinionsToolTests
{
    const string Sample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t3401OutBlock": {
        "cts_date": "20251015", "price": 278500, "sign": "2",
        "change": 3000, "diff": "1.09", "volume": 11403693, "value": 3150554
      },
      "t3401OutBlock1": [
        { "shcode": "005930", "tradno": "046", "date": "20260518", "tradname": "iM증권",
          "bopn": "BUY", "nopn": "", "boga": 0, "noga": 400000, "close": 281000 },
        { "shcode": "005930", "tradno": "004", "date": "20260408", "tradname": "대신증권",
          "bopn": "BUY", "nopn": "HOLD", "boga": 300000, "noga": 350000, "close": 210500 }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetAnalystOpinions_Sample_ShapesPayloadAndMapsBeforeAfter()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetAnalystOpinionsTool.GetAnalystOpinions(client, "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t3401");
        (await handler.Requests[0].Content!.ReadAsStringAsync()).Should().Contain("\"shcode\":\"005930\"");

        root.GetProperty("shcode").GetString().Should().Be("005930");
        root.GetProperty("current").GetProperty("price").GetInt64().Should().Be(278500);
        root.GetProperty("current").GetProperty("change").GetInt64().Should().Be(3000, "sign=2 keeps change positive");
        root.GetProperty("count").GetInt32().Should().Be(2);

        JsonElement first = root.GetProperty("opinions")[0];
        first.GetProperty("date").GetString().Should().Be("20260518");
        first.GetProperty("broker").GetString().Should().Be("iM증권");
        first.GetProperty("opinion_to").GetString().Should().Be("BUY");
        first.TryGetProperty("opinion_from", out _).Should().BeFalse("blank nopn is omitted — a newly initiated rating");
        first.GetProperty("target_to").GetInt64().Should().Be(400000);

        JsonElement second = root.GetProperty("opinions")[1];
        second.GetProperty("opinion_from").GetString().Should().Be("HOLD");
        second.GetProperty("target_from").GetInt64().Should().Be(300000);
        second.GetProperty("target_to").GetInt64().Should().Be(350000);
    }

    [Fact]
    public async Task GetAnalystOpinions_EmptyShcode_ReturnsValidationError()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetAnalystOpinionsTool.GetAnalystOpinions(client, "  ");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().BeEmpty();
        root.GetProperty("error").GetString().Should().Contain("shcode");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public async Task GetAnalystOpinions_CountOutOfRange_ReturnsValidationError(int count)
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetAnalystOpinionsTool.GetAnalystOpinions(client, "005930", count);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("count");
    }

    [Fact]
    public async Task GetAnalystOpinions_CountCapsRows()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetAnalystOpinionsTool.GetAnalystOpinions(client, "005930", count: 1);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(1);
        root.GetProperty("opinions").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetAnalystOpinions_BusinessError_SurfacesLsCode()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""{"rsp_cd":"99999","rsp_msg":"오류"}"""));

        string result = await GetAnalystOpinionsTool.GetAnalystOpinions(client, "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("rsp_cd").GetString().Should().Be("99999");
    }
}
