using PcTidy.Catalog.PcCare;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Analysis;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>Leftovers tab: saved Wi-Fi networks, profiles of deleted accounts, optional Windows features. All unticked.</summary>
public static partial class PcCareResultBuilder
{
    private static void AddLeftovers(PcCareSnapshot s, PcCareBuildContext c, List<ScanGroup> groups, Dictionary<string, string> notes)
    {
        var note = new List<string>();

        if (s.Wlan is { Available: true } wlan)
        {
            var rows = wlan.Profiles
                .OrderBy(p => p.IsConnected).ThenBy(p => p.LastConnectedUtc ?? DateTime.MinValue)
                .Select(p =>
                {
                    var used = p.LastConnectedUtc is { } t ? "Last used " + Ago(t, c.NowUtc) : "Windows doesn't say when it was last used";
                    var detail = used + (wlan.Profiles.Select(x => x.InterfaceGuid).Distinct().Count() > 1 ? $" · {p.InterfaceName}" : "");
                    var e = LeftoverRules.ForWifi(p);
                    return e.Offered
                        ? Pick(p.Name, "Windows forgets this network. A backup is kept; to join it again you'll need its password.", Badge.Check,
                            new[] { PcCareActions.DeleteWifi(p.Name, p.InterfaceGuid) }, detail: detail,
                            state: LeftoverRules.IsOld(p, c.NowUtc) ? "Old" : null)
                        : Disabled(p.Name, "The network you're on now.", e.DisabledReason!, detail: detail, state: "Connected");
                }).ToList();
            groups.Add(Group(T.Leftovers, "wifi", "Saved Wi-Fi networks", "Networks Windows remembers. Old ones from hotels, cafés or past homes can go.", Badge.Check, rows));
        }
        else if (s.Wlan is not null)
            note.Add("No Wi-Fi on this PC.");

        if (s.Profiles is { } profiles)
        {
            var rows = profiles.Profiles.Where(p => LeftoverRules.IsWorthShowing(p, profiles.CurrentSid)).Select(p =>
            {
                var folder = Path.GetFileName(p.LocalPath.TrimEnd('\\'));
                var e = LeftoverRules.ForUserProfile(p, profiles.CurrentSid);
                var detail = p.LocalPath + (p.LastUseUtc is { } t ? " · last used " + Date(t) : "");
                return e.Offered
                    ? Pick(folder, "The account was removed, but its folder and settings are still here. Removing deletes everything in this folder for good.",
                        Badge.Permanent, new[] { PcCareActions.DeleteUserProfile(p.Sid, folder) }, detail: detail)
                    : Disabled(folder, "A profile whose account PC Tidy can't find.", e.DisabledReason!, Badge.Permanent, detail: detail);
            }).ToList();
            groups.Add(Group(T.Leftovers, "profiles", "Leftover user profiles",
                "Profiles of accounts that no longer exist on this PC. Removed the way Windows does it (never yours or a signed-in one).", Badge.Permanent, rows));
            if (rows.Count == 0) note.Add("No leftover user profiles.");
        }

        if (s.Features is { } f)
        {
            var enabled = f.Features.Where(x => x.Enabled).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var caps = f.InstalledCapabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rows = OptionalFeatureCatalog.All
                .Where(p => p.Kind == OptionalPartKind.Feature ? enabled.Contains(p.Name) : caps.Contains(p.Name))
                .OrderByDescending(p => p.SecurityBenefit).ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => Pick(p.Title,
                    (p.SecurityBenefit ? "Safer off. " : "") + p.Why + " " + p.ReAdd,
                    Badge.Check, new[] { PcCareActions.RemoveOptional(p.Name, p.Kind == OptionalPartKind.Capability, p.Title) },
                    detail: p.Kind == OptionalPartKind.Feature ? "Windows feature · restart may be needed" : "Windows capability · needs a download to add back",
                    restart: p.Kind == OptionalPartKind.Feature,
                    details: new Dictionary<string, string> { ["Windows name"] = p.Name }))
                .ToList();
            foreach (var lang in f.UiLanguages.Skip(1))
                rows.Add(Pick($"Display language {lang}",
                    "An extra Windows display language. Remove it in Settings > Time & language > Language & region if you don't use it.",
                    Badge.Check, new[] { PcCareActions.Open(PcCareLinks.Languages, "Open Settings > Language & region") }));
            groups.Add(Group(T.Leftovers, "features", "Optional Windows features",
                "Parts of Windows many home PCs don't use. Situational: only remove what you know you don't need.", Badge.Check, rows));
        }

        if (note.Count > 0) notes[T.Leftovers] = string.Join(" ", note);
    }

    private static void AddShredder(PcCareBuildContext c, List<ScanGroup> groups, Dictionary<string, string> notes)
    {
        notes[T.Shredder] = ShredderPlanner.TabNote;
        var rows = c.Drives.Select(d => d.Media switch
        {
            DriveMedia.Hdd => Pick($"Wipe free space on {d.Root.TrimEnd('\\')}",
                "Overwrites the drive's free space so files you already deleted can't be recovered. Your files aren't touched. Takes hours on a big drive.",
                Badge.Permanent, new[] { PcCareActions.WipeFreeSpace(d.Root) }, detail: $"{d.Label} · hard drive · {Core.Sizes.Format(d.FreeBytes)} free · Windows' cipher tool"),
            DriveMedia.Ssd => Disabled($"Free space on {d.Root.TrimEnd('\\')} (SSD)",
                ShredderPlanner.SsdExplanation,
                "Not offered on SSDs: it only wears the drive and can't reach every copy. BitLocker is the honest protection.", Badge.Permanent,
                detail: $"{d.Label} · SSD"),
            _ => Disabled($"Free space on {d.Root.TrimEnd('\\')}",
                "PC Tidy couldn't tell whether this is an SSD or a hard drive.",
                "Not offered: on an SSD it would only wear the drive.", Badge.Permanent, detail: d.Label),
        }).ToList();
        groups.Add(Group(T.Shredder, "freespace", "Wipe free space",
            "Overwrites space where deleted files used to be. Only offered on hard drives.", Badge.Permanent, rows));
    }
}
