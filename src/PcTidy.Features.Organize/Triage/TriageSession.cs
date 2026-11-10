using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Planning;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.Triage;

/// <summary>The keys of keyboard triage (the page maps real keys to these).</summary>
public enum TriageKey { Target1, Target2, Target3, Target4, Target5, Target6, Target7, Target8, Target9, Keep, Quarantine, Rename, Previous, Next, Finish }

public enum TriageDecisionKind { Undecided, Move, Keep, Quarantine, Rename }

/// <summary>What pressing a key did.</summary>
public enum TriageOutcome
{
    /// <summary>A decision was recorded (and the next file is shown).</summary>
    Decided,
    /// <summary>Moved to another file without deciding.</summary>
    Navigated,
    /// <summary>R: the page should ask for the new name, then call <see cref="TriageSession.Rename"/>.</summary>
    NeedsName,
    /// <summary>Esc: build the plan.</summary>
    Finished,
    /// <summary>The key means nothing here (no 7th folder, already at the first file…).</summary>
    Ignored,
}

/// <summary>A folder a number key sends files to: one of the user's move rules (first nine, in rule order).</summary>
public sealed record TriageTarget(int Number, string Label, RuleAction Action, string RuleId)
{
    public string KeyText => Number.ToString(CultureInfo.InvariantCulture);
}

public sealed record TriageDecision(TriageDecisionKind Kind, TriageTarget? Target = null, string? NewName = null)
{
    public static readonly TriageDecision None = new(TriageDecisionKind.Undecided);
}

/// <summary>One file in the triage queue.</summary>
public sealed class TriageFile(FileEntry entry, TriageTarget? suggestedTarget, string? suggestion)
{
    public FileEntry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Path => Entry.FullPath;
    /// <summary>What the rules would do with it ("Rules say: 2 · Pictures"), or null.</summary>
    public string? Suggestion { get; } = suggestion;
    public TriageTarget? SuggestedTarget { get; } = suggestedTarget;
    public TriageDecision Decision { get; internal set; } = TriageDecision.None;
}

/// <summary>
/// Keyboard triage for Downloads (Plan census v3): one file at a time; <c>1–9</c> send it to a rule's folder, <c>K</c>
/// keeps it, <c>Q</c> quarantines it, <c>R</c> renames it, <c>←/→</c> move between files and <c>Esc</c> finishes.
/// Pure decision-keeping: nothing changes on disk here. <see cref="BuildPlan"/> turns every decision into ONE plan for
/// the standard confirm (moves never overwrite, every item passes the safety guard).
/// </summary>
public sealed class TriageSession
{
    private readonly ISafetyGuard _safety;
    private readonly bool _useDateFolders;
    private readonly CultureInfo? _culture;
    private readonly List<string> _problems = new();

    public TriageSession(string root, IReadOnlyList<TriageFile> files, IReadOnlyList<TriageTarget> targets, ISafetyGuard safety, bool useDateFolders = false, CultureInfo? culture = null)
    {
        Root = PathUtil.Normalize(root);
        Files = files;
        Targets = targets;
        _safety = safety;
        _useDateFolders = useDateFolders;
        _culture = culture;
    }

    public string Root { get; }
    public IReadOnlyList<TriageFile> Files { get; }
    public IReadOnlyList<TriageTarget> Targets { get; }
    public int Index { get; private set; }
    public TriageFile? Current => Index < Files.Count ? Files[Index] : null;
    public bool IsFinished { get; private set; }
    public int DecidedCount => Files.Count(f => f.Decision.Kind != TriageDecisionKind.Undecided);
    /// <summary>Decisions that couldn't become plan items (safety guard, bad names), in plain words.</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>
    /// Lists the loose files of <paramref name="root"/> that may be sorted (the same "never moved" rules as a scan:
    /// no shortcuts, unfinished downloads, hidden, system, online-only or just-changed files), newest first, and the
    /// first nine move rules as number keys. Read-only.
    /// </summary>
    public static TriageSession Load(string root, OrganizeSettings settings, IFileWalker walker, ISafetyGuard safety, DateTime nowUtc, CultureInfo? culture = null, CancellationToken ct = default)
    {
        var targets = TargetsFrom(settings.Rules);
        var entries = new List<FileEntry>();
        walker.Walk(root, new WalkOptions { Recurse = false, IncludeHidden = true, IncludeSystem = true }, e => { if (!e.IsDirectory) entries.Add(e); return false; }, ct);
        var files = new List<TriageFile>();
        foreach (var e in entries.OrderByDescending(e => e.LastWriteUtc).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (SkipRules.Check(e, nowUtc) is not null) continue;
            if (safety.ClassifyPath(e.FullPath).Protection == PathProtection.Never) continue;
            var (target, suggestion) = Suggest(settings.Rules, targets, new FileFacts(e, root, ScanDepth.Quick), nowUtc);
            files.Add(new TriageFile(e, target, suggestion));
        }
        return new TriageSession(root, files, targets, safety, settings.UseDateFolders, culture);
    }

    /// <summary>The first nine enabled move rules, one per destination, in rule order.</summary>
    public static IReadOnlyList<TriageTarget> TargetsFrom(IEnumerable<OrganizeRule> rules)
        => rules.Where(r => r.Enabled && r.Action.Kind == RuleActionKind.MoveToFolder && !string.IsNullOrWhiteSpace(r.Action.Destination))
            .DistinctBy(r => r.Action.Destination.Trim().TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .Take(9)
            .Select((r, i) => new TriageTarget(i + 1, r.Name, r.Action.Clone(), r.Id))
            .ToList();

    private static (TriageTarget?, string?) Suggest(IEnumerable<OrganizeRule> rules, IReadOnlyList<TriageTarget> targets, FileFacts facts, DateTime nowUtc)
    {
        try
        {
            var rule = RuleEvaluator.FirstMatch(rules, facts, nowUtc);
            if (rule is null) return (null, null);
            if (rule.Action.Kind == RuleActionKind.Quarantine) return (null, $"Rules say: Q · quarantine ({rule.Name})");
            var target = targets.FirstOrDefault(t => string.Equals(t.Action.Destination.Trim().TrimEnd('\\'), rule.Action.Destination?.Trim().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            return target is null ? (null, null) : (target, $"Rules say: {target.KeyText} · {target.Label}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, null);
        }
    }

    // ------------------------------------------------------------------ keys

    public TriageOutcome Press(TriageKey key)
    {
        if (IsFinished) return TriageOutcome.Ignored;
        switch (key)
        {
            case TriageKey.Finish:
                IsFinished = true;
                return TriageOutcome.Finished;
            case TriageKey.Previous:
                if (Index == 0) return TriageOutcome.Ignored;
                Index--;
                return TriageOutcome.Navigated;
            case TriageKey.Next:
                if (Index >= Files.Count - 1) return TriageOutcome.Ignored;
                Index++;
                return TriageOutcome.Navigated;
        }
        if (Current is not { } file) return TriageOutcome.Ignored;
        switch (key)
        {
            case TriageKey.Keep: return Decide(file, new TriageDecision(TriageDecisionKind.Keep));
            case TriageKey.Quarantine: return Decide(file, new TriageDecision(TriageDecisionKind.Quarantine));
            case TriageKey.Rename: return TriageOutcome.NeedsName;
        }
        int number = key - TriageKey.Target1 + 1;
        var target = Targets.FirstOrDefault(t => t.Number == number);
        return target is null ? TriageOutcome.Ignored : Decide(file, new TriageDecision(TriageDecisionKind.Move, target));
    }

    /// <summary>
    /// Renames the current file (after R). The box shows the name without its extension, so what's typed replaces only
    /// the part before the extension: the file keeps its real extension (Path.GetExtension of the original), even when
    /// the typed name has dots of its own ("Invoice 2024.03", "setup v1.2"). Typing the extension too doesn't double it.
    /// Returns the problem in plain words when the name can't be used, or null when the decision was recorded.
    /// </summary>
    public string? Rename(string newName)
    {
        if (Current is not { } file) return "There's no file to rename.";
        newName = (newName ?? "").Trim();
        var extension = Path.GetExtension(file.Name);
        if (newName.Length > 0 && extension.Length > 0 && !newName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) newName += extension;
        if (WindowsNames.Problem(newName) is { } problem) return problem;
        if (string.Equals(newName, file.Name, StringComparison.Ordinal)) return "That's the name it already has.";
        Decide(file, new TriageDecision(TriageDecisionKind.Rename, NewName: newName));
        return null;
    }

    private TriageOutcome Decide(TriageFile file, TriageDecision decision)
    {
        file.Decision = decision;
        if (Index < Files.Count - 1) Index++;
        return TriageOutcome.Decided;
    }

    /// <summary>Plain words for a decision, for the list and screen readers.</summary>
    public static string Describe(TriageDecision d) => d.Kind switch
    {
        TriageDecisionKind.Move => $"Move to {d.Target!.Action.Destination}",
        TriageDecisionKind.Keep => "Keep where it is",
        TriageDecisionKind.Quarantine => "Quarantine (restorable)",
        TriageDecisionKind.Rename => $"Rename to \"{d.NewName}\"",
        _ => "Not decided yet",
    };

    // ------------------------------------------------------------------ plan

    /// <summary>
    /// ONE plan with every decision (folders first, parents before children). Nothing moves until the user confirms
    /// it in the standard flow. Decisions the safety guard refuses are left out and listed in <see cref="Problems"/>.
    /// </summary>
    public ActionPlan BuildPlan(string title)
    {
        _problems.Clear();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var leaving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files.Where(f => f.Decision.Kind is TriageDecisionKind.Move or TriageDecisionKind.Quarantine or TriageDecisionKind.Rename))
            leaving.Add(f.Path);
        var items = new List<PlanItem>();
        foreach (var f in Files)
        {
            var planned = f.Decision.Kind switch
            {
                TriageDecisionKind.Move => MoveItems(f, taken),
                TriageDecisionKind.Quarantine => new[] { Item(ActionKind.QuarantinePath, f, null, $"Quarantine \"{f.Name}\" (restorable)", "triage.quarantine") },
                TriageDecisionKind.Rename => RenameItems(f, taken, leaving),
                _ => Array.Empty<PlanItem>(),
            };
            var refused = planned.Where(i => i.Kind != ActionKind.CreateDirectory).Select(i => _safety.Check(i)).FirstOrDefault(v => !v.Allowed);
            if (refused is not null) { _problems.Add($"{f.Name}: {refused.Reason ?? "PC Tidy's safety check said no."} It stays where it is."); continue; }
            items.AddRange(planned);
        }
        return new ActionPlan { FeatureId = OrganizeModule.FeatureId, Title = title, Items = PlanAssembler.Order(items) };
    }

    private IReadOnlyList<PlanItem> MoveItems(TriageFile f, HashSet<string> taken)
    {
        var target = f.Decision.Target!;
        var folder = DestinationResolver.ResolveFolder(target.Action, Root, f.Entry.LastWriteUtc.ToLocalTime(), _useDateFolders, _culture);
        if (folder is null) { _problems.Add($"{f.Name}: the folder of \"{target.Label}\" isn't valid. It stays where it is."); return Array.Empty<PlanItem>(); }
        var desired = Path.Combine(folder, f.Name);
        var final = taken.Contains(desired) || File.Exists(desired) || Directory.Exists(desired) ? NextFree(desired, taken) : desired;
        taken.Add(final);
        var shortDest = DisplayPaths.Short(folder, new[] { Root });
        var move = Item(ActionKind.MovePath, f, final,
            $"Move \"{f.Name}\" to {shortDest}" + (final == desired ? "" : $" as \"{Path.GetFileName(final)}\""), target.RuleId);
        return MissingFolders(folder).Append(move).ToList();
    }

    private IReadOnlyList<PlanItem> RenameItems(TriageFile f, HashSet<string> taken, HashSet<string> leaving)
    {
        var desired = Path.Combine(Path.GetDirectoryName(f.Path)!, f.Decision.NewName!);
        bool caseOnly = string.Equals(desired, f.Path, StringComparison.OrdinalIgnoreCase);
        var final = !caseOnly && (taken.Contains(desired) || ((File.Exists(desired) || Directory.Exists(desired)) && !leaving.Contains(desired)))
            ? NextFree(desired, taken) : desired;
        taken.Add(final);
        var item = Item(ActionKind.RenamePath, f, final, $"Rename \"{f.Name}\" to \"{Path.GetFileName(final)}\"", "triage.rename") with { ClashPolicy = NameClashPolicy.Skip };
        return new[] { item };
    }

    private static PlanItem Item(ActionKind kind, TriageFile f, string? destination, string description, string ruleId) => new()
    {
        Kind = kind,
        FeatureId = OrganizeModule.FeatureId,
        Description = description,
        SourcePath = f.Path,
        DestinationPath = destination,
        ExpectedSize = f.Entry.Size,
        ExpectedLastWriteUtc = f.Entry.LastWriteUtc,
        ClashPolicy = NameClashPolicy.AutoNumber,
        Args = new Dictionary<string, string> { [OrganizeLedger.RuleIdArg] = ruleId },
    };

    private static string NextFree(string desired, HashSet<string> taken)
    {
        var dir = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (int i = 2; i < 100_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!taken.Contains(candidate) && !File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("No free name for " + desired);
    }

    private static IEnumerable<PlanItem> MissingFolders(string folder)
    {
        var missing = new Stack<string>();
        for (var d = folder; d is not null && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Push(d);
        return missing.Select(d => new PlanItem
        {
            Kind = ActionKind.CreateDirectory, FeatureId = OrganizeModule.FeatureId,
            Description = $"Create folder \"{Path.GetFileName(d)}\"", DestinationPath = d, IsDirectory = true,
        }).ToList();
    }
}
