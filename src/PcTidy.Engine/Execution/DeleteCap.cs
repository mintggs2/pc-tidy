using System.IO.Enumeration;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine;

/// <summary>
/// The delete cap: a plan that removes or moves more originals than the limit (default 50,000 files or 200 GB, from
/// the "engine" settings blob) is refused as a whole before anything changes. Sizes are measured on disk when the run
/// starts (folders are walked), not taken from the plan. Override: every capped item must carry
/// <c>Args["capOverride"] = "true"</c>, which the UI sets on all of them only after the user confirmed the size.
/// Used by the executor and, with default limits, by the elevated helper.
/// </summary>
public static class DeleteCap
{
    /// <summary>Every kind that removes or moves originals.</summary>
    public static readonly IReadOnlySet<ActionKind> CappedKinds = new HashSet<ActionKind>
    {
        ActionKind.QuarantinePath, ActionKind.ShredFile, ActionKind.MovePath, ActionKind.RenamePath,
        ActionKind.MoveFolderWithJunction, ActionKind.ArchiveToZip,
    };

    public static bool IsCapped(PlanItem item) => CappedKinds.Contains(item.Kind);

    public static bool HasOverride(PlanItem item)
        => item.Args is { } a && a.TryGetValue(EngineSettings.CapOverrideArg, out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the plan is refused, or null when it's within the limits (or every capped item is overridden).</summary>
    public static string? Problem(IEnumerable<PlanItem> items, int maxItems, long maxBytes, CancellationToken ct)
    {
        var capped = items.Where(IsCapped).ToList();
        if (capped.Count == 0 || capped.All(HasOverride)) return null;
        long files = 0, bytes = 0;
        foreach (var item in capped)
        {
            ct.ThrowIfCancellationRequested();
            var (f, b) = Measure(item, ct);
            files += f;
            bytes += b;
            if (files > maxItems || bytes > maxBytes) break; // already over: no need to measure the rest
        }
        if (files > maxItems)
            return $"Stopped before any change: this plan removes or moves {(files > maxItems + 1 ? "more than " : "")}{maxItems:N0} files, the safety limit. Confirm the size to go ahead.";
        if (bytes > maxBytes)
            return $"Stopped before any change: this plan removes or moves more than {Sizes.Format(maxBytes)}, the safety limit. Confirm the size to go ahead.";
        return null;
    }

    /// <summary>Files and bytes an item would remove or move right now (never follows links; unreadable → plan values).</summary>
    private static (long Files, long Bytes) Measure(PlanItem item, CancellationToken ct)
    {
        if (item.SourcePath is not { Length: > 0 } source) return (1, Math.Max(0, item.ExpectedSize));
        FileSystemInfo? info;
        try { info = FsOps.Stat(source); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return (1, Math.Max(0, item.ExpectedSize)); }
        if (info is null) return (0, 0);
        if (info is FileInfo f) return (1, f.Length);
        if (FsOps.IsReparsePoint(info)) return (1, 0);

        long files = 0, bytes = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        var stack = new Stack<string>();
        stack.Push(LongPath.Io(source));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var (path, isDir, length, isLink) in new FileSystemEnumerable<(string, bool, long, bool)>(dir,
                             (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory, e.IsDirectory ? 0 : e.Length, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                             options))
                {
                    if (isDir && !isLink) stack.Push(path);
                    else { files++; bytes += length; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* unreadable folder: counted as nothing */ }
        }
        return (Math.Max(1, files), bytes);
    }
}
