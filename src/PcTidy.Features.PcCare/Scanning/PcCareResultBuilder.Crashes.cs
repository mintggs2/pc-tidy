using System.Globalization;
using PcTidy.Catalog.PcCare;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Model;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>Crash report tab (read-only): blue screens, unexpected restarts, hardware errors, app crashes and minidumps.</summary>
public static partial class PcCareResultBuilder
{
    /// <summary>Faulting modules that belong to graphics drivers (an app crash in one of these points at the GPU driver).</summary>
    private static readonly string[] GraphicsModules = { "nvwgf2umx", "nvoglv64", "nvd3dumx", "atidxx64", "atiumd64", "amdxc64", "igd10iumd64", "igd12umd64", "igxelpicd64" };

    private static void AddCrashes(PcCareSnapshot s, PcCareBuildContext c, List<ScanGroup> groups, Dictionary<string, string> notes, HashSet<CrashFixArea> fixes)
    {
        var cr = s.Crashes;
        if (cr is null || (!cr.EventLogAvailable && cr.Minidumps.Count == 0 && cr.MinidumpAccess != ReadAccess.NeedsAdmin))
        {
            notes[T.Crashes] = "Windows' event log couldn't be read, so there's no crash report this time.";
            return;
        }

        int days = Math.Max(1, c.Options.CrashDays);
        var since = c.NowUtc.AddDays(-days);
        var events = cr.Events.Where(e => e.TimeUtc >= since).OrderByDescending(e => e.TimeUtc).ToList();

        var blue = events.Where(e => e.Kind == CrashKind.BlueScreen || (e.Kind == CrashKind.UnexpectedShutdown && e.BugcheckCode is not null))
            .Select(e => BlueScreenRow(e.BugcheckCode, e.TimeUtc, fixes, source: e.Kind == CrashKind.BlueScreen ? "Windows Error Reporting" : "Kernel-Power 41"))
            .ToList();
        groups.Add(Group(T.Crashes, "bluescreens", "Blue screens", "Stop codes from Windows' event log, with the usual cause and where to look.", Badge.Safe, blue));

        var dumps = cr.Minidumps.Where(d => d.WrittenUtc >= since).OrderByDescending(d => d.WrittenUtc).Select(d =>
        {
            var row = BlueScreenRow(d.BugcheckCode, d.WrittenUtc, fixes, source: "crash dump " + Path.GetFileName(d.Path));
            return row with
            {
                Title = $"Crash dump {Path.GetFileName(d.Path)}",
                Detail = d.Path,
                SizeBytes = 0, // shown for information; Disk cleanup is where dumps are removed
                Details = new Dictionary<string, string>(row.Details!) { ["Parameters"] = string.Join(", ", d.Parameters.Select(p => "0x" + p.ToString("X", CultureInfo.InvariantCulture))) },
            };
        }).ToList();
        groups.Add(Group(T.Crashes, "dumps", "Crash dumps", "Small files Windows writes after a blue screen. Read-only here; Disk cleanup can remove old ones.", Badge.Safe, dumps));

        var power = events.Where(e => e.Kind == CrashKind.UnexpectedShutdown && e.BugcheckCode is null).Select(e => Info(
            $"Restarted without shutting down ({Date(e.TimeUtc)})",
            "Usually a power cut, holding the power button, or a frozen PC. If it keeps happening, the power supply or overheating is worth checking.",
            state: Date(e.TimeUtc))).ToList();
        groups.Add(Group(T.Crashes, "shutdowns", "Unexpected restarts", "Times Windows started again without a clean shutdown (Kernel-Power 41).", Badge.Safe, power));

        var hardware = new List<ScanItem>();
        int fatal = events.Count(e => e.Kind == CrashKind.HardwareError);
        int corrected = events.Count(e => e.Kind == CrashKind.HardwareCorrected);
        if (fatal > 0)
        {
            fixes.Add(CrashFixArea.Hardware);
            hardware.Add(Info($"{Plural(fatal, "hardware error")} Windows couldn't fix",
                "The processor, memory or a device reported a serious error. Undo any overclock (including XMP/EXPO memory profiles), check temperatures and the power supply.",
                state: fatal.ToString(CultureInfo.CurrentCulture)));
        }
        if (corrected > 0)
            hardware.Add(Info($"{Plural(corrected, "corrected hardware error")}",
                "Windows fixed these by itself. A lot of them in a short time can point to an unstable overclock, heat or a failing part.",
                state: corrected.ToString(CultureInfo.CurrentCulture)));
        groups.Add(Group(T.Crashes, "hardware", "Hardware errors", "Reported by the hardware itself (WHEA).", Badge.Safe, hardware));

        var apps = events.Where(e => e.Kind is CrashKind.AppCrash or CrashKind.AppHang && e.AppName is not null)
            .GroupBy(e => e.AppName!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => AppRow(g.Key, g.ToList(), fixes)).ToList();
        groups.Add(Group(T.Crashes, "apps", "App crashes", "Apps that closed with an error or stopped responding.", Badge.Safe, apps));

        var parts = new List<string>();
        if (blue.Count > 0) parts.Add(Plural(blue.Count, "blue screen"));
        if (power.Count > 0) parts.Add(Plural(power.Count, "unexpected restart"));
        if (fatal > 0) parts.Add(Plural(fatal, "hardware error"));
        int appCount = apps.Count;
        if (appCount > 0) parts.Add(Plural(appCount, "app") + " with crashes");
        var note = parts.Count == 0 ? $"No crashes in the last {days} days." : $"In the last {days} days: {string.Join(", ", parts)}. Everything here is read-only.";
        if (cr.MinidumpAccess == ReadAccess.NeedsAdmin) note += " Crash dump files need admin to read: run PC Tidy as admin to see their stop codes.";
        if (cr.FromReliabilityMonitor) note += " (Read from Reliability Monitor.)";
        notes[T.Crashes] = note;
    }

    private static ScanItem BlueScreenRow(uint? code, DateTime whenUtc, HashSet<CrashFixArea> fixes, string source)
    {
        var info = code is { } c ? BugcheckCatalog.Find(c) : null;
        var area = info?.Fix ?? CrashFixArea.Unknown;
        fixes.Add(area);
        string codeText = code is { } x ? BugcheckCatalog.Format(x) : "unknown";
        string cause = info?.PlainCause ?? "Windows stopped to protect itself. One crash on its own is usually nothing to worry about.";
        return Info(
            $"Blue screen on {Date(whenUtc)}" + (info is null ? "" : $": {info.Name}"),
            cause + " " + FixText(area),
            state: codeText,
            details: new Dictionary<string, string> { ["Stop code"] = codeText, ["From"] = source, ["Where to look"] = FixTitle(area) });
    }

    private static ScanItem AppRow(string app, IReadOnlyList<CrashEvent> list, HashSet<CrashFixArea> fixes)
    {
        int crashes = list.Count(e => e.Kind == CrashKind.AppCrash);
        int hangs = list.Count(e => e.Kind == CrashKind.AppHang);
        var last = list.Max(e => e.TimeUtc);
        var module = list.Where(e => e.ModuleName is not null).GroupBy(e => e.ModuleName!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
        bool graphics = module is not null && GraphicsModules.Any(m => module.StartsWith(m, StringComparison.OrdinalIgnoreCase));
        if (graphics) fixes.Add(CrashFixArea.GraphicsDriver);

        var what = new List<string>();
        if (crashes > 0) what.Add(Plural(crashes, "crash", "crashes"));
        if (hangs > 0) what.Add($"stopped responding {Plural(hangs, "time")}");
        var reason = $"Last on {Date(last)}." + (module is null ? "" : $" Faulting part: {module}.")
                     + (graphics ? " That's part of the graphics driver: see the Drivers section for an update." : " Updating the app usually helps.");
        return Info(app, reason, detail: string.Join(", ", what), state: (crashes + hangs).ToString(CultureInfo.CurrentCulture));
    }

    internal static string FixText(CrashFixArea area) => area switch
    {
        CrashFixArea.Drivers => "Look in the Drivers section for problem devices and official driver updates.",
        CrashFixArea.GraphicsDriver => "Update (or roll back) the graphics driver in the Drivers section.",
        CrashFixArea.WindowsRepair => "Run \"Repair Windows system files\" on the Repair tab.",
        CrashFixArea.Disk => "Check the drive's health on the Health report tab and back up your files.",
        CrashFixArea.Memory => "Run Windows Memory Diagnostic and undo any memory overclock (XMP/EXPO).",
        CrashFixArea.Hardware => "Undo any overclock, check temperatures and the power supply.",
        _ => "If it happens again, the Drivers section is the best place to start.",
    };

    internal static string FixTitle(CrashFixArea area) => area switch
    {
        CrashFixArea.Drivers or CrashFixArea.GraphicsDriver => "Drivers",
        CrashFixArea.WindowsRepair => "PC care > Repair",
        CrashFixArea.Disk => "Drive health",
        CrashFixArea.Memory => "Memory",
        CrashFixArea.Hardware => "Hardware",
        _ => "Drivers",
    };
}
