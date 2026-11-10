using PcTidy.Core;

namespace PcTidy.App.Notifications;

/// <summary>One fixed drive's space right now.</summary>
public sealed record DriveSpace(string Root, long TotalBytes, long FreeBytes)
{
    public double FreeFraction => TotalBytes <= 0 ? 1 : (double)FreeBytes / TotalBytes;
}

/// <summary>Where drive space comes from: the real drives, or the practice PC's simulated ones.</summary>
public interface IDriveSpaceSource
{
    IReadOnlyList<DriveSpace> Read();
}

/// <summary>Real fixed drives (DriveInfo, read-only). Drives that aren't ready are left out.</summary>
public sealed class FixedDriveSpace : IDriveSpaceSource
{
    public IReadOnlyList<DriveSpace> Read()
    {
        var list = new List<DriveSpace>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType == DriveType.Fixed && d.IsReady) list.Add(new DriveSpace(d.RootDirectory.FullName, d.TotalSize, d.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip that drive */ }
        }
        return list;
    }
}

/// <summary>Practice PC / demo: the simulated drives from <see cref="ISystemInfo"/>.</summary>
public sealed class SimulatedDriveSpace(ISystemInfo system) : IDriveSpaceSource
{
    public IReadOnlyList<DriveSpace> Read() => system.Drives.Select(d => new DriveSpace(d.Root, d.TotalBytes, d.FreeBytes)).ToList();
}

/// <summary>
/// "Low disk" means the Windows drive has under 10 % free AND under 20 GB free (Plan: tray alerts). Other drives
/// (backup disks, media archives, game drives) are often full on purpose and aren't what a clean-up helps with, and on
/// a big drive 10 % is hundreds of GB, so neither nags.
/// </summary>
public static class LowDiskCheck
{
    public const double Threshold = 0.10;
    public const long FloorBytes = 20L << 30;

    /// <param name="systemDrive">The Windows drive ("C:\"); null = every drive given (callers that already chose).</param>
    public static IReadOnlyList<DriveSpace> LowDrives(IEnumerable<DriveSpace> drives, string? systemDrive = null)
        => drives.Where(d => d.TotalBytes > 0 && d.FreeFraction < Threshold && d.FreeBytes < FloorBytes)
            .Where(d => systemDrive is null || string.Equals(d.Root.TrimEnd('\\'), systemDrive.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d.FreeFraction).ToList();

    /// <summary>Calm wording, no urgency: "C: has 6 % free (9.1 GB of 150 GB)".</summary>
    public static AppNotification? Notification(IReadOnlyList<DriveSpace> low)
    {
        if (low.Count == 0) return null;
        var d = low[0];
        var name = d.Root.TrimEnd('\\');
        var body = $"{name} has {d.FreeFraction:P0} free ({Sizes.Format(d.FreeBytes)} of {Sizes.Format(d.TotalBytes)})."
                   + (low.Count > 1 ? $" {low.Count - 1} other drive{(low.Count == 2 ? " is" : "s are")} low too." : "")
                   + " Nothing has been changed. A quick look in PC Tidy shows what's safe to clean.";
        return new AppNotification(AlertKind.LowDisk, $"{name} is getting full", body, "Have a look", "optimize");
    }
}
