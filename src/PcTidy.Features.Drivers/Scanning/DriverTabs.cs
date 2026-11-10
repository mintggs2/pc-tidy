namespace PcTidy.Features.Drivers.Scanning;

/// <summary>The five tabs of the Drivers page. Every scan group id starts with its tab id and a dot ("store.old").</summary>
public static class DriverTabs
{
    public const string Devices = "devices";
    public const string Problems = "problems";
    public const string Updates = "updates";
    public const string Store = "store";
    public const string Ghosts = "ghosts";

    public static readonly IReadOnlyList<(string Id, string Title)> All = new[]
    {
        (Devices, "Devices"),
        (Problems, "Problems"),
        (Updates, "Updates"),
        (Store, "Clean up driver store"),
        (Ghosts, "Old devices"),
    };

    public static string GroupId(string tab, string name) => tab + "." + name;

    /// <summary>The tab a group belongs to ("store.old" → "store").</summary>
    public static string Of(string groupId)
    {
        var dot = groupId.IndexOf('.');
        return dot > 0 ? groupId[..dot] : groupId;
    }
}

/// <summary>Options the Drivers page passes to its scanner (ScanContext.Options["drivers.options"]).</summary>
public sealed record DriverScanOptions
{
    public const string Key = "drivers.options";

    /// <summary>Let old packages of important classes (system, storage, network, Bluetooth, display, input) be picked one by one.</summary>
    public bool IncludeImportantClasses { get; init; }

    /// <summary>Ask Windows Update for driver updates (takes a few seconds and needs internet).</summary>
    public bool SearchWindowsUpdate { get; init; } = true;

    public static DriverScanOptions From(IReadOnlyDictionary<string, object> options)
        => options.TryGetValue(Key, out var o) && o is DriverScanOptions d ? d : new DriverScanOptions();
}
