using PcTidy.Core.Model;

namespace PcTidy.Catalog.Tweaks;

/// <summary>
/// Exactly the registry values the Tweaks section may write: every value an offered tweak sets (with the
/// tweak's data and the Windows default), and every value the bad-tweak finder may reset (with the Windows
/// default it writes, or delete-only). Built from the catalogs, so a value can't be written unless a catalog
/// row describes it. One entry per hive+key+value (entries from both catalogs are merged).
///
/// Two deliberate exceptions to "AllowedData lists the data":
///  - the windowed-games settings string (DirectXUserGlobalSettings) is merged with the user's own tokens,
///    so its data can't be listed: AllowedData is empty for that one value and a strict DataPattern is used;
///  - delete-only resets have AllowedData empty and AllowDelete = true (nothing is ever written there
///    except the recorded prior value on undo). <see cref="DeleteOnly"/> lists them so the guard can enforce it.
/// </summary>
public sealed class TweakRegistryAllowances : IRegistryAllowanceSource
{
    public const string TweakSourcePrefix = "tweaks.";
    public const string FinderSourcePrefix = "tweaks.badtweaks.";

    private static readonly Lazy<(IReadOnlyList<RegistryAllowance> All, IReadOnlySet<string> DeleteOnly)> _built = new(Build);

    public IEnumerable<RegistryAllowance> Allowances() => _built.Value.All;

    /// <summary>"HIVE\key\value" (upper-case) of entries that may only be deleted, never written.</summary>
    public static IReadOnlySet<string> DeleteOnly => _built.Value.DeleteOnly;

    public static string KeyOf(RegistryHive hive, string keyPath, string? valueName)
        => $@"{RegistryNames.Short(hive)}\{keyPath.Trim('\\')}\{valueName ?? ""}".ToUpperInvariant();

    private sealed class Entry
    {
        public required string Source;
        public required RegistryHive Hive;
        public required string KeyPath;
        public required string ValueName;
        public required RegistryKind Kind;
        public readonly List<string> Data = new();
        public bool AnyData;
        public bool Writes;
        public bool Deletes;
    }

    private static (IReadOnlyList<RegistryAllowance>, IReadOnlySet<string>) Build()
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        Entry Get(string source, RegistryHive hive, string key, string value, RegistryKind kind)
        {
            var k = KeyOf(hive, key, value);
            if (!map.TryGetValue(k, out var e))
                map[k] = e = new Entry { Source = source, Hive = hive, KeyPath = key, ValueName = value, Kind = kind };
            return e;
        }

        foreach (var t in TweakCatalog.All.Where(t => t.IsOffered))
        {
            foreach (var c in t.ActionableChanges)
            {
                switch (c)
                {
                    case RegistryValueChange r:
                        var e = Get(TweakSourcePrefix + t.Id, r.Hive, r.KeyPath, r.ValueName, r.Kind);
                        e.Writes = true;
                        Add(e, r.Data);
                        if (r.WindowsDefault is { } d) Add(e, d);
                        break;
                    case RegistryTokenChange tok:
                        var te = Get(TweakSourcePrefix + t.Id, tok.Hive, tok.KeyPath, tok.ValueName, RegistryKind.String);
                        te.Writes = true;
                        te.AnyData = true;
                        break;
                }
            }
        }

        foreach (var check in BadTweakCatalog.All)
        {
            foreach (var signal in check.Signals)
            {
                foreach (var fix in signal.Fixes)
                {
                    var probe = signal.Probe as RegistryProbe;
                    switch (fix)
                    {
                        case RegistrySetFix set when probe is not null:
                            var se = Get(FinderSourcePrefix + check.Id, probe.Hive, probe.KeyPath, probe.ValueName, set.Kind);
                            se.Writes = true;
                            Add(se, set.Data);
                            break;
                        case RegistryWriteFix write:
                            var we = Get(FinderSourcePrefix + check.Id, write.Hive, write.KeyPath, write.ValueName, write.Kind);
                            we.Writes = true;
                            Add(we, write.Data);
                            break;
                        case RegistryDeleteFix del:
                            var hive = del.Hive ?? probe?.Hive;
                            var key = del.KeyPath ?? probe?.KeyPath;
                            var value = del.ValueName ?? probe?.ValueName;
                            if (hive is null || key is null || value is null) break; // custom probes always name their target
                            Get(FinderSourcePrefix + check.Id, hive.Value, key, value, del.ValueName is null && probe is not null ? probe.Kind : del.Kind).Deletes = true;
                            break;
                    }
                }
            }
        }

        var all = map.Values
            .OrderBy(e => e.Hive).ThenBy(e => e.KeyPath, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.ValueName, StringComparer.OrdinalIgnoreCase)
            .Select(e => new RegistryAllowance(e.Source, e.Hive, e.KeyPath, e.ValueName, e.Kind,
                e.AnyData ? Array.Empty<string>() : e.Data.ToArray(),
                // Only values a bad-tweak repair removes may be deleted. Undo of a set restores the recorded prior
                // state inside the handler, not through a new plan item.
                AllowDelete: e.Deletes,
                NeedsAdmin: e.Hive == RegistryHive.LocalMachine || RegistryNames.IsPolicyKey(e.KeyPath),
                // Token strings (DirectXUserGlobalSettings): only "Name=Value;" pairs, short, letters/digits only.
                DataPattern: e.AnyData ? @"^([A-Za-z0-9]{1,64}=[A-Za-z0-9]{1,32};){0,32}$" : null))
            .ToList();
        var deleteOnly = map.Where(kv => !kv.Value.Writes).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (all, deleteOnly);
    }

    private static void Add(Entry e, string data)
    {
        if (!e.Data.Contains(data, StringComparer.Ordinal)) e.Data.Add(data);
    }
}
