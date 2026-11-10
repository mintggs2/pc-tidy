using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.UI.Flow;

/// <summary>One line in the confirm dialog ("Move 128 items (6.2 GB) to quarantine").</summary>
public sealed record ConfirmGroup(string Kind, string Title, string Detail, IReadOnlyList<string> Examples);

/// <summary>
/// What the confirm dialog says, worked out from the plans (pure logic, no UI, unit-testable).
/// Lines: quarantine · moves · startup · settings · pages to open · needs admin · needs restart · restore point; permanent box.
/// Every count comes from <see cref="PlanCounts"/>, the same numbers the bottom bar and Done card show.
/// </summary>
public sealed class ConfirmModel
{
    public const string KindQuarantine = "quarantine";
    public const string KindMoves = "moves";
    public const string KindStartup = "startup";
    public const string KindSettings = "settings";
    public const string KindAdmin = "admin";
    public const string KindRestart = "restart";
    public const string KindRestorePoint = "restorepoint";
    public const string KindOpen = "open";
    public const string KindCookies = "cookies";

    public ConfirmModel(IReadOnlyList<ActionPlan> plans, IReadOnlySet<string>? restartIds, int quarantineDays)
    {
        Plans = plans;
        var all = plans.SelectMany(p => p.Items).ToList();
        Counts = PlanCounts.Of(all);
        Permanent = all.Where(i => PlanFacts.Categorize(i) == PlanCategory.Permanent).ToList();
        List<PlanItem> Of(PlanCategory c) => all.Where(i => PlanFacts.Categorize(i) == c).ToList();
        var groups = new List<ConfirmGroup>();

        var q = Of(PlanCategory.Quarantine);
        if (q.Count > 0)
        {
            long bytes = q.Sum(i => i.ExpectedSize);
            groups.Add(new(KindQuarantine,
                $"Move {PlanFacts.Items(q.Count)}" + (bytes > 0 ? $" ({Sizes.Format(bytes)})" : "") + " to quarantine",
                $"Restorable for {quarantineDays} days from History or Quarantine. Nothing is deleted today.", Examples(q)));
        }

        var moves = Of(PlanCategory.Move);
        if (moves.Count > 0)
            groups.Add(new(KindMoves, $"Move or rename {moves.Count:N0} file{PlanFacts.S(moves.Count)}",
                "Undo puts every file back where it was. Nothing is overwritten: clashes get a new name.", Examples(moves)));

        var startup = Of(PlanCategory.Startup);
        if (startup.Count > 0)
            groups.Add(new(KindStartup, $"Change {startup.Count} startup item{PlanFacts.S(startup.Count)}",
                "Apps stay installed; they just don't start with Windows. Turn any back on from History.", Examples(startup)));

        // Cookies are counted with settings, but named for what they do: every website signs you out except the kept ones.
        var cookies = Of(PlanCategory.Setting).Where(PlanFacts.IsCookies).ToList();
        if (cookies.Count > 0) groups.Add(CookiesGroup(cookies));

        var settings = Of(PlanCategory.Setting).Where(i => !PlanFacts.IsCookies(i)).ToList();
        if (settings.Count > 0)
            groups.Add(new(KindSettings, $"Change {settings.Count} setting{PlanFacts.S(settings.Count)}",
                "The current values are saved first, so each one can be undone.", Examples(settings)));

        // Pages or launcher screens PC Tidy only opens ("Open Steam to uninstall"): listed so the dialog says what the button does.
        var opens = all.Where(PlanFacts.IsOpen).ToList();
        if (opens.Count > 0)
            groups.Add(new(KindOpen, opens.Count == 1 ? opens[0].Description : PlanFacts.OpenAction(opens),
                "PC Tidy only opens it for you. Nothing changes on your PC unless you go ahead there.", Examples(opens)));

        var admin = all.Where(i => i.NeedsAdmin && PlanFacts.Categorize(i) != PlanCategory.Housekeeping).ToList();
        if (admin.Count > 0)
            groups.Add(new(KindAdmin, $"{admin.Count} step{PlanFacts.S(admin.Count)} need{(admin.Count == 1 ? "s" : "")} admin (Windows asks once)",
                "These touch system folders or settings, so Windows asks you to allow them.", ShortExamples(admin)));

        var restart = all.Where(i => PlanFacts.NeedsRestart(i, restartIds)).ToList();
        if (restart.Count > 0)
            groups.Add(new(KindRestart, $"{restart.Count} change{PlanFacts.S(restart.Count)} need{(restart.Count == 1 ? "s" : "")} a restart",
                "They take effect after you restart. PC Tidy never restarts your PC for you.", Examples(restart)));

        if (all.Any(i => i.IsSystemChange))
            groups.Add(new(KindRestorePoint, "A restore point is made first",
                "Takes about 20 seconds. If anything feels off later, you can roll back from History.", Array.Empty<string>()));

        Groups = groups;
        PermanentBytes = Permanent.Sum(i => i.ExpectedSize);
        // Pages to open always run (nothing to undo, nothing at risk), so they count as "something else still runs".
        ReversibleCount = Counts.Total - Counts.Permanent + Counts.Opens;
    }

    public IReadOnlyList<ActionPlan> Plans { get; }
    public PlanCounts Counts { get; }
    public IReadOnlyList<ConfirmGroup> Groups { get; }
    public IReadOnlyList<PlanItem> Permanent { get; }
    public long PermanentBytes { get; }
    public int ReversibleCount { get; }
    public bool HasPermanent => Permanent.Count > 0;
    /// <summary>A restore point line is shown (any item is a system change).</summary>
    public bool NeedsRestorePoint => Groups.Any(g => g.Kind == KindRestorePoint);

    /// <summary>Can "Do it" be pressed? Needs something reversible, or the permanent box ticked.</summary>
    public bool CanRun(bool permanentAcknowledged) => ReversibleCount > 0 || (HasPermanent && permanentAcknowledged);

    /// <summary>Names exactly what pressing the button does: "Clean 6.3 GB", "Move 214 files", "Apply 9 changes".</summary>
    public string PrimaryText(bool permanentAcknowledged)
        => PlanFacts.PrimaryAction(PlansToRun(permanentAcknowledged).SelectMany(p => p.Items));

    /// <summary>
    /// The plans that actually run. Permanent items are dropped unless the user ticked
    /// "I understand these can't be undone"; everything else still runs.
    /// </summary>
    public IReadOnlyList<ActionPlan> PlansToRun(bool permanentAcknowledged)
    {
        if (permanentAcknowledged || !HasPermanent) return Plans;
        return Plans.Select(p => p with { Items = p.Items.Where(i => PlanFacts.Categorize(i) != PlanCategory.Permanent).ToList() })
                    .Where(p => p.Items.Any(PlanFacts.Runs)).ToList();
    }

    /// <summary>
    /// "Delete cookies except 2 kept sites": the browsers, that you'll be signed out of every other website, that the
    /// browsers must be closed, and that Undo puts the old cookies back.
    /// </summary>
    private static ConfirmGroup CookiesGroup(IReadOnlyList<PlanItem> cookies)
    {
        var kept = cookies.SelectMany(PlanFacts.KeptSites).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        var browsers = PlanFacts.JoinAnd(cookies.Select(PlanFacts.BrowserName).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        var title = kept.Count == 0 ? "Delete all cookies (0 kept sites)" : $"Delete cookies except {kept.Count} kept site{PlanFacts.S(kept.Count)}";
        var keptNames = kept.Take(3).ToList();
        if (kept.Count > 3) keptNames.Add($"{kept.Count - 3} more");
        var signedOut = kept.Count == 0
            ? $"You'll be signed out of every website in {browsers}: no sites are kept. To stay signed in somewhere, go Back and use Choose sites first."
            : $"You'll be signed out of every website in {browsers} except {PlanFacts.JoinAnd(keptNames)}.";
        var detail = $"{cookies.Count} browser profile{PlanFacts.S(cookies.Count)}. {signedOut} Close {browsers} first, or that browser is skipped. "
                     + "Saved passwords and history stay, and Undo puts the old cookies back.";
        return new ConfirmGroup(KindCookies, title, detail, Examples(cookies));
    }

    /// <summary>Short names for the admin line (the service name, not the whole step again).</summary>
    private static IReadOnlyList<string> ShortExamples(IReadOnlyList<PlanItem> items)
    {
        var list = items.Select(i => i.ServiceName is { Length: > 0 } s ? s : i.Description).Distinct().Take(3).ToList();
        if (items.Count > 3) list.Add($"and {items.Count - 3:N0} more");
        return list;
    }

    private static IReadOnlyList<string> Examples(IReadOnlyList<PlanItem> items)
    {
        var list = items.Select(i => i.Description).Distinct().Take(3).ToList();
        if (items.Count > 3) list.Add($"and {items.Count - 3:N0} more");
        return list;
    }
}
