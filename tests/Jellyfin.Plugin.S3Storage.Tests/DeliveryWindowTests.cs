using Xunit;

namespace Jellyfin.Plugin.S3Storage.Tests;

public sealed class DeliveryWindowTests
{
    private static DateTimeOffset At(int hour, int minute, int second) => new(2026, 10, 19, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void RequestsWithinOneHourShareTheSameExpiry()
    {
        var first = DeliveryWindow.Compute(At(10, 0, 0), 24);
        var last = DeliveryWindow.Compute(At(10, 59, 59), 24);

        Assert.Equal(first.ExpiresAt, last.ExpiresAt);
        Assert.Equal(At(11, 0, 0).AddHours(24), first.ExpiresAt);
        Assert.Equal(3600, first.MaxAge);
        Assert.Equal(1, last.MaxAge);
    }

    [Fact]
    public void ExpiryAdvancesWithTheNextHour()
    {
        var current = DeliveryWindow.Compute(At(10, 59, 59), 24);
        var next = DeliveryWindow.Compute(At(11, 0, 0), 24);

        Assert.Equal(current.ExpiresAt.AddHours(1), next.ExpiresAt);
        Assert.Equal(3600, next.MaxAge);
    }

    [Fact]
    public void HoursAreCountedInUtcRegardlessOfOffset()
    {
        var utc = DeliveryWindow.Compute(At(10, 30, 0), 6);
        var local = DeliveryWindow.Compute(new DateTimeOffset(2026, 10, 19, 19, 30, 0, TimeSpan.FromHours(9)), 6);

        Assert.Equal(utc, local);
    }

    [Fact]
    public void SevenDayLimitCapsTheLongestGrant()
    {
        var now = At(10, 30, 0);
        var (expiresAt, maxAge) = DeliveryWindow.Compute(now, 168);

        Assert.Equal(now.AddSeconds(7 * 24 * 3600 - 60), expiresAt);
        Assert.Equal(1800, maxAge);
    }

    [Fact]
    public void LongGrantsFitUnderTheLimitOnlyAfterTheFirstMinuteOfTheHour()
    {
        // 167 hours plus the rest of the current hour stay within seven days minus one minute from 10:01 on.
        Assert.Equal(At(11, 0, 0).AddHours(167), DeliveryWindow.Compute(At(10, 30, 0), 167).ExpiresAt);
        Assert.Equal(At(11, 0, 0).AddHours(167), DeliveryWindow.Compute(At(10, 1, 0), 167).ExpiresAt);
        Assert.Equal(At(10, 0, 0).AddSeconds(7 * 24 * 3600 - 60), DeliveryWindow.Compute(At(10, 0, 0), 167).ExpiresAt);
    }

    [Fact]
    public void FractionalSecondsStayInTheirSecond()
    {
        var (expiresAt, maxAge) = DeliveryWindow.Compute(At(10, 59, 59).AddMilliseconds(900), 1);

        Assert.Equal(At(12, 0, 0), expiresAt);
        Assert.Equal(1, maxAge);
    }
}
