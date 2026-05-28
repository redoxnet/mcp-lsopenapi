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

    /// <summary>Same upper bound for the t0425 cts_ordno cursor.</summary>
    const int MaxOrdersPages = 20;

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

    [McpServerTool(Name = "ls_account_orders")]
    [Description("""
        Returns today's orders (filled + pending) for one of your LS broker accounts via TR t0425. Real-time read from LS — no cache.

        USE WHEN: the user asks "오늘 주문", "체결됐나", "미체결 남은 거", "오늘 매매 내역", or wants the live order book for the active account.
        AVOID WHEN: the user wants a historical multi-day order log — use ls_account_order_history (TR CSPAQ13700) instead. AVOID WHEN: the user wants positions — use ls_account_holdings.

        Filters: `status` ("all" default / "filled" / "pending") maps to LS chegb. `side` ("all" / "buy" / "sell") maps to medosu (매수/매도). `symbol` narrows to a specific shcode. Sort default is ascending by order number (older first).

        Account resolution and envelopes match ls_account_holdings (RequiresAccount / AmbiguousAccount). _meta carries account_used / data_as_of / tr_code / source="live".
        """)]
    public static async Task<string> Orders(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("Optional account_number or nickname. Omit to use the active mode's default account.")]
        string? account = null,
        [Description("Order status filter: 'all' (default), 'filled', or 'pending'.")]
        string? status = null,
        [Description("Side filter: 'all' (default), 'buy' (매수), or 'sell' (매도).")]
        string? side = null,
        [Description("Optional 6-character Korean short code to narrow the result. Omit for all symbols.")]
        string? symbol = null,
        [Description("Sort order: 'asc' (default, oldest first by ordno) or 'desc'.")]
        string? sort = null,
        CancellationToken cancellationToken = default)
    {
        string chegb = NormalizeStatus(status, out string? statusError);
        if (statusError is not null) return McpJson.Error(statusError);
        string medosu = NormalizeSide(side, out string? sideError);
        if (sideError is not null) return McpJson.Error(sideError);
        string sortgb = NormalizeSort(sort, out string? sortError);
        if (sortError is not null) return McpJson.Error(sortError);

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
            string normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? "" : symbol.Trim().ToUpperInvariant();
            var summary = new OrdersSummary();
            var rows = new List<OrderRow>();
            string ctsOrdno = "";
            string? continuationKey = null;

            for (int page = 0; page < MaxOrdersPages; page++)
            {
                var inBlock = new JsonObject
                {
                    ["expcode"] = normalizedSymbol,
                    ["chegb"] = chegb,
                    ["medosu"] = medosu,
                    ["sortgb"] = sortgb,
                    ["cts_ordno"] = ctsOrdno,
                };

                LsTrResponse response = await apiClient.CallTrAsync(
                    "t0425", inBlock, continuationKey, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccess)
                {
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        tr_code = "t0425",
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        account_used = ToAccountEcho(resolved),
                    });
                }

                JsonElement? headerBlock = response.GetBlock("t0425OutBlock");
                if (headerBlock is not null && page == 0)
                {
                    JsonElement h = headerBlock.Value;
                    summary.TotalOrderQuantity = h.ReadLong("tqty");
                    summary.TotalFilledQuantity = h.ReadLong("tcheqty");
                    summary.TotalPendingQuantity = h.ReadLong("tordrem");
                    summary.EstimatedFee = h.ReadLong("cmss");
                    summary.TotalOrderAmount = h.ReadLong("tamt");
                    summary.TotalSellFilledAmount = h.ReadLong("tmdamt");
                    summary.TotalBuyFilledAmount = h.ReadLong("tmsamt");
                    summary.EstimatedTax = h.ReadLong("tax");
                }

                JsonElement? listBlock = response.GetBlock("t0425OutBlock1");
                if (listBlock is not null && listBlock.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement row in listBlock.Value.EnumerateArray())
                    {
                        string sym = (row.ReadString("expcode") ?? "").Trim();
                        if (string.IsNullOrEmpty(sym))
                            continue;
                        rows.Add(new OrderRow(
                            OrderNo: row.ReadLong("ordno"),
                            OriginalOrderNo: row.ReadLong("orgordno"),
                            Symbol: sym,
                            Side: (row.ReadString("medosu") ?? "").Trim(),
                            OrderType: (row.ReadString("ordgb") ?? "").Trim(),
                            QuoteType: (row.ReadString("hogagb") ?? "").Trim(),
                            OrderQuantity: row.ReadLong("qty"),
                            OrderPrice: row.ReadLong("price"),
                            FilledQuantity: row.ReadLong("cheqty"),
                            FilledPrice: row.ReadLong("cheprice"),
                            PendingQuantity: row.ReadLong("ordrem"),
                            ConfirmedQuantity: row.ReadLong("cfmqty"),
                            Status: (row.ReadString("status") ?? "").Trim(),
                            OrderTime: FormatHmsFraction(row.ReadString("ordtime")),
                            OrderChannel: (row.ReadString("ordermtd") ?? "").Trim(),
                            CurrentPrice: row.ReadLong("price1"),
                            CreditType: (row.ReadString("singb") ?? "").Trim(),
                            LoanDate: NormalizeYmd(row.ReadString("loandt")),
                            Exchange: (row.ReadString("exchname") ?? "").Trim()));
                    }
                }

                if (!response.HasContinuation || string.IsNullOrEmpty(response.ContinuationKey))
                    break;
                continuationKey = response.ContinuationKey;
                if (headerBlock is not null)
                    ctsOrdno = (headerBlock.Value.ReadString("cts_ordno") ?? "").Trim();
            }

            var payload = new
            {
                filter = new
                {
                    status = StatusLabel(chegb),
                    side = SideLabel(medosu),
                    symbol = string.IsNullOrEmpty(normalizedSymbol) ? null : normalizedSymbol,
                    sort = sortgb == "1" ? "desc" : "asc",
                },
                summary,
                count = rows.Count,
                orders = rows,
                _meta = new
                {
                    account_used = ToAccountEcho(resolved),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = "t0425",
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
                tr_code = "t0425",
                account_used = ToAccountEcho(resolved),
            });
        }
    }

    [McpServerTool(Name = "ls_account_balance")]
    [Description("""
        Returns the LIVE cash, buying power, and total valuation for one of your LS broker accounts. Routes to TR CSPAQ12200 in real mode and CSPAQ22200 in virtual mode automatically — same response shape, with valuation/PnL fields available only in real mode where LS computes them.

        USE WHEN: the user asks "예수금 얼마야", "내 잔고", "주문가능금액", "총 평가금액", "투자원금 대비 손익" — anything about cash, buying power, or roll-up portfolio value.
        AVOID WHEN: the user wants per-symbol positions — use ls_account_holdings. AVOID WHEN: the user wants per-day P&L history — use ls_account_performance.

        Account resolution matches ls_account_holdings (default-account / RequiresAccount / AmbiguousAccount). _meta carries account_used, data_as_of, tr_code (CSPAQ12200 or CSPAQ22200), and source="live".
        """)]
    public static async Task<string> Balance(
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

        bool isVirtual = string.Equals(resolved.Mode, "virtual", StringComparison.Ordinal);
        string trCode = isVirtual ? "CSPAQ22200" : "CSPAQ12200";
        string outBlock2Name = isVirtual ? "CSPAQ22200OutBlock2" : "CSPAQ12200OutBlock2";
        string inBlockName = $"{trCode}InBlock1"; // suffix '1' is LS convention for the CSPAQ family.

        try
        {
            var body = new JsonObject
            {
                [inBlockName] = new JsonObject { ["BalCreTp"] = "0" },
            };
            LsTrRequest request = new(trCode, body);
            LsTrResponse response = await apiClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // CSPAQ accno TRs return rsp_cd="00136" alongside "조회가 완료되었습니다."
            // as their success envelope, instead of the usual "00000". Treat
            // both as success when the expected output block is present;
            // anything else falls through to the business-error path.
            JsonElement? body2 = response.GetBlock(outBlock2Name);
            if (!IsCspaqSuccess(response.RspCode, body2))
            {
                return McpJson.Error("LS reported a business-level error.", new
                {
                    tr_code = trCode,
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    account_used = ToAccountEcho(resolved),
                });
            }
            // After the success check, body2 cannot be null per IsCspaqSuccess.
            ArgumentNullException.ThrowIfNull(body2);

            JsonElement b = body2.Value;
            var balance = new BalancePayload
            {
                BranchName = (b.ReadString("BrnNm") ?? "").Trim(),
                AccountName = (b.ReadString("AcntNm") ?? "").Trim(),
                Deposit = b.ReadLong("Dps"),
                D1Deposit = b.ReadLong("D1Dps"),
                D2Deposit = b.ReadLong("D2Dps"),
                CashOrderableAmount = b.ReadLong("MnyOrdAbleAmt"),
                SubstituteOrderableAmount = b.ReadLong("SubstOrdAbleAmt"),
                SubstituteAmount = b.ReadLong("SubstAmt"),
                KospiOrderableAmount = b.ReadLong("SeOrdAbleAmt"),
                KosdaqOrderableAmount = b.ReadLong("KdqOrdAbleAmt"),
                CreditOrderableAmount = b.ReadLong("CrdtOrdAbleAmt"),
                CreditCollateralOrderAmount = b.ReadLong("CrdtPldgOrdAmt"),
                MarginRate100Orderable = b.ReadLong("MgnRat100pctOrdAbleAmt"),
                MarginRate50Orderable = b.ReadLong("MgnRat50ordAbleAmt"),
                MarginRate35Orderable = b.ReadLong("MgnRat35ordAbleAmt"),
                ReceivableAmount = b.ReadLong("RcvblAmt"),
                LoanAmount = b.ReadLong("MloanAmt"),
            };

            // Real-mode only fields. Virtual TR omits these because LS doesn't
            // track investment basis on the paper-trading book.
            if (!isVirtual)
            {
                balance.WithdrawableAmount = b.ReadLong("MnyoutAbleAmt");
                balance.EvaluationAmount = b.ReadLong("BalEvalAmt");
                balance.DepositedAssetTotal = b.ReadLong("DpsastTotamt");
                balance.PnlPct = b.ReadDouble("PnlRat");
                balance.InvestmentOriginal = b.ReadLong("InvstOrgAmt");
                balance.InvestmentPnl = b.ReadLong("InvstPlAmt");
                balance.D1WithdrawablePresumed = b.ReadLong("D1PrsmptWthdwAbleAmt");
                balance.D2WithdrawablePresumed = b.ReadLong("D2PrsmptWthdwAbleAmt");
            }

            var payload = new
            {
                balance,
                _meta = new
                {
                    account_used = ToAccountEcho(resolved),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
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
                tr_code = trCode,
                account_used = ToAccountEcho(resolved),
            });
        }
    }

    [McpServerTool(Name = "ls_account_bep")]
    [Description("""
        Returns the break-even price (BEP 단가) per holding via TR CSPAQ12300. BEP is the price you'd need to sell at to fully cover commissions and taxes — different from the raw average purchase price.

        USE WHEN: the user asks "BEP 단가", "손익분기 단가", "수수료 포함 평단", or wants the after-fee breakeven on their LS positions.
        AVOID WHEN: the user wants just the simple average purchase price — that's already in ls_account_holdings.average_price.

        `symbol` optionally narrows to a single 6-digit code (Korean shcode); the LS field IsuNo accepts the "A+code" form but the wrapper takes the bare 6-digit shcode for the user. Account resolution matches ls_account_holdings.
        """)]
    public static async Task<string> Bep(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("Optional account_number or nickname. Omit to use the active mode's default account.")]
        string? account = null,
        [Description("Optional 6-digit Korean short code (e.g. '005930'). Omit to return BEP for every holding.")]
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        Account resolved;
        try { resolved = await accountResolver.ResolveAsync(account, cancellationToken).ConfigureAwait(false); }
        catch (RequiresAccountException ex) { return McpJson.Error(ex.Message, new { error_code = ex.Code }); }
        catch (AmbiguousAccountException ex) { return McpJson.Error(ex.Message, new { error_code = ex.Code, candidates = ex.Candidates }); }
        catch (AccountNotFoundException ex) { return McpJson.Error(ex.Message, new { error_code = ex.Code, identifier = ex.Identifier, candidates = ex.Candidates }); }

        string? symbolFilter = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim().ToUpperInvariant();
        const string trCode = "CSPAQ12300";

        try
        {
            var body = new JsonObject
            {
                [$"{trCode}InBlock1"] = new JsonObject
                {
                    ["BalCreTp"] = "0",
                    ["CmsnAppTpCode"] = "1",      // include fees in evaluation
                    ["D2balBaseQryTp"] = "0",
                    ["UprcTpCode"] = "1",          // BEP, not average
                },
            };
            LsTrResponse response = await apiClient.SendAsync(new LsTrRequest(trCode, body), cancellationToken).ConfigureAwait(false);

            JsonElement? body3 = response.GetBlock($"{trCode}OutBlock3");
            if (!IsCspaqSuccess(response.RspCode, body3))
            {
                return McpJson.Error("LS reported a business-level error.", new
                {
                    tr_code = trCode,
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    account_used = ToAccountEcho(resolved),
                });
            }
            ArgumentNullException.ThrowIfNull(body3);

            JsonElement? body2 = response.GetBlock($"{trCode}OutBlock2");
            BepSummary? summary = null;
            if (body2 is not null)
            {
                JsonElement s = body2.Value;
                summary = new BepSummary
                {
                    AccountName = (s.ReadString("AcntNm") ?? "").Trim(),
                    Deposit = s.ReadLong("Dps"),
                    EvaluationAmount = s.ReadLong("BalEvalAmt"),
                    PurchaseAmount = s.ReadLong("PchsAmt"),
                    EvaluationPnlSum = s.ReadLong("EvalPnlSum"),
                    PnlPct = s.ReadDouble("PnlRat"),
                    InvestmentOriginal = s.ReadLong("InvstOrgAmt"),
                    InvestmentPnl = s.ReadLong("InvstPlAmt"),
                };
            }

            var rows = new List<BepRow>();
            foreach (JsonElement row in body3.Value.EnumerateArray())
            {
                string isuNo = (row.ReadString("IsuNo") ?? "").Trim();
                string sym = ExtractShcode(isuNo);
                if (symbolFilter is not null && !string.Equals(sym, symbolFilter, StringComparison.Ordinal))
                    continue;
                rows.Add(new BepRow(
                    Symbol: sym,
                    Name: GetIndexQuoteTool.CompactName(row.ReadString("IsuNm")),
                    Quantity: row.ReadLong("BalQty") != 0 ? row.ReadLong("BalQty") : row.ReadLong("BnsBaseBalQty"),
                    SellableQuantity: row.ReadLong("SellAbleQty"),
                    AveragePrice: row.ReadDouble("AvrUprc"),
                    BepSellPrice: row.ReadDouble("SellPrc"),
                    BepBuyPrice: row.ReadDouble("BuyPrc"),
                    CurrentPrice: row.ReadDouble("NowPrc"),
                    EvaluationAmount: row.ReadLong("BalEvalAmt"),
                    EvaluationPnl: row.ReadLong("EvalPnl"),
                    EvaluationPnlPct: row.ReadDouble("PnlRat"),
                    PurchaseAmount: row.ReadLong("PchsAmt")));
            }

            var payload = new
            {
                summary,
                filter = new { symbol = symbolFilter },
                count = rows.Count,
                holdings = rows,
                _meta = new
                {
                    account_used = ToAccountEcho(resolved),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = ToAccountEcho(resolved) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = ToAccountEcho(resolved) }); }
    }

    [McpServerTool(Name = "ls_account_credit_limit")]
    [Description("""
        Returns the credit margin loan limits (융자/대주 한도) for one of your LS accounts via TR CSPAQ00600. Reports both the broker-wide limits and your remaining headroom for the selected loan type.

        USE WHEN: the user asks "신용한도", "융자 한도", "대주 한도", "담보비율" — anything about margin trading capacity.
        AVOID WHEN: the user is asking about regular cash-account orderable amounts — use ls_account_balance.

        `loan_type` defaults to '유통융자' (the most common case). `symbol` and `order_price` are required by LS (the limit calculation is symbol-aware); omitting them defaults to a low-impact probe (price=1 on the same default test symbol LS uses).
        """)]
    public static async Task<string> CreditLimit(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("Optional account_number or nickname. Omit to use the active mode's default account.")]
        string? account = null,
        [Description("Loan type: 'distribution_margin' (유통융자, default), 'self_margin' (자기융자), 'distribution_short' (유통대주), 'self_short' (자기대주').")]
        string? loan_type = null,
        [Description("6-digit Korean short code that anchors the limit calculation. Default '005930' (Samsung) as a low-impact probe.")]
        string? symbol = null,
        [Description("Order price reference for the limit calculation. Default 1.0.")]
        double? order_price = null,
        CancellationToken cancellationToken = default)
    {
        string loanCode = NormalizeLoanType(loan_type, out string? loanError);
        if (loanError is not null) return McpJson.Error(loanError);

        string sym = string.IsNullOrWhiteSpace(symbol) ? "005930" : symbol.Trim().ToUpperInvariant();
        double price = order_price ?? 1.0;

        Account resolved;
        try { resolved = await accountResolver.ResolveAsync(account, cancellationToken).ConfigureAwait(false); }
        catch (RequiresAccountException ex) { return McpJson.Error(ex.Message, new { error_code = ex.Code }); }
        catch (AmbiguousAccountException ex) { return McpJson.Error(ex.Message, new { error_code = ex.Code, candidates = ex.Candidates }); }
        catch (AccountNotFoundException ex) { return McpJson.Error(ex.Message, new { error_code = ex.Code, identifier = ex.Identifier, candidates = ex.Candidates }); }

        const string trCode = "CSPAQ00600";
        try
        {
            var body = new JsonObject
            {
                [$"{trCode}InBlock1"] = new JsonObject
                {
                    ["LoanDtlClssCode"] = loanCode,
                    ["IsuNo"] = $"A{sym}",
                    ["OrdPrc"] = price,
                    ["CommdaCode"] = "41",
                },
            };
            LsTrResponse response = await apiClient.SendAsync(new LsTrRequest(trCode, body), cancellationToken).ConfigureAwait(false);

            JsonElement? body2 = response.GetBlock($"{trCode}OutBlock2");
            if (!IsCspaqSuccess(response.RspCode, body2))
            {
                return McpJson.Error("LS reported a business-level error.", new
                {
                    tr_code = trCode,
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    account_used = ToAccountEcho(resolved),
                });
            }
            ArgumentNullException.ThrowIfNull(body2);

            JsonElement b = body2.Value;
            var payload = new
            {
                filter = new { loan_type = LoanLabel(loanCode), symbol = sym, order_price = price },
                limits = new
                {
                    account_name = (b.ReadString("AcntNm") ?? "").Trim(),
                    distribution_margin_limit = b.ReadLong("MktcplMloanLmtAmt"),
                    distribution_margin_used = b.ReadLong("MktcplMloanAmtSum"),
                    self_margin_limit = b.ReadLong("SfaccMloanLmtAmt"),
                    self_margin_used = b.ReadLong("SfaccMloanAmtSum"),
                    short_loan_limit = b.ReadLong("SloanLmtAmt"),
                    short_loan_used = b.ReadLong("SloanAmtSum"),
                    pledge_ratio_pct = b.ReadDouble("PldgRat"),
                    pledge_maintenance_ratio_pct = b.ReadDouble("PldgMaintRat"),
                    deposited_asset_sum = b.ReadLong("DpsastSum"),
                    orderable_amount = b.ReadLong("OrdAbleAmt"),
                    orderable_quantity = b.ReadLong("OrdAbleQty"),
                    receivable_unable_orderable_quantity = b.ReadLong("RcvblUablOrdAbleQty"),
                },
                _meta = new
                {
                    account_used = ToAccountEcho(resolved),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = ToAccountEcho(resolved) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = ToAccountEcho(resolved) }); }
    }

    [McpServerTool(Name = "ls_account_max_order_qty")]
    [Description("""
        Returns the maximum orderable quantity for a specific symbol / side / price triple via TR CSPBQ00200. INQUIRY ONLY — this never places an order. Lets the model answer "how many can I afford to buy at price X" without simulating any execution.

        USE WHEN: the user asks "최대 몇 주 살 수 있어", "주문 가능 수량", "지금 가격에 풀매수 하면 몇 주", or anywhere they want to know capacity before placing an order.
        AVOID WHEN: the user wants to actually place an order — v1.6 does NOT support order placement. v1.7 will ship ls_place_order with proper safety gating.

        Returns 증거금률별 (20% / 30% / 40% / 100% margin tiers) quantities so the model can distinguish between cash-only capacity and leveraged capacity. order_price=0 lets LS pick the current best quote automatically.
        """)]
    public static async Task<string> MaxOrderQty(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("6-digit Korean short code (e.g. '005930' Samsung).")]
        string symbol,
        [Description("Side: 'buy' (매수) or 'sell' (매도).")]
        string side,
        [Description("Optional account_number or nickname. Omit to use the active mode's default account.")]
        string? account = null,
        [Description("Order price reference. Default 0 — LS uses the current best quote.")]
        double order_price = 0.0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return McpJson.Error("symbol is required.");
        string bns = NormalizeOrderSide(side, out string? sideError);
        if (sideError is not null) return McpJson.Error(sideError);

        Account resolved;
        try { resolved = await accountResolver.ResolveAsync(account, cancellationToken).ConfigureAwait(false); }
        catch (RequiresAccountException ex) { return McpJson.Error(ex.Message, new { error_code = ex.Code }); }
        catch (AmbiguousAccountException ex) { return McpJson.Error(ex.Message, new { error_code = ex.Code, candidates = ex.Candidates }); }
        catch (AccountNotFoundException ex) { return McpJson.Error(ex.Message, new { error_code = ex.Code, identifier = ex.Identifier, candidates = ex.Candidates }); }

        const string trCode = "CSPBQ00200";
        try
        {
            string sym = symbol.Trim().ToUpperInvariant();
            var body = new JsonObject
            {
                [$"{trCode}InBlock1"] = new JsonObject
                {
                    ["BnsTpCode"] = bns,
                    ["IsuNo"] = $"A{sym}",
                    ["OrdPrc"] = order_price,
                },
            };
            LsTrResponse response = await apiClient.SendAsync(new LsTrRequest(trCode, body), cancellationToken).ConfigureAwait(false);

            JsonElement? body2 = response.GetBlock($"{trCode}OutBlock2");
            if (!IsCspaqSuccess(response.RspCode, body2))
            {
                return McpJson.Error("LS reported a business-level error.", new
                {
                    tr_code = trCode,
                    rsp_cd = response.RspCode,
                    rsp_msg = response.RspMessage,
                    account_used = ToAccountEcho(resolved),
                });
            }
            ArgumentNullException.ThrowIfNull(body2);

            JsonElement b = body2.Value;
            var payload = new
            {
                filter = new { symbol = sym, side = bns == "1" ? "sell" : "buy", order_price },
                capacity = new
                {
                    account_name = (b.ReadString("AcntNm") ?? "").Trim(),
                    symbol_name = (b.ReadString("IsuNm") ?? "").Trim(),
                    deposit = b.ReadLong("Dps"),
                    orderable_amount = b.ReadLong("OrdAbleAmt"),
                    orderable_quantity = b.ReadLong("OrdAbleQty"),
                    cash_orderable_amount = b.ReadLong("MnyOrdAbleAmt"),
                    kospi_orderable_amount = b.ReadLong("SeOrdAbleAmt"),
                    kosdaq_orderable_amount = b.ReadLong("KdqOrdAbleAmt"),
                    margin_rate_symbol_pct = b.ReadDouble("IsuMgnRat") * 100,
                    margin_rate_account_pct = b.ReadDouble("AcntMgnRat") * 100,
                    commission = b.ReadLong("Cmsn"),
                    commission_rate = b.ReadDouble("CmsnRat"),
                },
                margin_tiers = new
                {
                    pct20_orderable_quantity = b.ReadLong("MgnRat20OrdAbleQty"),
                    pct30_orderable_quantity = b.ReadLong("MgnRat30OrdAbleQty"),
                    pct40_orderable_quantity = b.ReadLong("MgnRat40OrdAbleQty"),
                    pct100_orderable_quantity = b.ReadLong("MgnRat100OrdAbleQty"),
                    pct100_cash_only_quantity = b.ReadLong("MgnRat100MnyOrdAbleQty"),
                },
                _meta = new
                {
                    account_used = ToAccountEcho(resolved),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = ToAccountEcho(resolved) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = ToAccountEcho(resolved) }); }
    }

    static string ExtractShcode(string isuNo)
    {
        // LS uses "A005930" for stock IsuNo (A + 6-digit shcode) and "KR7000020008" ISIN
        // in some TRs. The wrapper exposes the bare 6-digit shcode users actually type.
        if (string.IsNullOrEmpty(isuNo)) return "";
        string trimmed = isuNo.Trim();
        if (trimmed.Length == 7 && (trimmed[0] == 'A' || trimmed[0] == 'J'))
            return trimmed[1..].ToUpperInvariant();
        if (trimmed.Length == 12 && trimmed.StartsWith("KR7", StringComparison.OrdinalIgnoreCase))
            return trimmed.Substring(3, 6).ToUpperInvariant();
        return trimmed.ToUpperInvariant();
    }

    static string NormalizeLoanType(string? loanType, out string? error)
    {
        error = null;
        string normalized = string.IsNullOrWhiteSpace(loanType) ? "distribution_margin" : loanType.Trim().ToLowerInvariant();
        return normalized switch
        {
            "distribution_margin" or "유통융자" or "01" => "01",
            "self_margin" or "자기융자" or "03" => "03",
            "distribution_short" or "유통대주" or "05" => "05",
            "self_short" or "자기대주" or "07" => "07",
            _ => SetLoanError(loanType, out error),
        };
    }

    static string SetLoanError(string? raw, out string? error)
    {
        error = $"loan_type '{raw}' is not recognized. Use 'distribution_margin' (default), 'self_margin', 'distribution_short', or 'self_short'.";
        return "01";
    }

    static string LoanLabel(string code) => code switch
    {
        "01" => "distribution_margin",
        "03" => "self_margin",
        "05" => "distribution_short",
        "07" => "self_short",
        _ => code,
    };

    static string NormalizeOrderSide(string? side, out string? error)
    {
        error = null;
        string normalized = string.IsNullOrWhiteSpace(side) ? "" : side.Trim().ToLowerInvariant();
        return normalized switch
        {
            "sell" or "매도" or "1" => "1",
            "buy" or "매수" or "2" => "2",
            _ => SetOrderSideError(side, out error),
        };
    }

    static string SetOrderSideError(string? raw, out string? error)
    {
        error = $"side '{raw}' is required and must be 'buy' or 'sell'.";
        return "2";
    }

    /// <summary>
    /// /stock/accno family success check. LS routes account-inquiry TRs through
    /// a gateway that stamps non-<c>"00000"</c> rsp_cd values alongside
    /// "조회가 완료되었습니다." or "조회내역이 없습니다." — the docs catalog
    /// includes <c>00133</c> (FOCCQ33600 paginated success), <c>00136</c>
    /// (CSPAQ snapshot success), and <c>00200</c> (no-data success). Pair the
    /// known codes with a present expected block so genuine error codes still
    /// fall through to the business-error envelope.
    /// </summary>
    /// <remarks>See <see cref="LsTrResponse.SuccessCode"/> for the global default and
    /// <c>docs/LS-API-QUIRKS.md</c> §4.2b for the catalog.</remarks>
    static bool IsCspaqSuccess(string? rspCode, JsonElement? expectedBlock)
    {
        if (expectedBlock is null)
            return false;
        if (string.IsNullOrEmpty(rspCode))
            return false;
        return rspCode is LsTrResponse.SuccessCode or "00133" or "00136" or "00200";
    }

    static object ToAccountEcho(Account account) => new
    {
        account_number = account.AccountNo,
        nickname = account.Nickname,
        broker = account.Broker,
        mode = account.Mode,
        is_default = account.IsDefault,
    };

    static string NormalizeStatus(string? status, out string? error)
    {
        error = null;
        string normalized = string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToLowerInvariant();
        return normalized switch
        {
            "all" or "0" => "0",
            "filled" or "체결" or "1" => "1",
            "pending" or "미체결" or "2" => "2",
            _ => SetStatusError(status, out error),
        };
    }

    static string SetStatusError(string? raw, out string? error)
    {
        error = $"status '{raw}' is not recognized. Use 'all' (default), 'filled', or 'pending'.";
        return "0";
    }

    static string NormalizeSide(string? side, out string? error)
    {
        error = null;
        string normalized = string.IsNullOrWhiteSpace(side) ? "all" : side.Trim().ToLowerInvariant();
        return normalized switch
        {
            "all" or "0" => "0",
            "sell" or "매도" or "1" => "1",
            "buy" or "매수" or "2" => "2",
            _ => SetSideError(side, out error),
        };
    }

    static string SetSideError(string? raw, out string? error)
    {
        error = $"side '{raw}' is not recognized. Use 'all' (default), 'buy', or 'sell'.";
        return "0";
    }

    static string NormalizeSort(string? sort, out string? error)
    {
        error = null;
        string normalized = string.IsNullOrWhiteSpace(sort) ? "asc" : sort.Trim().ToLowerInvariant();
        return normalized switch
        {
            "asc" or "ascending" or "2" => "2",
            "desc" or "descending" or "1" => "1",
            _ => SetSortError(sort, out error),
        };
    }

    static string SetSortError(string? raw, out string? error)
    {
        error = $"sort '{raw}' is not recognized. Use 'asc' (default) or 'desc'.";
        return "2";
    }

    static string StatusLabel(string chegb) => chegb switch
    {
        "1" => "filled",
        "2" => "pending",
        _ => "all",
    };

    static string SideLabel(string medosu) => medosu switch
    {
        "1" => "sell",
        "2" => "buy",
        _ => "all",
    };

    static string? FormatHmsFraction(string? raw)
    {
        // LS returns ordtime as HHMMSSff (8 chars, last 2 are 1/100 sec).
        // Normalize to HH:MM:SS.ff so the model doesn't mistake it for a date.
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string trimmed = raw.Trim();
        if (trimmed.Length != 8 || !trimmed.All(char.IsDigit)) return trimmed;
        return $"{trimmed.AsSpan(0, 2)}:{trimmed.AsSpan(2, 2)}:{trimmed.AsSpan(4, 2)}.{trimmed.AsSpan(6, 2)}";
    }

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

    sealed class BepSummary
    {
        public string AccountName { get; set; } = "";
        public long Deposit { get; set; }
        public long EvaluationAmount { get; set; }
        public long PurchaseAmount { get; set; }
        public long EvaluationPnlSum { get; set; }
        public double PnlPct { get; set; }
        public long InvestmentOriginal { get; set; }
        public long InvestmentPnl { get; set; }
    }

    sealed record BepRow(
        string Symbol,
        string Name,
        long Quantity,
        long SellableQuantity,
        double AveragePrice,
        double BepSellPrice,
        double BepBuyPrice,
        double CurrentPrice,
        long EvaluationAmount,
        long EvaluationPnl,
        double EvaluationPnlPct,
        long PurchaseAmount);

    sealed class BalancePayload
    {
        public string BranchName { get; set; } = "";
        public string AccountName { get; set; } = "";
        // Common fields (both real and virtual TRs return these).
        public long Deposit { get; set; }
        public long D1Deposit { get; set; }
        public long D2Deposit { get; set; }
        public long CashOrderableAmount { get; set; }
        public long SubstituteOrderableAmount { get; set; }
        public long SubstituteAmount { get; set; }
        public long KospiOrderableAmount { get; set; }
        public long KosdaqOrderableAmount { get; set; }
        public long CreditOrderableAmount { get; set; }
        public long CreditCollateralOrderAmount { get; set; }
        public long MarginRate100Orderable { get; set; }
        public long MarginRate50Orderable { get; set; }
        public long MarginRate35Orderable { get; set; }
        public long ReceivableAmount { get; set; }
        public long LoanAmount { get; set; }
        // Real-mode only fields — omitted from JSON when null so the virtual
        // shape doesn't carry zeros that look like "definitely zero" answers.
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public long? WithdrawableAmount { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public long? EvaluationAmount { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public long? DepositedAssetTotal { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public double? PnlPct { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public long? InvestmentOriginal { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public long? InvestmentPnl { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public long? D1WithdrawablePresumed { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public long? D2WithdrawablePresumed { get; set; }
    }

    sealed class OrdersSummary
    {
        public long TotalOrderQuantity { get; set; }
        public long TotalFilledQuantity { get; set; }
        public long TotalPendingQuantity { get; set; }
        public long EstimatedFee { get; set; }
        public long EstimatedTax { get; set; }
        public long TotalOrderAmount { get; set; }
        public long TotalSellFilledAmount { get; set; }
        public long TotalBuyFilledAmount { get; set; }
    }

    sealed record OrderRow(
        long OrderNo,
        long OriginalOrderNo,
        string Symbol,
        string Side,
        string OrderType,
        string QuoteType,
        long OrderQuantity,
        long OrderPrice,
        long FilledQuantity,
        long FilledPrice,
        long PendingQuantity,
        long ConfirmedQuantity,
        string Status,
        string? OrderTime,
        string OrderChannel,
        long CurrentPrice,
        string CreditType,
        string? LoanDate,
        string Exchange);

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
