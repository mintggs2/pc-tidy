using PcTidy.Catalog.DriveMove;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.DriveMove.Common;

namespace PcTidy.Features.DriveMove.Caches;

/// <summary>
/// npm and pnpm both keep their setting in .npmrc. Each ticked edit was planned from the scan-time text, so running two
/// of them one after another would keep only the last. Right before the plan runs, edits of the same file are folded
/// into one, computed from the file as it is now; if the file changed since the scan, the merge refuses.
/// </summary>
public static class ConfigEditMerger
{
    public sealed class FileChangedException(string path)
        : Exception($"\"{Path.GetFileName(path)}\" changed since the scan. Scan again, then move.");

    public static IReadOnlyList<PlanItem> Merge(IReadOnlyList<PlanItem> items, Func<string, (string Text, DateTime LastWriteUtc)?>? read = null)
    {
        read ??= ReadFile;
        var groups = items.Where(IsOurEdit).GroupBy(i => PathUtil.Normalize(i.SourcePath!), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0) return items;

        var result = new List<PlanItem>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (!IsOurEdit(item) || !groups.TryGetValue(PathUtil.Normalize(item.SourcePath!), out var group)) { result.Add(item); continue; }
            if (emitted.Add(PathUtil.Normalize(item.SourcePath!))) result.Add(Combine(group, read));
        }
        return result;
    }

    private static bool IsOurEdit(PlanItem i)
        => i.Kind == ActionKind.EditTextFile && i.SourcePath is not null && i.Args?.ContainsKey(DriveMoveArgs.Key) == true;

    private static PlanItem Combine(List<PlanItem> group, Func<string, (string Text, DateTime LastWriteUtc)?> read)
    {
        var first = group[0];
        var creating = group.All(i => i.Args!.ContainsKey(DriveMoveArgs.CreateIfMissing));
        var now = read(first.SourcePath!);
        // Planned as "create": the file must still be missing. Planned as "edit": it must be unchanged.
        if (creating ? now is not null : now is null || group.Any(i => i.ExpectedLastWriteUtc is { } t && t != now.Value.LastWriteUtc))
            throw new FileChangedException(first.SourcePath!);

        var text = now?.Text ?? "";
        foreach (var item in group)
        {
            var a = item.Args!;
            if (!Enum.TryParse<ConfigFormat>(a.GetValueOrDefault(DriveMoveArgs.Format), out var format)) throw new FileChangedException(first.SourcePath!);
            text = ConfigText.Set(format, text, a[DriveMoveArgs.Key], a[DriveMoveArgs.NewLocation], a.GetValueOrDefault(DriveMoveArgs.Section))
                   ?? throw new FileChangedException(first.SourcePath!);
        }
        var args = new Dictionary<string, string>(first.Args!) { [DriveMoveArgs.Content] = text };
        args.Remove(DriveMoveArgs.Tool);
        args.Remove(DriveMoveArgs.Key);
        args.Remove(DriveMoveArgs.NewLocation);
        return first with
        {
            Id = Guid.NewGuid().ToString("N"),
            Description = string.Join("; ", group.Select(i => i.Description)),
            Args = args,
            RequiresClosedApps = group.SelectMany(i => i.RequiresClosedApps ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } close ? close : null,
        };
    }

    private static (string, DateTime)? ReadFile(string path)
    {
        try { return File.Exists(path) ? (File.ReadAllText(path), File.GetLastWriteTimeUtc(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
