using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Features.Boost;
using PcTidy.Features.Organize;
using PcTidy.Features.Schedule;

namespace PcTidy.App.Portability;

/// <summary>
/// One difference between this PC's settings and an imported file, with its own tick (each section's settings are
/// compared setting by setting, see <see cref="SectionDiff"/>). Changes that would loosen PC Tidy's protection (fewer
/// protected folders, more OneDrive folders allowed, Expert mode, no purge question, any address PC Tidy would contact), changes
/// that let it act on its own (watch mode, automatic rules and quarantine, Boost closing apps) and paths that aren't on
/// this PC start unticked and are flagged; things that change Windows are listed, never applied.
/// </summary>
public sealed partial class ImportChange : ObservableObject
{
    public required string Area { get; init; }
    public required string Name { get; init; }
    public required string Before { get; init; }
    public required string After { get; init; }
    /// <summary>Applying it makes PC Tidy less protective; the user must tick it on purpose.</summary>
    public bool Loosens { get; init; }
    /// <summary>Paths in this change that point to a drive this PC doesn't have.</summary>
    public IReadOnlyList<string> OffPcPaths { get; init; } = Array.Empty<string>();
    /// <summary>Shown but never applied (it would change Windows: right-click menu, start-up, schedules).</summary>
    public bool InfoOnly { get; init; }
    /// <summary>
    /// Applying it lets PC Tidy act on its own, without a confirm each time (watch mode, an automatic rule or automatic
    /// quarantine, Boost closing apps). Starts unticked; the user must tick it on purpose.
    /// </summary>
    public bool Automatic { get; init; }
    public string? Note { get; init; }
    internal Action<AppSettings>? ApplyTo { get; init; }
    /// <summary>The learned-data row (applied by the store, not to the settings object).</summary>
    public bool IsLearning { get; init; }

    [ObservableProperty] private bool _include;

    public bool CanInclude => !InfoOnly;
    public string Flag => InfoOnly ? "Not imported" : OffPcPaths.Count > 0 ? "Not on this PC" : Automatic ? "Runs on its own" : Loosens ? "Less protection" : "";
}

/// <summary>Works out what an import would change (pure; tested), and applies the ticked rows to a settings object.</summary>
public static class ImportPreview
{
    public static IReadOnlyList<ImportChange> Compare(AppSettings current, LoadedPackage incoming, IReadOnlyList<string> thisPcDrives, bool learningExistsHere)
    {
        var s = incoming.Settings;
        var list = new List<ImportChange>();
        bool OnPc(string p) => OnThisPc(p, thisPcDrives);

        // ---- simple settings ----
        Scalar(list, "Safety", "Days to keep quarantined items", current.QuarantineDays, s.QuarantineDays, x => x.QuarantineDays = Math.Clamp(s.QuarantineDays, 1, 90), loosens: s.QuarantineDays < current.QuarantineDays);
        Scalar(list, "Safety", "Ask before emptying old quarantine", current.AskBeforePurge, s.AskBeforePurge, x => x.AskBeforePurge = s.AskBeforePurge, loosens: !s.AskBeforePurge);
        Scalar(list, "Safety", "Suggest Simulation mode", current.SuggestSimulationOnFirstUse, s.SuggestSimulationOnFirstUse, x => x.SuggestSimulationOnFirstUse = s.SuggestSimulationOnFirstUse);
        Scalar(list, "Safety", "Expert mode", current.ExpertMode, s.ExpertMode, x => x.ExpertMode = s.ExpertMode, loosens: s.ExpertMode);
        Scalar(list, "Scanning", "Default scan depth", current.DefaultDepth, s.DefaultDepth, x => x.DefaultDepth = s.DefaultDepth);
        Scalar(list, "Scanning", "Faster scans with admin", current.AllowAdminFastScan, s.AllowAdminFastScan, x => x.AllowAdminFastScan = s.AllowAdminFastScan);
        Scalar(list, "Appearance", "Theme", current.Theme, s.Theme, x => x.Theme = s.Theme);
        Scalar(list, "Your data", "Learn from my choices", current.LearningEnabled, s.LearningEnabled, x => x.LearningEnabled = s.LearningEnabled);
        if (!string.Equals(current.ObsidianExportFolder, s.ObsidianExportFolder, StringComparison.OrdinalIgnoreCase))
        {
            var off = s.ObsidianExportFolder is { } f && !OnPc(f) ? new[] { f } : Array.Empty<string>();
            list.Add(new ImportChange
            {
                Area = "Your data", Name = "Copy run reports to a notes folder", Before = current.ObsidianExportFolder ?? "Not set", After = s.ObsidianExportFolder ?? "Not set",
                OffPcPaths = off, Include = off.Length == 0, ApplyTo = x => x.ObsidianExportFolder = s.ObsidianExportFolder,
            });
        }

        // ---- folder lists, one row per folder ----
        Folders(list, "Extra protected folder", current.ExtraProtectedFolders, s.ExtraProtectedFolders, OnPc, x => x.ExtraProtectedFolders, addLoosens: false);
        Folders(list, "OneDrive folder PC Tidy may organize", current.AllowedAskFirstFolders, s.AllowedAskFirstFolders, OnPc, x => x.AllowedAskFirstFolders, addLoosens: true);
        Folders(list, "Folder to skip when scanning", current.ScanExclusions, s.ScanExclusions, OnPc, x => x.ScanExclusions, addLoosens: null);

        // ---- each section's settings and rules: one row per changed setting (never one opaque "whole section" row) ----
        foreach (var (key, value) in s.Features.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Length == 0) continue;
            var incomingBlob = Normalized(key, value, current);
            current.Features.TryGetValue(key, out var stored);
            JsonElement? mine = stored.ValueKind != JsonValueKind.Undefined ? stored : SectionDefaults(key);
            // The shell section is compared as ShellPrefs on both sides, so a setting that no longer exists (the old
            // custom update address, UpdateFeedUrl) is neither shown nor imported: nothing can redirect the updater.
            if (key == ShellPrefs.Key) mine = JsonSerializer.SerializeToElement(SafeShell(current), AppSettings.Json);
            if (mine is { } m && JsonText(m) == JsonText(incomingBlob)) continue;
            foreach (var row in SectionDiff.Rows(key, mine, incomingBlob))
            {
                var off = row.Paths.Where(p => !OnPc(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var section = key;
                list.Add(new ImportChange
                {
                    Area = SectionName(key), Name = row.Name, Before = row.Before, After = row.After,
                    Automatic = row.Automatic, Loosens = row.Loosens, OffPcPaths = off, Note = row.Note,
                    Include = off.Count == 0 && !row.Automatic && !row.Loosens,
                    ApplyTo = x => ApplyToSection(x, section, row.Apply),
                });
            }
            if (key == ScheduleSettings.FeatureKey && JsonText(value) != JsonText(incomingBlob))
                list.Add(new ImportChange { Area = SectionName(key), Name = "Scheduled scans switched on", Before = "This PC's choice", After = "On in the file", InfoOnly = true, Note = "Schedules aren't switched on by an import; turn them on in Schedule & reports." });
            if (key == OrganizeSettings.FeatureKey && WatchOn(value) && !OrganizeSettings.Load(current).Watch.Enabled)
                list.Add(new ImportChange { Area = SectionName(key), Name = "Watch mode switched on", Before = "Off", After = "On in the file", InfoOnly = true, Note = "Watch mode isn't switched on by an import; turn it on in Organize yourself." });
        }

        // ---- things that change Windows: listed, never applied ----
        if (s.TrayEnabled != current.TrayEnabled || s.StartWithWindows != current.StartWithWindows)
            list.Add(new ImportChange { Area = "Background", Name = "Tray icon and start with Windows", Before = OnOff(current.TrayEnabled, current.StartWithWindows), After = OnOff(s.TrayEnabled, s.StartWithWindows), InfoOnly = true, Note = "Turn these on in Settings yourself if you want them here." });
        var (myShell, theirShell) = (ShellPrefs.Get(current), SafeShell(s));
        if (myShell.WhatIsMenu != theirShell.WhatIsMenu || myShell.TidyFolderMenu != theirShell.TidyFolderMenu)
            list.Add(new ImportChange { Area = "Background", Name = "Right-click menu entries", Before = Menus(myShell), After = Menus(theirShell), InfoOnly = true, Note = "They change Explorer's menu, so turn them on in Settings yourself." });

        // ---- learned data ----
        if (incoming.LearningDb is { } db)
            list.Add(new ImportChange
            {
                Area = "Learned data", Name = "What PC Tidy learned", Before = learningExistsHere ? "This PC's own" : "Nothing yet", After = $"From the file ({Sizes.Format(db.LongLength)})",
                Include = !learningExistsHere, IsLearning = true,
                Note = learningExistsHere ? "Replaces what this PC learned. A copy of it is kept in the backups folder." : null,
            });
        return list;
    }

    /// <summary>Applies the ticked rows (except learned data, which the store handles) to <paramref name="target"/>.</summary>
    public static int Apply(AppSettings target, IEnumerable<ImportChange> changes)
    {
        int n = 0;
        foreach (var c in changes.Where(c => c.Include && c.CanInclude && c.ApplyTo is not null))
        {
            c.ApplyTo!(target);
            n++;
        }
        return n;
    }

    /// <summary>True when the path is on one of this PC's fixed drives (a network path never is).</summary>
    public static bool OnThisPc(string path, IReadOnlyList<string> drives)
    {
        try { return Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && drives.Any(d => PathUtil.IsUnder(path, d)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // ---------------- helpers ----------------

    private static void Scalar<T>(List<ImportChange> list, string area, string name, T before, T after, Action<AppSettings> apply, bool loosens = false)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)) return;
        list.Add(new ImportChange { Area = area, Name = name, Before = Text(before), After = Text(after), Loosens = loosens, Include = !loosens, ApplyTo = apply });
    }

    private static void Folders(List<ImportChange> list, string name, List<string> mine, List<string> theirs, Func<string, bool> onPc,
        Func<AppSettings, List<string>> target, bool? addLoosens)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        foreach (var add in theirs.Where(t => !mine.Contains(t, cmp)).Distinct(cmp))
        {
            bool off = !onPc(add), loosens = addLoosens == true;
            list.Add(new ImportChange
            {
                Area = "Folders", Name = name + " added", Before = "—", After = add, Loosens = loosens,
                OffPcPaths = off ? new[] { add } : Array.Empty<string>(), Include = !off && !loosens,
                ApplyTo = x => { var l = target(x); if (!l.Contains(add, cmp)) l.Add(add); },
            });
        }
        foreach (var gone in mine.Where(m => !theirs.Contains(m, cmp)))
        {
            bool loosens = addLoosens == false; // removing a protected folder makes PC Tidy less careful
            list.Add(new ImportChange
            {
                Area = "Folders", Name = name + " removed", Before = gone, After = "—", Loosens = loosens, Include = !loosens,
                ApplyTo = x => target(x).RemoveAll(f => cmp.Equals(f, gone)),
            });
        }
    }

    /// <summary>
    /// Section blobs whose parts describe the state of THIS PC keep this PC's values: the shell's right-click menu
    /// flags, and whether the schedules are switched on (they need a task in Windows' Task Scheduler).
    /// </summary>
    private static JsonElement Normalized(string key, JsonElement incoming, AppSettings current)
    {
        try
        {
            if (key == ShellPrefs.Key && incoming.Deserialize<ShellPrefs>(AppSettings.Json) is { } shell)
            {
                var mine = ShellPrefs.Get(current);
                return JsonSerializer.SerializeToElement(shell with { WhatIsMenu = mine.WhatIsMenu, TidyFolderMenu = mine.TidyFolderMenu }, AppSettings.Json);
            }
            if (key == OrganizeSettings.FeatureKey && System.Text.Json.Nodes.JsonNode.Parse(incoming.GetRawText()) is System.Text.Json.Nodes.JsonObject org)
            {
                // Watch mode stays this PC's choice (it runs rules with no confirm), and every rule gets a unique id so the
                // rules are compared one by one (IMP-1): a missing, empty or repeated id gets a fresh one.
                if (org["Watch"] is System.Text.Json.Nodes.JsonObject watch) watch["Enabled"] = OrganizeSettings.Load(current).Watch.Enabled;
                if (org["Rules"] is System.Text.Json.Nodes.JsonArray rules)
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var rule in rules.OfType<System.Text.Json.Nodes.JsonObject>())
                    {
                        var id = rule["Id"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                        if (string.IsNullOrEmpty(id) || !seen.Add(id)) rule["Id"] = Guid.NewGuid().ToString("N");
                    }
                }
                return JsonSerializer.SerializeToElement(org, AppSettings.Json);
            }
            if (key == ScheduleSettings.FeatureKey && incoming.Deserialize<ScheduleSettings>(AppSettings.Json) is { } sched)
            {
                var mine = ScheduleSettings.Load(current);
                sched.Weekly.Enabled = mine.Weekly.Enabled;
                sched.Monthly.Enabled = mine.Monthly.Enabled;
                return JsonSerializer.SerializeToElement(sched, AppSettings.Json);
            }
        }
        catch (JsonException) { /* an odd blob is compared as it is */ }
        return incoming;
    }

    private static bool WatchOn(JsonElement organize)
        => organize.ValueKind == JsonValueKind.Object && organize.TryGetProperty("Watch", out var w) && w.ValueKind == JsonValueKind.Object
           && w.TryGetProperty("Enabled", out var e) && e.ValueKind == JsonValueKind.True;

    private static ShellPrefs SafeShell(AppSettings s)
    {
        try { return ShellPrefs.Get(s); } catch (JsonException) { return new ShellPrefs(); }
    }

    /// <summary>
    /// What a section's settings are on a PC that never changed them, so an import of a section this PC hasn't stored yet
    /// still shows only real differences (and applies them on top of the same defaults). Null = an empty object.
    /// </summary>
    private static JsonElement? SectionDefaults(string key) => key switch
    {
        OrganizeSettings.FeatureKey => JsonSerializer.SerializeToElement(new OrganizeSettings(), AppSettings.Json),
        BoostSettings.FeatureKey => JsonSerializer.SerializeToElement(new BoostSettings(), AppSettings.Json),
        ScheduleSettings.FeatureKey => JsonSerializer.SerializeToElement(new ScheduleSettings(), AppSettings.Json),
        ShellPrefs.Key => JsonSerializer.SerializeToElement(new ShellPrefs(), AppSettings.Json),
        _ => null,
    };

    /// <summary>Applies one row to the section's settings as they are in <paramref name="x"/> now (earlier ticked rows included).</summary>
    private static void ApplyToSection(AppSettings x, string key, Action<System.Text.Json.Nodes.JsonNode> apply)
    {
        System.Text.Json.Nodes.JsonNode node = x.Features.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Object
            ? System.Text.Json.Nodes.JsonNode.Parse(e.GetRawText())!
            : SectionDefaults(key) is { } d ? System.Text.Json.Nodes.JsonNode.Parse(d.GetRawText())! : new System.Text.Json.Nodes.JsonObject();
        apply(node);
        x.Features[key] = JsonSerializer.SerializeToElement(node, AppSettings.Json);
    }

    private static string JsonText(JsonElement e) => JsonSerializer.Serialize(e);

    private static string SectionName(string key) => key switch
    {
        "organize" => "Organize rules and options",
        "schedule" => "Schedule & reports",
        "shell" => "Alerts, quiet hours and updates",
        "assistant" => "Ask PC Tidy",
        _ => char.ToUpperInvariant(key[0]) + key[1..] + " settings",
    };

    private static string Text<T>(T v) => v switch
    {
        bool b => b ? "On" : "Off",
        null => "Not set",
        _ => v.ToString() ?? "",
    };

    private static string OnOff(bool tray, bool start) => $"Tray {(tray ? "on" : "off")}, start with Windows {(start ? "on" : "off")}";
    private static string Menus(ShellPrefs p) => $"What is this? {(p.WhatIsMenu ? "on" : "off")}, Tidy this folder {(p.TidyFolderMenu ? "on" : "off")}";
}
