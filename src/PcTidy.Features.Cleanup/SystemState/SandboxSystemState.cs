using System.Globalization;
using PcTidy.Core;

namespace PcTidy.Features.Cleanup.SystemState;

/// <summary>
/// Simulated Windows state for the testbed. Reads <c>&lt;sandbox C&gt;\Windows\PcTidy.Sandbox\system-state.ini</c>
/// (simple <c>key=value</c> lines) so tests can stage "restart pending", hibernation settings and so on
/// without reading the real PC's registry. Missing file or key = Windows defaults.
/// </summary>
/// <remarks>
/// Keys: RebootPending (0/1), HibernateEnabled (0/1), HiberFileType (1 = Reduced, 2 = Full), HasBattery (0/1),
/// TotalMemoryBytes, PagingFiles (lines separated by '|'), ReservedStorage (0/1), CompactOS (0/1).
/// The Recycle Bin is the sandbox's <c>C\$Recycle.Bin</c> folder; restore storage is <c>C\System Volume Information</c>.
/// </remarks>
internal sealed class SandboxSystemState : ISystemState
{
    private readonly IKnownLocations _k;
    private readonly IFileWalker _walker;
    private readonly Dictionary<string, string> _values;

    public SandboxSystemState(IKnownLocations k, IFileWalker walker)
    {
        _k = k;
        _walker = walker;
        _values = Load(StateFilePath(k));
    }

    public static string StateFilePath(IKnownLocations k) => Path.Combine(k.WindowsDir, "PcTidy.Sandbox", "system-state.ini");

    public bool IsRestartPending() => Flag("RebootPending") == true;

    public RecycleBinInfo? QueryRecycleBin(CancellationToken ct)
    {
        var bin = Path.Combine(_k.SystemDrive, "$Recycle.Bin");
        if (!Directory.Exists(bin)) return new RecycleBinInfo(0, 0);
        var stats = _walker.Walk(bin, new WalkOptions { IncludeSystem = true }, _ => true, ct);
        return new RecycleBinInfo(stats.Bytes, stats.Files);
    }

    public PowerState ReadPower()
        => new(Flag("HibernateEnabled"), Number("HiberFileType") is { } t ? (int)t : null, Flag("HasBattery"), Number("TotalMemoryBytes"));

    public IReadOnlyList<string>? PagingFiles()
        => _values.TryGetValue("PagingFiles", out var v) ? v.Split('|', StringSplitOptions.TrimEntries) : null;

    public bool? ReservedStorageOn() => Flag("ReservedStorage");

    public bool? CompactOsOn() => Flag("CompactOS");

    public long? RestoreStorageBytes(CancellationToken ct)
    {
        var svi = Path.Combine(_k.SystemDrive, "System Volume Information");
        if (!Directory.Exists(svi)) return null;
        return _walker.Walk(svi, new WalkOptions { IncludeSystem = true }, _ => true, ct).Bytes;
    }

    private bool? Flag(string key) => Number(key) is { } n ? n != 0 : null;

    private long? Number(string key)
        => _values.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static Dictionary<string, string> Load(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return values;
        foreach (var line in File.ReadLines(path))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0 || line.TrimStart().StartsWith('#')) continue;
            values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return values;
    }
}
