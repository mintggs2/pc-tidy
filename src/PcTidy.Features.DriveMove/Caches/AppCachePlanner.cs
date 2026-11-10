using PcTidy.Catalog.DriveMove;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;
using PcTidy.Features.DriveMove.Drives;
using PcTidy.Features.DriveMove.Folders;
using PcTidy.Features.DriveMove.PcFacts;

namespace PcTidy.Features.DriveMove.Caches;

/// <summary>
/// App caches with a supported "new location" setting (Plan §8, Research.md §7). For each tool that's installed (its folder,
/// config file or variable exists) the current size is measured and ONLY the tool's own mechanism is offered:
/// a config-file edit PC Tidy can make (EditTextFile, backed up and undoable) followed by an unticked "old cache to
/// quarantine" item, or plain steps (environment variables, app settings, WSL, Docker). Nothing is moved by hand.
/// </summary>
internal static class AppCachePlanner
{
    public static Task<PartResult> PlanAsync(DriveMoveContext c, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        if (!c.Drives.MovingAvailable) return Task.FromResult(PartResult.Empty with { Notes = new[] { c.Drives.UnavailableReason! } });
        var edits = new List<ScanItem>();
        var steps = new List<ScanItem>();
        var movables = new List<Movable>();
        var tools = AppCacheCatalog.All;
        for (int i = 0; i < tools.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tool = tools[i];
            progress?.Report(new ScanProgress($"Looking for {tool.Name}…", 0.05 + 0.9 * i / tools.Count));
            if (tool.Mechanism == RelocationMechanism.WslMove) { steps.AddRange(Wsl(c, tool)); continue; }
            var found = Locate(c, tool);
            if (found is null) continue;
            var totals = FolderMeasure.Measure(c.Walker, found.Value.Folder, ct);
            if (!totals.Exists || totals.Bytes < c.Thresholds.MinCacheBytes) continue;
            var plan = PlanTool(c, tool, found.Value.Folder, found.Value.Via, totals);
            (plan.Editable ? edits : steps).AddRange(plan.Rows);
            if (plan.Movable is not null) movables.Add(plan.Movable);
        }

        var groups = new List<ScanGroup>();
        if (edits.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = DriveMoveIds.GroupCachesConfig,
                Title = "Caches PC Tidy can point to another drive",
                Description = "PC Tidy changes the tool's own setting (its config file is backed up first, and Undo puts it back). The tool fills the new folder as it needs to. The old cache can then go to quarantine.",
                Badge = Badge.Check,
                Items = edits,
            });
        if (steps.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = DriveMoveIds.GroupCachesSteps,
                Title = "Moves you do in the app itself",
                Description = "These tools move their data with their own setting or command. PC Tidy shows the exact steps and the folder to use.",
                Badge = Badge.Check,
                Items = steps,
            });
        var notes = groups.Count == 0
            ? new[] { $"No developer or AI tool caches of {Sizes.Format(c.Thresholds.MinCacheBytes)} or more were found." }
            : Array.Empty<string>();
        return Task.FromResult(new PartResult(groups, movables, notes));
    }

    private readonly record struct Located(string Folder, string Via);

    private sealed record ToolPlan(bool Editable, IReadOnlyList<ScanItem> Rows, Movable? Movable);

    /// <summary>Where the tool's data is now: its variable, then its own config file, then the usual folder.</summary>
    private static Located? Locate(DriveMoveContext c, AppCacheTool tool)
    {
        foreach (var name in tool.LocationVariables)
            if (PathText.TryNormalize(c.Pc.Variable(name)) is { } fromVariable && FolderMeasure.DirectoryExists(fromVariable))
                return new Located(fromVariable, $"set by {name}");
        if (tool.Config is { } cfg && ReadConfig(c, cfg) is { } read && PathText.TryNormalize(ConfigText.Get(cfg.Format, read.Text, cfg.Key, cfg.Section)) is { } fromConfig
            && FolderMeasure.DirectoryExists(fromConfig))
            return new Located(fromConfig, $"set in {Path.GetFileName(read.Path)}");
        foreach (var f in tool.DefaultFolders)
        {
            var path = Resolve(c.Locations, f.Base, f.RelativePath);
            if (FolderMeasure.DirectoryExists(path)) return new Located(path, "its usual place");
        }
        return null;
    }

    private static ToolPlan PlanTool(DriveMoveContext c, AppCacheTool tool, string folder, string via, FolderTotals totals)
    {
        var from = c.Drives.DriveOf(folder);
        var to = c.Drives.TargetsFor(from, totals.Bytes, c.Thresholds).FirstOrDefault();
        var state = $"{Sizes.Format(totals.Bytes)} on {from?.Name ?? "this PC"} ({via})";
        if (to is null)
        {
            var roomiest = c.Drives.RoomiestOther(from);
            var why = roomiest is null ? "There's no other drive with room for it."
                : $"{roomiest.Name} has {Sizes.Format(roomiest.FreeBytes)} free; it needs {Sizes.Format(c.Thresholds.Needed(totals.Bytes))} (its size plus 10% to spare).";
            return new ToolPlan(false, new[] { InfoRow(tool, folder, state, why) }, null);
        }
        var target = Path.Combine(to.Root, DriveMoveIds.MovedFolderName, tool.Name);
        var steps = Fill(tool.Steps, target, tool.Variable);
        var warning = tool.Warning is null ? "" : " " + Fill(tool.Warning, target, tool.Variable);

        if (tool.Mechanism == RelocationMechanism.ConfigFile && tool.Config is { } cfg && EditRows(c, tool, cfg, folder, target, to, totals, state, warning) is { } rows)
            return new ToolPlan(true, rows, new Movable(MovableKind.AppCache, tool.Name, folder, totals.Bytes, null, rows[0].Actions, to));

        var how = tool.Mechanism switch
        {
            RelocationMechanism.EnvironmentVariable => steps + " PC Tidy shows the steps; it doesn't change environment variables.",
            // A config file PC Tidy can't edit here (unreadable, or a kind the safety rules don't edit): the line to add yourself.
            RelocationMechanism.ConfigFile when tool.Config is { } c2 => ManualStep(c2, Resolve(c.Locations, c2.Base, c2.RelativePath), target),
            _ => steps,
        };
        return new ToolPlan(false, new[] { InfoRow(tool, folder, state, $"{tool.What} {how}{warning}", target) }, null);
    }

    private static string ManualStep(ConfigFileTarget cfg, string file, string target) => cfg.Format switch
    {
        ConfigFormat.NpmRc => $"Close your terminals, then add the line {cfg.Key}={target} to {file} (make the file if it isn't there).",
        ConfigFormat.PipIni => $"Add {cfg.Key} = {target} under [{cfg.Section}] in {file} (make the file if it isn't there).",
        ConfigFormat.NuGetConfig => $"Add <add key=\"{cfg.Key}\" value=\"{target}\" /> inside <config> in {file}.",
        _ => $"Add <{cfg.Key}>{target}</{cfg.Key}> inside <settings> in {file}.",
    };

    /// <summary>The config edit plus the unticked "old cache to quarantine" row. Null when the guard refuses the edit (steps are shown instead).</summary>
    private static IReadOnlyList<ScanItem>? EditRows(DriveMoveContext c, AppCacheTool tool, ConfigFileTarget cfg, string folder, string target,
        DriveFacts to, FolderTotals totals, string state, string warning)
    {
        var file = Resolve(c.Locations, cfg.Base, cfg.RelativePath);
        var read = ReadConfig(c, cfg);
        var exists = read is not null;
        // There but unreadable (locked, huge, online-only): never replaced by a fresh file. Steps instead.
        if (!exists && FolderMeasure.FileExists(file)) return null;
        var content = ConfigText.Set(cfg.Format, read?.Text ?? "", cfg.Key, target, cfg.Section);
        if (content is null) return null; // a config file PC Tidy can't read safely: steps instead
        var edit = Plans.EditConfig(file, content, exists, read?.LastWriteUtc, tool.Id, tool.Name, target, cfg, tool.CloseFirst);
        var verdict = c.Safety.Check(edit);
        if (!verdict.Allowed) return null;

        var rows = new List<ScanItem>
        {
            new()
            {
                Title = $"Put the {tool.Name} on {to.Name}",
                Detail = $"{folder} → {target}",
                Reason = $"{tool.What} {Fill(tool.Steps, target, tool.Variable)} The old file is backed up and Undo puts it back.{warning}",
                Badge = Badge.Check,
                CurrentState = state,
                RequiresClosedApps = tool.CloseFirst.Count > 0 ? tool.CloseFirst : null,
                Details = new Dictionary<string, string> { ["Setting"] = $"{cfg.Key} in {file}", ["New folder"] = target, ["Source"] = tool.Source },
                Actions = new[] { edit },
            },
        };
        if (tool.OldCopyRebuildable)
        {
            var quarantine = Plans.Quarantine(folder, totals, tool.Name);
            var q = c.Safety.Check(quarantine);
            rows.Add(new ScanItem
            {
                Title = $"Then clear the old {tool.Name} from {c.Drives.DriveOf(folder)?.Name ?? "this drive"}",
                Detail = folder,
                Reason = q.Allowed
                    ? $"Tick this together with the row above. The old copy goes to quarantine, and {tool.Name.Split(' ')[0]} downloads anything it needs into the new folder."
                    : q.Reason ?? "PC Tidy's safety rules don't allow clearing this folder.",
                Badge = Badge.Check,
                SizeBytes = q.Allowed ? totals.Bytes : 0,
                FileCount = (int)Math.Min(int.MaxValue, totals.Files),
                Actions = new[] { quarantine },
                DisabledReason = q.Allowed ? null : q.Reason ?? "PC Tidy's safety rules don't allow clearing this folder.",
            });
        }
        return rows;
    }

    /// <summary>One row per WSL distribution: the stop-WSL step PC Tidy can do, with the move command to run yourself.</summary>
    private static IEnumerable<ScanItem> Wsl(DriveMoveContext c, AppCacheTool tool)
    {
        foreach (var distro in c.Pc.WslDistros())
        {
            var disk = Path.Combine(distro.BasePath, "ext4.vhdx");
            var bytes = FolderMeasure.FileSize(disk);
            if (bytes < c.Thresholds.MinCacheBytes) continue;
            var from = c.Drives.DriveOf(disk);
            var to = c.Drives.TargetsFor(from, bytes, c.Thresholds).FirstOrDefault();
            var state = $"{Sizes.Format(bytes)} on {from?.Name ?? "this PC"}";
            if (to is null)
            {
                yield return InfoRow(tool, disk, state, "There's no other drive with room for this Linux disk plus 10% to spare.", title: $"WSL: {distro.Name}");
                continue;
            }
            var target = Path.Combine(to.Root, DriveMoveIds.MovedFolderName, "WSL", distro.Name);
            var steps = tool.Steps.Replace("{distro}", distro.Name, StringComparison.Ordinal).Replace("{target}", target, StringComparison.Ordinal);
            yield return new ScanItem
            {
                Title = $"WSL: move {distro.Name} to {to.Name}",
                Detail = disk,
                Reason = $"{tool.What} {steps} {tool.Warning} Ticking this stops WSL for you; the move itself is WSL's own command.",
                Badge = Badge.Check,
                CurrentState = state,
                Details = new Dictionary<string, string>
                {
                    ["Command"] = $"wsl --manage {distro.Name} --move \"{target}\"",
                    ["Source"] = tool.Source,
                },
                Actions = new[] { Plans.StopWsl() },
            };
        }
    }

    private static ScanItem InfoRow(AppCacheTool tool, string folder, string state, string reason, string? target = null, string? title = null) => new()
    {
        Title = title ?? tool.Name,
        Detail = folder,
        Reason = reason,
        Badge = Badge.Check,
        CurrentState = state,
        RequiresClosedApps = tool.CloseFirst.Count > 0 ? tool.CloseFirst : null,
        Details = target is null
            ? new Dictionary<string, string> { ["Source"] = tool.Source }
            : new Dictionary<string, string> { ["Suggested folder"] = target, ["Source"] = tool.Source },
    };

    private static string Fill(string text, string target, string? variable)
        => text.Replace("{target}", target, StringComparison.Ordinal).Replace("{variable}", variable ?? "", StringComparison.Ordinal);

    private static (string Path, string Text, DateTime LastWriteUtc)? ReadConfig(DriveMoveContext c, ConfigFileTarget cfg)
    {
        var path = Resolve(c.Locations, cfg.Base, cfg.RelativePath);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1 << 20 || PcTidy.Core.CloudFile.IsPlaceholder(info.Attributes)) return null;
            return (path, File.ReadAllText(path), info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string Resolve(IKnownLocations k, CacheBase b, string relative) => Path.Combine(b switch
    {
        CacheBase.LocalAppData => k.LocalAppData,
        CacheBase.RoamingAppData => k.RoamingAppData,
        CacheBase.ProgramData => k.ProgramData,
        _ => k.UserProfile,
    }, relative);
}
