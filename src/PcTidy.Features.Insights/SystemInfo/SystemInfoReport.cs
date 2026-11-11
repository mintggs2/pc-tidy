using System.Globalization;
using System.Text.RegularExpressions;
using PcTidy.Catalog.Insights;
using PcTidy.Core;
using PcTidy.Features.Insights.Source;

namespace PcTidy.Features.Insights.SystemInfo;

/// <summary>One label/value line in a System info category.</summary>
public sealed record SystemInfoRow(string Label, string Value);

/// <summary>A link from a category to the Insights card that explains it (RAM, Resizable BAR, refresh rate, Secure Boot/TPM...).</summary>
public sealed record SystemInfoLink(string Label, string CardId);

/// <summary>One category in the left list (CPU, Mainboard, Memory, Graphics, Storage, Network).</summary>
public sealed record SystemInfoCategory(string Id, string Title, string Icon, IReadOnlyList<SystemInfoRow> Rows, IReadOnlyList<SystemInfoLink> Links);

/// <summary>Everything the System info window shows: the Windows line for the header and the six categories, already masked.</summary>
public sealed record SystemInfoReport(string Windows, IReadOnlyList<SystemInfoCategory> Categories)
{
    public SystemInfoCategory? Find(string id) => Categories.FirstOrDefault(c => c.Id == id);
}

/// <summary>
/// Builds the System info report (layout spec §4.5) from read-only facts. Pure: tests feed it invented facts.
/// Every row passes <see cref="SystemInfoPrivacy"/> on the way out.
/// </summary>
public static class SystemInfoBuilder
{
    public const string Cpu = "cpu", Mainboard = "mainboard", Memory = "memory", Graphics = "graphics", Storage = "storage", Network = "network";

    private const string NotReported = "Windows didn't report this on this PC.";

    /// <param name="privateWords">The user name and computer name: replaced wherever they show up in a value.</param>
    public static SystemInfoReport Build(InventoryFacts inv, SecurityFacts security, IReadOnlyList<DisplayFacts> displays, WindowsIdentity windows,
        string? cpuName, IReadOnlyList<string> gpuNamesFallback, IEnumerable<string> privateWords)
    {
        var words = privateWords.Where(w => !string.IsNullOrWhiteSpace(w) && w.Trim().Length >= 3).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var categories = new[]
        {
            CpuCategory(inv, cpuName),
            BoardCategory(inv, security),
            MemoryCategory(inv),
            GraphicsCategory(inv, displays, gpuNamesFallback),
            StorageCategory(inv),
            NetworkCategory(inv),
        }.Select(c => c with { Rows = SystemInfoPrivacy.CleanRows(c.Rows, words) }).ToList();
        var win = windows.Build > 0 ? HardwareFamilies.BuildText(windows.Build, windows.Ubr) : "Windows";
        if (windows.EditionId.Length > 0) win += " · " + windows.EditionId;
        return new SystemInfoReport(SystemInfoPrivacy.Clean(win, words), categories);
    }

    private static SystemInfoCategory CpuCategory(InventoryFacts inv, string? cpuName)
    {
        var rows = new List<SystemInfoRow>();
        var name = inv.Cpu?.Name is { Length: > 0 } n ? n : cpuName;
        if (name is { Length: > 0 }) rows.Add(new("Processor", name));
        if (inv.Cpu is { } c)
        {
            if (c.Cores > 0) rows.Add(new("Cores", Num(c.Cores)));
            if (c.Threads > 0) rows.Add(new("Threads", Num(c.Threads)));
            if (c.MaxMhz > 0) rows.Add(new("Base speed", $"{(c.MaxMhz / 1000.0).ToString("0.0#", CultureInfo.CurrentCulture)} GHz"));
            if (c.L2Kb > 0) rows.Add(new("L2 cache", Cache(c.L2Kb)));
            if (c.L3Kb > 0) rows.Add(new("L3 cache", Cache(c.L3Kb)));
            if (c.Socket.Length > 0) rows.Add(new("Socket", c.Socket));
        }
        var links = new List<SystemInfoLink>();
        if (name is { Length: > 0 })
        {
            var family = HardwareFamilies.Classify(name);
            rows.Add(new("Family", HardwareFamilies.Describe(family)));
            if (HardwareFamilies.IsX3D(name))
                rows.Add(new("3D V-Cache", HardwareFamilies.IsDualCcdX3D(name) ? "Yes, on one of two core groups" : "Yes"));
            links.Add(new("Windows build and your processor", InsightsIds.WindowsBuild));
            if (HardwareFamilies.IsDualCcdX3D(name)) links.Add(new("Ryzen X3D with two core groups", InsightsIds.DualCcdX3D));
        }
        return new(Cpu, "CPU", "DeveloperBoard24", OrMissing(rows), links);
    }

    private static SystemInfoCategory BoardCategory(InventoryFacts inv, SecurityFacts s)
    {
        var rows = new List<SystemInfoRow>();
        if (inv.Board is { } b)
        {
            Add(rows, "PC maker", b.SystemMaker);
            Add(rows, "PC model", b.SystemModel);
            Add(rows, "Board maker", b.BoardMaker);
            Add(rows, "Board model", b.BoardModel);
            Add(rows, "Firmware (BIOS/UEFI)", string.Join(" ", new[] { b.BiosVendor, b.BiosVersion }.Where(x => x.Length > 0)));
            if (b.BiosDate is { } d) rows.Add(new("Firmware date", d.ToString("d MMM yyyy", CultureInfo.CurrentCulture)));
        }
        rows.Add(new("Secure Boot", OnOff(s.SecureBoot)));
        rows.Add(new("TPM", s.TpmNeedsAdmin ? "Needs admin to check" : s.TpmPresent switch
        {
            TriState.Yes => s.TpmVersion is { Length: > 0 } v ? $"Found (version {v})" : "Found",
            TriState.No => "Not found",
            _ => "Unknown",
        }));
        rows.Add(new("Memory integrity", OnOff(s.MemoryIntegrity)));
        var links = new[]
        {
            new SystemInfoLink("Secure Boot and TPM (anti-cheat requirements)", InsightsIds.Vanguard),
            new SystemInfoLink("Memory integrity and game speed", InsightsIds.MemoryIntegrity),
        };
        return new(Mainboard, "Mainboard", "Board24", rows, links);
    }

    private static SystemInfoCategory MemoryCategory(InventoryFacts inv)
    {
        var rows = new List<SystemInfoRow>();
        if (inv.Memory.Count > 0)
        {
            long total = inv.Memory.Sum(m => m.CapacityBytes);
            rows.Add(new("Installed", inv.MemorySlots > 0
                ? $"{Sizes.Format(total)} in {inv.Memory.Count} of {inv.MemorySlots} slots"
                : $"{Sizes.Format(total)} in {Plural(inv.Memory.Count, "module", "modules")}"));
            foreach (var m in inv.Memory)
            {
                var speed = m.ConfiguredMts > 0 ? $"{Num(m.ConfiguredMts)} MT/s" : "unknown speed";
                if (m.RatedMts > 0 && m.RatedMts != m.ConfiguredMts) speed += $" (reports up to {Num(m.RatedMts)} MT/s)";
                var maker = string.Join(" ", new[] { m.Maker, m.PartNumber }.Where(x => x.Length > 0 && !IsPlaceholder(x)));
                rows.Add(new(m.Slot.Length > 0 ? m.Slot : "Module", $"{Sizes.Format(m.CapacityBytes)}, {speed}{(maker.Length > 0 ? " · " + maker : "")}"));
            }
        }
        return new(Memory, "Memory", "Ram20", OrMissing(rows), new[] { new SystemInfoLink("Memory speed", InsightsIds.Ram) });
    }

    private static SystemInfoCategory GraphicsCategory(InventoryFacts inv, IReadOnlyList<DisplayFacts> displays, IReadOnlyList<string> fallback)
    {
        var rows = new List<SystemInfoRow>();
        var gpus = inv.Gpus.Count > 0 ? inv.Gpus : fallback.Select(n => new GpuInventory(n, "", null, 0)).ToList();
        foreach (var g in gpus)
        {
            var parts = new List<string>();
            if (g.MemoryBytes > 0) parts.Add(Sizes.Format(g.MemoryBytes));
            if (g.DriverVersion.Length > 0) parts.Add("driver " + g.DriverVersion + (g.DriverDate is { } d ? $" ({d.ToString("MMM yyyy", CultureInfo.CurrentCulture)})" : ""));
            rows.Add(new(g.Name, parts.Count > 0 ? string.Join(" · ", parts) : "Found"));
        }
        foreach (var d in displays)
            rows.Add(new(d.Name.Length > 0 ? d.Name : "Display", $"{d.Width} × {d.Height} at {d.CurrentHz} Hz (up to {d.MaxHzAtCurrentResolution} Hz)"));
        var links = new List<SystemInfoLink>
        {
            new("Resizable BAR", InsightsIds.ResizableBar),
            new("Display refresh rate", InsightsIds.RefreshRate),
        };
        if (gpus.Any(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))) links.Add(new("NVIDIA App game filters", InsightsIds.NvidiaApp));
        return new(Graphics, "Graphics", "Desktop24", OrMissing(rows), links);
    }

    private static SystemInfoCategory StorageCategory(InventoryFacts inv)
    {
        var rows = inv.Disks.Select(d =>
        {
            var media = d.Media switch { DiskMedia.Ssd => "SSD", DiskMedia.Hdd => "hard disk", _ => "disk" };
            var kind = d.Bus.Length > 0 ? $"{d.Bus} {media}" : media;
            return new SystemInfoRow(d.Model.Length > 0 ? d.Model : "Disk", $"{Sizes.Format(d.SizeBytes)} · {char.ToUpperInvariant(kind[0])}{kind[1..]}");
        }).ToList();
        return new(Storage, "Storage", "HardDrive20", OrMissing(rows), new[] { new SystemInfoLink("Games on a hard disk", InsightsIds.GamesOnHdd) });
    }

    private static SystemInfoCategory NetworkCategory(InventoryFacts inv)
    {
        var rows = inv.Network.Select(n =>
        {
            var kind = n.Kind switch { NetworkKind.WiFi => "Wi-Fi", NetworkKind.Ethernet => "Ethernet", NetworkKind.Bluetooth => "Bluetooth", _ => "Network" };
            var state = n.Connected ? "Connected" : "Not connected";
            var speed = n.Connected && n.SpeedBitsPerSecond > 0 ? " · " + LinkSpeed(n.SpeedBitsPerSecond) : "";
            return new SystemInfoRow(n.Name, $"{kind} · {state}{speed}");
        }).ToList();
        return new(Network, "Network", "Wifi124", OrMissing(rows), Array.Empty<SystemInfoLink>());
    }

    // ------------------------------------------------------------------ helpers

    private static void Add(List<SystemInfoRow> rows, string label, string value)
    {
        if (value.Length == 0) return;
        rows.Add(new(label, IsPlaceholder(value) ? "Not set by the maker" : value));
    }

    /// <summary>Text board makers leave in the firmware when they don't fill a field in.</summary>
    private static bool IsPlaceholder(string value)
        => Regex.IsMatch(value, @"^(to be filled by o\.?e\.?m\.?|default string|system (product name|manufacturer|version)|not applicable|undefined|none|0+|n/?a)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IReadOnlyList<SystemInfoRow> OrMissing(List<SystemInfoRow> rows) => rows.Count > 0 ? rows : new[] { new SystemInfoRow("Status", NotReported) };

    private static string OnOff(TriState s) => s switch { TriState.Yes => "On", TriState.No => "Off", _ => "Unknown" };

    private static string Num(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";

    private static string Cache(int kb) => kb >= 1024 && kb % 1024 == 0 ? $"{kb / 1024} MB" : $"{Num(kb)} KB";

    private static string LinkSpeed(long bps) => bps >= 1_000_000_000
        ? $"{(bps / 1_000_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)} Gbit/s"
        : $"{(bps / 1_000_000.0).ToString("0", CultureInfo.CurrentCulture)} Mbit/s";
}

/// <summary>
/// What System info never shows: serial numbers, MAC and IP addresses, UUIDs/GUIDs, product keys, asset tags, and user or
/// computer names (also inside paths). Rows with such a label are dropped; such text inside a value is replaced.
/// </summary>
public static partial class SystemInfoPrivacy
{
    public const string Hidden = "(hidden)";

    public static bool IsPrivateLabel(string label) => PrivateLabel().IsMatch(label);

    public static string Clean(string value, IReadOnlyCollection<string> privateWords)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var v = UserPath().Replace(value, m => m.Groups["root"].Value + "…");
        v = Mac().Replace(v, Hidden);
        v = GuidText().Replace(v, Hidden);
        v = ProductKey().Replace(v, Hidden);
        foreach (var w in privateWords.Where(w => w.Length >= 3))
            v = Regex.Replace(v, $@"(?<![A-Za-z0-9]){Regex.Escape(w)}(?![A-Za-z0-9])", "…", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return v;
    }

    public static IReadOnlyList<SystemInfoRow> CleanRows(IEnumerable<SystemInfoRow> rows, IReadOnlyCollection<string> privateWords)
        => rows.Where(r => !IsPrivateLabel(r.Label))
            .Select(r => new SystemInfoRow(Clean(r.Label, privateWords), Clean(r.Value, privateWords)))
            .ToList();

    [GeneratedRegex(@"serial|\bMAC\b|\bUUID\b|\bGUID\b|product\s*key|asset\s*tag|\bIP\b|IPv[46]|user\s*name|computer\s*name|device\s*name|\bSSID\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateLabel();
    [GeneratedRegex(@"(?<root>(?:[A-Za-z]:)?\\(?:Users|Documents and Settings)\\)[^\\/\r\n""]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserPath();
    [GeneratedRegex(@"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Mac();
    [GeneratedRegex(@"\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?", RegexOptions.CultureInvariant)]
    private static partial Regex GuidText();
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z0-9]{5}(?:-[A-Za-z0-9]{5}){4}(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex ProductKey();
}
