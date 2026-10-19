namespace Jellyfin.Plugin.S3Storage;

/// <summary>
/// Chooses link lifetimes on hour boundaries, so every request within the same hour receives the same link
/// and the redirect itself can be cached by clients.
/// </summary>
public static class DeliveryWindow
{
    private const long HourSeconds = 3600;

    // Pre-signed links last at most seven days; stay one minute below that.
    private const long MaxLifetimeSeconds = 7 * 24 * 3600 - 60;

    /// <summary>Returns when a link issued at <paramref name="now"/> expires, and how many seconds the redirect to it may be cached.</summary>
    public static (DateTimeOffset ExpiresAt, int MaxAge) Compute(DateTimeOffset now, int hours)
    {
        var seconds = now.ToUnixTimeSeconds();
        var bucketEnd = (seconds / HourSeconds + 1) * HourSeconds;
        var expires = Math.Min(bucketEnd + hours * HourSeconds, seconds + MaxLifetimeSeconds);
        return (DateTimeOffset.FromUnixTimeSeconds(expires), (int)Math.Max(1, bucketEnd - seconds));
    }
}
