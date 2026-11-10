namespace PcTidy.Features.SpaceMap.Index;

/// <summary>
/// Timestamps are stored as whole minutes since 1601-01-01 UTC in an int (4 bytes instead of 8).
/// Minute precision is plenty for "not touched in 6 months"; plan items re-read exact times before acting.
/// </summary>
public static class SpaceTime
{
    private static readonly long EpochTicks = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    public static int ToMinutes(DateTime utc)
    {
        long minutes = (utc.Ticks - EpochTicks) / TimeSpan.TicksPerMinute;
        return (int)Math.Clamp(minutes, 0, int.MaxValue);
    }

    public static DateTime FromMinutes(int minutes)
        => new(EpochTicks + minutes * TimeSpan.TicksPerMinute, DateTimeKind.Utc);
}
