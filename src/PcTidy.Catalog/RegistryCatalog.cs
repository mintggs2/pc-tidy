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
    string? DataPattern = null);

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

    /// <summary>Finds the allowance for a key/value (case-insensitive key and name).</summary>
    public static RegistryAllowance? Find(RegistryHive hive, string keyPath, string? valueName)
        => All.FirstOrDefault(a => a.Hive == hive
            && string.Equals(a.KeyPath.TrimEnd('\\'), keyPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.ValueName ?? "", valueName ?? "", StringComparison.OrdinalIgnoreCase));
}
