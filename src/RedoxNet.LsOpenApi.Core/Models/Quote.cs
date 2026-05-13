namespace RedoxNet.LsOpenApi.Core.Models;

/// <summary>
/// One side of the order book at a single level.
/// </summary>
/// <param name="Level">Level (1 = nearest to last trade).</param>
/// <param name="Price">Order price.</param>
/// <param name="Volume">Resting quantity in shares.</param>
public sealed record OrderBookLevel(int Level, long Price, long Volume);

/// <summary>
/// A snapshot of a stock's current price and 10-level order book, returned by
/// the <c>t1101</c> TR.
/// </summary>
/// <param name="Shcode">6-digit stock code.</param>
/// <param name="Name">Korean stock name.</param>
/// <param name="Price">Last traded price.</param>
/// <param name="Change">Price change from previous close (signed).</param>
/// <param name="ChangePercent">Percent change from previous close.</param>
/// <param name="Sign">LS-published change sign code (1상한 2상승 3보합 4하한 5하락).</param>
/// <param name="Volume">Accumulated trade volume for the session.</param>
/// <param name="PreviousClose">Previous session close.</param>
/// <param name="Open">Session open.</param>
/// <param name="High">Session high.</param>
/// <param name="Low">Session low.</param>
/// <param name="Asks">Ask side of the order book.</param>
/// <param name="Bids">Bid side of the order book.</param>
public sealed record QuoteSnapshot(
    string Shcode,
    string Name,
    long Price,
    long Change,
    double ChangePercent,
    string? Sign,
    long Volume,
    long PreviousClose,
    long Open,
    long High,
    long Low,
    IReadOnlyList<OrderBookLevel> Asks,
    IReadOnlyList<OrderBookLevel> Bids);
