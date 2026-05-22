using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Charting;
using Xunit;
using Xunit.Abstractions;

namespace RedoxNet.LsOpenApi.Core.Tests.Charting;

/// <summary>
/// Renders <see cref="ProgramRankingChartBuilder"/> against a real t1636
/// (종목별 프로그램매매 동향) capture into a standalone, browser-viewable HTML file.
/// Visual dev aid — open the <c>chart-output/*.html</c> path the test logs; the
/// assertion only sanity-checks that a horizontal-bar chart came out.
/// </summary>
public class ProgramRankingChartHarnessTests
{
    readonly ITestOutputHelper _output;

    /// <summary>Captures the xUnit output sink so the test can log its HTML path.</summary>
    public ProgramRankingChartHarnessTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The KOSPI program-trading net-buy ranking from the real 2026-05-22 t1636
    /// capture — top 20 stocks as a horizontal bar chart, rank 1 at the top.
    /// </summary>
    [Fact]
    public void Ranking_RealCapture_WritesViewableHtml()
    {
        IReadOnlyList<ProgramRankingRow> rows = ParseCapture();
        var meta = new ProgramRankingChartMeta(
            "kospi", "순매수 상위", "순매수 금액 (억원)", "2026-05-22");

        JsonObject envelope = ProgramRankingChartBuilder.Build(meta, rows);
        string path = ChartHtmlHarness.Write("program-trading-ranking", envelope["spec"]!);

        _output.WriteLine("chart written — open in a browser:\n  " + path);
        File.ReadAllText(path).Should().Contain("\"bar\"");
    }

    /// <summary>
    /// Parses the embedded t1636 capture into ranking rows. t1636 ships program
    /// amounts in 천원; the chart shows 억원 (= 천원 ÷ 100,000).
    /// </summary>
    static IReadOnlyList<ProgramRankingRow> ParseCapture()
    {
        using JsonDocument doc = JsonDocument.Parse(RealT1636Capture);
        JsonElement rows = doc.RootElement.GetProperty("t1636OutBlock1");

        var list = new List<ProgramRankingRow>(rows.GetArrayLength());
        foreach (JsonElement r in rows.EnumerateArray())
        {
            list.Add(new ProgramRankingRow(
                Rank: r.GetProperty("rank").GetInt32(),
                Name: r.GetProperty("hname").GetString()!,
                Shcode: r.GetProperty("shcode").GetString()!,
                NetValue: r.GetProperty("svalue").GetInt64() / 100_000.0,
                MktCapRatio: ParseDouble(r, "mkcap_cmpr_val"),
                Diff: ParseDouble(r, "diff")));
        }
        return list;
    }

    static double ParseDouble(JsonElement row, string field) =>
        double.Parse(row.GetProperty(field).GetString()!, CultureInfo.InvariantCulture);

    /// <summary>
    /// A real KOSPI t1636 response from 2026-05-22 (gubun1=금액, gubun2=순매수상위),
    /// top-20 page. Only the fields the chart needs are retained; <c>svalue</c> is
    /// LS amount-basis 천원. Rank 1's <c>hname</c> is LS's length-truncated form
    /// (the live capture's broken trailing byte is dropped).
    /// </summary>
    const string RealT1636Capture = """
        {"t1636OutBlock":{"cts_idx":20},"rsp_cd":"00000","rsp_msg":"정상","t1636OutBlock1":[
        {"rank":1,"hname":"LIG디펜스앤에어로스","shcode":"079550","diff":"6.03","svalue":23053046,"mkcap_cmpr_val":"0.11"},
        {"rank":2,"hname":"셀트리온","shcode":"068270","diff":"4.75","svalue":17101778,"mkcap_cmpr_val":"0.04"},
        {"rank":3,"hname":"한화솔루션","shcode":"009830","diff":"7.64","svalue":10899697,"mkcap_cmpr_val":"0.15"},
        {"rank":4,"hname":"두산로보틱스","shcode":"454910","diff":"-1.39","svalue":8148851,"mkcap_cmpr_val":"0.12"},
        {"rank":5,"hname":"SK","shcode":"034730","diff":"7.29","svalue":6726820,"mkcap_cmpr_val":"0.02"},
        {"rank":6,"hname":"KB금융","shcode":"105560","diff":"1.41","svalue":5792223,"mkcap_cmpr_val":"0.01"},
        {"rank":7,"hname":"한화에어로스페이스","shcode":"012450","diff":"0.96","svalue":5446749,"mkcap_cmpr_val":"0.01"},
        {"rank":8,"hname":"현대오토에버","shcode":"307950","diff":"-2.54","svalue":5325094,"mkcap_cmpr_val":"0.03"},
        {"rank":9,"hname":"미래산업","shcode":"025560","diff":"28.26","svalue":5069179,"mkcap_cmpr_val":"2.81"},
        {"rank":10,"hname":"NC","shcode":"036570","diff":"3.99","svalue":4959224,"mkcap_cmpr_val":"0.08"},
        {"rank":11,"hname":"한화시스템","shcode":"272210","diff":"5.20","svalue":4206363,"mkcap_cmpr_val":"0.02"},
        {"rank":12,"hname":"S-Oil","shcode":"010950","diff":"2.70","svalue":4048288,"mkcap_cmpr_val":"0.03"},
        {"rank":13,"hname":"현대글로비스","shcode":"086280","diff":"2.57","svalue":4015504,"mkcap_cmpr_val":"0.02"},
        {"rank":14,"hname":"현대로템","shcode":"064350","diff":"5.12","svalue":3970234,"mkcap_cmpr_val":"0.02"},
        {"rank":15,"hname":"후성","shcode":"093370","diff":"5.17","svalue":3958880,"mkcap_cmpr_val":"0.28"},
        {"rank":16,"hname":"한화오션","shcode":"042660","diff":"5.37","svalue":3787684,"mkcap_cmpr_val":"0.01"},
        {"rank":17,"hname":"유한양행","shcode":"000100","diff":"4.40","svalue":3563251,"mkcap_cmpr_val":"0.05"},
        {"rank":18,"hname":"흥아해운","shcode":"003280","diff":"3.88","svalue":2998900,"mkcap_cmpr_val":"0.49"},
        {"rank":19,"hname":"LS","shcode":"006260","diff":"9.60","svalue":2987625,"mkcap_cmpr_val":"0.02"},
        {"rank":20,"hname":"LG","shcode":"003550","diff":"-3.90","svalue":2829384,"mkcap_cmpr_val":"0.02"}
        ]}
        """;
}
