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

    /// <summary>
    /// CSPAQ family success check. LS routes these account TRs through a
    /// gateway that stamps <c>rsp_cd="00136"</c> alongside "조회가 완료되었습니다."
    /// rather than the usual <c>"00000"</c>. The presence of the expected
    /// output block is the canonical "data arrived" signal; pair it with the
    /// known success codes so non-success codes still fall through to the
    /// business-error envelope.
    /// </summary>
    /// <remarks>See <see cref="LsTrResponse.SuccessCode"/> for the global default and
    /// <c>docs/LS-API-QUIRKS.md</c> §4 for the broader pattern.</remarks>
    static bool IsCspaqSuccess(string? rspCode, JsonElement? expectedBlock)
    {
        if (expectedBlock is null)
            return false;
        if (string.IsNullOrEmpty(rspCode))
            return false;
        return rspCode == LsTrResponse.SuccessCode || rspCode == "00136";
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
