using System.Reflection;
using PcTidy.Core.Model;

namespace PcTidy.Catalog;

/// <summary>
/// One registry value PC Tidy may change (set or delete), with the values it may write.
/// The SafetyGuard allows registry PlanItems ONLY when they match an entry here exactly
/// (hive + key + value name, and the data is one of <see cref="AllowedData"/>, or the item restores a recorded prior value).
/// </summary>
public sealed record RegistryAllowance(
    string Source,              // e.g. "tweaks.file-extensions"
    RegistryHive Hive,
    string KeyPath,             // without hive
    string? ValueName,          // null = the whole key (key create/delete, e.g. the classic context menu CLSID key)
    RegistryKind Kind,
    IReadOnlyList<string> AllowedData, // empty = delete-only: the value may be removed, never set
    bool AllowDelete = true,
    bool NeedsAdmin = false,
    // For values whose data can't be listed (e.g. a settings string merged with the user's own tokens):
    // a strict regex the whole data must match. Only used when AllowedData is empty.
    string? DataPattern = null)
{
    /// <summary>
    /// For keys that can't be listed exactly (e.g. one network adapter's class subkey "...\{4d36e972-…}\0007"): a strict,
    /// anchored regex (^…$) the whole key path (without hive) must match. KeyPath stays a readable example for docs/tests.
    /// </summary>
    public string? KeyPathPattern { get; init; }

    /// <summary>For value names that can't be listed (e.g. per-app GPU preference, named after the game's exe path): an anchored regex.</summary>
    public string? ValueNamePattern { get; init; }

    /// <summary>
    /// Optional precondition: the value is changed (set or deleted) only while its current data is one of these (in
    /// <see cref="Kind"/>), for example IFEO CpuPriorityClass removed only while it is 4 (Realtime). Checked by the
    /// registry handler right before the change, where the value can be read. Null or empty = no precondition.
    /// </summary>
    public IReadOnlyList<string>? OnlyWhenDataIs { get; init; }

    internal bool Matches(RegistryHive hive, string keyPath, string? valueName)
    {
        if (Hive != hive) return false;
        var key = keyPath.TrimEnd('\\');
        var keyOk = KeyPathPattern is { } kp
            ? RegistryCatalog.FullMatch(kp, key)
            : string.Equals(KeyPath.TrimEnd('\\'), key, StringComparison.OrdinalIgnoreCase);
        if (!keyOk) return false;
        return ValueNamePattern is { } vp
            ? valueName is not null && RegistryCatalog.FullMatch(vp, valueName)
            : string.Equals(ValueName ?? "", valueName ?? "", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Implemented by a static-friendly class in each catalog subfolder (Tweaks, Speed, Games...).</summary>
public interface IRegistryAllowanceSource
{
    IEnumerable<RegistryAllowance> Allowances();
}

/// <summary>Aggregates every <see cref="IRegistryAllowanceSource"/> in this assembly (no shared file to edit).</summary>
public static class RegistryCatalog
{
    private static readonly Lazy<IReadOnlyList<RegistryAllowance>> _all = new(() =>
        typeof(RegistryCatalog).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IRegistryAllowanceSource).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .SelectMany(t => ((IRegistryAllowanceSource)Activator.CreateInstance(t)!).Allowances())
            .ToList());

    public static IReadOnlyList<RegistryAllowance> All => _all.Value;

    /// <summary>
    /// Finds the allowance for a key/value (case-insensitive key and name). Exact entries win; patterned entries
    /// (<see cref="RegistryAllowance.KeyPathPattern"/>, <see cref="RegistryAllowance.ValueNamePattern"/>) are checked after.
    /// </summary>
    public static RegistryAllowance? Find(RegistryHive hive, string keyPath, string? valueName)
        => Find(_testAllowances.Value is { Count: > 0 } extra ? All.Concat(extra) : All, hive, keyPath, valueName);

    /// <summary>The same lookup over any list (exact entries first, then patterned ones).</summary>
    internal static RegistryAllowance? Find(IEnumerable<RegistryAllowance> all, RegistryHive hive, string keyPath, string? valueName)
    {
        var list = all as IReadOnlyCollection<RegistryAllowance> ?? all.ToList();
        return list.FirstOrDefault(a => a.KeyPathPattern is null && a.ValueNamePattern is null && a.Matches(hive, keyPath, valueName))
               ?? list.FirstOrDefault(a => (a.KeyPathPattern is not null || a.ValueNamePattern is not null) && a.Matches(hive, keyPath, valueName));
    }

    private static readonly AsyncLocal<IReadOnlyList<RegistryAllowance>?> _testAllowances = new();

    /// <summary>
    /// Tests only (internal): extra allowances that <see cref="Find(RegistryHive, string, string?)"/> sees in the calling
    /// flow until the result is disposed. Nothing in the app can add one; other threads and tests never see them.
    /// </summary>
    internal static IDisposable WithTestAllowances(params RegistryAllowance[] extra)
    {
        var previous = _testAllowances.Value;
        _testAllowances.Value = extra;
        return new Restore(() => _testAllowances.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>Whole-string, case-insensitive match with a timeout. Patterns must be anchored (^…$); unanchored ones never match.</summary>
    internal static bool FullMatch(string pattern, string input) => StrictFullMatch(pattern, input, ignoreCase: true);

    /// <summary>
    /// The one strict whole-string match for allow-list patterns (key paths, value names, data). The pattern must be
    /// anchored (^…$, the final $ not escaped). Stricter than .NET's own reading of that pattern:
    /// <list type="bullet">
    /// <item>input with any control character (CR, LF, NUL, tab…) never matches, so a trailing line break can't slip past $;</item>
    /// <item>the end is matched with \z (the very end), not $ (which also matches before a final line break);</item>
    /// <item>\d means the ASCII digits 0-9 only, never other scripts' digits (\D: anything else);</item>
    /// <item>a 100 ms timeout, and a pattern .NET can't read, never match.</item>
    /// </list>
    /// </summary>
    public static bool StrictFullMatch(string pattern, string input, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(pattern) || input is null || !IsAnchored(pattern)) return false;
        if (input.Any(char.IsControl)) return false;
        if (AsciiDigits(pattern[1..^1]) is not { } inner) return false;
        var options = System.Text.RegularExpressions.RegexOptions.CultureInvariant
                      | (ignoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None);
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(input, "^(?:" + inner + @")\z", options, TimeSpan.FromMilliseconds(100));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return false; }
        catch (ArgumentException) { return false; } // not a valid pattern
    }

    /// <summary>Starts with ^ and ends with an unescaped $ (an even number of backslashes before it).</summary>
    private static bool IsAnchored(string pattern)
    {
        if (pattern.Length < 2 || pattern[0] != '^' || pattern[^1] != '$') return false;
        var slashes = 0;
        for (var i = pattern.Length - 2; i >= 0 && pattern[i] == '\\'; i--) slashes++;
        return slashes % 2 == 0;
    }

    /// <summary>
    /// The pattern with \d written as [0-9] (0-9 inside a [...] class) and \D as [^0-9]. Null (never matches) for \D inside
    /// a class, which has no simple ASCII form.
    /// </summary>
    private static string? AsciiDigits(string pattern)
    {
        var sb = new System.Text.StringBuilder(pattern.Length + 8);
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                var next = pattern[++i];
                if (next == 'd') sb.Append(inClass ? "0-9" : "[0-9]");
                else if (next == 'D')
                {
                    if (inClass) return null;
                    sb.Append("[^0-9]");
                }
                else sb.Append(c).Append(next);
                continue;
            }
            if (c == '[' && !inClass) inClass = true;
            else if (c == ']' && inClass) inClass = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
