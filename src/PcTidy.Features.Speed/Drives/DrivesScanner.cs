using System.Diagnostics;
using System.Globalization;
using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Sources;

namespace PcTidy.Features.Speed.Drives;

/// <summary>
/// Drive health and optimize (Plan §8 Speed + approved feature 2): health and type of every physical disk,
/// detailed counters with admin, free-space warnings, TRIM state, the Optimize Drives schedule, and "Optimize now"
/// with Windows' own optimizer (ReTrim for SSDs, defrag for hard drives). Never defrags an SSD. Read-only.
/// </summary>
public sealed class DrivesScanner : IFeatureScanner
{
    public const string ScannerId = "speed.drives";
    public const string HealthGroup = "drives-health";
    public const string SpaceGroup = "drives-space";
    public const string OptimizeGroup = "drives-optimize";
    internal const string FileSystemKey = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    internal const string TrimValue = "DisableDeleteNotification";
    public const string DefragTaskPath = @"\Microsoft\Windows\Defrag\ScheduledDefrag";

    private readonly Func<ScanContext, SpeedEnvironment> _environment;

    public DrivesScanner(Func<ScanContext, SpeedEnvironment>? environment = null) => _environment = environment ?? SpeedEnvironment.For;

    public string FeatureId => ScannerId;
    public string Title => "Drives";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var env = _environment(ctx);
            var admin = ctx.System?.IsAdmin == true;
            progress?.Report(new ScanProgress("Reading drive health…", 0.2));
            var disks = env.Disks.PhysicalDisks(withReliability: admin, ct);
            ct.ThrowIfCancellationRequested();
            var volumes = ctx.System?.Drives ?? ctx.Locations.FixedDrives.Select(d => new DriveDetails(d, "", DriveMedia.Unknown, 0, 0, false)).ToList();
            var trimOn = TrimEnabled(env);
            var schedule = env.Tasks.GetState(DefragTaskPath);
            progress?.Report(new ScanProgress("Done", 1));

            var notes = new List<string>();
            if (!admin && disks.Count > 0) notes.Add("Temperature, wear and error counts need admin. Run PC Tidy as admin to see them.");
            if (disks.Count == 0) notes.Add("Windows didn't report any physical disks, so drive health isn't available on this PC.");

            return new ScanResult
            {
                FeatureId = ScannerId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Notes = notes,
                Groups = new[]
                {
                    new ScanGroup { Id = HealthGroup, Title = "Drive health", Description = "What Windows' storage service reports for each disk. Read-only.", Badge = Badge.Check,
                        Items = disks.Select(d => HealthItem(d, admin)).ToList() },
                    new ScanGroup { Id = SpaceGroup, Title = "Free space", Description = "Windows and apps slow down when a drive is nearly full.", Badge = Badge.Check,
                        Items = volumes.Select(SpaceItem).ToList() },
                    new ScanGroup { Id = OptimizeGroup, Title = "Optimize drives", Description = "Windows' own optimizer: ReTrim for SSDs, defragment for hard drives. Never defrags an SSD.", Badge = Badge.Check,
                        Items = OptimizeItems(volumes, disks, trimOn, schedule, env).ToList() },
                }.Where(g => g.Items.Count > 0).ToList(),
            };
        }, ct);

    /// <summary>TRIM: DisableDeleteNotification 0 (or missing) = on.</summary>
    internal static bool? TrimEnabled(SpeedEnvironment env)
        => env.Registry.With<bool?>(RegistryHive.LocalMachine, FileSystemKey, k => k.Dword(TrimValue) is { } v ? v == 0 : true, null);

    // ------------------------------------------------------------------ health

    private static ScanItem HealthItem(PhysicalDiskInfo d, bool admin)
    {
        var letters = d.Letters.Count == 0 ? "no drive letter" : string.Join(", ", d.Letters.Select(l => l + ":"));
        var details = new Dictionary<string, string>
        {
            ["Type"] = MediaText(d.Media),
            ["Connection"] = d.BusType,
            ["Size"] = d.SizeBytes > 0 ? Sizes.Format(d.SizeBytes) : "Unknown",
            ["Drives on it"] = letters,
            ["Health"] = HealthText(d.Health),
        };
        if (d.Reliability is { } r)
        {
            if (r.TemperatureC is { } t && t > 0) details["Temperature"] = $"{t} °C";
            if (r.WearPercent is { } w) details["Wear"] = $"{w}% used";
            if (r.ReadErrors is { } re) details["Read errors"] = re.ToString(CultureInfo.CurrentCulture);
            if (r.WriteErrors is { } we) details["Write errors"] = we.ToString(CultureInfo.CurrentCulture);
            if (r.PowerOnHours is { } h) details["Hours powered on"] = h.ToString("N0", CultureInfo.CurrentCulture);
        }
        else details["Temperature, wear, errors"] = admin ? "Not reported by this disk" : "Available with admin";

        return new ScanItem
        {
            Id = "speed.drives.health:" + d.DeviceId,
            Title = $"{d.Name} ({letters})",
            Detail = $"{MediaText(d.Media)} · {d.BusType}",
            Reason = d.Health switch
            {
                DiskHealth.Healthy => "Windows reports this disk as healthy.",
                DiskHealth.Warning => "Windows reports a warning for this disk. Back up anything important on it soon.",
                DiskHealth.Unhealthy => "Windows reports this disk as unhealthy. Back up what's on it now and plan to replace it.",
                _ => "Windows doesn't report a health status for this disk.",
            },
            Badge = d.Health is DiskHealth.Warning or DiskHealth.Unhealthy ? Badge.Check : Badge.Safe,
            CurrentState = HealthText(d.Health),
            Details = details,
            DisabledReason = "Information only.",
        };
    }

    // ------------------------------------------------------------------ free space

    /// <summary>Getting full: under 10% or under 10 GB free. Almost full: under 5% or under 2 GB.</summary>
    internal static (string State, string Reason) SpaceVerdict(long total, long free)
    {
        if (total <= 0) return ("Unknown", "Windows didn't report this drive's size.");
        var pct = (double)free / total;
        if (pct < 0.05 || free < 2L << 30) return ("Almost full", "Very little space left: Windows Update, apps and games may fail or slow down. Clean up or move something off this drive.");
        if (pct < 0.10 || free < 10L << 30) return ("Getting full", "Less than 10% free. Clean or Space map can show what's taking the room.");
        return ("Plenty of space", "Enough free space.");
    }

    private static ScanItem SpaceItem(DriveDetails v)
    {
        var (state, reason) = SpaceVerdict(v.TotalBytes, v.FreeBytes);
        return new ScanItem
        {
            Id = "speed.drives.space:" + v.Root,
            Title = (string.IsNullOrWhiteSpace(v.Label) ? "Drive" : v.Label) + " (" + v.Root.TrimEnd('\\') + ")",
            Detail = v.Root,
            Reason = reason,
            Badge = state == "Plenty of space" ? Badge.Safe : Badge.Check,
            CurrentState = v.TotalBytes > 0 ? $"{Sizes.Format(v.FreeBytes)} free of {Sizes.Format(v.TotalBytes)} · {state}" : state,
            Details = new Dictionary<string, string>
            {
                ["Free"] = Sizes.Format(v.FreeBytes),
                ["Size"] = Sizes.Format(v.TotalBytes),
                ["Type"] = MediaText(v.Media),
                ["Windows drive"] = v.IsSystem ? "Yes" : "No",
            },
            DisabledReason = "Information only.",
        };
    }

    // ------------------------------------------------------------------ optimize

    private static IEnumerable<ScanItem> OptimizeItems(IReadOnlyList<DriveDetails> volumes, IReadOnlyList<PhysicalDiskInfo> disks, bool? trimOn,
        TaskState? schedule, SpeedEnvironment env)
    {
        yield return ScheduleItem(schedule);
        if (trimOn is { } trim)
            yield return new ScanItem
            {
                Id = "speed.drives.trim",
                Title = trim ? "TRIM is on" : "TRIM is off",
                Reason = trim
                    ? "Windows tells your SSDs which space is free, which keeps them fast. Nothing to do."
                    : "SSDs aren't told which space is free, so they slow down over time. Tweaks > Find bad tweaks can turn it back on.",
                Badge = trim ? Badge.Safe : Badge.Check,
                CurrentState = trim ? "On" : "Off",
                Details = new Dictionary<string, string>
                {
                    ["Registry key"] = env.Registry.Describe(RegistryHive.LocalMachine, FileSystemKey),
                    ["Value"] = $"{TrimValue} = {(trim ? 0 : 1)} (NTFS)",
                },
                DisabledReason = "Information only.",
            };

        foreach (var v in volumes)
        {
            var media = MediaFor(v, disks);
            var item = OptimizePlanner.Plan(v.Root, media, trimOn);
            var letter = OptimizePlanner.LetterOf(v.Root);
            yield return new ScanItem
            {
                Id = "speed.drives.optimize:" + v.Root,
                Title = (media == DriveMedia.Ssd ? "ReTrim " : media == DriveMedia.Hdd ? "Defragment " : "Optimize ") + (letter is { } l ? l + ":" : v.Root),
                Detail = v.Root,
                Reason = media switch
                {
                    DriveMedia.Ssd => "Tells the SSD which space is free so it stays fast. Quick, harmless, and Windows does it on a schedule anyway. An SSD is never defragmented.",
                    DriveMedia.Hdd => "Puts scattered pieces of files back together on this hard drive. Can take a while; you can keep working.",
                    _ => "PC Tidy can't tell whether this is an SSD or a hard drive, so it doesn't optimize it.",
                },
                Badge = Badge.Check,
                NeedsAdmin = item is not null,
                CurrentState = MediaText(media),
                Actions = item is null ? Array.Empty<PlanItem>() : new[] { item },
                DisabledReason = item is null ? OptimizePlanner.WhyNot(v.Root, media) : null,
            };
        }
    }

    private static ScanItem ScheduleItem(TaskState? schedule)
    {
        var details = new Dictionary<string, string> { ["Task"] = DefragTaskPath };
        if (schedule?.LastRunUtc is { } last) details["Last run"] = last.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        if (schedule?.NextRunUtc is { } next) details["Next run"] = next.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        return new ScanItem
        {
            Id = "speed.drives.schedule",
            Title = schedule is null ? "Optimize Drives schedule" : schedule.Enabled ? "Optimize Drives runs on a schedule" : "Optimize Drives schedule is off",
            Reason = schedule switch
            {
                null => "Couldn't read Windows' Optimize Drives schedule.",
                { Enabled: true } => "Windows optimizes your drives by itself (usually weekly). Nothing to do.",
                _ => "Windows no longer optimizes your drives by itself. Turn it back on in Optimize Drives (search for it in Start) > Change settings.",
            },
            Badge = schedule?.Enabled == false ? Badge.Check : Badge.Safe,
            CurrentState = schedule is null ? "Couldn't check" : schedule.Enabled ? "On" : "Off",
            Details = details,
            DisabledReason = "Information only. Windows' own tasks are switched in Optimize Drives.",
        };
    }

    /// <summary>The physical disk's media type wins (WMI); the drive list's guess is the fallback.</summary>
    internal static DriveMedia MediaFor(DriveDetails v, IReadOnlyList<PhysicalDiskInfo> disks)
    {
        if (OptimizePlanner.LetterOf(v.Root) is { } letter)
        {
            var onDisks = disks.Where(d => d.Letters.Contains(letter)).Select(d => d.Media).Distinct().ToList();
            // A volume spread over disks of different kinds (Storage Spaces) is treated as unknown.
            if (onDisks.Count == 1) return onDisks[0];
            if (onDisks.Count > 1) return DriveMedia.Unknown;
        }
        return v.Media;
    }

    private static string MediaText(DriveMedia m) => m switch
    {
        DriveMedia.Ssd => "SSD",
        DriveMedia.Hdd => "Hard drive",
        DriveMedia.Removable => "Removable",
        DriveMedia.Network => "Network",
        _ => "Unknown type",
    };

    private static string HealthText(DiskHealth h) => h switch
    {
        DiskHealth.Healthy => "Healthy",
        DiskHealth.Warning => "Warning",
        DiskHealth.Unhealthy => "Unhealthy",
        _ => "Unknown",
    };
}

/// <summary>Decides what "Optimize now" may do for a drive. Never defrags an SSD; never acts on an unknown type.</summary>
public static class OptimizePlanner
{
    public static PlanItem? Plan(string root, DriveMedia media, bool? trimOn)
    {
        if (LetterOf(root) is not { } letter) return null;
        return media switch
        {
            // ReTrim only helps when TRIM is on; with TRIM off the optimizer can't tell the SSD anything.
            DriveMedia.Ssd when trimOn != false => SpeedPlanItems.OptimizeVolume(letter, isSsd: true),
            DriveMedia.Hdd => SpeedPlanItems.OptimizeVolume(letter, isSsd: false),
            _ => null,
        };
    }

    public static string WhyNot(string root, DriveMedia media)
    {
        if (LetterOf(root) is null) return "Not a drive letter on this PC.";
        return media switch
        {
            DriveMedia.Ssd => "TRIM is off, so ReTrim can't help. Turn TRIM back on first.",
            DriveMedia.Removable or DriveMedia.Network => "Removable and network drives aren't optimized.",
            _ => "PC Tidy can't tell whether this is an SSD or a hard drive, and it never risks defragmenting an SSD.",
        };
    }

    /// <summary>"C:\" → 'C'. Null for anything that isn't a plain drive root (sandbox folders, network paths).</summary>
    public static char? LetterOf(string root)
    {
        var r = root.TrimEnd('\\');
        return r.Length == 2 && r[1] == ':' && char.IsAsciiLetter(r[0]) ? char.ToUpperInvariant(r[0]) : null;
    }
}
