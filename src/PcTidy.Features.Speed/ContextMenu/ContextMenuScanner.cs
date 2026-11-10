using System.Diagnostics;
using PcTidy.Features.Speed.Common;

namespace PcTidy.Features.Speed.ContextMenu;

/// <summary>
/// The right-click menu cleaner: what apps added to Explorer's menu, who added it, and entries whose file is gone.
/// Hiding uses Windows' own switches (the Blocked list for extensions, LegacyDisable for entries); nothing is
/// deleted and everything can be shown again. Microsoft's entries are hidden from the list by default. Read-only.
/// </summary>
public sealed class ContextMenuScanner : IFeatureScanner
{
    public const string ScannerId = "speed.contextmenu";
    public const string BrokenGroup = "contextmenu-broken";
    public const string AppsGroup = "contextmenu-apps";
    public const string MicrosoftGroup = "contextmenu-microsoft";
    public const string HiddenGroup = "contextmenu-hidden";

    private readonly Func<ScanContext, SpeedEnvironment> _environment;

    public ContextMenuScanner(Func<ScanContext, SpeedEnvironment>? environment = null) => _environment = environment ?? SpeedEnvironment.For;

    public string FeatureId => ScannerId;
    public string Title => "Right-click menu";
    public Release Release => Release.V2;
    public bool IncludedInOptimizeAll => false;

    public Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var env = _environment(ctx);
            var hideMicrosoft = SpeedOptions.HidesMicrosoft(ctx);
            progress?.Report(new ScanProgress("Reading the right-click menu…", 0.2));
            var entries = new ContextMenuReader(env, ctx.Locations).Read(ct);

            List<ScanItem> broken = new(), apps = new(), microsoft = new(), hidden = new();
            int hiddenMicrosoft = 0, n = 0;
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ScanProgress("Checking " + e.Name, 0.2 + 0.7 * n++ / Math.Max(1, entries.Count)));
                var facts = env.Files.Get(e.FilePath);
                var isMicrosoft = facts.IsMicrosoft;
                var isWindows = e.FilePath is { } f && PathUtil.IsUnder(f, ctx.Locations.WindowsDir) && isMicrosoft;
                var missing = e.FilePath is not null && facts.Signature == SignatureState.FileMissing;

                if (isMicrosoft && hideMicrosoft) { hiddenMicrosoft++; continue; }
                var item = Item(e, facts, isWindows, missing);
                if (e.IsDisabled) hidden.Add(item);
                else if (missing) broken.Add(item);
                else if (isMicrosoft) microsoft.Add(item);
                else apps.Add(item);
            }

            var notes = new List<string>();
            if (hiddenMicrosoft > 0)
                notes.Add($"{SpeedOptions.Plural(hiddenMicrosoft, "Microsoft entry is", "Microsoft entries are")} hidden. Switch off \"Hide Microsoft\" to see {(hiddenMicrosoft == 1 ? "it" : "them")}.");
            notes.Add("On Windows 11, most of these appear under \"Show more options\". Changes show the next time you open a menu (sometimes after restarting Explorer).");

            progress?.Report(new ScanProgress("Done", 1));
            return new ScanResult
            {
                FeatureId = ScannerId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Notes = notes,
                Groups = new[]
                {
                    Group(BrokenGroup, "Broken: the file is gone", "Left behind by apps you removed. They can slow the menu down; hiding them is safe.", broken),
                    Group(AppsGroup, "Added by apps", "Menu entries other apps added. Hide the ones you never use.", apps),
                    Group(MicrosoftGroup, "From Microsoft", "Entries from Microsoft apps. Windows' own core entries can't be hidden here.", microsoft),
                    Group(HiddenGroup, "Already hidden", "Switched off with Windows' own setting. Tick one to show it again.", hidden),
                }.Where(g => g.Items.Count > 0).ToList(),
            };
        }, ct);

    private static ScanGroup Group(string id, string title, string description, List<ScanItem> items) => new()
    {
        Id = id, Title = title, Description = description, Badge = Badge.Check,
        Items = items.OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
    };

    internal static ScanItem Item(ShellEntry e, FileFacts facts, bool isWindows, bool missing)
    {
        var enable = e.IsDisabled;
        var actions = e.Kind == ShellEntryKind.Extension && e.Clsid is { } clsid
            ? new[] { SpeedPlanItems.ShellExtension(e.Name, clsid, enable) }
            : e.VerbKeys.Select(k => SpeedPlanItems.ShellVerb(e.Name, e.Hive, k, enable)).ToArray();
        // Windows' own entries (some ship switched off, like "find") are left exactly as Windows set them.
        string? disabled = isWindows
            ? "Part of Windows itself. Changing it can break Explorer's menus, so PC Tidy leaves it as Windows set it."
            : e.FilePath is null
                ? "PC Tidy can't tell which program this entry runs, so it leaves it alone."
                : null;

        var details = new Dictionary<string, string>
        {
            ["Kind"] = e.Kind == ShellEntryKind.Extension ? "Shell extension" : "Menu entry",
            ["Shows on"] = string.Join(", ", e.ShowsOn),
            ["Added for"] = e.Scope,
            ["Publisher"] = facts.Publisher ?? "Unknown",
            ["Signature"] = facts.SignatureText,
        };
        if (e.Clsid is { } id) details["CLSID"] = id.ToString("B").ToUpperInvariant();
        if (e.FilePath is { } file) details["File"] = file;
        details["Registry key"] = e.Keys[0];
        if (e.Keys.Count > 1) details["Also at"] = string.Join("\n", e.Keys.Skip(1));

        return new ScanItem
        {
            Id = $"speed.contextmenu:{e.Kind}:{e.Hive}:{(object?)e.Clsid ?? e.VerbKeys.FirstOrDefault()}",
            Title = e.Name,
            Detail = e.FilePath ?? "No file listed",
            Reason = enable
                ? "Hidden from the right-click menu. Tick it to show it again."
                : missing
                    ? "The file behind this entry is gone, so it does nothing except slow the menu. Hiding it is safe and can be undone."
                    : $"Shows on {string.Join(", ", e.ShowsOn)}. Hiding it removes it from the menu without uninstalling anything; you can show it again any time.",
            Badge = Badge.Check,
            NeedsAdmin = disabled is null && actions.Any(a => a.NeedsAdmin),
            OwnerApp = facts.Publisher,
            CurrentState = enable ? "Hidden" : missing ? "File missing" : "Shown",
            Details = details,
            Actions = disabled is null ? actions : Array.Empty<PlanItem>(),
            DisabledReason = disabled,
        };
    }
}
