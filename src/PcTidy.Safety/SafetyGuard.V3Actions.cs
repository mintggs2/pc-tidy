using System.Text.RegularExpressions;
using PcTidy.Core.Model;
using PcTidy.Safety.Paths;
using PcTidy.Safety.Rules;

namespace PcTidy.Safety;

/// <summary>Rules for the v3 action kinds (own scheduled tasks, start with Windows, copies, app relaunch, OneDrive, Quick access).</summary>
public sealed partial class SafetyGuard
{
    [GeneratedRegex(@"^[A-Za-z0-9 ._\-]{1,64}$", RegexOptions.CultureInvariant)] private static partial Regex OwnTaskNamePattern();
    [GeneratedRegex(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.CultureInvariant)] private static partial Regex TimePattern();

    private static readonly HashSet<string> TaskSchedules = new(StringComparer.OrdinalIgnoreCase) { "daily", "weekly", "monthly", "logon" };
    private static readonly HashSet<string> TaskArguments = new(StringComparer.Ordinal) { "--scheduled-scan", "--scheduled-scan --checkup" };

    /// <summary>Programs LaunchApp never starts: shells, script hosts and system tools that could run anything.</summary>
    private static readonly HashSet<string> NeverLaunch = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe",
        "msiexec.exe", "reg.exe", "regedit.exe", "schtasks.exe", "bcdedit.exe", "netsh.exe", "sc.exe", "wmic.exe",
        "certutil.exe", "bitsadmin.exe", "installutil.exe", "msbuild.exe", "cmstp.exe", "conhost.exe", "explorer.exe",
    };

    private SafetyVerdict CheckOwnTask(PlanItem item, bool create)
    {
        var name = Arg(item, "name");
        if (string.IsNullOrEmpty(name) || !OwnTaskNamePattern().IsMatch(name) || name.Trim() is "." or "..") return SafetyVerdict.Block(Reasons.Incomplete);
        if (!create) return SafetyVerdict.Ok;
        if (!TaskSchedules.Contains(Arg(item, "schedule") ?? "")) return SafetyVerdict.Block(Reasons.Incomplete);
        if (!TaskArguments.Contains(Arg(item, "arguments") ?? "")) return SafetyVerdict.Block(Reasons.Incomplete);
        var time = Arg(item, "time");
        if (time is not null && !TimePattern().IsMatch(time)) return SafetyVerdict.Block(Reasons.Incomplete);
        return SafetyVerdict.Ok;
    }

    private SafetyVerdict CheckCopy(PlanItem item)
    {
        var src = Source(item, use: PathUse.Read);
        if (src.Blocked) return src.Verdict;
        if (!src.Facts.Exists) return SafetyVerdict.Block(Reasons.MissingSource);
        // Copying an online-only file would download it.
        if (IsPlaceholder(src.Facts)) return SafetyVerdict.Block(Reasons.OnlineOnly, PathProtection.AskFirst);
        var dst = Target(item);
        return dst.Verdict;
    }

    private SafetyVerdict CheckLaunch(PlanItem item)
    {
        var exe = Arg(item, "exePath");
        var parsed = SafePath.TryNormalize(exe);
        if (parsed is null || !parsed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return SafetyVerdict.Block(Reasons.Incomplete);
        if (NeverLaunch.Contains(Path.GetFileName(parsed))) return SafetyVerdict.Block(Reasons.Incomplete);
        var windows = SafePath.TryNormalize(_locations.WindowsDir);
        if (windows is not null && SafePath.IsSameOrUnder(parsed, windows)) return SafetyVerdict.Block(Reasons.Incomplete);
        if ((Arg(item, "arguments") ?? "").Length > 2048) return SafetyVerdict.Block(Reasons.Incomplete);
        try { return File.Exists(@"\\?\" + parsed) ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.MissingSource); }
        catch { return SafetyVerdict.Block(Reasons.Incomplete); }
    }

    private SafetyVerdict CheckFreeUpSpace(PlanItem item)
    {
        var src = Source(item, use: PathUse.Read);
        if (src.Blocked) return src.Verdict;
        if (src.Path is null || !IsInCloudRoot(src.Path)) return SafetyVerdict.Block(Reasons.Incomplete);
        if (!src.Facts.Exists || (src.Facts.Attributes & FileAttributes.Directory) != 0) return SafetyVerdict.Block(Reasons.Incomplete);
        return SafetyVerdict.Ok;
    }

    private SafetyVerdict CheckQuickAccessPin(PlanItem item)
    {
        var src = Source(item, use: PathUse.Read);
        if (src.Path is null) return src.Verdict;
        // Pinning only adds a shortcut in Explorer; any real local folder is fine (even a protected one).
        return src.Facts.Exists && (src.Facts.Attributes & FileAttributes.Directory) != 0 ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.MissingSource);
    }
}
