using PcTidy.Core.Model;

namespace PcTidy.Features.Apps.Leftovers;

/// <summary>Turns leftover findings into the standard scan rows (checkbox, badge, reason, plan item).</summary>
public static class LeftoverItems
{
    public static string Label(LeftoverConfidence c) => c switch
    {
        LeftoverConfidence.VeryGood => "Very good",
        LeftoverConfidence.Good => "Good",
        LeftoverConfidence.Questionable => "Questionable",
        _ => "Bad",
    };

    public static ScanItem ToScanItem(LeftoverFinding f, string appName)
    {
        var reason = f.Kind == LeftoverKind.Folder
            ? $"{Label(f.Confidence)} match: {f.Why} Moved to quarantine, so you can put it back."
            : $"{Label(f.Confidence)} match: {f.Why}";
        if (f.MayHoldYourFiles) reason += " It may hold your own files (saves, documents): check it first.";
        var details = new Dictionary<string, string>
        {
            ["Confidence"] = Label(f.Confidence),
            ["Why it matched"] = f.Why,
            ["Left by"] = appName,
        };
        if (f.Kind == LeftoverKind.Folder) details["Files"] = f.FileCount.ToString("N0");
        return new ScanItem
        {
            Id = "leftover:" + f.Path,
            Title = f.Kind == LeftoverKind.Folder ? Path.GetFileName(f.Path) : f.Path,
            Detail = f.Path,
            Reason = reason,
            // Quarantine is restorable, so a very good match is Safe; everything else is the user's call.
            Badge = f.PreSelected ? Badge.Safe : Badge.Check,
            DefaultSelected = f.PreSelected,
            SizeBytes = f.SizeBytes,
            FileCount = f.FileCount,
            NeedsAdmin = f.Action?.NeedsAdmin ?? false,
            OwnerApp = appName,
            LastModifiedUtc = f.LastWriteUtc,
            CurrentState = Label(f.Confidence),
            Details = details,
            Actions = f.Action is null ? Array.Empty<PlanItem>() : new[] { f.Action },
            DisabledReason = f.NotRemovableReason,
        };
    }

    /// <summary>"Leftover folders" and "Leftover registry keys" groups (empty groups left out).</summary>
    public static IReadOnlyList<ScanGroup> ToGroups(IReadOnlyList<LeftoverFinding> findings, string appName)
    {
        var groups = new List<ScanGroup>();
        var folders = findings.Where(f => f.Kind == LeftoverKind.Folder).Select(f => ToScanItem(f, appName)).ToList();
        if (folders.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "apps.leftover-folders",
                Title = "Leftover folders",
                Description = $"Folders {appName} left behind, matched by exact name only. They go to quarantine, so you can put them back. Only very good matches are ticked.",
                Badge = Badge.Check,
                Items = folders,
            });
        var keys = findings.Where(f => f.Kind == LeftoverKind.RegistryKey).Select(f => ToScanItem(f, appName)).ToList();
        if (keys.Count > 0)
            groups.Add(new ScanGroup
            {
                Id = "apps.leftover-keys",
                Title = "Leftover registry keys",
                Description = keys.All(k => k.DisabledReason is not null)
                    ? $"Settings keys with {appName}'s exact name under its publisher. Shown so you know; PC Tidy doesn't remove them yet."
                    : $"Settings keys with {appName}'s exact name under its publisher. A .reg backup is saved before any key is removed.",
                Badge = Badge.Check,
                Items = keys,
            });
        return groups;
    }
}
