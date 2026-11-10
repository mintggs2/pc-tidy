using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize.Rename;

public enum NameCase { Unchanged, Lower, Upper, Title, Sentence }
public enum NumberPosition { Prefix, Suffix }

/// <summary>Rename helper options (PowerRename-style). Applied in this order: fix messy names, find/replace, case, number.</summary>
public sealed record RenameOptions
{
    public string Find { get; init; } = "";
    /// <summary>Replacement text. May use {date} {year} {month} {monthName} {day} (file's modified date) and, with regex, $1.</summary>
    public string Replace { get; init; } = "";
    public bool UseRegex { get; init; }
    public bool MatchCase { get; init; }
    /// <summary>Find/replace also looks at the extension.</summary>
    public bool IncludeExtension { get; init; }
    public NameCase Case { get; init; } = NameCase.Unchanged;
    public bool AddNumber { get; init; }
    public int NumberStart { get; init; } = 1;
    public int NumberStep { get; init; } = 1;
    public int NumberDigits { get; init; } = 2;
    public NumberPosition NumberPosition { get; init; } = NumberPosition.Suffix;
    public string NumberSeparator { get; init; } = " ";
    /// <summary>The Plan §9 "fix messy names" set.</summary>
    public bool FixMessyNames { get; init; }
    public bool LowercaseExtension { get; init; }
    public TimeSpan RegexTimeout { get; init; } = NamePattern.DefaultTimeout;
    public CultureInfo? Culture { get; init; }
}

public sealed record RenameSource(string Path, DateTime LastWriteUtc)
{
    public static RenameSource FromEntry(FileEntry e) => new(e.FullPath, e.LastWriteUtc);
}

/// <summary>One live-preview row. Rows with a <see cref="Problem"/> can't be ticked.</summary>
public sealed record RenameRow(string SourcePath, string OldName, string NewName, string? Problem, string? Warning)
{
    public bool Changed => !string.Equals(OldName, NewName, StringComparison.Ordinal);
    public bool CanRename => Changed && Problem is null;
    public string NewPath => Path.Combine(Path.GetDirectoryName(SourcePath)!, NewName);
    public bool IsCaseOnly => Changed && string.Equals(OldName, NewName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Computes new names and turns the ticked rows into RenamePath plan items. Read-only.</summary>
public static partial class RenameHelper
{
    private static readonly string[] DoubleExtensions = { ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst", ".tar.lz" };

    public static IReadOnlyList<RenameRow> Preview(IReadOnlyList<RenameSource> files, RenameOptions options, Func<string, bool>? pathExists = null)
    {
        pathExists ??= p => File.Exists(p) || Directory.Exists(p);
        Regex? regex = null;
        string? patternProblem = null;
        if (options.Find.Length > 0)
        {
            try
            {
                var source = options.UseRegex ? options.Find : Regex.Escape(options.Find);
                var flags = RegexOptions.CultureInvariant | (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
                regex = new Regex(source, flags, options.RegexTimeout);
            }
            catch (ArgumentException) { patternProblem = "That pattern isn't valid."; }
        }

        var rows = new List<RenameRow>(files.Count);
        int number = options.NumberStart;
        foreach (var f in files)
        {
            var oldName = Path.GetFileName(f.Path);
            if (patternProblem is not null) { rows.Add(new RenameRow(f.Path, oldName, oldName, patternProblem, null)); continue; }
            string newName;
            try
            {
                newName = NewName(oldName, f.LastWriteUtc.ToLocalTime(), regex, options, options.AddNumber ? number : null);
            }
            catch (RegexMatchTimeoutException)
            {
                rows.Add(new RenameRow(f.Path, oldName, oldName, "The pattern took too long on this name.", null));
                continue;
            }
            if (options.AddNumber) number += options.NumberStep;
            rows.Add(new RenameRow(f.Path, oldName, newName, WindowsNames.Problem(newName), null));
        }
        return MarkCollisions(rows, pathExists);
    }

    /// <summary>The new name for one file (exposed for the rule engine and tests).</summary>
    public static string NewName(string oldName, DateTime localDate, Regex? find, RenameOptions o, int? number)
    {
        var (stem, ext) = SplitName(oldName);
        if (o.FixMessyNames) { stem = FixMessyStem(stem); ext = ext.ToLowerInvariant(); }

        if (find is not null)
        {
            var replacement = DestinationResolver.ExpandTokens(o.Replace, localDate, o.Culture);
            if (!o.UseRegex) replacement = replacement.Replace("$", "$$");
            if (o.IncludeExtension)
            {
                (stem, ext) = SplitName(find.Replace(stem + ext, replacement));
            }
            else stem = find.Replace(stem, replacement);
        }

        stem = o.Case switch
        {
            NameCase.Lower => stem.ToLowerInvariant(),
            NameCase.Upper => stem.ToUpperInvariant(),
            NameCase.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(stem.ToLowerInvariant()),
            NameCase.Sentence => stem.Length == 0 ? stem : char.ToUpperInvariant(stem[0]) + stem[1..].ToLowerInvariant(),
            _ => stem,
        };

        if (number is { } n)
        {
            var digits = n.ToString("D" + Math.Clamp(o.NumberDigits, 1, 9), CultureInfo.InvariantCulture);
            stem = o.NumberPosition == NumberPosition.Prefix ? digits + o.NumberSeparator + stem : stem + o.NumberSeparator + digits;
        }

        if (o.LowercaseExtension) ext = ext.ToLowerInvariant();
        if (o.FixMessyNames && WindowsNames.IsReserved(stem + ext)) stem += " file";
        return stem + ext;
    }

    /// <summary>"photo.JPG" → ("photo", ".JPG"); "backup.tar.gz" → ("backup", ".tar.gz"); ".gitignore" → (".gitignore", "").</summary>
    public static (string Stem, string Ext) SplitName(string name)
    {
        foreach (var dbl in DoubleExtensions)
            if (name.Length > dbl.Length && name.EndsWith(dbl, StringComparison.OrdinalIgnoreCase))
                return (name[..^dbl.Length], name[^dbl.Length..]);
        var ext = Path.GetExtension(name);
        if (ext.Length == 0 || ext.Length == name.Length || ext.Contains(' ')) return (name, "");
        return (name[..^ext.Length], ext);
    }

    [GeneratedRegex(@"\s*\(\d+\)$")] private static partial Regex CopyNumber();
    [GeneratedRegex(@"\s*-\s*Copy(\s*\(\d+\))?$", RegexOptions.IgnoreCase)] private static partial Regex DashCopy();
    [GeneratedRegex(@"\s*-\s*Shortcut$", RegexOptions.IgnoreCase)] private static partial Regex DashShortcut();
    [GeneratedRegex(@"^Copy(\s*\(\d+\))?\s+of\s+", RegexOptions.IgnoreCase)] private static partial Regex CopyOf();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex Spaces();

    /// <summary>
    /// Plan §9 "fix messy names" on the name without its extension: drop " (1)", " - Copy", "Copy of ",
    /// " - Shortcut"; "_" and "." become spaces; illegal characters dropped; spaces squashed; no trailing dots/spaces.
    /// Idempotent: fixing an already fixed name changes nothing.
    /// </summary>
    public static string FixMessyStem(string stem)
    {
        if (stem.StartsWith('.') && !stem[1..].Contains('.')) return stem; // dotfile like ".gitignore"
        var s = stem;
        string before;
        do
        {
            before = s;
            s = CopyNumber().Replace(s, "");
            s = DashCopy().Replace(s, "");
            s = DashShortcut().Replace(s, "");
            s = CopyOf().Replace(s, "");
        } while (s != before);

        s = s.Replace('_', ' ').Replace('.', ' ');
        s = WindowsNames.RemoveIllegal(s);
        s = Spaces().Replace(s, " ").Trim().TrimEnd('.', ' ');
        return s.Length == 0 ? "Untitled" : s;
    }

    /// <summary>Two rows wanting the same name, or a name already used by a file that isn't moving away.</summary>
    private static IReadOnlyList<RenameRow> MarkCollisions(List<RenameRow> rows, Func<string, bool> pathExists)
    {
        var leaving = new HashSet<string>(rows.Where(r => r.CanRename).Select(r => r.SourcePath), StringComparer.OrdinalIgnoreCase);
        var targets = rows.Where(r => r.CanRename).GroupBy(r => r.NewPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var unchanged = new HashSet<string>(rows.Where(r => !r.CanRename).Select(r => r.SourcePath), StringComparer.OrdinalIgnoreCase);

        return rows.Select(r =>
        {
            if (!r.CanRename) return r;
            var target = r.NewPath;
            if (targets[target] > 1) return r with { Problem = "Another file in this batch would get the same name." };
            if (!r.IsCaseOnly && (unchanged.Contains(target) || (pathExists(target) && !leaving.Contains(target))))
                return r with { Problem = "A file with this name is already there." };
            var warning = target.Length > WindowsNames.ClassicMaxPath ? "The full path is over 260 characters. Some apps can't open it." : null;
            return r with { Warning = warning };
        }).ToList();
    }

    /// <summary>
    /// RenamePath items for the ticked rows. When a row's new name is currently another row's old name
    /// (a swap or a chain), both phases go through a temporary name so nothing is ever overwritten.
    /// </summary>
    public static IReadOnlyList<PlanItem> ToPlanItems(IEnumerable<RenameRow> selected)
    {
        var rows = selected.Where(r => r.CanRename).ToList();
        var sources = new HashSet<string>(rows.Select(r => r.SourcePath), StringComparer.OrdinalIgnoreCase);
        var direct = new List<PlanItem>();
        var phase1 = new List<PlanItem>();
        var phase2 = new List<PlanItem>();
        foreach (var r in rows)
        {
            if (!r.IsCaseOnly && sources.Contains(r.NewPath))
            {
                var temp = Path.Combine(Path.GetDirectoryName(r.SourcePath)!, $"~pctidy-{Guid.NewGuid().ToString("N")[..8]}-{r.OldName}");
                phase1.Add(Item(r.SourcePath, temp, $"Rename \"{r.OldName}\" (step 1 of 2)"));
                phase2.Add(Item(temp, r.NewPath, $"Rename \"{r.OldName}\" to \"{r.NewName}\" (step 2 of 2)", expectSize: false));
            }
            else direct.Add(Item(r.SourcePath, r.NewPath, $"Rename \"{r.OldName}\" to \"{r.NewName}\""));
        }
        return phase1.Concat(direct).Concat(phase2).ToList();

        static PlanItem Item(string from, string to, string description, bool expectSize = true)
        {
            FileInfo? info = null;
            if (expectSize) { try { info = new FileInfo(from); if (!info.Exists) info = null; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return new PlanItem
            {
                Kind = ActionKind.RenamePath,
                FeatureId = OrganizeModule.FeatureId,
                Description = description,
                SourcePath = from,
                DestinationPath = to,
                ExpectedSize = info?.Length ?? 0,
                ExpectedLastWriteUtc = info?.LastWriteTimeUtc,
                ClashPolicy = NameClashPolicy.Skip,
            };
        }
    }
}
