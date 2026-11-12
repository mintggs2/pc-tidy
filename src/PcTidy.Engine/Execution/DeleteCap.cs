using System.IO.Enumeration;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Engine.IO;

namespace PcTidy.Engine;

/// <summary>
/// The delete cap: a plan that removes or moves more originals than the limit (default 50,000 files or 200 GB, from
/// the "engine" settings blob) is refused as a whole before anything changes. Sizes are measured on disk when the run
/// starts (folders are walked), not taken from the plan. Override: every capped item must carry
/// <c>Args["capOverride"] = "true"</c>, which the app sets on all of them only after the user confirmed the measured size
/// (<see cref="RemovalMeasure"/>).
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

    /// <summary>The start of every cap message (the executor splits admin requests the helper refuses with it).</summary>
    public const string StoppedPrefix = "Stopped before any change: this plan removes or moves";

    /// <summary>What the app says after the limit when the user can confirm the size on the page they ran it from.</summary>
    public const string AppAdvice = "Run it again from its page: PC Tidy shows the size and asks before going ahead.";

    /// <summary>True for a message from <see cref="Problem"/> (the run or the admin helper hit the delete cap).</summary>
    public static bool IsCapMessage(string? message) => message?.StartsWith(StoppedPrefix, StringComparison.Ordinal) == true;

    /// <summary>Why the plan is refused, or null when it's within the limits (or every capped item is overridden).</summary>
    /// <param name="advice">The sentence after the limit (what the user can do).</param>
    public static string? Problem(IEnumerable<PlanItem> items, int maxItems, long maxBytes, CancellationToken ct, string advice = AppAdvice)
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
            return $"{StoppedPrefix} {(files > maxItems + 1 ? "more than " : "")}{maxItems:N0} files, the safety limit. {advice}";
        if (bytes > maxBytes)
            return $"{StoppedPrefix} more than {Sizes.Format(maxBytes)}, the safety limit. {advice}";
        return null;
    }

    /// <summary>Everything the capped items would remove or move right now (no early stop: the numbers the user is shown).</summary>
    public static RemovalSize MeasureAll(IEnumerable<PlanItem> items, int maxItems, long maxBytes, CancellationToken ct)
    {
        var capped = items.Where(IsCapped).ToList();
        long files = 0, bytes = 0;
        foreach (var item in capped)
        {
            ct.ThrowIfCancellationRequested();
            var (f, b) = Measure(item, ct);
            files += f;
            bytes += b;
        }
        return new RemovalSize(capped.Count, files, bytes, capped.All(i => i.Kind == ActionKind.QuarantinePath), maxItems, maxBytes);
    }

    /// <summary>The item with the cap override set (the user confirmed the measured size). Items outside the cap are unchanged.</summary>
    public static PlanItem WithOverride(PlanItem item)
    {
        if (!IsCapped(item) || HasOverride(item)) return item;
        var args = item.Args is { } a ? new Dictionary<string, string>(a, a.Comparer) : new Dictionary<string, string>();
        args[EngineSettings.CapOverrideArg] = "true";
        return item with { Args = args };
    }

    /// <summary>The item without any cap override (whatever its spelling).</summary>
    public static PlanItem WithoutOverride(PlanItem item)
        => item.Args is { } a && a.Keys.Any(k => k.Equals(EngineSettings.CapOverrideArg, StringComparison.OrdinalIgnoreCase))
            ? item with { Args = a.Where(kv => !kv.Key.Equals(EngineSettings.CapOverrideArg, StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value, a.Comparer) }
            : item;

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

/// <summary>
/// The app's size check before a run (<see cref="IRemovalMeasure"/>): the delete cap's own measure with this PC's limits,
/// so the numbers in "This moves N files (X GB). Continue?" are exactly what the executor would count.
/// </summary>
public sealed class RemovalMeasure(AppSettings settings) : IRemovalMeasure
{
    public RemovalSize Measure(ActionPlan plan, CancellationToken ct)
    {
        var cfg = EngineSettings.From(settings);
        return DeleteCap.MeasureAll(plan.Items, cfg.MaxRemoveItemsPerRun, cfg.MaxRemoveBytesPerRun, ct);
    }

    public ActionPlan WithSizeConfirmed(ActionPlan plan) => plan with { Items = plan.Items.Select(DeleteCap.WithOverride).ToList() };

    public ActionPlan WithoutSizeConfirmed(ActionPlan plan)
        => plan.Items.Any(i => i.Args?.ContainsKey(EngineSettings.CapOverrideArg) == true)
            ? plan with { Items = plan.Items.Select(DeleteCap.WithoutOverride).ToList() }
            : plan;
}
