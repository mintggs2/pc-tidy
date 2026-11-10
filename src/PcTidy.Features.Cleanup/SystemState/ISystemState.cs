using PcTidy.Core;

namespace PcTidy.Features.Cleanup.SystemState;

internal readonly record struct RecycleBinInfo(long Bytes, long Items);

/// <param name="HibernateEnabled">Null when the setting couldn't be read.</param>
/// <param name="HiberFileType">1 = Reduced, 2 = Full (Windows' own values); null when not set.</param>
/// <param name="HasBattery">True on laptops/tablets; null when unknown.</param>
internal sealed record PowerState(bool? HibernateEnabled, int? HiberFileType, bool? HasBattery, long? TotalMemoryBytes);

/// <summary>
/// Read-only facts about Windows that aren't files: registry values and Win32 queries.
/// Registry / API only, never parsed command output (it's localized). Never changes anything.
/// </summary>
internal interface ISystemState
{
    /// <summary>Windows is waiting for a restart to finish installing updates.</summary>
    bool IsRestartPending();

    /// <summary>Size and item count of the current user's Recycle Bin on every drive; null if it can't be read.</summary>
    RecycleBinInfo? QueryRecycleBin(CancellationToken ct);

    PowerState ReadPower();

    /// <summary>Raw "PagingFiles" setting lines; null if unreadable.</summary>
    IReadOnlyList<string>? PagingFiles();

    /// <summary>Reserved storage on/off; null if unknown.</summary>
    bool? ReservedStorageOn();

    /// <summary>CompactOS on/off; null if unknown.</summary>
    bool? CompactOsOn();

    /// <summary>System Restore storage in bytes; null when it needs admin to measure.</summary>
    long? RestoreStorageBytes(CancellationToken ct);
}

internal static class SystemStateFactory
{
    /// <summary>The sandbox reads a state file inside the testbed, so tests never look at the real PC's settings.</summary>
    public static ISystemState For(IKnownLocations k, IFileWalker walker)
        => k.IsSandbox ? new SandboxSystemState(k, walker) : new WindowsSystemState();
}
