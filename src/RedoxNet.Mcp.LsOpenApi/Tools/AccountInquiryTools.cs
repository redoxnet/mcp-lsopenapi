using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.Mcp.LsOpenApi.Portfolio;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// MCP tools that wrap LS broker account inquiry TRs (v1.6 §2).
/// </summary>
/// <remarks>
/// Every tool here:
/// 1. Resolves the target account via <see cref="LsAccountResolver"/>,
///    which filters by the active LS_MARKET so real vs virtual stay separate.
/// 2. Sends one or more pages to LS REST — no caching, no daemon. Per
///    SPEC-v1.6 §2.5 fresh data wins over staleness on broker state.
/// 3. Returns a <c>_meta</c> envelope with <c>account_used</c>, <c>data_as_of</c>,
///    <c>tr_code</c>, and <c>source: "live"</c> so the model can anchor analysis
///    to a concrete account and timestamp.
///
/// Inquiry-only — order placement lives in v1.7.
/// </remarks>
[McpServerToolType]
internal static class AccountInquiryTools
{
    /// <summary>
    /// LS rate-limit-safe upper bound for cts_expcode page chases. Single
    /// account rarely exceeds a handful of pages even at the platform cap.
    /// </summary>
    const int MaxHoldingsPages = 20;

    [McpServerTool(Name = "ls_account_holdings")]
    [Description("""
        Returns the LIVE LS broker holdings for one of your registered accounts via TR t0424. Real-time read of what LS actually holds — separate from the manually-tracked ls_holdings_list (which lives in the local portfolio.db).

        USE WHEN: the user asks "내 LS 계좌 잔고", "실제 보유 종목", "지금 LS 증권에 뭐가 있어?", or any phrasing that asks for the broker's view of positions.
        AVOID WHEN: the user wants their paper-portfolio or manually-recorded holdings — use ls_holdings_list. AVOID WHEN: the user wants the watchlist — use ls_watchlist.

        `account` is optional — when omitted the active mode's default account is used (per LS_MARKET=real|virtual). With zero accounts registered returns RequiresAccount. With multiple accounts and no default returns AmbiguousAccount with candidates.

        Output: per-symbol rows + a portfolio summary (estimated net assets, deposit, total evaluation / P&L), plus _meta.account_used / data_as_of / tr_code / source="live".
        """)]
    public static async Task<string> Holdings(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("Optional account_number or nickname. Omit to use the active mode's default account.")]
        string? account = null,
        CancellationToken cancellationToken = default)
    {
        Account resolved;
        try
        {
            resolved = await accountResolver.ResolveAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (RequiresAccountException ex)
        {
            return McpJson.Error(ex.Message, new { error_code = ex.Code });
        }
        catch (AmbiguousAccountException ex)
        {
            return McpJson.Error(ex.Message, new { error_code = ex.Code, candidates = ex.Candidates });
        }
        catch (AccountNotFoundException ex)
        {
            return McpJson.Error(ex.Message, new { error_code = ex.Code, identifier = ex.Identifier, candidates = ex.Candidates });
        }

        try
        {
            var summary = new HoldingsSummary();
            var rows = new List<HoldingsRow>();
            string ctsExpcode = "";
            string? continuationKey = null;

            for (int page = 0; page < MaxHoldingsPages; page++)
            {
                var inBlock = new JsonObject
                {
                    ["prcgb"] = "1",      // 평균단가 (BEP는 별도 ls_account_bep)
                    ["chegb"] = "0",      // 결제기준잔고
                    ["dangb"] = "0",      // 정규장
                    ["charge"] = "1",     // 제비용포함
                    ["cts_expcode"] = ctsExpcode,
                };

                LsTrResponse response = await apiClient.CallTrAsync(
                    "t0424", inBlock, continuationKey, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccess)
                {
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        tr_code = "t0424",
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        account_used = ToAccountEcho(resolved),
                    });
                }

                JsonElement? headerBlock = response.GetBlock("t0424OutBlock");
                if (headerBlock is not null && page == 0)
                {
                    JsonElement h = headerBlock.Value;
                    summary.EstimatedNetAssets = h.ReadLong("sunamt");
                    summary.RealizedPnl = h.ReadLong("dtsunik");
                    summary.PurchaseAmount = h.ReadLong("mamt");
                    summary.EstimatedD2Deposit = h.ReadLong("sunamt1");
                    summary.TotalEvaluation = h.ReadLong("tappamt");
                    summary.TotalEvaluationPnl = h.ReadLong("tdtsunik");
                }

                JsonElement? listBlock = response.GetBlock("t0424OutBlock1");
                if (listBlock is not null && listBlock.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement row in listBlock.Value.EnumerateArray())
                    {
                        string symbol = (row.ReadString("expcode") ?? "").Trim();
                        if (string.IsNullOrEmpty(symbol))
                            continue;
                        rows.Add(new HoldingsRow(
                            Symbol: symbol,
                            Name: GetIndexQuoteTool.CompactName(row.ReadString("hname")),
                            Quantity: row.ReadLong("janqty"),
                            SellableQuantity: row.ReadLong("mdposqt"),
                            AveragePrice: row.ReadLong("pamt"),
                            PurchaseAmount: row.ReadLong("mamt"),
                            CurrentPrice: row.ReadLong("price"),
                            EvaluationAmount: row.ReadLong("appamt"),
                            EvaluationPnl: row.ReadLong("dtsunik"),
                            EvaluationPnlPct: row.ReadDouble("sunikrt"),
                            HoldingWeight: row.ReadDouble("janrt"),
                            Fee: row.ReadLong("fee"),
                            Tax: row.ReadLong("tax"),
                            CreditInterest: row.ReadLong("sininter"),
                            LoanAmount: row.ReadLong("sinamt"),
                            LoanDate: NormalizeYmd(row.ReadString("loandt")),
                            MaturityDate: NormalizeYmd(row.ReadString("lastdt")),
                            MarketCategory: NormalizeMarketCategory(row.ReadString("marketgb")),
                            SymbolCategory: NormalizeSymbolCategory(row.ReadString("jonggb"))));
                    }
                }

                if (!response.HasContinuation || string.IsNullOrEmpty(response.ContinuationKey))
                    break;
                continuationKey = response.ContinuationKey;
                // Header-based pagination carries the cursor in the key; the
                // cts_expcode body field is what LS echoes back, so we forward it.
                if (headerBlock is not null)
                    ctsExpcode = (headerBlock.Value.ReadString("cts_expcode") ?? "").Trim();
            }

            var payload = new
            {
                summary,
                count = rows.Count,
                holdings = rows,
                _meta = new
                {
                    account_used = ToAccountEcho(resolved),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = "t0424",
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("LS authentication failed for the account inquiry. Verify LS_APPKEY / LS_APPSECRETKEY / LS_MARKET match the requested account.",
                new { reason = ex.Message, account_used = ToAccountEcho(resolved) });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new
            {
                reason = ex.Message,
                status = ex.StatusCode,
                tr_code = "t0424",
                account_used = ToAccountEcho(resolved),
            });
        }
    }

    static object ToAccountEcho(Account account) => new
    {
        account_number = account.AccountNo,
        nickname = account.Nickname,
        broker = account.Broker,
        mode = account.Mode,
        is_default = account.IsDefault,
    };

    static string? NormalizeYmd(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        if (trimmed.Length != 8 || !trimmed.All(char.IsDigit)) return trimmed;
        return $"{trimmed.AsSpan(0, 4)}-{trimmed.AsSpan(4, 2)}-{trimmed.AsSpan(6, 2)}";
    }

    static string NormalizeMarketCategory(string? raw) => raw?.Trim() switch
    {
        "1" => "stock",
        "2" => "bond",
        _ => raw?.Trim() ?? "",
    };

    static string NormalizeSymbolCategory(string? raw) => raw?.Trim() switch
    {
        "1" => "freeboard",
        "2" => "kosdaq",
        "3" => "kospi",
        "Z" => "delisted",
        "8" => "konex",
        "9" => "cma_rp",
        "" or null => "",
        _ => raw.Trim(),
    };

    sealed class HoldingsSummary
    {
        public long EstimatedNetAssets { get; set; }
        public long RealizedPnl { get; set; }
        public long PurchaseAmount { get; set; }
        public long EstimatedD2Deposit { get; set; }
        public long TotalEvaluation { get; set; }
        public long TotalEvaluationPnl { get; set; }
    }

    sealed record HoldingsRow(
        string Symbol,
        string Name,
        long Quantity,
        long SellableQuantity,
        long AveragePrice,
        long PurchaseAmount,
        long CurrentPrice,
        long EvaluationAmount,
        long EvaluationPnl,
        double EvaluationPnlPct,
        double HoldingWeight,
        long Fee,
        long Tax,
        long CreditInterest,
        long LoanAmount,
        string? LoanDate,
        string? MaturityDate,
        string MarketCategory,
        string SymbolCategory);
}
