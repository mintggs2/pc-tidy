using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.PcFacts;

namespace PcTidy.Features.DriveMove.Drives;

/// <summary>One fixed drive of this PC as the section sees it.</summary>
/// <param name="Root">Normalised root ("C:\" on a real PC, "&lt;testbed&gt;\D\" in the sandbox).</param>
public sealed record DriveFacts(string Root, string Label, DriveMedia Media, long TotalBytes, long FreeBytes, bool IsSystem)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public double FreeFraction => TotalBytes > 0 ? (double)FreeBytes / TotalBytes : 0;
    /// <summary>Sizes are known (a drive that couldn't be read reports 0).</summary>
    public bool SizeKnown => TotalBytes > 0;

    /// <summary>"C:" (the sandbox's fake drive folders "…\D\" read as "D:" too).</summary>
    public string Name
    {
        get
        {
            var trimmed = Root.TrimEnd('\\');
            return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed.ToUpperInvariant() : Path.GetFileName(trimmed).ToUpperInvariant() + ":";
        }
    }

    /// <summary>On an external bus (USB, card, FireWire): it can be unplugged, so nothing is moved or linked onto it.</summary>
    public bool IsExternal { get; init; }

    /// <summary>Things may be moved onto it: a fixed drive inside the PC, not an external (USB), removable or network one.</summary>
    public bool CanReceive => !IsExternal && Media is not (DriveMedia.Removable or DriveMedia.Network) && SizeKnown;

    /// <summary>Why nothing is moved onto it, in plain words; null when <see cref="CanReceive"/>.</summary>
    public string? NotReceivingReason => CanReceive ? null
        : IsExternal ? $"{Name} is an external drive (USB). PC Tidy only moves things onto drives inside this PC, because an external one can be unplugged."
        : Media is DriveMedia.Removable or DriveMedia.Network ? $"{Name} is a removable or network drive. PC Tidy only moves things onto drives inside this PC."
        : $"PC Tidy couldn't read how big {Name} is, so nothing is moved onto it.";

    public string MediaText => IsExternal ? "External (USB)" : Media switch
    {
        DriveMedia.Ssd => "SSD",
        DriveMedia.Hdd => "Hard drive",
        DriveMedia.Removable => "Removable",
        DriveMedia.Network => "Network",
        _ => "Drive",
    };

    public bool IsFull(DriveMoveThresholds t) => SizeKnown && (FreeFraction < t.FullFreeFraction || FreeBytes < t.FullFreeBytes);
}

/// <summary>
/// The PC's fixed drives that really exist: the ones both <see cref="IKnownLocations.FixedDrives"/> and
/// <see cref="ISystemInfo.Drives"/> report (a drive that isn't ready or vanished is left out). Never assumes a second drive.
/// </summary>
public sealed class DriveList
{
    private DriveList(IReadOnlyList<DriveFacts> drives) => All = drives;

    public IReadOnlyList<DriveFacts> All { get; }

    /// <summary>Drives things may be moved onto.</summary>
    public IReadOnlyList<DriveFacts> Receivers => All.Where(d => d.CanReceive).ToList();

    /// <summary>Moving needs at least two fixed drives that can hold things.</summary>
    public bool MovingAvailable => Receivers.Count >= 2;

    /// <summary>Plain reason when <see cref="MovingAvailable"/> is false.</summary>
    public string? UnavailableReason => MovingAvailable ? null
        : All.Count <= 1 ? NotAvailable.SingleDrive
        : NotAvailable.NoOtherFixedDrive;

    public static DriveList Of(IEnumerable<DriveFacts> drives) => new(drives.ToList());

    public static DriveList From(IKnownLocations locations, ISystemInfo? system)
    {
        var details = SafeDrives(system);
        var list = new List<DriveFacts>();
        foreach (var raw in locations.FixedDrives)
        {
            var root = PathText.TryNormalize(raw);
            if (root is null || !Exists(root)) continue;
            if (details is not null)
            {
                var d = details.FirstOrDefault(x => PathText.TryNormalize(x.Root) is { } r && r.Equals(root, StringComparison.OrdinalIgnoreCase));
                if (d is null) continue; // listed by Windows' known locations but not ready: not offered
                list.Add(new DriveFacts(root, d.Label, d.Media, d.TotalBytes, d.FreeBytes, d.IsSystem) { IsExternal = d.IsExternal });
            }
            else
            {
                list.Add(ReadDirectly(root, locations));
            }
        }
        return new DriveList(list);
    }

    /// <summary>The drive that holds <paramref name="path"/> (longest matching root, so sandbox drive folders work), or null.</summary>
    public DriveFacts? DriveOf(string path)
    {
        var p = PathText.TryNormalize(path);
        if (p is null) return null;
        return All.Where(d => PathUtil.IsUnder(p, d.Root)).OrderByDescending(d => d.Root.Length).FirstOrDefault();
    }

    /// <summary>Other drives that could hold <paramref name="bytes"/> plus headroom, most free first.</summary>
    public IReadOnlyList<DriveFacts> TargetsFor(DriveFacts? source, long bytes, DriveMoveThresholds t)
        => Receivers.Where(d => source is null || !d.Root.Equals(source.Root, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.FreeBytes >= t.Needed(bytes))
            .OrderByDescending(d => d.FreeBytes)
            .ToList();

    /// <summary>The other receiving drive with the most free space (whether or not it fits), for "not enough room" messages.</summary>
    public DriveFacts? RoomiestOther(DriveFacts? source)
        => Receivers.Where(d => source is null || !d.Root.Equals(source.Root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.FreeBytes).FirstOrDefault();

    private static IReadOnlyList<DriveDetails>? SafeDrives(ISystemInfo? system)
    {
        if (system is null) return null;
        try { return system.Drives; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { return Array.Empty<DriveDetails>(); }
    }

    /// <summary>No system facts (older callers): a real drive root is read with DriveInfo; a sandbox folder has unknown size.</summary>
    private static DriveFacts ReadDirectly(string root, IKnownLocations k)
    {
        bool isSystem = PathText.TryNormalize(k.SystemDrive) is { } s && s.Equals(root, StringComparison.OrdinalIgnoreCase);
        if (!k.IsSandbox && root.Length == 3)
        {
            try
            {
                var info = new DriveInfo(root);
                if (info.IsReady && info.DriveType == DriveType.Fixed)
                    return new DriveFacts(root, info.VolumeLabel, DriveMedia.Unknown, info.TotalSize, info.AvailableFreeSpace, isSystem);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return new DriveFacts(root, "", DriveMedia.Unknown, 0, 0, isSystem);
    }

    private static bool Exists(string root)
    {
        try { return Directory.Exists(root); } catch { return false; }
    }
}

/// <summary>The friendly "not available" sentences (shown instead of an error).</summary>
public static class NotAvailable
{
    public const string SingleDrive = "You have one drive, so moving isn't available. Add a second drive inside the PC and PC Tidy can move big folders, games and caches onto it.";
    public const string NoOtherFixedDrive = "Your other drives are external (USB), removable or network drives. PC Tidy only moves things onto a drive inside this PC, because those can be unplugged.";
}
