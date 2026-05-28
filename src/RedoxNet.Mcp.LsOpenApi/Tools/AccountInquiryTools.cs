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
        Returns the LIVE LS broker holdings for the appkey-bound account via TR t0424. Real-time read of what LS actually holds — separate from the manually-tracked ls_holdings_list (which lives in the local portfolio.db).

        USE WHEN: the user asks "내 LS 계좌 잔고", "실제 보유 종목", "지금 LS 증권에 뭐가 있어?", or any phrasing that asks for the broker's view of positions.
        AVOID WHEN: the user wants their paper-portfolio or manually-recorded holdings — use ls_holdings_list. AVOID WHEN: the user wants the watchlist — use ls_watchlist.

        No `account` argument — LS account-inquiry TRs do not accept AcntNo in the request, the appkey's authenticated session resolves to one subaccount server-side. The label in _meta.account_used is whatever LS returns; nicknames can be attached via ls_account(action="set_live_nickname").

        Output: per-symbol rows + a portfolio summary (estimated net assets, deposit, total evaluation / P&L), plus _meta.account_used / data_as_of / tr_code / source="live".
        """)]
    public static async Task<string> Holdings(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        CancellationToken cancellationToken = default)
    {
        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

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
                        account_used = accountResolver.BuildEcho(registered),
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
                    account_used = accountResolver.BuildEcho(registered),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = "t0424",
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("LS authentication failed for the account inquiry. Verify LS_APPKEY / LS_APPSECRETKEY / LS_MARKET match the appkey-bound LS account.",
                new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new
            {
                reason = ex.Message,
                status = ex.StatusCode,
                tr_code = "t0424",
                account_used = accountResolver.BuildEcho(registered),
            });
        }
    }

    [McpServerTool(Name = "ls_account_orders")]
    [Description("""
        Returns today's orders (filled + pending) for one of your LS broker accounts via TR t0425. Real-time read from LS — no cache.

        USE WHEN: the user asks "오늘 주문", "체결됐나", "미체결 남은 거", "오늘 매매 내역", or wants the live order book for the active account.
        AVOID WHEN: the user wants a historical multi-day order log — use ls_account_order_history (TR CSPAQ13700) instead. AVOID WHEN: the user wants positions — use ls_account_holdings.

        Filters: `status` ("all" default / "filled" / "pending") maps to LS chegb. `side` ("all" / "buy" / "sell") maps to medosu (매수/매도). `symbol` narrows to a specific shcode. Sort default is ascending by order number (older first).

        Like every ls_account_* tool, no `account` argument — the appkey's session resolves to the bound LS subaccount. _meta carries account_used / data_as_of / tr_code / source="live".
        """)]
    public static async Task<string> Orders(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
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

        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

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
                        account_used = accountResolver.BuildEcho(registered),
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
                    account_used = accountResolver.BuildEcho(registered),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = "t0425",
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("LS authentication failed for the account inquiry. Verify LS_APPKEY / LS_APPSECRETKEY / LS_MARKET match the appkey-bound LS account.",
                new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new
            {
                reason = ex.Message,
                status = ex.StatusCode,
                tr_code = "t0425",
                account_used = accountResolver.BuildEcho(registered),
            });
        }
    }

    [McpServerTool(Name = "ls_account_balance")]
    [Description("""
        Returns the LIVE cash, buying power, and total valuation for one of your LS broker accounts via TR CSPAQ12200. Reports the full asset snapshot: deposit, D1/D2, orderable amounts (cash / kospi / kosdaq / margin tiers), substitute amount, evaluation amount, deposited asset total, investment principal, investment P&L, and pledge ratios.

        USE WHEN: the user asks "예수금 얼마야", "내 잔고", "주문가능금액", "총 평가금액", "투자원금 대비 손익" — anything about cash, buying power, or roll-up portfolio value.
        AVOID WHEN: the user wants per-symbol positions — use ls_account_holdings. AVOID WHEN: the user wants per-day P&L history — use ls_account_performance.

        Like every ls_account_* tool, no `account` argument — appkey resolves the bound LS subaccount server-side; CSPAQ12200 additionally returns BrnNm / AcntNm which the wrapper caches into the live registry on first call. _meta carries account_used, data_as_of, tr_code, and source="live".
        """)]
    public static async Task<string> Balance(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        CancellationToken cancellationToken = default)
    {
        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

        // CSPAQ22200 is a v2 of CSPAQ12200, not a virtual variant — both TRs
        // work with both real and virtual appkey pairs and return whatever
        // account the appkey is tied to. The real/virtual distinction lives
        // in the appkey, not in the TR code (see docs/LS-API-QUIRKS.md §4.2b).
        // v12200 carries the richer field set (evaluation amount, investment
        // P&L, withdrawable presumed), so we always send it.
        const string trCode = "CSPAQ12200";
        string outBlock2Name = $"{trCode}OutBlock2";
        string inBlockName = $"{trCode}InBlock1";

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
                    account_used = accountResolver.BuildEcho(registered),
                });
            }
            // After the success check, body2 cannot be null per IsCspaqSuccess.
            ArgumentNullException.ThrowIfNull(body2);

            JsonElement b = body2.Value;
            string branchName = (b.ReadString("BrnNm") ?? "").Trim();
            string accountName = (b.ReadString("AcntNm") ?? "").Trim();
            var balance = new BalancePayload
            {
                BranchName = branchName,
                AccountName = accountName,
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

            // CSPAQ12200 returns the full field set regardless of which
            // appkey pair (real or virtual) was used to obtain the token.
            // Some fields may be zero on virtual accounts that don't track
            // investment basis, but we surface them either way — a zero is
            // a real value, not a missing field.
            balance.WithdrawableAmount = b.ReadLong("MnyoutAbleAmt");
            balance.EvaluationAmount = b.ReadLong("BalEvalAmt");
            balance.DepositedAssetTotal = b.ReadLong("DpsastTotamt");
            balance.PnlPct = b.ReadDouble("PnlRat");
            balance.InvestmentOriginal = b.ReadLong("InvstOrgAmt");
            balance.InvestmentPnl = b.ReadLong("InvstPlAmt");
            balance.D1WithdrawablePresumed = b.ReadLong("D1PrsmptWthdwAbleAmt");
            balance.D2WithdrawablePresumed = b.ReadLong("D2PrsmptWthdwAbleAmt");

            LsLiveAccountInfo accountEcho = await BuildSuccessEchoAsync(
                registered, response, $"{trCode}OutBlock1", accountResolver, cancellationToken,
                branchName: branchName, accountName: accountName).ConfigureAwait(false);
            var payload = new
            {
                balance,
                _meta = new
                {
                    account_used = accountEcho,
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex)
        {
            return McpJson.Error("LS authentication failed for the account inquiry. Verify LS_APPKEY / LS_APPSECRETKEY / LS_MARKET match the appkey-bound LS account.",
                new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) });
        }
        catch (LsTrException ex)
        {
            return McpJson.Error("TR call failed.", new
            {
                reason = ex.Message,
                status = ex.StatusCode,
                tr_code = trCode,
                account_used = accountResolver.BuildEcho(registered),
            });
        }
    }

    [McpServerTool(Name = "ls_account_bep")]
    [Description("""
        Returns the break-even price (BEP 단가) per holding via TR CSPAQ12300. BEP is the price you'd need to sell at to fully cover commissions and taxes — different from the raw average purchase price.

        USE WHEN: the user asks "BEP 단가", "손익분기 단가", "수수료 포함 평단", or wants the after-fee breakeven on their LS positions.
        AVOID WHEN: the user wants just the simple average purchase price — that's already in ls_account_holdings.average_price.

        `symbol` optionally narrows to a single 6-digit code (Korean shcode); the LS field IsuNo accepts the "A+code" form but the wrapper takes the bare 6-digit shcode for the user. Like every ls_account_* tool, no `account` argument — the appkey-bound LS subaccount answers.
        """)]
    public static async Task<string> Bep(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("Optional 6-digit Korean short code (e.g. '005930'). Omit to return BEP for every holding.")]
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

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
                    account_used = accountResolver.BuildEcho(registered),
                });
            }
            ArgumentNullException.ThrowIfNull(body3);

            JsonElement? body2 = response.GetBlock($"{trCode}OutBlock2");
            BepSummary? summary = null;
            string? acntNmCache = null;
            if (body2 is not null)
            {
                JsonElement s = body2.Value;
                acntNmCache = (s.ReadString("AcntNm") ?? "").Trim();
                summary = new BepSummary
                {
                    AccountName = acntNmCache,
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

            LsLiveAccountInfo accountEcho = await BuildSuccessEchoAsync(
                registered, response, $"{trCode}OutBlock1", accountResolver, cancellationToken,
                accountName: acntNmCache).ConfigureAwait(false);
            var payload = new
            {
                summary,
                filter = new { symbol = symbolFilter },
                count = rows.Count,
                holdings = rows,
                _meta = new
                {
                    account_used = accountEcho,
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = accountResolver.BuildEcho(registered) }); }
    }

    [McpServerTool(Name = "ls_account_credit_limit")]
    [Description("""
        Returns the credit margin loan limits (융자/대주 한도) for one of your LS accounts via TR CSPAQ00600. Reports both the broker-wide limits and your remaining headroom for the selected loan type.

        USE WHEN: the user asks "신용한도", "융자 한도", "대주 한도", "담보비율" — anything about margin trading capacity.
        AVOID WHEN: the user is asking about regular cash-account orderable amounts — use ls_account_balance.

        `loan_type` defaults to '유통융자' (the most common case). `symbol` and `order_price` are required by LS (the limit calculation is symbol-aware); omitting them defaults to a low-impact probe (price=1 on the same default test symbol LS uses).

        Like every ls_account_* tool, no `account` argument.
        """)]
    public static async Task<string> CreditLimit(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
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

        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

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
                    account_used = accountResolver.BuildEcho(registered),
                });
            }
            ArgumentNullException.ThrowIfNull(body2);

            JsonElement b = body2.Value;
            string acntNmCl = (b.ReadString("AcntNm") ?? "").Trim();
            LsLiveAccountInfo accountEcho = await BuildSuccessEchoAsync(
                registered, response, $"{trCode}OutBlock1", accountResolver, cancellationToken,
                accountName: acntNmCl).ConfigureAwait(false);
            var payload = new
            {
                filter = new { loan_type = LoanLabel(loanCode), symbol = sym, order_price = price },
                limits = new
                {
                    account_name = acntNmCl,
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
                    account_used = accountEcho,
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = accountResolver.BuildEcho(registered) }); }
    }

    [McpServerTool(Name = "ls_account_max_order_qty")]
    [Description("""
        Returns the maximum orderable quantity for a specific symbol / side / price triple via TR CSPBQ00200. INQUIRY ONLY — this never places an order. Lets the model answer "how many can I afford to buy at price X" without simulating any execution.

        USE WHEN: the user asks "최대 몇 주 살 수 있어", "주문 가능 수량", "지금 가격에 풀매수 하면 몇 주", or anywhere they want to know capacity before placing an order.
        AVOID WHEN: the user wants to actually place an order — v1.6 does NOT support order placement. v1.7 will ship ls_place_order with proper safety gating.

        Returns 증거금률별 (20% / 30% / 40% / 100% margin tiers) quantities so the model can distinguish between cash-only capacity and leveraged capacity. order_price=0 lets LS pick the current best quote automatically.

        Like every ls_account_* tool, no `account` argument.
        """)]
    public static async Task<string> MaxOrderQty(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("6-digit Korean short code (e.g. '005930' Samsung).")]
        string symbol,
        [Description("Side: 'buy' (매수) or 'sell' (매도).")]
        string side,
        [Description("Order price reference. Default 0 — LS uses the current best quote.")]
        double order_price = 0.0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return McpJson.Error("symbol is required.");
        string bns = NormalizeOrderSide(side, out string? sideError);
        if (sideError is not null) return McpJson.Error(sideError);

        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

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
                    account_used = accountResolver.BuildEcho(registered),
                });
            }
            ArgumentNullException.ThrowIfNull(body2);

            JsonElement b = body2.Value;
            string acntNmMoq = (b.ReadString("AcntNm") ?? "").Trim();
            LsLiveAccountInfo accountEcho = await BuildSuccessEchoAsync(
                registered, response, $"{trCode}OutBlock1", accountResolver, cancellationToken,
                accountName: acntNmMoq).ConfigureAwait(false);
            var payload = new
            {
                filter = new { symbol = sym, side = bns == "1" ? "sell" : "buy", order_price },
                capacity = new
                {
                    account_name = acntNmMoq,
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
                    account_used = accountEcho,
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = accountResolver.BuildEcho(registered) }); }
    }

    [McpServerTool(Name = "ls_account_order_history")]
    [Description("""
        Returns the order/fill history for a specific date via TR CSPAQ13700. Per-order rows include the entire lifecycle — placed / modified / cancelled / executed — so the model sees both the live orders and what they ultimately became.

        USE WHEN: the user asks "어제 주문 내역", "지난 주문 다 보여줘", or wants a forensic per-order log on a specific date.
        AVOID WHEN: the user wants today's live order book — use ls_account_orders. AVOID WHEN: they want raw transaction history including deposits — use ls_account_transactions.

        `order_date` defaults to today. Filters: `status` (all / filled / pending), `side` (all / buy / sell), `symbol`, `market` (all / kospi / kosdaq / freeboard), `sort` (asc default / desc).

        Like every ls_account_* tool, no `account` argument.
        """)]
    public static async Task<string> OrderHistory(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("Date to inspect (YYYY-MM-DD or YYYYMMDD). Default = today.")]
        string? order_date = null,
        [Description("Status filter: 'all' (default), 'filled', or 'pending'.")]
        string? status = null,
        [Description("Side filter: 'all' (default), 'buy', or 'sell'.")]
        string? side = null,
        [Description("Optional 6-digit shcode. Omit for all symbols.")]
        string? symbol = null,
        [Description("Market filter: 'all' (default), 'kospi', 'kosdaq', 'freeboard'.")]
        string? market = null,
        [Description("Sort: 'asc' (default, oldest first) or 'desc'.")]
        string? sort = null,
        CancellationToken cancellationToken = default)
    {
        string ord = NormalizeYmdInput(order_date, out string? dateError, defaultToToday: true);
        if (dateError is not null) return McpJson.Error(dateError);
        string statusCode = NormalizeExecStatus(status, out string? statusError);
        if (statusError is not null) return McpJson.Error(statusError);
        string sideCode = NormalizeSide(side, out string? sideError);
        if (sideError is not null) return McpJson.Error(sideError);
        string marketCode = NormalizeOrderMarket(market, out string? marketError);
        if (marketError is not null) return McpJson.Error(marketError);
        string bkseq = NormalizeBkSort(sort, out string? sortError);
        if (sortError is not null) return McpJson.Error(sortError);

        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

        const string trCode = "CSPAQ13700";
        try
        {
            string sym = string.IsNullOrWhiteSpace(symbol) ? "" : $"A{symbol.Trim().ToUpperInvariant()}";
            long startOrdNo = bkseq == "0" ? 999999999L : 0L;

            var summary = new OrderHistorySummary();
            var rows = new List<OrderHistoryRow>();
            long cursorOrdNo = startOrdNo;
            string? continuationKey = null;
            string? discoveredAcntNo = null;

            for (int page = 0; page < MaxOrdersPages; page++)
            {
                var body = new JsonObject
                {
                    [$"{trCode}InBlock1"] = new JsonObject
                    {
                        ["OrdMktCode"] = marketCode,
                        ["BnsTpCode"] = sideCode,
                        ["IsuNo"] = sym,
                        ["ExecYn"] = statusCode,
                        ["OrdDt"] = ord,
                        ["SrtOrdNo2"] = cursorOrdNo,
                        ["BkseqTpCode"] = bkseq,
                        ["OrdPtnCode"] = "00",
                    },
                };
                LsTrResponse response = await apiClient.SendAsync(new LsTrRequest(trCode, body, continuationKey), cancellationToken).ConfigureAwait(false);

                JsonElement? body3 = response.GetBlock($"{trCode}OutBlock3");
                if (!IsCspaqSuccess(response.RspCode, body3))
                {
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        tr_code = trCode,
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        account_used = accountResolver.BuildEcho(registered),
                    });
                }
                ArgumentNullException.ThrowIfNull(body3);

                // Auto-discovery has to happen on the first successful page
                // regardless of whether pagination continues. Single-page
                // responses (common case for narrow date filters) skip the
                // post-loop tail otherwise, leaving _meta.account_used as
                // synthetic discovered=false even though OutBlock1 echoed
                // the AcntNo. See E2E-v1.6-2nd_claude_codex.txt.
                if (page == 0 && discoveredAcntNo is null)
                    discoveredAcntNo = TryReadAcntNo(response, $"{trCode}OutBlock1");

                if (page == 0)
                {
                    JsonElement? body2 = response.GetBlock($"{trCode}OutBlock2");
                    if (body2 is not null)
                    {
                        JsonElement s = body2.Value;
                        summary.SellFilledAmount = s.ReadLong("SellExecAmt");
                        summary.BuyFilledAmount = s.ReadLong("BuyExecAmt");
                        summary.SellFilledQuantity = s.ReadLong("SellExecQty");
                        summary.BuyFilledQuantity = s.ReadLong("BuyExecQty");
                        summary.SellOrderQuantity = s.ReadLong("SellOrdQty");
                        summary.BuyOrderQuantity = s.ReadLong("BuyOrdQty");
                    }
                }

                int beforeCount = rows.Count;
                foreach (JsonElement row in body3.Value.EnumerateArray())
                {
                    string isuNo = (row.ReadString("IsuNo") ?? "").Trim();
                    rows.Add(new OrderHistoryRow(
                        OrderNo: row.ReadLong("OrdNo"),
                        OriginalOrderNo: row.ReadLong("OrgOrdNo"),
                        OrderDate: NormalizeYmd(row.ReadString("OrdDt")),
                        Symbol: ExtractShcode(isuNo),
                        Name: GetIndexQuoteTool.CompactName(row.ReadString("IsuNm")),
                        Side: (row.ReadString("BnsTpNm") ?? "").Trim(),
                        OrderType: (row.ReadString("OrdPtnNm") ?? "").Trim(),
                        ProcessingType: (row.ReadString("OrdTrxPtnNm") ?? "").Trim(),
                        ModifyOrCancel: (row.ReadString("MrcTpNm") ?? "").Trim(),
                        OrderQuantity: row.ReadLong("OrdQty"),
                        OrderPrice: row.ReadDouble("OrdPrc"),
                        FilledQuantity: row.ReadLong("ExecQty"),
                        FilledPrice: row.ReadDouble("ExecPrc"),
                        AllFilledQuantity: row.ReadLong("AllExecQty"),
                        OrderTime: FormatHmsFraction(row.ReadString("OrdTime")),
                        LastFilledTime: FormatHmsFraction(row.ReadString("LastExecTime")),
                        QuoteType: (row.ReadString("OrdprcPtnNm") ?? "").Trim(),
                        OrderChannel: (row.ReadString("CommdaNm") ?? "").Trim()));
                }

                if (rows.Count == beforeCount)
                    break;
                if (!response.HasContinuation || string.IsNullOrEmpty(response.ContinuationKey))
                    break;
                continuationKey = response.ContinuationKey;
                cursorOrdNo = rows[^1].OrderNo;
            }

            LsLiveAccount? upserted = await accountResolver
                .RecordDiscoveredAsync(discoveredAcntNo, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            registered = upserted ?? registered;
            var payload = new
            {
                filter = new
                {
                    order_date = FormatYmdDisplay(ord),
                    status = ExecStatusLabel(statusCode),
                    side = SideLabel(sideCode),
                    symbol = string.IsNullOrEmpty(sym) ? null : sym[1..],
                    market = MarketLabel(marketCode),
                    sort = bkseq == "0" ? "desc" : "asc",
                },
                summary,
                count = rows.Count,
                orders = rows,
                _meta = new
                {
                    account_used = accountResolver.BuildEcho(registered, discoveredAcntNo),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = accountResolver.BuildEcho(registered) }); }
    }

    [McpServerTool(Name = "ls_account_transactions")]
    [Description("""
        Returns the full transaction log over a date range via TR CDPCQ04700 — deposits, withdrawals, fills, transfers, all in one timeline. Differs from ls_account_order_history (orders only) and ls_account_balance (current snapshot).

        USE WHEN: the user asks "거래내역", "입출금 내역", "통장", or wants reconciliation across a period.
        Filter `kind` ('all' default / 'cashflow' (입출금) / 'transfer' (입출고) / 'trade' (매매) / 'fx' (환전) / 'misc' (기타)) lets the model narrow without re-querying.

        Date range defaults to the past 7 days; both bounds inclusive.

        Like every ls_account_* tool, no `account` argument.
        """)]
    public static async Task<string> Transactions(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("Start date YYYY-MM-DD or YYYYMMDD. Default = 7 days ago.")]
        string? start_date = null,
        [Description("End date YYYY-MM-DD or YYYYMMDD. Default = today.")]
        string? end_date = null,
        [Description("Kind filter: 'all' (default), 'cashflow' (입출금), 'transfer' (입출고), 'trade' (매매), 'fx' (환전), 'misc' (기타).")]
        string? kind = null,
        [Description("Optional 6-digit shcode to narrow to one symbol.")]
        string? symbol = null,
        CancellationToken cancellationToken = default)
    {
        string startYmd = NormalizeYmdInput(start_date, out string? startError, defaultToToday: false, defaultDaysBack: 7);
        if (startError is not null) return McpJson.Error(startError);
        string endYmd = NormalizeYmdInput(end_date, out string? endError, defaultToToday: true);
        if (endError is not null) return McpJson.Error(endError);
        string qryTp = NormalizeTransactionKind(kind, out string? kindError);
        if (kindError is not null) return McpJson.Error(kindError);

        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

        const string trCode = "CDPCQ04700";
        try
        {
            string sym = string.IsNullOrWhiteSpace(symbol) ? "" : $"A{symbol.Trim().ToUpperInvariant()}";
            var rows = new List<TransactionRow>();
            long srtNo = 0;
            string? continuationKey = null;
            string? discoveredAcntNo = null;

            for (int page = 0; page < MaxOrdersPages; page++)
            {
                var body = new JsonObject
                {
                    [$"{trCode}InBlock1"] = new JsonObject
                    {
                        ["QryTp"] = qryTp,
                        ["QrySrtDt"] = startYmd,
                        ["QryEndDt"] = endYmd,
                        ["SrtNo"] = srtNo,
                        ["PdptnCode"] = "01",
                        ["IsuLgclssCode"] = "00",
                        ["IsuNo"] = sym,
                    },
                };
                LsTrResponse response = await apiClient.SendAsync(new LsTrRequest(trCode, body, continuationKey), cancellationToken).ConfigureAwait(false);

                JsonElement? body3 = response.GetBlock($"{trCode}OutBlock3");
                if (!IsCspaqSuccess(response.RspCode, body3))
                {
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        tr_code = trCode,
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        account_used = accountResolver.BuildEcho(registered),
                    });
                }
                ArgumentNullException.ThrowIfNull(body3);

                // Auto-discovery on the first successful page — see the
                // ls_account_order_history twin for the rationale.
                if (page == 0 && discoveredAcntNo is null)
                    discoveredAcntNo = TryReadAcntNo(response, $"{trCode}OutBlock1");

                int beforeCount = rows.Count;
                foreach (JsonElement row in body3.Value.EnumerateArray())
                {
                    string isuNo = (row.ReadString("IsuNo") ?? "").Trim();
                    rows.Add(new TransactionRow(
                        TradeDate: NormalizeYmd(row.ReadString("TrdDt")),
                        TradeNo: row.ReadLong("TrdNo"),
                        CategoryName: (row.ReadString("TpCodeNm") ?? "").Trim(),
                        SummaryName: (row.ReadString("SmryNm") ?? "").Trim(),
                        CancellationName: (row.ReadString("CancTpNm") ?? "").Trim(),
                        Symbol: string.IsNullOrEmpty(isuNo) ? "" : ExtractShcode(isuNo),
                        Name: GetIndexQuoteTool.CompactName(row.ReadString("IsuNm")),
                        Quantity: row.ReadLong("TrdQty"),
                        UnitPrice: row.ReadDouble("TrdUprc"),
                        Amount: row.ReadLong("TrdAmt"),
                        Commission: row.ReadLong("CmsnAmt"),
                        TransactionTax: row.ReadLong("Trtax"),
                        TaxSum: row.ReadLong("TaxSumAmt"),
                        SettlementAmount: row.ReadLong("AdjstAmt"),
                        DepositBalanceAfter: row.ReadLong("DpsCrbalAmt"),
                        ProcessingTime: FormatHmsFraction(row.ReadString("TrxTime")),
                        Channel: (row.ReadString("TrdmdaNm") ?? "").Trim()));
                }

                if (rows.Count == beforeCount)
                    break;
                if (!response.HasContinuation || string.IsNullOrEmpty(response.ContinuationKey))
                    break;
                continuationKey = response.ContinuationKey;
                srtNo = rows[^1].TradeNo;
            }

            LsLiveAccount? upserted = await accountResolver
                .RecordDiscoveredAsync(discoveredAcntNo, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            registered = upserted ?? registered;
            var payload = new
            {
                filter = new
                {
                    start_date = FormatYmdDisplay(startYmd),
                    end_date = FormatYmdDisplay(endYmd),
                    kind = TransactionKindLabel(qryTp),
                    symbol = string.IsNullOrEmpty(sym) ? null : sym[1..],
                },
                count = rows.Count,
                transactions = rows,
                _meta = new
                {
                    account_used = accountResolver.BuildEcho(registered, discoveredAcntNo),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = accountResolver.BuildEcho(registered) }); }
    }

    [McpServerTool(Name = "ls_account_performance")]
    [Description("""
        Returns period P&L for the account via TR FOCCQ33600 — total invested principal, period return %, and a per-period breakdown (daily / weekly / monthly).

        USE WHEN: the user asks "이번 달 수익률", "지난 분기 손익", "주간 성과", or any time-bucketed performance question.
        Date range defaults to the last 30 days; `term` chooses bucket granularity (daily / weekly / monthly).

        Like every ls_account_* tool, no `account` argument.
        """)]
    public static async Task<string> Performance(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("Start date YYYY-MM-DD or YYYYMMDD. Default = 30 days ago.")]
        string? start_date = null,
        [Description("End date YYYY-MM-DD or YYYYMMDD. Default = today.")]
        string? end_date = null,
        [Description("Bucket: 'daily' (default), 'weekly', or 'monthly'.")]
        string? term = null,
        CancellationToken cancellationToken = default)
    {
        string startYmd = NormalizeYmdInput(start_date, out string? startError, defaultToToday: false, defaultDaysBack: 30);
        if (startError is not null) return McpJson.Error(startError);
        string endYmd = NormalizeYmdInput(end_date, out string? endError, defaultToToday: true);
        if (endError is not null) return McpJson.Error(endError);
        string termCode = NormalizePerformanceTerm(term, out string? termError);
        if (termError is not null) return McpJson.Error(termError);

        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

        const string trCode = "FOCCQ33600";
        try
        {
            PerformanceSummary? summary = null;
            var rows = new List<PerformanceRow>();
            string? continuationKey = null;
            string? discoveredAcntNo = null;

            for (int page = 0; page < MaxOrdersPages; page++)
            {
                var body = new JsonObject
                {
                    [$"{trCode}InBlock1"] = new JsonObject
                    {
                        ["QrySrtDt"] = startYmd,
                        ["QryEndDt"] = endYmd,
                        ["TermTp"] = termCode,
                    },
                };
                LsTrResponse response = await apiClient.SendAsync(new LsTrRequest(trCode, body, continuationKey), cancellationToken).ConfigureAwait(false);

                JsonElement? body3 = response.GetBlock($"{trCode}OutBlock3");
                if (!IsCspaqSuccess(response.RspCode, body3))
                {
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        tr_code = trCode,
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        account_used = accountResolver.BuildEcho(registered),
                    });
                }
                ArgumentNullException.ThrowIfNull(body3);

                // Auto-discovery on the first successful page — see the
                // ls_account_order_history twin for the rationale.
                if (page == 0 && discoveredAcntNo is null)
                    discoveredAcntNo = TryReadAcntNo(response, $"{trCode}OutBlock1");

                if (page == 0)
                {
                    JsonElement? body2 = response.GetBlock($"{trCode}OutBlock2");
                    if (body2 is not null)
                    {
                        JsonElement s = body2.Value;
                        summary = new PerformanceSummary
                        {
                            AccountName = (s.ReadString("AcntNm") ?? "").Trim(),
                            TradedAmount = s.ReadLong("BnsctrAmt"),
                            DepositedAmount = s.ReadLong("MnyinAmt"),
                            WithdrawnAmount = s.ReadLong("MnyoutAmt"),
                            InvestmentAveragePrincipal = s.ReadLong("InvstAvrbalPramt"),
                            InvestmentPnl = s.ReadLong("InvstPlAmt"),
                            InvestmentReturnPct = s.ReadDouble("InvstErnrat"),
                        };
                    }
                }

                int beforeCount = rows.Count;
                foreach (JsonElement row in body3.Value.EnumerateArray())
                {
                    rows.Add(new PerformanceRow(
                        BaseDate: NormalizeYmd(row.ReadString("BaseDt")),
                        OpeningEvaluation: row.ReadLong("FdEvalAmt"),
                        ClosingEvaluation: row.ReadLong("EotEvalAmt"),
                        AveragePrincipal: row.ReadLong("InvstAvrbalPramt"),
                        TradedAmount: row.ReadLong("BnsctrAmt"),
                        InflowAmount: row.ReadLong("MnyinSecinAmt"),
                        OutflowAmount: row.ReadLong("MnyoutSecoutAmt"),
                        EvaluationPnl: row.ReadLong("EvalPnlAmt"),
                        PeriodReturnPct: row.ReadDouble("TermErnrat"),
                        Index: row.ReadDouble("Idx")));
                }

                if (rows.Count == beforeCount)
                    break;
                if (!response.HasContinuation || string.IsNullOrEmpty(response.ContinuationKey))
                    break;
                continuationKey = response.ContinuationKey;
            }

            string? acntNmPerf = summary?.AccountName is { Length: > 0 } accName ? accName : null;
            LsLiveAccount? upserted = await accountResolver
                .RecordDiscoveredAsync(discoveredAcntNo, accountName: acntNmPerf, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            registered = upserted ?? registered;
            var payload = new
            {
                filter = new
                {
                    start_date = FormatYmdDisplay(startYmd),
                    end_date = FormatYmdDisplay(endYmd),
                    term = PerformanceTermLabel(termCode),
                },
                summary,
                count = rows.Count,
                periods = rows,
                _meta = new
                {
                    account_used = accountResolver.BuildEcho(registered, discoveredAcntNo),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = accountResolver.BuildEcho(registered) }); }
    }

    [McpServerTool(Name = "ls_account_daily_pnl")]
    [Description("""
        Returns the trade log + commission/tax summary for a single trading day via TR t0150 (today) or t0151 (any other date). Aggregate sell/buy quantities, fees, and net settlement; plus per-trade rows.

        USE WHEN: the user asks "오늘 매매일지", "어제 매수/매도 내역", "수수료 얼마 냈어", or wants the day-level closing log with fees.
        AVOID WHEN: the user wants a multi-day period — use ls_account_performance. AVOID WHEN: the user wants every transaction including deposits — use ls_account_transactions.

        `date` defaults to today (t0150). Any other date uses t0151. The wrapper picks the right TR automatically.

        Like every ls_account_* tool, no `account` argument.
        """)]
    public static async Task<string> DailyPnl(
        LsApiClient apiClient,
        LsAccountResolver accountResolver,
        [Description("Date YYYY-MM-DD / YYYYMMDD / 'today' / 'yesterday'. Default = today.")]
        string? date = null,
        CancellationToken cancellationToken = default)
    {
        bool isToday = string.IsNullOrWhiteSpace(date)
            || string.Equals(date.Trim(), "today", StringComparison.OrdinalIgnoreCase)
            || string.Equals(date.Trim(), "오늘", StringComparison.OrdinalIgnoreCase);

        string ymd;
        if (isToday)
            ymd = SeoulYmd();
        else
        {
            ymd = NormalizeYmdInput(date, out string? dateError, defaultToToday: false);
            if (dateError is not null) return McpJson.Error(dateError);
            if (string.Equals(ymd, SeoulYmd(), StringComparison.Ordinal)) isToday = true;
        }

        LsLiveAccount? registered = await accountResolver.GetRegisteredAsync(cancellationToken).ConfigureAwait(false);

        string trCode = isToday ? "t0150" : "t0151";
        try
        {
            var inBlock = new JsonObject
            {
                ["cts_medosu"] = "",
                ["cts_expcode"] = "",
                ["cts_price"] = "",
                ["cts_middiv"] = "",
            };
            if (!isToday) inBlock["date"] = ymd;

            var summary = new DailyPnlSummary();
            var rows = new List<DailyPnlRow>();
            string? continuationKey = null;
            bool sawSummary = false;

            for (int page = 0; page < MaxOrdersPages; page++)
            {
                LsTrResponse response = await apiClient.CallTrAsync(trCode, inBlock, continuationKey, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccess)
                {
                    return McpJson.Error("LS reported a business-level error.", new
                    {
                        tr_code = trCode,
                        rsp_cd = response.RspCode,
                        rsp_msg = response.RspMessage,
                        account_used = accountResolver.BuildEcho(registered),
                    });
                }

                JsonElement? headerBlock = response.GetBlock($"{trCode}OutBlock");
                if (headerBlock is not null && !sawSummary)
                {
                    JsonElement h = headerBlock.Value;
                    summary.SellQuantity = h.ReadLong("mdqty");
                    summary.SellAmount = h.ReadLong("mdamt");
                    summary.SellFee = h.ReadLong("mdfee");
                    summary.SellTax = h.ReadLong("mdtax");
                    summary.SellSettlement = h.ReadLong("mdadjamt");
                    summary.BuyQuantity = h.ReadLong("msqty");
                    summary.BuyAmount = h.ReadLong("msamt");
                    summary.BuyFee = h.ReadLong("msfee");
                    summary.BuySettlement = h.ReadLong("msadjamt");
                    summary.TotalQuantity = h.ReadLong("tqty");
                    summary.TotalAmount = h.ReadLong("tamt");
                    summary.TotalFee = h.ReadLong("tfee");
                    summary.TotalTax = h.ReadLong("tottax");
                    summary.TotalLevy = h.ReadLong("ttax");
                    summary.TotalSettlement = h.ReadLong("tadjamt");
                    sawSummary = true;
                }

                JsonElement? listBlock = response.GetBlock($"{trCode}OutBlock1");
                int beforeCount = rows.Count;
                if (listBlock is not null && listBlock.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement row in listBlock.Value.EnumerateArray())
                    {
                        string sym = (row.ReadString("expcode") ?? "").Trim();
                        if (string.IsNullOrEmpty(sym)) continue;
                        rows.Add(new DailyPnlRow(
                            Side: (row.ReadString("medosu") ?? "").Trim(),
                            Symbol: sym,
                            Quantity: row.ReadLong("qty"),
                            UnitPrice: row.ReadLong("price"),
                            Amount: row.ReadLong("amt"),
                            Fee: row.ReadLong("fee"),
                            Tax: row.ReadLong("tax"),
                            Settlement: row.ReadLong("adjamt"),
                            Channel: (row.ReadString("middiv") ?? "").Trim()));
                    }
                }

                if (rows.Count == beforeCount)
                    break;
                if (!response.HasContinuation || string.IsNullOrEmpty(response.ContinuationKey))
                    break;
                continuationKey = response.ContinuationKey;
            }

            var payload = new
            {
                filter = new { date = FormatYmdDisplay(ymd), is_today = isToday },
                summary,
                count = rows.Count,
                trades = rows,
                _meta = new
                {
                    account_used = accountResolver.BuildEcho(registered),
                    data_as_of = GetIndexQuoteTool.SeoulNowIsoString(),
                    tr_code = trCode,
                    source = "live",
                },
            };
            return JsonSerializer.Serialize(payload, McpJson.Tool);
        }
        catch (LsAuthException ex) { return McpJson.Error("LS authentication failed for the account inquiry.", new { reason = ex.Message, account_used = accountResolver.BuildEcho(registered) }); }
        catch (LsTrException ex) { return McpJson.Error("TR call failed.", new { reason = ex.Message, status = ex.StatusCode, tr_code = trCode, account_used = accountResolver.BuildEcho(registered) }); }
    }

    // ---- Date / filter helpers ----

    static string SeoulYmd()
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Korea Standard Time" : "Asia/Seoul"); }
        catch (TimeZoneNotFoundException) { return DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).ToString("yyyyMMdd"); }
        return TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).ToString("yyyyMMdd");
    }

    static string NormalizeYmdInput(string? input, out string? error, bool defaultToToday = true, int defaultDaysBack = 0)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            DateTime today = DateTime.UtcNow.AddHours(9).Date;
            DateTime defaultDate = defaultToToday ? today : today.AddDays(-defaultDaysBack);
            return defaultDate.ToString("yyyyMMdd");
        }
        string trimmed = input.Trim();
        if (string.Equals(trimmed, "today", StringComparison.OrdinalIgnoreCase) || trimmed == "오늘")
            return DateTime.UtcNow.AddHours(9).ToString("yyyyMMdd");
        if (string.Equals(trimmed, "yesterday", StringComparison.OrdinalIgnoreCase) || trimmed == "어제")
            return DateTime.UtcNow.AddHours(9).AddDays(-1).ToString("yyyyMMdd");
        // Accept both YYYYMMDD and YYYY-MM-DD.
        string digits = new(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length != 8)
        {
            error = $"date '{input}' is not recognized. Use YYYY-MM-DD, YYYYMMDD, 'today', or 'yesterday'.";
            return DateTime.UtcNow.AddHours(9).ToString("yyyyMMdd");
        }
        if (!DateTime.TryParseExact(digits, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
        {
            error = $"date '{input}' is not a valid calendar date.";
            return digits;
        }
        return digits;
    }

    static string FormatYmdDisplay(string ymd) =>
        ymd.Length == 8 ? $"{ymd[..4]}-{ymd.AsSpan(4, 2)}-{ymd.AsSpan(6, 2)}" : ymd;

    static string NormalizeExecStatus(string? status, out string? error)
    {
        error = null;
        string n = string.IsNullOrWhiteSpace(status) ? "all" : status.Trim().ToLowerInvariant();
        return n switch
        {
            "all" or "0" => "0",
            "filled" or "체결" or "1" => "1",
            "pending" or "미체결" or "3" => "3",
            _ => SetErrAndDefault($"status '{status}' is not recognized. Use 'all', 'filled', or 'pending'.", "0", out error),
        };
    }

    static string ExecStatusLabel(string code) => code switch
    {
        "1" => "filled",
        "3" => "pending",
        _ => "all",
    };

    static string NormalizeOrderMarket(string? market, out string? error)
    {
        error = null;
        string n = string.IsNullOrWhiteSpace(market) ? "all" : market.Trim().ToLowerInvariant();
        return n switch
        {
            "all" or "00" => "00",
            "kospi" or "거래소" or "10" => "10",
            "kosdaq" or "코스닥" or "20" => "20",
            "freeboard" or "프리보드" or "30" => "30",
            _ => SetErrAndDefault($"market '{market}' is not recognized. Use 'all', 'kospi', 'kosdaq', or 'freeboard'.", "00", out error),
        };
    }

    static string MarketLabel(string code) => code switch
    {
        "10" => "kospi",
        "20" => "kosdaq",
        "30" => "freeboard",
        _ => "all",
    };

    static string NormalizeBkSort(string? sort, out string? error)
    {
        error = null;
        string n = string.IsNullOrWhiteSpace(sort) ? "asc" : sort.Trim().ToLowerInvariant();
        return n switch
        {
            "asc" or "ascending" or "1" => "1",
            "desc" or "descending" or "0" => "0",
            _ => SetErrAndDefault($"sort '{sort}' is not recognized. Use 'asc' or 'desc'.", "1", out error),
        };
    }

    static string NormalizeTransactionKind(string? kind, out string? error)
    {
        error = null;
        string n = string.IsNullOrWhiteSpace(kind) ? "all" : kind.Trim().ToLowerInvariant();
        return n switch
        {
            "all" or "0" => "0",
            "cashflow" or "입출금" or "1" => "1",
            "transfer" or "입출고" or "2" => "2",
            "trade" or "매매" or "3" => "3",
            "fx" or "환전" or "4" => "4",
            "misc" or "기타" or "9" => "9",
            _ => SetErrAndDefault($"kind '{kind}' is not recognized. Use 'all', 'cashflow', 'transfer', 'trade', 'fx', or 'misc'.", "0", out error),
        };
    }

    static string TransactionKindLabel(string code) => code switch
    {
        "1" => "cashflow",
        "2" => "transfer",
        "3" => "trade",
        "4" => "fx",
        "9" => "misc",
        _ => "all",
    };

    static string NormalizePerformanceTerm(string? term, out string? error)
    {
        error = null;
        string n = string.IsNullOrWhiteSpace(term) ? "daily" : term.Trim().ToLowerInvariant();
        return n switch
        {
            "daily" or "일별" or "1" => "1",
            "weekly" or "주별" or "2" => "2",
            "monthly" or "월별" or "3" => "3",
            _ => SetErrAndDefault($"term '{term}' is not recognized. Use 'daily', 'weekly', or 'monthly'.", "1", out error),
        };
    }

    static string PerformanceTermLabel(string code) => code switch
    {
        "2" => "weekly",
        "3" => "monthly",
        _ => "daily",
    };

    static string SetErrAndDefault(string message, string fallback, out string? error)
    {
        error = message;
        return fallback;
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
    /// /stock/accno family success check. LS routes account-inquiry TRs
    /// through a gateway that stamps non-<c>"00000"</c> rsp_cd values
    /// alongside "조회가 완료되었습니다." or "조회내역이 없습니다." —
    /// the full success catalog observed in the wild and cross-checked
    /// against programgarden's tracker.SUCCESS_CODES:
    /// <list type="bullet">
    ///   <item><c>00000</c> — standard success</item>
    ///   <item><c>00001</c> — CSPAQ family generic success</item>
    ///   <item><c>00133</c> — FOCCQ33600 paginated success</item>
    ///   <item><c>00136</c> — CSPAQ snapshot success (live-confirmed CSPAQ22200)</item>
    ///   <item><c>00200</c> — no-data success on CSPAQ13700 / CDPCQ04700</item>
    ///   <item><c>00707</c> — overseas/derivatives "조회할 내역 없음" success</item>
    /// </list>
    /// Pair the known codes with a present expected block so genuine error
    /// codes still fall through to the business-error envelope.
    /// </summary>
    /// <remarks>See <see cref="LsTrResponse.SuccessCode"/> for the global default and
    /// <c>docs/LS-API-QUIRKS.md</c> §4.2b for the catalog.</remarks>
    static bool IsCspaqSuccess(string? rspCode, JsonElement? expectedBlock)
    {
        if (expectedBlock is null)
            return false;
        if (string.IsNullOrEmpty(rspCode))
            return false;
        return rspCode is LsTrResponse.SuccessCode
            or "00001" or "00133" or "00136" or "00200" or "00707";
    }

    /// <summary>
    /// Reads an LS-style <c>AcntNo</c> field from the given output block.
    /// Returns null when the block is missing or the field is blank.
    /// CSPAQ / CDPCQ / FOCCQ tools call this to surface the broker's own
    /// view of which account answered — see <see cref="LsAccountResolver"/>
    /// for why the response (not the request) is the authoritative
    /// account identifier.
    /// </summary>
    static string? TryReadAcntNo(LsTrResponse response, string outBlockName)
    {
        JsonElement? block = response.GetBlock(outBlockName);
        if (block is null) return null;
        string? acntNo = block.Value.ReadString("AcntNo");
        if (string.IsNullOrWhiteSpace(acntNo)) return null;
        return acntNo.Trim();
    }

    /// <summary>
    /// Success-path echo for CSPAQ / CDPCQ / FOCCQ tools. Reads the
    /// broker-resolved <c>AcntNo</c> from the named OutBlock, upserts it
    /// into the live registry (idempotent fire-and-forget — failures fall
    /// through to a synthetic echo so the user-visible payload stays
    /// honest), then returns the echo to use in <c>_meta.account_used</c>.
    /// Optional <paramref name="branchName"/> / <paramref name="accountName"/>
    /// let CSPAQ12200 (which already extracts <c>BrnNm</c> / <c>AcntNm</c>
    /// for the balance payload) propagate those labels into the registry
    /// without a second read.
    /// </summary>
    static async Task<LsLiveAccountInfo> BuildSuccessEchoAsync(
        LsLiveAccount? registered,
        LsTrResponse response,
        string outBlockName,
        LsAccountResolver resolver,
        CancellationToken cancellationToken,
        string? branchName = null,
        string? accountName = null)
    {
        string? acntNo = TryReadAcntNo(response, outBlockName);
        LsLiveAccount? upserted = await resolver
            .RecordDiscoveredAsync(acntNo, branchName, accountName, cancellationToken)
            .ConfigureAwait(false);
        return resolver.BuildEcho(upserted ?? registered, acntNo);
    }

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

    sealed class OrderHistorySummary
    {
        public long SellFilledAmount { get; set; }
        public long BuyFilledAmount { get; set; }
        public long SellFilledQuantity { get; set; }
        public long BuyFilledQuantity { get; set; }
        public long SellOrderQuantity { get; set; }
        public long BuyOrderQuantity { get; set; }
    }

    sealed record OrderHistoryRow(
        long OrderNo,
        long OriginalOrderNo,
        string? OrderDate,
        string Symbol,
        string Name,
        string Side,
        string OrderType,
        string ProcessingType,
        string ModifyOrCancel,
        long OrderQuantity,
        double OrderPrice,
        long FilledQuantity,
        double FilledPrice,
        long AllFilledQuantity,
        string? OrderTime,
        string? LastFilledTime,
        string QuoteType,
        string OrderChannel);

    sealed record TransactionRow(
        string? TradeDate,
        long TradeNo,
        string CategoryName,
        string SummaryName,
        string CancellationName,
        string Symbol,
        string Name,
        long Quantity,
        double UnitPrice,
        long Amount,
        long Commission,
        long TransactionTax,
        long TaxSum,
        long SettlementAmount,
        long DepositBalanceAfter,
        string? ProcessingTime,
        string Channel);

    sealed class PerformanceSummary
    {
        public string AccountName { get; set; } = "";
        public long TradedAmount { get; set; }
        public long DepositedAmount { get; set; }
        public long WithdrawnAmount { get; set; }
        public long InvestmentAveragePrincipal { get; set; }
        public long InvestmentPnl { get; set; }
        public double InvestmentReturnPct { get; set; }
    }

    sealed record PerformanceRow(
        string? BaseDate,
        long OpeningEvaluation,
        long ClosingEvaluation,
        long AveragePrincipal,
        long TradedAmount,
        long InflowAmount,
        long OutflowAmount,
        long EvaluationPnl,
        double PeriodReturnPct,
        double Index);

    sealed class DailyPnlSummary
    {
        public long SellQuantity { get; set; }
        public long SellAmount { get; set; }
        public long SellFee { get; set; }
        public long SellTax { get; set; }
        public long SellSettlement { get; set; }
        public long BuyQuantity { get; set; }
        public long BuyAmount { get; set; }
        public long BuyFee { get; set; }
        public long BuySettlement { get; set; }
        public long TotalQuantity { get; set; }
        public long TotalAmount { get; set; }
        public long TotalFee { get; set; }
        public long TotalTax { get; set; }
        public long TotalLevy { get; set; }
        public long TotalSettlement { get; set; }
    }

    sealed record DailyPnlRow(
        string Side,
        string Symbol,
        long Quantity,
        long UnitPrice,
        long Amount,
        long Fee,
        long Tax,
        long Settlement,
        string Channel);

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
        public long WithdrawableAmount { get; set; }
        public long EvaluationAmount { get; set; }
        public long DepositedAssetTotal { get; set; }
        public double PnlPct { get; set; }
        public long InvestmentOriginal { get; set; }
        public long InvestmentPnl { get; set; }
        public long D1WithdrawablePresumed { get; set; }
        public long D2WithdrawablePresumed { get; set; }
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
