using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Time;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Time;

public class DateEnvelopeTests
{
    [Fact]
    public void TryResolveKrxDailySnapshot_ExplicitWeekday_UsesRequestedDate()
    {
        bool ok = DateEnvelope.TryResolveKrxDailySnapshot(
            "20260522",
            out DateEnvelope envelope,
            out string? error,
            now: Kst(2026, 5, 24, 14, 0));

        ok.Should().BeTrue();
        error.Should().BeNull();
        envelope.DataAsOf.Should().Be("20260522");
        envelope.QueryDateResolution.Should().Be(QueryDateResolution.Used);
    }

    [Fact]
    public void TryResolveKrxDailySnapshot_ExplicitWeekend_ReturnsPriorFriday()
    {
        bool ok = DateEnvelope.TryResolveKrxDailySnapshot(
            "20260524",
            out DateEnvelope envelope,
            out _,
            now: Kst(2026, 5, 24, 14, 0));

        ok.Should().BeTrue();
        envelope.DataAsOf.Should().Be("20260522");
        envelope.QueryDateResolution.Should().Be(QueryDateResolution.Weekend);
    }

    [Fact]
    public void TryResolveKrxDailySnapshot_ImplicitWeekend_ReturnsPriorFriday()
    {
        DateEnvelope.TryResolveKrxDailySnapshot(
            null,
            out DateEnvelope envelope,
            out _,
            now: Kst(2026, 5, 24, 14, 0)).Should().BeTrue();

        envelope.DataAsOf.Should().Be("20260522");
        envelope.QueryDateResolution.Should().Be(QueryDateResolution.Weekend);
    }

    [Fact]
    public void TryResolveKrxDailySnapshot_ImplicitPreMarket_ReturnsPriorTradingDay()
    {
        DateEnvelope.TryResolveKrxDailySnapshot(
            null,
            out DateEnvelope envelope,
            out _,
            now: Kst(2026, 5, 25, 8, 59)).Should().BeTrue();

        envelope.DataAsOf.Should().Be("20260522");
        envelope.QueryDateResolution.Should().Be(QueryDateResolution.PreMarket);
    }

    [Fact]
    public void TryResolveKrxDailySnapshot_FutureDate_ClampsToAvailableTradingDay()
    {
        DateEnvelope.TryResolveKrxDailySnapshot(
            "20260526",
            out DateEnvelope envelope,
            out _,
            now: Kst(2026, 5, 25, 8, 59)).Should().BeTrue();

        envelope.DataAsOf.Should().Be("20260522");
        envelope.QueryDateResolution.Should().Be(QueryDateResolution.FutureDate);
    }

    [Fact]
    public void TryResolveKrxDailySnapshot_InvalidFormat_ReturnsError()
    {
        bool ok = DateEnvelope.TryResolveKrxDailySnapshot(
            "2026-05-24",
            out _,
            out string? error,
            now: Kst(2026, 5, 24, 14, 0));

        ok.Should().BeFalse();
        error.Should().Contain("yyyyMMdd");
    }

    static DateTimeOffset Kst(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(9));
}
