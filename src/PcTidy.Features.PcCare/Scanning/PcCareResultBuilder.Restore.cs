using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Planning;
using T = PcTidy.Features.PcCare.Scanning.PcCareTabs;

namespace PcTidy.Features.PcCare.Scanning;

/// <summary>Restore point manager: make one, delete old ones (never the newest), restore point space (never below 5%).</summary>
public static partial class PcCareResultBuilder
{
    public const string ManualRestorePointName = "PC Tidy: made by hand";

    private static void AddRestore(PcCareSnapshot s, PcCareBuildContext c, List<ScanGroup> groups, Dictionary<string, string> notes)
    {
        var r = s.Restore;
        var drive = c.SystemDrive.Length >= 2 ? c.SystemDrive[..2] + "\\" : c.SystemDrive;
        var note = new List<string>();

        if (r?.ProtectionOn == false)
        {
            note.Add($"System Protection is off for {drive.TrimEnd('\\')}, so Windows can't make restore points. Turn it on in Control Panel > System > System protection > Configure.");
        }
        else
        {
            groups.Add(Group(T.Restore, "create", "Make a restore point",
                "A snapshot of Windows' system files and settings you can go back to. Your documents aren't included.", Badge.Check, new[]
                {
                    Pick("Make a restore point now", "Takes about a minute. Needs admin.", Badge.Check,
                        new[] { PcCareActions.CreateRestorePoint(ManualRestorePointName) }),
                }));
        }

        if (r is null || r.Access != ReadAccess.Ok)
        {
            note.Add(r?.Access == ReadAccess.NotAvailable
                ? "Windows didn't list restore points on this PC."
                : "Listing restore points needs admin. Run PC Tidy as admin to see and tidy them.");
            notes[T.Restore] = string.Join(" ", note);
            return;
        }

        var points = r.Points.OrderByDescending(p => p.CreatedUtc).ToList();
        var rows = points.Select((p, i) => i == 0
            ? Disabled(PointTitle(p), "Your newest restore point.", "The newest restore point always stays, so you can go back.", Badge.Safe, state: Date(p.CreatedUtc))
            : Pick(PointTitle(p), "Deletes this one restore point for good. Newer ones are kept.", Badge.Permanent,
                new[] { PcCareActions.DeleteRestorePoint(p.SequenceNumber, $"Delete the restore point \"{p.Description}\" ({Date(p.CreatedUtc)})") },
                state: Date(p.CreatedUtc),
                details: new Dictionary<string, string> { ["Number"] = p.SequenceNumber.ToString(CultureInfo.InvariantCulture), ["Made"] = Date(p.CreatedUtc) }))
            .ToList();
        groups.Add(Group(T.Restore, "points", "Restore points", "Restore points on this PC, newest first.", Badge.Permanent, rows));
        note.Add(points.Count == 0 ? "There are no restore points yet." : $"{Plural(points.Count, "restore point")}, newest on {Date(points[0].CreatedUtc)}.");

        if (r.Storage is { } st && st.VolumeBytes > 0)
        {
            var space = new List<ScanItem>
            {
                Info($"Restore points use {Sizes.Format(st.UsedBytes)} of up to {Sizes.Format(st.MaxBytes)}",
                    "When the space is full, Windows deletes the oldest restore points to make room.",
                    state: st.MaxPercent.ToString("0", CultureInfo.CurrentCulture) + "%"),
            };
            if (SuggestedPercent(st) is { } pct)
            {
                long newMax = st.VolumeBytes * pct / 100;
                bool shrinking = newMax < st.MaxBytes;
                bool losesPoints = shrinking && st.UsedBytes > newMax;
                space.Add(Pick(
                    shrinking ? $"Lower the limit to {pct}%" : $"Raise the limit to {pct}%",
                    shrinking
                        ? (losesPoints ? "Frees space on the drive. The oldest restore points are deleted to fit." : "Frees space on the drive; your current restore points fit.")
                        : "Gives Windows room for more restore points, so you can go back further.",
                    losesPoints ? Badge.Permanent : Badge.Check,
                    new[] { PcCareActions.ResizeRestoreSpace(pct, drive) },
                    state: $"{pct}% = {Sizes.Format(newMax)}"));
            }
            groups.Add(Group(T.Restore, "space", "Restore point space", $"How much of {drive.TrimEnd('\\')} Windows may use for restore points. Never set below 5%.", Badge.Check, space));
        }
        notes[T.Restore] = string.Join(" ", note);
    }

    /// <summary>Below 5% → 5%; under 8% → 10%; above 15% → 10%. Otherwise the limit is fine and nothing is offered.</summary>
    internal static int? SuggestedPercent(ShadowStorage st)
    {
        var pct = st.MaxPercent;
        if (pct < 4.5) return 5;
        if (pct < 8) return 10;
        if (pct > 15.5) return 10;
        return null;
    }

    private static string PointTitle(RestorePoint p) => string.IsNullOrWhiteSpace(p.Description) ? $"Restore point {p.SequenceNumber}" : p.Description;
}
