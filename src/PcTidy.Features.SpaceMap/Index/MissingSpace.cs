using PcTidy.Core;
using PcTidy.Features.SpaceMap.Actions;

namespace PcTidy.Features.SpaceMap.Index;

public enum MeasuredBy
{
    /// <summary>Size read from file metadata (no admin, nothing opened).</summary>
    Measured,
    /// <summary>An upper bound (e.g. hard links counted more than once).</summary>
    Estimated,
    /// <summary>Only an administrator can measure it.</summary>
    NeedsAdmin,
}

public sealed record MissingSpacePart(string Name, long? Bytes, MeasuredBy How, string Explanation);

/// <summary>Why a drive's used space is more than the files a normal account can see.</summary>
public sealed record MissingSpaceReport(long? DriveUsedBytes, long ScannedBytesOnDisk, long UnscannedBytes, IReadOnlyList<MissingSpacePart> Parts)
{
    public const string Hint = "For safe options, open Clean → Big hidden Windows files.";
}

/// <summary>
/// "Explain missing space": names what usually hides in the Unscanned / system block, using only metadata
/// a standard account can read (sizes of pagefile.sys / hiberfil.sys / swapfile.sys from the directory listing,
/// or from their attributes if the walk didn't list them). Restore points and NTFS metadata need admin.
/// </summary>
public static class MissingSpace
{
    private static readonly (string File, string Name, string Why)[] RootFiles =
    {
        ("hiberfil.sys", "Hibernation file", "Holds memory when the PC hibernates or uses Fast Startup. Windows sizes it; Clean can make it smaller."),
        ("pagefile.sys", "Page file", "Extra memory Windows uses when RAM is full. Leave it on; Windows manages its size."),
        ("swapfile.sys", "Swap file", "Used by Store apps when they're suspended. Small and managed by Windows."),
    };

    public static MissingSpaceReport Explain(SpaceIndex index, IPathFacts? facts = null)
    {
        facts ??= DiskPathFacts.Instance;
        var s = index.Summary;
        long unscanned = index.UnscannedNode >= 0 ? index.Size(index.UnscannedNode) : 0;
        var parts = new List<MissingSpacePart>();
        bool wholeDrive = s.DriveUsedBytes is not null;

        if (wholeDrive)
        {
            foreach (var (file, name, why) in RootFiles)
            {
                long? bytes = ChildNamed(index, SpaceIndex.RootNode, file) is int n and >= 0 ? index.Size(n)
                    : facts.File(Path.Combine(index.RootPath, file))?.Size;
                if (bytes is > 0) parts.Add(new MissingSpacePart(name, bytes, MeasuredBy.Measured, why + " Shown as a locked block."));
            }
            parts.Add(new MissingSpacePart("Restore points and shadow copies", null, MeasuredBy.NeedsAdmin,
                "Kept in System Volume Information. Only an admin can measure it; Clean shows it and can limit it safely."));
            parts.Add(new MissingSpacePart("File system bookkeeping", null, MeasuredBy.NeedsAdmin,
                "The master file table and other NTFS records. Never shown as files."));
        }

        int winsxs = FindPath(index, "Windows", "WinSxS");
        if (winsxs >= 0)
            parts.Add(new MissingSpacePart("Windows component store (WinSxS)", index.Size(winsxs), MeasuredBy.Estimated,
                "Many of its files are hard links shared with Windows\\System32, so its real size is smaller than shown. Clean measures it exactly with Windows' own tool."));

        if (s.Inaccessible > 0)
            parts.Add(new MissingSpacePart($"{s.Inaccessible:N0} folder{(s.Inaccessible == 1 ? "" : "s")} this account can't open", null, MeasuredBy.NeedsAdmin,
                "Their contents are counted in Unscanned / system."));

        parts.Add(new MissingSpacePart("Hard links", null, MeasuredBy.Estimated,
            "A file with several hard links is counted at each place it appears, so some folder totals are higher than the space they really use."));

        return new MissingSpaceReport(s.DriveUsedBytes, s.ScannedBytesOnDisk, unscanned, parts);
    }

    /// <summary>Plain text for a message box.</summary>
    public static string Format(MissingSpaceReport r)
    {
        var lines = new List<string>();
        if (r.DriveUsedBytes is long used)
            lines.Add($"Windows says {Sizes.Format(used)} is used. PC Tidy could measure {Sizes.Format(r.ScannedBytesOnDisk)} as files; {Sizes.Format(r.UnscannedBytes)} is in Unscanned / system.");
        lines.Add("");
        foreach (var p in r.Parts)
        {
            var size = p.How switch
            {
                MeasuredBy.Measured => Sizes.Format(p.Bytes ?? 0),
                MeasuredBy.Estimated => p.Bytes is long b ? $"up to {Sizes.Format(b)}" : "varies",
                _ => "measured with admin",
            };
            lines.Add($"• {p.Name}: {size}. {p.Explanation}");
        }
        lines.Add("");
        lines.Add(MissingSpaceReport.Hint);
        return string.Join(Environment.NewLine, lines);
    }

    private static int ChildNamed(SpaceIndex index, int folder, string name)
    {
        foreach (var c in index.Children(folder))
            if (index.NameSpan(c).Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
        return -1;
    }

    private static int FindPath(SpaceIndex index, params string[] parts)
    {
        int node = SpaceIndex.RootNode;
        foreach (var p in parts)
        {
            node = ChildNamed(index, node, p);
            if (node < 0) return -1;
        }
        return node;
    }
}
