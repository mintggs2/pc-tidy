using System.Security.Principal;
using System.Text.RegularExpressions;
using PcTidy.Core.Model;
using PcTidy.Safety.Paths;
using PcTidy.Safety.Rules;

namespace PcTidy.Safety;

/// <summary>Rules for the v2 action kinds (config edits, shredding, profiles, features, volumes, apps).</summary>
public sealed partial class SafetyGuard
{
    /// <summary>Config/text formats EditTextFile may rewrite. Never scripts or programs.</summary>
    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ini", ".cfg", ".conf", ".config", ".vdf", ".json", ".xml", ".txt", ".toml", ".yaml", ".yml", ".properties", ".md", ".npmrc" };

    /// <summary>Processes CloseApp may never close: Windows itself, security, anti-cheat, and PC Tidy.</summary>
    private static readonly string[] NeverCloseProcesses =
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "svchost", "dwm",
        "explorer", "sihost", "fontdrvhost", "ctfmon", "conhost", "taskhostw", "spoolsv", "audiodg", "LogonUI", "RuntimeBroker",
        "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost", "MsMpEng", "NisSrv", "SecurityHealthService",
        "SecurityHealthSystray", "MpDefenderCoreService", "SgrmBroker", "TrustedInstaller", "TiWorker", "MsiExec", "wuauclt",
        "EasyAntiCheat*", "EAAntiCheat*", "BEService*", "BattlEye*", "vgc", "vgtray", "FACEIT*", "ESEA*", "PnkBstr*", "PcTidy*",
    };

    /// <summary>Optional features whose removal breaks security or common apps.</summary>
    private static readonly string[] NeverRemoveFeatures = { "Windows-Defender*", "NetFx4*", "Microsoft-Windows-Client-Content*", "Windows-Identity-Foundation" };

    [GeneratedRegex(@"^S-1-5-21(-\d+){3,4}$", RegexOptions.CultureInvariant)] private static partial Regex UserSidPattern();
    [GeneratedRegex(@"^[A-Za-z0-9_.~\-+]{1,256}$", RegexOptions.CultureInvariant)] private static partial Regex FeatureNamePattern();

    private SafetyVerdict CheckEditText(PlanItem item)
    {
        var path = item.SourcePath ?? item.DestinationPath;
        if (string.IsNullOrWhiteSpace(path) || !HasArgs(item, "content")) return SafetyVerdict.Block(Reasons.Incomplete);
        if (!EditableExtensions.Contains(Path.GetExtension(path))) return SafetyVerdict.Block(Reasons.EditFileType);
        // Games may edit Steam's per-user launch options file, and nothing else inside Steam's (protected) folder.
        if (IsSteamLocalConfig(item.FeatureId, path)) return SafetyVerdict.Ok;
        var target = Evaluate(path, PathUse.Target, item.FeatureId, null);
        if (target.Blocked) return target.Verdict;
        return IsPlaceholder(target.Facts) ? SafetyVerdict.Block(Reasons.OnlineOnly, PathProtection.AskFirst) : target.Verdict;
    }

    [GeneratedRegex(@"^userdata\\\d{1,20}\\config\\localconfig\.vdf$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SteamLocalConfigPattern();

    /// <summary>
    /// Exactly &lt;SteamRoot&gt;\userdata\&lt;digits&gt;\config\localconfig.vdf, for feature "games", with no link anywhere on the way
    /// and an existing regular file (never created here).
    /// </summary>
    private bool IsSteamLocalConfig(string? featureId, string path)
    {
        if (!string.Equals(featureId, "games", StringComparison.OrdinalIgnoreCase) || _locations.SteamRoot is not { } steam) return false;
        var root = SafePath.TryNormalize(steam);
        var file = SafePath.TryNormalize(path);
        if (root is null || file is null || !SafePath.IsStrictlyUnder(file, root)) return false;
        if (!SteamLocalConfigPattern().IsMatch(file[root.Length..].TrimStart('\\'))) return false;
        var cur = root;
        foreach (var part in file[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Prepend(""))
        {
            cur = part.Length == 0 ? cur : Path.Combine(cur, part);
            try
            {
                var a = File.GetAttributes(@"\\?\" + cur);
                if ((a & FileAttributes.ReparsePoint) != 0) return false;
                if (part.Length > 0 && cur.Equals(file, StringComparison.OrdinalIgnoreCase) && (a & FileAttributes.Directory) != 0) return false;
            }
            catch { return false; }
        }
        return true;
    }

    private SafetyVerdict CheckShred(PlanItem item)
    {
        if (item.Reversible) return SafetyVerdict.Block(Reasons.MustBeMarkedPermanent);
        var src = Source(item);
        if (CloudMoveOut(src, destination: null) is { } outOfCloud) return outOfCloud;
        if (src.Blocked) return src.Verdict;
        if ((src.Facts.Attributes & FileAttributes.Directory) != 0) return SafetyVerdict.Block(Reasons.ShredFilesOnly);
        return IsPlaceholder(src.Facts) ? SafetyVerdict.Block(Reasons.OnlineOnly, PathProtection.AskFirst) : src.Verdict;
    }

    private static SafetyVerdict CheckShellExtension(PlanItem item)
    {
        if (item.Enable is null) return SafetyVerdict.Block(Reasons.Incomplete);
        var clsid = Arg(item, "clsid");
        if (!string.IsNullOrEmpty(clsid)) return Guid.TryParse(clsid, out _) ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.Incomplete);
        return NormalizeKeyPath(Arg(item, "verbKey")) is null ? SafetyVerdict.Block(Reasons.Incomplete) : SafetyVerdict.Ok;
    }

    private SafetyVerdict CheckCookies(PlanItem item)
    {
        if (!HasArgs(item, "browser", "profileDir")) return SafetyVerdict.Block(Reasons.Incomplete);
        // The cookie database inside the browser profile is rewritten, so the profile folder is checked like a destination.
        return Evaluate(Arg(item, "profileDir"), PathUse.Target, item.FeatureId, null).Verdict;
    }

    private static SafetyVerdict CheckRestorePointDelete(PlanItem item)
    {
        if (item.Reversible) return SafetyVerdict.Block(Reasons.MustBeMarkedPermanent);
        return uint.TryParse(Arg(item, "sequenceNumber"), out _) ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.Incomplete);
    }

    private static SafetyVerdict CheckWlanProfile(PlanItem item)
        => HasArgs(item, "profileName") && Guid.TryParse(Arg(item, "interfaceGuid"), out _) ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.Incomplete);

    private static SafetyVerdict CheckUserProfileDelete(PlanItem item)
    {
        var sid = Arg(item, "sid");
        if (string.IsNullOrEmpty(sid) || !UserSidPattern().IsMatch(sid)) return SafetyVerdict.Block(Reasons.Incomplete);
        string? current = null;
        try { current = WindowsIdentity.GetCurrent().User?.Value; } catch { /* unknown: treat as "could be you" below */ }
        if (current is null || current.Equals(sid, StringComparison.OrdinalIgnoreCase)) return SafetyVerdict.Block(Reasons.OwnProfile);
        return item.Reversible ? SafetyVerdict.Block(Reasons.MustBeMarkedPermanent) : SafetyVerdict.Ok;
    }

    private static SafetyVerdict CheckOptionalFeature(PlanItem item)
    {
        var name = Arg(item, "name");
        var kind = Arg(item, "kind");
        if (string.IsNullOrEmpty(name) || !FeatureNamePattern().IsMatch(name) || kind is not ("feature" or "capability"))
            return SafetyVerdict.Block(Reasons.Incomplete);
        if (SafetyPolicy.MatchesAny(name, NeverRemoveFeatures)) return SafetyVerdict.Block(Reasons.CriticalFeature);
        // Allow-list: only the parts PC Tidy's catalog describes (with how to add them back). A block-list can't cover
        // Wi-Fi drivers, languages, WSL, VM platform and the rest of what Windows ships as removable.
        return PcTidy.Catalog.PcCare.OptionalFeatureCatalog.All.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)
                                                                         || (p.Name.EndsWith('*') && name.StartsWith(p.Name[..^1], StringComparison.OrdinalIgnoreCase)))
            ? SafetyVerdict.Ok
            : SafetyVerdict.Block(Reasons.CriticalFeature);
    }

    private SafetyVerdict CheckVolumeOptimize(PlanItem item)
    {
        var drive = Arg(item, "drive")?.TrimEnd('\\', ':');
        if (drive is not { Length: 1 } || !char.IsAsciiLetter(drive[0])) return SafetyVerdict.Block(Reasons.Incomplete);
        // Only real drive letters of this PC (a sandbox has none, so nothing is ever optimized from a test).
        return Map.Drives.Any(d => d.Length == 3 && char.ToUpperInvariant(d[0]) == char.ToUpperInvariant(drive[0]))
            ? SafetyVerdict.Ok
            : SafetyVerdict.Block(Reasons.NotThisPc);
    }

    private static SafetyVerdict CheckCloseApp(PlanItem item)
    {
        var name = Path.GetFileNameWithoutExtension(Arg(item, "processName") ?? "");
        if (!int.TryParse(Arg(item, "pid"), out var pid) || pid <= 4 || string.IsNullOrEmpty(name)) return SafetyVerdict.Block(Reasons.Incomplete);
        if (pid == Environment.ProcessId) return SafetyVerdict.Block(Reasons.NeverClose);
        return SafetyPolicy.MatchesAny(name, NeverCloseProcesses) ? SafetyVerdict.Block(Reasons.NeverClose) : SafetyVerdict.Ok;
    }

    private static SafetyVerdict CheckStoreRepair(PlanItem item)
        => HasArgs(item, "packageFamilyName") || item.Args?.ContainsKey("wsreset") == true ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.Incomplete);
}
