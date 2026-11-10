using PcTidy.Core;

namespace PcTidy.Features.SpaceMap.Index;

public readonly record struct DriveSpace(long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>Drive capacity queries (APIs only, never parsed command output).</summary>
public interface IDriveSpaceProbe
{
    /// <summary>Space of the drive when <paramref name="scanRoot"/> is a whole drive (e.g. "C:\"); null for a folder.</summary>
    DriveSpace? ForScanRoot(string scanRoot);
    /// <summary>Space of one of <see cref="IKnownLocations.FixedDrives"/>; null if unknown.</summary>
    DriveSpace? ForFixedDrive(string driveRoot);
}

/// <summary>Uses <see cref="ISystemInfo.Drives"/> when available (also covers sandbox drives), else <see cref="DriveInfo"/>.</summary>
public sealed class DriveSpaceProbe : IDriveSpaceProbe
{
    private readonly ISystemInfo? _system;

    public DriveSpaceProbe(ISystemInfo? system = null) => _system = system;

    public DriveSpace? ForScanRoot(string scanRoot)
    {
        var full = Path.GetFullPath(scanRoot);
        var root = Path.GetPathRoot(full);
        if (root is null || !full.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
        return FromDriveInfo(root);
    }

    public DriveSpace? ForFixedDrive(string driveRoot)
    {
        var match = _system?.Drives.FirstOrDefault(d => SamePath(d.Root, driveRoot));
        if (match is not null) return new DriveSpace(match.TotalBytes, match.FreeBytes);
        var root = Path.GetPathRoot(Path.GetFullPath(driveRoot));
        return root is not null && SamePath(root, driveRoot) ? FromDriveInfo(root) : null;
    }

    private static DriveSpace? FromDriveInfo(string root)
    {
        try
        {
            var d = new DriveInfo(root);
            return d.IsReady ? new DriveSpace(d.TotalSize, d.TotalFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static bool SamePath(string a, string b)
        => a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
