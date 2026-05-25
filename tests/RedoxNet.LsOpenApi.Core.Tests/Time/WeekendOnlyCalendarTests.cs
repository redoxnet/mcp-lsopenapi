using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Time;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Time;

public class WeekendOnlyCalendarTests
{
    readonly WeekendOnlyCalendar _calendar = new();

    [Theory]
    [InlineData(2026, 5, 22, true)]
    [InlineData(2026, 5, 23, false)]
    [InlineData(2026, 5, 24, false)]
    [InlineData(2026, 5, 25, true)]
    public void IsTradingDay_ExcludesOnlyWeekends(int year, int month, int day, bool expected)
    {
        _calendar.IsTradingDay(new DateOnly(year, month, day)).Should().Be(expected);
    }

    [Fact]
    public void LastTradingDayOnOrBefore_Weekend_ReturnsFriday()
    {
        _calendar.LastTradingDayOnOrBefore(new DateOnly(2026, 5, 24))
            .Should().Be(new DateOnly(2026, 5, 22));
    }

    [Fact]
    public void NextTradingDayOnOrAfter_Weekend_ReturnsMonday()
    {
        _calendar.NextTradingDayOnOrAfter(new DateOnly(2026, 5, 23))
            .Should().Be(new DateOnly(2026, 5, 25));
    }
}
