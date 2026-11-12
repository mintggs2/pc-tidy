using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.UI.Flow;

/// <summary>
/// What a plan item is, for counting. Every number the user sees (bottom bar, confirm, simulation, Done, History)
/// comes from this one classification, so "49 items" means the same thing on every screen.
/// </summary>
public enum PlanCategory
{
    /// <summary>Moved into quarantine (restorable).</summary>
    Quarantine,
    /// <summary>Moved, renamed, zipped or unzipped files.</summary>
    Move,
    /// <summary>Startup apps, startup tasks and services.</summary>
    Startup,
    /// <summary>Registry values, tweaks, power plans and other settings (old value saved first).</summary>
    Setting,
    /// <summary>Can't be undone (Recycle Bin, Windows' own tools...).</summary>
    Permanent,
    /// <summary>Bookkeeping that isn't a change the user picked (new folders for sorting, report files, opening a page).</summary>
    Housekeeping,
}

/// <summary>Counts of plan items by category: the single source for every total on screen.</summary>
public sealed record PlanCounts
{
    public int Quarantine { get; init; }
    public long QuarantineBytes { get; init; }
    public int Moves { get; init; }
    public int Startup { get; init; }
    public int Settings { get; init; }
    /// <summary>Of <see cref="Settings"/>: browser profiles whose cookies are cleared (named as such, never "settings").</summary>
    public int Cookies { get; init; }
    public int Permanent { get; init; }
    public long PermanentBytes { get; init; }
    public int Admin { get; init; }
    public bool SystemChange { get; init; }
    /// <summary>
    /// Pages or launcher screens PC Tidy only opens (OpenExternal), e.g. "Open Steam to uninstall". Not part of
    /// <see cref="Total"/> (nothing on the PC changes), but when they are all that's picked they are what runs.
    /// </summary>
    public int Opens { get; init; }
    /// <summary>Items the user picked (everything except housekeeping).</summary>
    public int Total => Quarantine + Moves + Startup + Settings + Permanent;
    /// <summary>True when pressing the main button would do something: a change, or only opening pages.</summary>
    public bool HasWork => Total > 0 || Opens > 0;
    /// <summary>Only pages to open, nothing that changes the PC.</summary>
    public bool OnlyOpens => Total == 0 && Opens > 0;
    /// <summary>Bytes that leave their place (quarantine + permanent). Moves don't free anything.</summary>
    public long CleanBytes => QuarantineBytes + PermanentBytes;
    /// <summary>True when nothing here deals in file sizes (settings, startup, moves), so byte totals would read "0 B".</summary>
    public bool NoBytes => CleanBytes == 0;

    public static PlanCounts Of(IEnumerable<PlanItem> items)
    {
        int q = 0, m = 0, st = 0, se = 0, p = 0, admin = 0, opens = 0, cookies = 0;
        long qb = 0, pb = 0;
        bool sys = false;
        foreach (var i in items)
        {
            if (PlanFacts.IsOpen(i)) { opens++; continue; }
            switch (PlanFacts.Categorize(i))
            {
                case PlanCategory.Quarantine: q++; qb += i.ExpectedSize; break;
                case PlanCategory.Move: m++; break;
                case PlanCategory.Startup: st++; break;
                case PlanCategory.Setting: se++; if (PlanFacts.IsCookies(i)) cookies++; break;
                case PlanCategory.Permanent: p++; pb += i.ExpectedSize; break;
                default: continue;
            }
            if (i.NeedsAdmin) admin++;
            sys |= i.IsSystemChange;
        }
        return new PlanCounts { Quarantine = q, QuarantineBytes = qb, Moves = m, Startup = st, Settings = se, Cookies = cookies, Permanent = p, PermanentBytes = pb, Admin = admin, SystemChange = sys, Opens = opens };
    }
}

/// <summary>Plain-English facts about plan items, shared by the confirm dialog, totals lines and Done card.</summary>
public static class PlanFacts
{
    public static PlanCategory Categorize(PlanItem i)
    {
        // Opening a page changes nothing, so it is never "permanent", whatever its Reversible flag says.
        if (IsOpen(i)) return PlanCategory.Housekeeping;
        if (!i.Reversible) return PlanCategory.Permanent;
        if (i.Kind == ActionKind.QuarantinePath) return PlanCategory.Quarantine;
        if (IsFileMove(i)) return PlanCategory.Move;
        if (i.Kind is ActionKind.CreateDirectory or ActionKind.WriteTextFile) return PlanCategory.Housekeeping;
        if (IsStartupChange(i)) return PlanCategory.Startup;
        return PlanCategory.Setting;
    }

    public static bool IsQuarantine(PlanItem i) => Categorize(i) == PlanCategory.Quarantine;

    /// <summary>Opens a page or a launcher's own screen (OpenExternal). Nothing on the PC changes.</summary>
    public static bool IsOpen(PlanItem i) => i.Kind == ActionKind.OpenExternal;

    /// <summary>Does this item run when picked? Everything except bookkeeping, and pages to open count.</summary>
    public static bool Runs(PlanItem i) => IsOpen(i) || Categorize(i) != PlanCategory.Housekeeping;

    public static bool IsFileMove(PlanItem i) => i.Kind is ActionKind.MovePath or ActionKind.RenamePath
        or ActionKind.MoveFolderWithJunction or ActionKind.ArchiveToZip or ActionKind.ExtractZip;

    public static bool IsStartupChange(PlanItem i) => i.Kind is ActionKind.StartupItemSetEnabled
        || (i.Kind is ActionKind.ScheduledTaskSetEnabled or ActionKind.ServiceSetStartType && i.FeatureId.Contains("speed", StringComparison.OrdinalIgnoreCase));

    /// <summary>Deletes a browser profile's cookies (except kept sites). Counted with settings, but always named for what it is.</summary>
    public static bool IsCookies(PlanItem i) => i.Kind == ActionKind.CookiesDeleteExcept;

    /// <summary>"Chrome", "Edge", "Firefox" from a cookie item's Args["browser"].</summary>
    public static string BrowserName(PlanItem i)
    {
        var arg = i.Args is { } a && a.TryGetValue("browser", out var b) ? b.Trim() : "";
        return arg.ToLowerInvariant() switch
        {
            "chrome" => "Chrome", "edge" => "Edge", "brave" => "Brave", "firefox" => "Firefox", "" => "your browser",
            _ => char.ToUpperInvariant(arg[0]) + arg[1..],
        };
    }

    /// <summary>The sites a cookie item keeps (Args["keepHosts"], one per line).</summary>
    public static IReadOnlyList<string> KeptSites(PlanItem i)
        => i.Args is { } a && a.TryGetValue("keepHosts", out var k)
            ? k.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();

    /// <summary>"Chrome", "Chrome and Edge", "Chrome, Edge and Firefox".</summary>
    public static string JoinAnd(IReadOnlyList<string> names)
        => names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    public static bool IsSettingChange(PlanItem i) => Categorize(i) == PlanCategory.Setting;

    public static bool IsPermanent(PlanItem i) => !i.Reversible;

    /// <summary>Restart flag: from the scan item, or Args["needsRestart"]="true" for custom pages.</summary>
    public static bool NeedsRestart(PlanItem i, IReadOnlySet<string>? restartIds)
        => (restartIds?.Contains(i.Id) ?? false)
           || (i.Args != null && i.Args.TryGetValue("needsRestart", out var v) && string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>"Clean 6.3 GB (49 items) · sort 63 files · 1 startup item · 3 settings" (only the parts that apply).</summary>
    public static string TotalsLine(IEnumerable<PlanItem> items) => TotalsLine(PlanCounts.Of(items));

    /// <param name="quarantineFiles">Files inside the quarantine items, when known: "(49 items, 3,120 files)".</param>
    public static string TotalsLine(PlanCounts c, long quarantineFiles = 0)
    {
        var parts = new List<string>();
        var inside = quarantineFiles > c.Quarantine ? $", {quarantineFiles:N0} files" : "";
        if (c.Quarantine > 0) parts.Add(c.QuarantineBytes > 0 ? $"clean {Sizes.Format(c.QuarantineBytes)} ({Items(c.Quarantine)}{inside})" : $"clean {Items(c.Quarantine)}{inside}");
        if (c.Moves > 0) parts.Add($"sort {c.Moves:N0} file{S(c.Moves)}");
        if (c.Startup > 0) parts.Add($"{c.Startup} startup item{S(c.Startup)}");
        if (c.Cookies > 0) parts.Add($"cookies in {c.Cookies} browser profile{S(c.Cookies)}");
        if (c.Settings - c.Cookies > 0) parts.Add($"{c.Settings - c.Cookies} setting{S(c.Settings - c.Cookies)}");
        if (c.Permanent > 0) parts.Add(c.PermanentBytes > 0 ? $"{c.Permanent} permanent ({Sizes.Format(c.PermanentBytes)})" : $"{c.Permanent} permanent");
        if (parts.Count == 0) return c.Opens > 0 ? OpenCount(c.Opens) : "Nothing selected";
        parts[0] = char.ToUpperInvariant(parts[0][0]) + parts[0][1..];
        return string.Join(" · ", parts);
    }

    /// <summary>Primary button text for a selection: "Clean 6.3 GB", "Move 214 files", "Apply 9 changes".</summary>
    public static string PrimaryAction(IEnumerable<PlanItem> items)
    {
        var list = items as IReadOnlyCollection<PlanItem> ?? items.ToList();
        var c = PlanCounts.Of(list);
        // Only pages to open: name them from the items ("Open Steam to uninstall (1)").
        return c.OnlyOpens ? OpenAction(list.Where(IsOpen).ToList()) : PrimaryAction(c);
    }

    public static string PrimaryAction(PlanCounts c)
    {
        if (c.OnlyOpens) return OpenCount(c.Opens);
        if (c.Total == 0) return "Nothing selected";
        int cleans = c.Quarantine + c.Permanent;
        int changes = c.Startup + c.Settings;
        // A mix (Optimize everything): no single verb is honest, so name the whole thing.
        if ((cleans > 0 ? 1 : 0) + (c.Moves > 0 ? 1 : 0) + (changes > 0 ? 1 : 0) > 1) return $"Apply these {c.Total:N0} changes";
        if (cleans > 0 && cleans >= c.Moves && cleans >= changes)
            return c.CleanBytes > 0 ? "Clean " + Sizes.Format(c.CleanBytes) : $"Clean {Items(cleans)}";
        if (c.Moves > 0 && c.Moves >= changes) return $"Move {c.Moves:N0} file{S(c.Moves)}";
        // Only cookies: say so ("Apply 7 changes" hid that every website signs you out).
        if (c.Cookies > 0 && c.Cookies == changes) return $"Delete cookies ({c.Cookies:N0} profile{S(c.Cookies)})";
        return $"Apply {c.Total:N0} change{S(c.Total)}";
    }

    /// <summary>Icon that matches <see cref="PrimaryAction(PlanCounts)"/>.</summary>
    public static Wpf.Ui.Controls.SymbolRegular PrimaryIcon(PlanCounts c)
    {
        if (c.OnlyOpens) return Wpf.Ui.Controls.SymbolRegular.Open24;
        int cleans = c.Quarantine + c.Permanent, changes = c.Startup + c.Settings;
        if ((cleans > 0 ? 1 : 0) + (c.Moves > 0 ? 1 : 0) + (changes > 0 ? 1 : 0) > 1) return Wpf.Ui.Controls.SymbolRegular.Sparkle24;
        if (cleans > 0 && cleans >= c.Moves && cleans >= changes) return Wpf.Ui.Controls.SymbolRegular.Broom24;
        if (c.Moves > 0 && c.Moves >= changes) return Wpf.Ui.Controls.SymbolRegular.FolderArrowRight24;
        if (c.Cookies > 0 && c.Cookies == changes) return Wpf.Ui.Controls.SymbolRegular.Cookies24;
        return Wpf.Ui.Controls.SymbolRegular.Checkmark24;
    }

    /// <summary>"Open 2 pages": the count-only wording when the items themselves aren't at hand.</summary>
    public static string OpenCount(int n) => $"Open {n:N0} page{S(n)}";

    /// <summary>
    /// Button text for pages to open, from their descriptions with the quoted name taken out:
    /// 'Open Steam to uninstall "Some Game"' x2 → "Open Steam to uninstall (2)"; one "Open Windows Update settings" stays as is;
    /// Steam + Epic uninstalls → "Open 2 apps to uninstall (3)"; anything else → "Open 3 pages".
    /// </summary>
    public static string OpenAction(IReadOnlyList<PlanItem> opens)
    {
        if (opens.Count == 0) return "Nothing selected";
        var phrases = opens.Select(i => WithoutQuotedName(i.Description)).ToList();
        bool named = phrases.Any(p => p.Trimmed);
        var distinct = phrases.Select(p => p.Text).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (distinct.Count == 1 && distinct[0].Length is > 0 and <= MaxOpenLabel)
            return opens.Count == 1 && !named ? distinct[0] : $"{distinct[0]} ({opens.Count:N0})";
        // "Open <app> to <do something>" with the same "to ..." part: count the apps.
        var shaped = distinct.Select(p => OpenTo.Match(p)).ToList();
        if (shaped.All(m => m.Success) && shaped.Select(m => m.Groups[2].Value).Distinct(StringComparer.CurrentCultureIgnoreCase).Count() == 1)
        {
            var label = $"Open {shaped.Count} apps to {shaped[0].Groups[2].Value} ({opens.Count:N0})";
            if (label.Length <= MaxOpenLabel + 8) return label;
        }
        return OpenCount(opens.Count);
    }

    private const int MaxOpenLabel = 40;
    private static readonly System.Text.RegularExpressions.Regex OpenTo = new(@"^Open (.+?) to (.+)$");
    private static readonly System.Text.RegularExpressions.Regex QuotedName = new("\\s*[\"\u201C][^\"\u201D]*[\"\u201D]");

    private static (string Text, bool Trimmed) WithoutQuotedName(string description)
    {
        var text = QuotedName.Replace(description ?? "", "").Trim();
        return (text, text.Length != (description ?? "").Trim().Length);
    }

    public static string Items(int n) => $"{n:N0} item{S(n)}";
    public static string S(int n) => n == 1 ? "" : "s";
    public static string S(long n) => n == 1 ? "" : "s";
}
