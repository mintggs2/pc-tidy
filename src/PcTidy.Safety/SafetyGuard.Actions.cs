using System.Text.RegularExpressions;
using PcTidy.Catalog.Services;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety.Detection;
using PcTidy.Safety.Paths;
using PcTidy.Safety.Rules;

namespace PcTidy.Safety;

/// <summary>Per-ActionKind rules for <see cref="Check"/>.</summary>
public sealed partial class SafetyGuard
{
    private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".csv", ".json", ".html", ".htm", ".xml", ".log" };

    [GeneratedRegex(@"^[A-Za-z0-9_.\-$ ]{1,256}$", RegexOptions.CultureInvariant)] private static partial Regex ServiceNamePattern();
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.CultureInvariant)] private static partial Regex VerbPattern();

    public SafetyVerdict Check(PlanItem item)
    {
        if (item is null) return SafetyVerdict.Block(Reasons.Incomplete);
        if (string.IsNullOrWhiteSpace(item.FeatureId)) return SafetyVerdict.Block(Reasons.MissingFeature);

        CarveOut? carveOut = null;
        if (item.CarveOutId is not null)
        {
            carveOut = CarveOuts.Find(item.CarveOutId);
            if (carveOut is null) return SafetyVerdict.Block(Reasons.UnknownCarveOut);
            if (!Policy.CarveOutFeatures.Contains(item.FeatureId)) return SafetyVerdict.Block(Reasons.CarveOutWrongFeature);
            // A carve-out only ever widens access for quarantine moves. On any other action the id is just a
            // label (e.g. the Windows-tool cleanup items) and grants nothing, so the normal rules apply.
            if (item.Kind != ActionKind.QuarantinePath) carveOut = null;
        }

        try
        {
            return item.Kind switch
            {
                ActionKind.QuarantinePath => CheckQuarantine(item, carveOut),
                ActionKind.MovePath or ActionKind.RenamePath => CheckMove(item, junction: false),
                ActionKind.MoveFolderWithJunction => CheckMove(item, junction: true),
                ActionKind.CreateDirectory => Target(item).Verdict,
                ActionKind.ArchiveToZip => CheckArchive(item),
                ActionKind.ExtractZip => CheckExtract(item),
                ActionKind.WriteTextFile => CheckWriteText(item),
                ActionKind.RegistrySetValue or ActionKind.RegistryDeleteValue or ActionKind.RegistryDeleteKey => CheckRegistry(item),
                ActionKind.ServiceSetStartType => CheckService(item),
                ActionKind.ScheduledTaskSetEnabled => CheckTask(item),
                ActionKind.StartupItemSetEnabled => HasArgs(item, "location", "name") && item.Enable is not null ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.Incomplete),
                ActionKind.EmptyRecycleBin => item.Reversible ? SafetyVerdict.Block(Reasons.MustBeMarkedPermanent) : SafetyVerdict.Ok,
                ActionKind.RunSystemTool => CheckTool(item),
                ActionKind.KnownFolderRedirect => CheckRedirect(item),
                ActionKind.AppxRemoveForUser => CheckAppx(item),
                ActionKind.RunUninstaller => CheckUninstaller(item),
                ActionKind.OpenExternal => CheckExternal(item),
                ActionKind.PowerPlanSet => Guid.TryParse(Arg(item, "schemeGuid"), out _) ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.Incomplete),
                ActionKind.HostsCommentOut => HasArgs(item, "lines") ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.Incomplete),
                ActionKind.ShellVerbSetInstalled => CheckShellVerb(item),
                ActionKind.EditTextFile => CheckEditText(item),
                ActionKind.ShredFile => CheckShred(item),
                ActionKind.ShellExtensionSetEnabled => CheckShellExtension(item),
                ActionKind.CookiesDeleteExcept => CheckCookies(item),
                ActionKind.RestorePointDelete => CheckRestorePointDelete(item),
                ActionKind.WlanProfileDelete => CheckWlanProfile(item),
                ActionKind.UserProfileDelete => CheckUserProfileDelete(item),
                ActionKind.OptionalFeatureRemove => CheckOptionalFeature(item),
                ActionKind.VolumeOptimize => CheckVolumeOptimize(item),
                ActionKind.CloseApp => CheckCloseApp(item),
                ActionKind.StoreRepair => CheckStoreRepair(item),
                ActionKind.ScheduledTaskCreate => CheckOwnTask(item, create: true),
                ActionKind.ScheduledTaskDelete => CheckOwnTask(item, create: false),
                ActionKind.StartWithWindowsSet => item.Enable is null ? SafetyVerdict.Block(Reasons.Incomplete) : SafetyVerdict.Ok,
                ActionKind.CopyPath => CheckCopy(item),
                ActionKind.LaunchApp => CheckLaunch(item),
                ActionKind.OneDriveFreeUpSpace => CheckFreeUpSpace(item),
                ActionKind.QuickAccessPin => CheckQuickAccessPin(item),
                _ => SafetyVerdict.Block(Reasons.UnknownAction),
            };
        }
        catch (Exception)
        {
            // A rule that can't be evaluated is a "no", never a crash and never a silent "yes".
            return SafetyVerdict.Block(Reasons.Incomplete);
        }
    }

    // ------------------------------------------------------------------ files

    /// <summary>
    /// The item's source. Rename and quarantine sources may end in an odd name (trailing dot or space, "nul"), so the
    /// rename helper can fix such names and cleanup can quarantine them; every other rule still applies to the literal path.
    /// </summary>
    private PathResult Source(PlanItem item, CarveOut? carveOut = null, PathUse use = PathUse.Remove)
        => string.IsNullOrWhiteSpace(item.SourcePath)
            ? Fail(Reasons.MissingSource)
            : Evaluate(item.SourcePath, use, item.FeatureId, carveOut, oddLeafOk: item.Kind is ActionKind.QuarantinePath or ActionKind.RenamePath);

    private PathResult Target(PlanItem item)
        => string.IsNullOrWhiteSpace(item.DestinationPath) ? Fail(Reasons.MissingDestination) : Evaluate(item.DestinationPath, PathUse.Target, item.FeatureId, null);

    private SafetyVerdict CheckQuarantine(PlanItem item, CarveOut? carveOut)
    {
        // Admin rights are only ever used inside an approved admin cleanup target (WindowsTemp, crash dumps...),
        // and an ordinary target never lends its permission to an elevated step.
        if (item.NeedsAdmin && carveOut is not { NeedsAdmin: true }) return SafetyVerdict.Block(Reasons.AdminOnlyForCarveOuts);
        var src = Source(item, carveOut);
        if (CloudMoveOut(src, destination: null) is { } outOfCloud) return outOfCloud;
        if (src.Blocked) return src.Verdict;
        // Quarantine on another drive is copy → verify → remove: copying an online-only file would download it.
        if (IsPlaceholder(src.Facts) && Map.DataRoot is { } data && !PathUtil.SameVolume(src.Path!, data))
            return SafetyVerdict.Block(Reasons.OnlineOnly, PathProtection.AskFirst);
        // AI chats: a Claude Code transcript that a running session is writing stays, checked again when the step runs (AIC-2).
        if (LiveClaudeTranscript(item) is { } live) return live;
        return src.Verdict;
    }

    private SafetyVerdict CheckMove(PlanItem item, bool junction)
    {
        var src = Source(item);
        if (CloudMoveOut(src, item.DestinationPath) is { } outOfCloud) return outOfCloud;
        if (src.Blocked) return src.Verdict;
        var dst = Target(item);
        if (dst.Blocked) return dst.Verdict;
        if (SafePath.IsSameOrUnder(dst.Path!, src.Path!)) return SafetyVerdict.Block(Reasons.MoveIntoItself);
        if (junction && (IsInCloudRoot(src.Path!) || IsInCloudRoot(dst.Path!))) return SafetyVerdict.Block(Reasons.JunctionCloud);
        // Moving a folder out of Documents/Videos/... and leaving a link behind isn't "organizing": it asks first
        // (the user allows that folder), even for features that may otherwise work inside the libraries.
        if (junction && Map.Libraries.FirstOrDefault(l => SafePath.IsStrictlyUnder(src.Path!, l.Path)) is { Path: not null } lib
            && !IsAllowedAskFirst(src.Path!))
            return SafetyVerdict.Block(Reasons.Library(lib.Name), PathProtection.AskFirst);
        if (IsPlaceholder(src.Facts) && !PathUtil.SameVolume(src.Path!, dst.Path!)) return SafetyVerdict.Block(Reasons.OnlineOnly, PathProtection.AskFirst);
        return Stricter(src.Verdict, dst.Verdict);
    }

    private SafetyVerdict CheckArchive(PlanItem item)
    {
        if (!IsZip(item.DestinationPath)) return SafetyVerdict.Block(Reasons.ZipOnly);
        var src = Source(item);
        if (CloudMoveOut(src, item.DestinationPath) is { } outOfCloud) return outOfCloud;
        if (src.Blocked) return src.Verdict;
        if (IsPlaceholder(src.Facts)) return SafetyVerdict.Block(Reasons.OnlineOnly, PathProtection.AskFirst);
        var dst = Target(item);
        if (dst.Blocked) return dst.Verdict;
        if (SafePath.IsSameOrUnder(dst.Path!, src.Path!)) return SafetyVerdict.Block(Reasons.MoveIntoItself);
        return Stricter(src.Verdict, dst.Verdict);
    }

    private SafetyVerdict CheckExtract(PlanItem item)
    {
        if (!IsZip(item.SourcePath)) return SafetyVerdict.Block(Reasons.ZipOnly);
        var src = Source(item, use: PathUse.Read);
        if (src.Blocked) return src.Verdict;
        if (IsPlaceholder(src.Facts)) return SafetyVerdict.Block(Reasons.OnlineOnly, PathProtection.AskFirst);
        return Target(item).Verdict;
    }

    private SafetyVerdict CheckWriteText(PlanItem item)
    {
        if (!TextFileExtensions.Contains(Path.GetExtension(item.DestinationPath ?? "")))
            return SafetyVerdict.Block(Reasons.TextFileType);
        // PC Tidy's own reports folder (inside its otherwise protected data folder): reports only, never anything else.
        if (IsReportsFile(item.DestinationPath)) return SafetyVerdict.Ok;
        return Target(item).Verdict;
    }

    /// <summary>A new file directly inside &lt;DataRoot&gt;\reports (not a folder, no link on the way, not an existing file's overwrite).</summary>
    private bool IsReportsFile(string? path)
    {
        var dataRoot = SafePath.TryNormalize(_settings.DataRoot);
        var file = SafePath.TryNormalize(path);
        if (dataRoot is null || file is null) return false;
        var reports = DataPaths.Reports(dataRoot);
        if (!string.Equals(Path.GetDirectoryName(file), reports, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            foreach (var p in new[] { dataRoot, reports })
                if (Directory.Exists(@"\\?\" + p) && (File.GetAttributes(@"\\?\" + p) & FileAttributes.ReparsePoint) != 0) return false;
            return !File.Exists(@"\\?\" + file) && !Directory.Exists(@"\\?\" + file);
        }
        catch { return false; }
    }

    /// <summary>
    /// Taking something out of a cloud/sync folder (move elsewhere, or quarantine) deletes the cloud copy on every
    /// device, so it's ask-first unless the user allowed that folder. Never-touched sources keep their own reason.
    /// </summary>
    private SafetyVerdict? CloudMoveOut(PathResult src, string? destination)
    {
        if (src.Path is null || src.Verdict.Protection == PathProtection.Never && src.Blocked) return null;
        var root = CloudRootOf(Map, src.Path);
        if (root is null || IsAllowedAskFirst(src.Path)) return null;
        var dst = SafePath.TryNormalize(destination);
        if (dst is not null && SafePath.IsStrictlyUnder(dst, root)) return null;
        return SafetyVerdict.Block(Reasons.CloudMoveOut, PathProtection.AskFirst);
    }

    private static bool IsZip(string? path) => path is not null && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlaceholder(PathFacts facts) => facts.Exists && CloudFile.IsPlaceholder(facts.Attributes);

    private static SafetyVerdict Stricter(SafetyVerdict a, SafetyVerdict b) => b.Protection > a.Protection ? b : a;

    // ------------------------------------------------------------------ registry, services, tasks

    private SafetyVerdict CheckRegistry(PlanItem item)
    {
        var spec = item.Registry;
        if (spec is null) return SafetyVerdict.Block(Reasons.Incomplete);
        var key = NormalizeKeyPath(spec.KeyPath);
        if (key is null) return SafetyVerdict.Block(Reasons.RegistryMalformed);
        var deletesKey = item.Kind == ActionKind.RegistryDeleteKey;
        if (!deletesKey && spec.ValueName is null) return SafetyVerdict.Block(Reasons.Incomplete);
        var valueName = deletesKey ? null : spec.ValueName;
        if (Policy.Registry.Any(r => r.Allows(spec.Hive, key, valueName, deletesKey))) return SafetyVerdict.Ok;
        return CatalogAllows(item, spec, key, valueName) ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.RegistryNotAllowed);
    }

    /// <summary>
    /// The shared catalog (tweaks, speed settings, bad-tweak repairs…) lists every exact registry value PC Tidy may
    /// change and the data it may write. Anything not listed is refused. HKLM entries require the item to be marked
    /// NeedsAdmin and IsSystemChange (so a restore point is made first).
    /// </summary>
    private static bool CatalogAllows(PlanItem item, RegistryValueSpec spec, string key, string? valueName)
    {
        var allowance = PcTidy.Catalog.RegistryCatalog.Find(spec.Hive, key, valueName);
        if (allowance is null) return false;
        if (spec.Hive == RegistryHive.LocalMachine && (!item.NeedsAdmin || !item.IsSystemChange)) return false;
        return item.Kind switch
        {
            ActionKind.RegistryDeleteValue or ActionKind.RegistryDeleteKey => allowance.AllowDelete,
            // No listed data = delete-only (e.g. bad-tweak repairs that remove a value). Never "any data".
            ActionKind.RegistrySetValue => allowance.Kind == spec.Kind && (allowance.AllowedData.Count > 0
                ? allowance.AllowedData.Any(d => SameData(spec.Kind, d, spec.Data))
                : allowance.DataPattern is { } pattern && spec.Data is { } data
                  && System.Text.RegularExpressions.Regex.IsMatch(data, pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))),
            _ => false,
        };
    }

    private static bool SameData(RegistryKind kind, string allowed, string? actual)
    {
        if (actual is null) return false;
        if (kind is RegistryKind.DWord or RegistryKind.QWord)
            return TryParseNumber(allowed, out var a) && TryParseNumber(actual, out var b) && a == b;
        return string.Equals(allowed, actual, kind == RegistryKind.Binary ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool TryParseNumber(string s, out ulong value)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value)
            : ulong.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    /// <summary>"\Software\\X\" → "Software\X". Refuses hive prefixes, "." / ".." segments and control characters.</summary>
    private static string? NormalizeKeyPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl)) return null;
        var parts = raw.Trim().Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p.Trim() is "." or "..")) return null;
        var first = parts[0].ToUpperInvariant();
        if (first.StartsWith("HKEY_", StringComparison.Ordinal) || first is "HKCU" or "HKLM" or "HKU" or "HKCR" or "HKCC" or "HKCU:" or "HKLM:") return null;
        return string.Join('\\', parts);
    }

    private SafetyVerdict CheckService(PlanItem item)
    {
        var name = item.ServiceName?.Trim();
        if (string.IsNullOrEmpty(name) || !ServiceNamePattern().IsMatch(name) || item.ServiceStartType is null)
            return SafetyVerdict.Block(Reasons.ServiceBad);
        if (SafetyPolicy.MatchesAny(name, Policy.NeverTouchServices))
        {
            // The one exception: the bad-tweak finder may switch a protected service (Defender, Windows Update, firewall…)
            // BACK ON after an old optimizer disabled it. Turning a protected service off is never allowed.
            // Only to that service's exact Windows default (see ServiceDefaults), so it can't be used to downgrade
            // e.g. the firewall from Automatic to Manual.
            var repairsIt = item.FeatureId is { } f
                && (f.Equals("tweaks.badtweaks", StringComparison.OrdinalIgnoreCase) || f.Equals("services.repair", StringComparison.OrdinalIgnoreCase))
                && item.ServiceStartType is { } start && ServiceDefaults.IsDefaultFor(name, start)
                && item.NeedsAdmin && item.IsSystemChange;
            if (!repairsIt) return SafetyVerdict.Block(Reasons.ServiceNever(name));
        }
        // Boot and System start types are for drivers; a service never needs them.
        if (item.ServiceStartType is ServiceStart.Boot or ServiceStart.System) return SafetyVerdict.Block(Reasons.ServiceBad);
        // Every section: a service the catalog calls Protected only ever goes back to its Windows default, and only
        // through a repair (the backstop for anything missing from NeverTouchServices).
        if (PcTidy.Catalog.Services.ServiceCatalog.Find(name) is { Verdict: ServiceVerdict.Protected } guarded)
        {
            var repair = item.FeatureId is { } rf
                && (rf.Equals("tweaks.badtweaks", StringComparison.OrdinalIgnoreCase) || rf.Equals("services.repair", StringComparison.OrdinalIgnoreCase));
            if (!repair || !(guarded.WindowsDefaults.Contains(item.ServiceStartType!.Value) || ServiceDefaults.IsDefaultFor(name, item.ServiceStartType!.Value)))
                return SafetyVerdict.Block(Reasons.ServiceNever(name));
        }
        return CheckServicesSection(item, name);
    }

    /// <summary>
    /// The Services section: only what the service catalog allows. Proven entries → their recommended start type;
    /// Situational → only in Expert mode; Placebo/Harmful/Protected → never; any entry → back to its Windows default;
    /// unknown (third-party) services → Manual only. The repair ("services.repair") may only restore a Windows default.
    /// Other features keep their own rules (Speed: Manual for third-party services, checked by the lists above).
    /// </summary>
    private SafetyVerdict CheckServicesSection(PlanItem item, string name)
    {
        var feature = item.FeatureId ?? "";
        var isSection = feature.Equals("services", StringComparison.OrdinalIgnoreCase);
        var isRepair = feature.Equals("services.repair", StringComparison.OrdinalIgnoreCase);
        // Only the Services section may switch a service off (Speed and Tweaks set Manual at most; the handler agrees).
        if (!isSection && item.ServiceStartType == ServiceStart.Disabled) return SafetyVerdict.Block(Reasons.ServiceNever(name));
        if (!isSection && !isRepair) return CheckNotWindowsOwn(item, name);
        if (!item.NeedsAdmin || !item.IsSystemChange || !item.Reversible) return SafetyVerdict.Block(Reasons.ServiceBad);
        var target = item.ServiceStartType!.Value;
        var entry = PcTidy.Catalog.Services.ServiceCatalog.Find(name);
        var isDefault = (entry?.WindowsDefaults.Contains(target) ?? false) || ServiceDefaults.IsDefaultFor(name, target);
        if (isRepair) return isDefault ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.ServiceNever(name));
        if (entry is null) return target == ServiceStart.Manual ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.ServiceNever(name));
        if (isDefault && entry.Verdict is not ServiceVerdict.Protected) return SafetyVerdict.Ok;
        var allowed = entry.Verdict switch
        {
            ServiceVerdict.Proven => entry.Recommended == target,
            ServiceVerdict.Situational => _settings.ExpertMode && entry.Recommended == target,
            _ => false,
        };
        return allowed ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.ServiceNever(name));
    }

    /// <summary>
    /// SVC-1: outside the Services section (which has its own catalog rules) a feature may change only services that
    /// aren't Windows' own: the service's program, read from its registry ImagePath, must not be in the Windows folder,
    /// and an unreadable ImagePath is a no. Services the catalog knows follow the catalog rules above instead, and the
    /// bad-tweak finder may still switch a Windows service back to its Windows default.
    /// </summary>
    private SafetyVerdict CheckNotWindowsOwn(PlanItem item, string name)
    {
        if (PcTidy.Catalog.Services.ServiceCatalog.Find(name) is not null) return SafetyVerdict.Ok;
        var repair = string.Equals(item.FeatureId, "tweaks.badtweaks", StringComparison.OrdinalIgnoreCase)
            && item.NeedsAdmin && item.IsSystemChange && ServiceDefaults.IsDefaultFor(name, item.ServiceStartType!.Value);
        if (repair) return SafetyVerdict.Ok;
        var image = _serviceImages.Find(name);
        return image.State switch
        {
            // A practice PC's services exist only in its practice data; nothing real can change there.
            ServiceImageState.Missing when _locations.IsSandbox => SafetyVerdict.Ok,
            ServiceImageState.Found when !image.UnderWindows => SafetyVerdict.Ok,
            ServiceImageState.Found => SafetyVerdict.Block(Reasons.ServiceWindowsOwn(name)),
            _ => SafetyVerdict.Block(Reasons.ServiceUnknown(name)),
        };
    }

    private static SafetyVerdict CheckTask(PlanItem item)
    {
        var path = item.TaskPath?.Trim().Replace('/', '\\');
        if (string.IsNullOrEmpty(path) || item.Enable is null || !path.StartsWith('\\')) return SafetyVerdict.Block(Reasons.Incomplete);
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p.Trim() is "." or "..")) return SafetyVerdict.Block(Reasons.Incomplete);
        if (!parts[0].Equals("Microsoft", StringComparison.OrdinalIgnoreCase)) return SafetyVerdict.Ok;
        // Windows' own tasks: only the bad-tweak finder may turn one back ON; never off.
        return string.Equals(item.FeatureId, "tweaks.badtweaks", StringComparison.OrdinalIgnoreCase) && item.Enable == true
            ? SafetyVerdict.Ok
            : SafetyVerdict.Block(Reasons.WindowsTask);
    }

    // ------------------------------------------------------------------ system

    private SafetyVerdict CheckTool(PlanItem item)
    {
        var problem = SystemToolRules.Validate(item);
        if (problem is not null) return SafetyVerdict.Block(problem);
        if (SystemToolRules.FolderArgument(item.ToolId!) is not { } folderArg) return SafetyVerdict.Ok;
        var folder = Arg(item, folderArg.ArgName);
        // Driver backups live in PC Tidy's own data folder (otherwise protected): <DataRoot>\backups\drivers\<name>.
        if (item.ToolId is ToolIds.PnpExportDrivers or ToolIds.PnpAddDrivers && IsDriverBackupFolder(folder))
            return SafetyVerdict.Ok;
        // Installing drivers elevated from any other folder (Downloads, a USB stick...) is refused: only PC Tidy's own backups.
        if (item.ToolId is ToolIds.PnpAddDrivers) return SafetyVerdict.Block(Reasons.ToolArgument("drivers from outside PC Tidy's own driver backups"));
        // Compression runs with the user's own rights only (the helper refuses it too): never on admin-only folders.
        if (item.ToolId is ToolIds.CompactLzx && item.NeedsAdmin) return SafetyVerdict.Block(Reasons.ToolArgument("compressing with admin rights"));
        var verdict = Evaluate(folder, folderArg.Writes ? PathUse.Target : PathUse.Read, item.FeatureId, null);
        if (verdict.Blocked) return verdict.Verdict;
        // Compressing a cloud folder would download every online-only file first.
        if (item.ToolId is ToolIds.CompactLzx && IsInCloudRoot(folder!))
            return SafetyVerdict.Block(Reasons.ToolArgument("a cloud or ask-first folder"), PathProtection.AskFirst);
        return verdict.Verdict;
    }

    /// <summary>True for a folder strictly inside &lt;DataRoot&gt;\backups\drivers with no link anywhere from the data root down.</summary>
    private bool IsDriverBackupFolder(string? folder)
    {
        var dataRoot = SafePath.TryNormalize(_settings.DataRoot);
        var path = SafePath.TryNormalize(folder);
        if (dataRoot is null || path is null) return false;
        var backups = Path.Combine(DataPaths.Backups(dataRoot), "drivers");
        if (!SafePath.IsStrictlyUnder(path, backups)) return false;
        // Walk from the data root to the folder: any existing reparse point on the way is refused.
        var cur = dataRoot;
        foreach (var part in Path.GetRelativePath(dataRoot, path).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':')) return false;
            cur = Path.Combine(cur, part);
            try
            {
                var a = File.GetAttributes(@"\\?\" + cur);
                if ((a & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (FileNotFoundException) { break; }
            catch (DirectoryNotFoundException) { break; }
            catch (Exception) { return false; }
        }
        return true;
    }

    private SafetyVerdict CheckRedirect(PlanItem item)
    {
        if (!Guid.TryParse(Arg(item, "folderId"), out var id) || !KnownFolders.UserLibraries.Contains(id)) return SafetyVerdict.Block(Reasons.Incomplete);
        var current = SafePath.TryNormalize(CurrentFolder(id));
        if (current is not null && IsInCloudRoot(current)) return SafetyVerdict.Block(Reasons.RedirectCloudBacked);

        var dst = Target(item);
        if (dst.Blocked) return dst.Verdict;
        var target = dst.Path!;
        // Never a drive root, never inside a cloud folder, never inside (or around) another user folder.
        if (target.Length <= 3 || IsInCloudRoot(target)) return SafetyVerdict.Block(Reasons.BadRedirect);
        var others = KnownFolders.UserLibraries.Select(CurrentFolder).Select(SafePath.TryNormalize).OfType<string>();
        if (others.Any(o => SafePath.IsSameOrUnder(target, o) || SafePath.IsSameOrUnder(o, target))) return SafetyVerdict.Block(Reasons.BadRedirect);
        if (dst.Facts.Exists && !IsEmptyDirectory(target)) return SafetyVerdict.Block(Reasons.BadRedirect);
        return dst.Verdict;
    }

    private string? CurrentFolder(Guid id)
    {
        if (id == KnownFolders.Desktop) return _locations.Desktop;
        if (id == KnownFolders.Documents) return _locations.Documents;
        if (id == KnownFolders.Downloads) return _locations.Downloads;
        if (id == KnownFolders.Pictures) return _locations.Pictures;
        if (id == KnownFolders.Videos) return _locations.Videos;
        if (id == KnownFolders.Music) return _locations.Music;
        return null;
    }

    private static bool IsEmptyDirectory(string path)
    {
        try { return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private SafetyVerdict CheckAppx(PlanItem item)
    {
        var full = Arg(item, "packageFullName");
        if (string.IsNullOrEmpty(full)) return SafetyVerdict.Block(Reasons.Incomplete);
        var name = full.Split('_')[0];
        if (Policy.RemovableStoreExceptions.Contains(name)) return SafetyVerdict.Ok;
        return SafetyPolicy.MatchesAny(name, Policy.CriticalStorePackages) ? SafetyVerdict.Block(Reasons.CriticalStoreApp) : SafetyVerdict.Ok;
    }

    private static SafetyVerdict CheckUninstaller(PlanItem item)
    {
        if (item.Reversible) return SafetyVerdict.Block(Reasons.MustBeMarkedPermanent);
        var code = Arg(item, "msiProductCode");
        if (!string.IsNullOrEmpty(code)) return Guid.TryParseExact(code, "B", out _) ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.Incomplete);
        return string.IsNullOrEmpty(Arg(item, "uninstallString")) ? SafetyVerdict.Block(Reasons.Incomplete) : SafetyVerdict.Ok;
    }

    private SafetyVerdict CheckExternal(PlanItem item)
    {
        var target = Arg(item, "target");
        if (string.IsNullOrEmpty(target) || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.IsFile || uri.IsUnc)
            return SafetyVerdict.Block(Reasons.ExternalTarget);
        return Policy.ExternalSchemes.Contains(uri.Scheme) ? SafetyVerdict.Ok : SafetyVerdict.Block(Reasons.ExternalTarget);
    }

    private static SafetyVerdict CheckShellVerb(PlanItem item)
    {
        if (item.Enable is null || !VerbPattern().IsMatch(Arg(item, "verb") ?? "")) return SafetyVerdict.Block(Reasons.Incomplete);
        return item.Enable == true && string.IsNullOrEmpty(Arg(item, "command")) ? SafetyVerdict.Block(Reasons.Incomplete) : SafetyVerdict.Ok;
    }

    private static string? Arg(PlanItem item, string name)
        => item.Args is not null && item.Args.TryGetValue(name, out var v) ? v?.Trim() : null;

    private static bool HasArgs(PlanItem item, params string[] names) => names.All(n => !string.IsNullOrEmpty(Arg(item, n)));
}
