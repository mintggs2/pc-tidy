using System.Management;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Volume facts and Windows' own optimizer. Real: Storage WMI (MSFT_Volume / MSFT_PhysicalDisk). Tests: a fake.</summary>
public interface IVolumeOptimizer
{
    bool TouchesRealSystem { get; }
    bool IsFixedDrive(char letter);
    /// <summary>SSD / HDD of the disk(s) under the volume; Unknown when it can't be told or the disks differ.</summary>
    DriveMedia MediaOf(char letter);
    /// <summary>MSFT_Volume.Optimize. Returns its result code (0 = success).</summary>
    uint Optimize(char letter, bool reTrim, bool defrag);
}

public sealed class WmiVolumeOptimizer : IVolumeOptimizer
{
    private const string Scope = @"root\Microsoft\Windows\Storage";

    public bool TouchesRealSystem => true;

    public bool IsFixedDrive(char letter)
        => DriveInfo.GetDrives().Any(d => d.DriveType == DriveType.Fixed && char.ToUpperInvariant(d.Name[0]) == letter);

    public DriveMedia MediaOf(char letter)
    {
        var disks = new HashSet<uint>();
        using (var partitions = new ManagementObjectSearcher(Scope, "SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
        using (var results = partitions.Get())
        {
            foreach (ManagementObject p in results)
                using (p)
                    if (Letter(p["DriveLetter"]) == letter) disks.Add(Convert.ToUInt32(p["DiskNumber"]));
        }
        if (disks.Count == 0) return DriveMedia.Unknown;

        var media = new HashSet<DriveMedia>();
        using (var physical = new ManagementObjectSearcher(Scope, "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk"))
        using (var results = physical.Get())
        {
            foreach (ManagementObject d in results)
                using (d)
                    if (uint.TryParse(d["DeviceId"] as string, out var n) && disks.Contains(n))
                        media.Add(Convert.ToUInt16(d["MediaType"] ?? 0) switch { 3 => DriveMedia.Hdd, 4 or 5 => DriveMedia.Ssd, _ => DriveMedia.Unknown });
        }
        return media.Count == 1 ? media.Single() : DriveMedia.Unknown;
    }

    public uint Optimize(char letter, bool reTrim, bool defrag)
    {
        using var volumes = new ManagementObjectSearcher(Scope, "SELECT * FROM MSFT_Volume");
        using var results = volumes.Get();
        foreach (ManagementObject v in results)
        {
            using (v)
            {
                if (Letter(v["DriveLetter"]) != letter) continue;
                using var input = v.GetMethodParameters("Optimize");
                input["ReTrim"] = reTrim;
                input["Defrag"] = defrag;
                input["Analyze"] = false;
                input["SlabConsolidate"] = false;
                input["TierOptimize"] = false;
                using var output = v.InvokeMethod("Optimize", input, null);
                return Convert.ToUInt32(output?["ReturnValue"] ?? uint.MaxValue);
            }
        }
        throw new InvalidOperationException("Windows didn't list this drive.");
    }

    private static char? Letter(object? value) => value switch
    {
        char c when char.IsAsciiLetter(c) => char.ToUpperInvariant(c),
        ushort u when char.IsAsciiLetter((char)u) => char.ToUpperInvariant((char)u),
        _ => null,
    };
}

/// <summary>
/// VolumeOptimize. Args: "drive" (letter), optional "expect" = retrim | defrag (what the scan saw).
/// Windows' own optimizer (MSFT_Volume.Optimize): ReTrim for an SSD, Defrag for a hard drive, decided here from the
/// disk's media type (MSFT_PhysicalDisk). An SSD is never defragmented; an unknown or mixed drive is left alone.
/// Not reversible and nothing to reverse: it only reorganizes free space.
/// </summary>
public sealed class VolumeOptimizeHandler(IVolumeOptimizer optimizer) : SystemActionHandler
{
    public VolumeOptimizeHandler() : this(new WmiVolumeOptimizer()) { }

    protected override ActionKind Kind => ActionKind.VolumeOptimize;
    protected override bool TouchesRealSystem => optimizer.TouchesRealSystem;
    protected override bool NeedsElevation(PlanItem item) => true;

    private static char Letter(PlanItem item) => PathArgs.DriveLetter(item.Arg("drive"))!.Value;

    protected override string? Validate(PlanItem item)
    {
        if (PathArgs.DriveLetter(item.Arg("drive")) is null) return "This step doesn't name a valid drive.";
        var expect = item.Arg("expect").Trim();
        return expect is "" or "retrim" or "defrag" ? null : "The kind of optimization is not valid.";
    }

    protected override string Describe(PlanItem item) => item.Arg("expect").Trim() == "defrag"
        ? $"Would defragment {Letter(item)}: if it is a hard drive (MSFT_Volume.Optimize -Defrag)"
        : $"Would run ReTrim on {Letter(item)}: if it is an SSD (MSFT_Volume.Optimize -ReTrim)";

    /// <summary>What to run for this media. SSD → ReTrim only; HDD → Defrag only; anything else → refused.</summary>
    internal static (bool ReTrim, bool Defrag, string? Problem) Decide(DriveMedia media, string expect) => (media, expect) switch
    {
        (DriveMedia.Ssd, "defrag") => (false, false, "This drive is an SSD. SSDs are never defragmented (it only wears them out)."),
        (DriveMedia.Ssd, _) => (true, false, null),
        (DriveMedia.Hdd, "retrim") => (false, false, "This drive turned out to be a hard drive, not an SSD, so ReTrim doesn't apply. Scan again."),
        (DriveMedia.Hdd, _) => (false, true, null),
        _ => (false, false, "Windows couldn't tell whether this is an SSD or a hard drive, so PC Tidy left it alone."),
    };

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var letter = Letter(step.Item);
        if (!optimizer.IsFixedDrive(letter)) return step.Skipped($"{letter}: isn't a fixed drive on this PC.");
        var (reTrim, defrag, problem) = Decide(optimizer.MediaOf(letter), step.Item.Arg("expect").Trim());
        if (problem is not null) return step.Skipped(problem);
        var rc = optimizer.Optimize(letter, reTrim, defrag);
        return rc == 0
            ? step.Done(reTrim ? $"Windows told the SSD {letter}: which space is free (ReTrim)." : $"Windows defragmented {letter}:.")
            : step.Failed($"Windows' optimizer reported a problem (code {rc}).");
    }, ct);

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(step.Skipped("Optimizing a drive only reorganizes it; nothing was removed, so there's nothing to undo."));
}
