using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Principal;
using Microsoft.Win32;
using PcTidy.Catalog.PcCare;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Parsing;

namespace PcTidy.Features.PcCare.Source;

/// <summary>Restore points, Store, user profiles, optional features, network basics and health facts. All read-only.</summary>
public sealed partial class WindowsPcCareSource
{
    // ------------------------------------------------------------------ restore points

    private RestoreFacts ReadRestore()
    {
        bool? protection = RegInt(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", "RPSessionInterval") switch
        {
            0 => false,
            null => null,
            _ => true,
        };
        if (RegInt(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR") == 1) protection = false;

        List<RestorePoint> points;
        try
        {
            points = Wmi(@"root\default", "SELECT SequenceNumber, Description, CreationTime, RestorePointType FROM SystemRestore",
                    o => new RestorePoint(
                        Prop(o, "SequenceNumber") switch { uint u => u, int i => (uint)i, _ => 0u },
                        Str(o, "Description") ?? "",
                        WmiDate(Prop(o, "CreationTime")) ?? DateTime.MinValue,
                        Prop(o, "RestorePointType") switch { uint u => (int)u, int i => i, _ => 0 }))
                .Where(p => p.SequenceNumber > 0).ToList();
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            return new RestoreFacts(ReadAccess.NeedsAdmin, Array.Empty<RestorePoint>(), null, protection);
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            return new RestoreFacts(ReadAccess.NotAvailable, Array.Empty<RestorePoint>(), null, protection);
        }
        return new RestoreFacts(ReadAccess.Ok, points, Safe(ReadShadowStorage), protection);
    }

    /// <summary>Shadow storage on the system drive: Win32_ShadowStorage (admin) joined to Win32_Volume by device id.</summary>
    private ShadowStorage? ReadShadowStorage()
    {
        var letter = _locations.SystemDrive[..2];
        var volumes = Wmi(@"root\cimv2", $"SELECT DeviceID, Capacity FROM Win32_Volume WHERE DriveLetter = '{letter}'",
            o => (DeviceId: Str(o, "DeviceID") ?? "", Capacity: Prop(o, "Capacity") switch { ulong u => (long)Math.Min(u, long.MaxValue), _ => 0L }));
        if (volumes.Count == 0) return null;
        var (deviceId, capacity) = volumes[0];
        var storages = Wmi(@"root\cimv2", "SELECT UsedSpace, MaxSpace, Volume FROM Win32_ShadowStorage",
            o => (Volume: Str(o, "Volume") ?? "", Used: Prop(o, "UsedSpace") as ulong?, Max: Prop(o, "MaxSpace") as ulong?));
        foreach (var o in storages)
        {
            // Volume is a reference string: Win32_Volume.DeviceID="\\\\?\\Volume{...}\\"
            var reference = o.Volume.Replace(@"\\", @"\", StringComparison.Ordinal);
            if (!reference.Contains(deviceId, StringComparison.OrdinalIgnoreCase)) continue;
            long used = o.Used is { } u ? (long)Math.Min(u, long.MaxValue) : 0;
            long max = o.Max is { } m ? (long)Math.Min(m, (ulong)Math.Max(capacity, 0)) : 0;
            return new ShadowStorage(letter + "\\", used, max, capacity);
        }
        return null;
    }

    // ------------------------------------------------------------------ Store

    private static StoreFacts ReadStore()
    {
        using var packages = OpenRead(RegistryHive.CurrentUser,
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        bool installed = packages?.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.WindowsStore_", StringComparison.OrdinalIgnoreCase)) ?? false;
        return new StoreFacts(installed, installed ? Scanning.PcCareResultBuilder.StorePackageFamily : null);
    }

    // ------------------------------------------------------------------ user profiles

    private static ProfileFacts ReadProfiles()
    {
        string? current;
        using (var id = WindowsIdentity.GetCurrent()) current = id.User?.Value;

        // This PC's own accounts: their SIDs, and the machine's account-domain prefix (the SID without its last part).
        var local = Wmi(@"root\cimv2", "SELECT SID FROM Win32_UserAccount WHERE LocalAccount = TRUE", o => Str(o, "SID"))
            .Where(s => s is not null).Select(s => s!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var machinePrefix = local.Select(Prefix).GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        var profiles = Wmi(@"root\cimv2", "SELECT SID, LocalPath, Loaded, Special, LastUseTime FROM Win32_UserProfile",
                o =>
                {
                    var sid = Str(o, "SID") ?? "";
                    return new UserProfileFacts(sid, Str(o, "LocalPath") ?? "", Prop(o, "Loaded") as bool? ?? true, Prop(o, "Special") as bool? ?? true,
                        WmiDate(Prop(o, "LastUseTime")), local.Contains(sid) || Resolves(sid),
                        machinePrefix is not null && string.Equals(Prefix(sid), machinePrefix, StringComparison.OrdinalIgnoreCase));
                })
            .Where(p => p.Sid.Length > 0).ToList();
        return new ProfileFacts(current, profiles);
    }

    private static string Prefix(string sid)
    {
        var dash = sid.LastIndexOf('-');
        return dash > 0 ? sid[..dash] : sid;
    }

    /// <summary>True when Windows can still name the account (a deleted account no longer maps).</summary>
    private static bool Resolves(string sid)
    {
        try
        {
            _ = new SecurityIdentifier(sid).Translate(typeof(NTAccount));
            return true;
        }
        catch (IdentityNotMappedException) { return false; }
        catch (Exception ex) when (ex is ArgumentException or SystemException) { return true; } // can't tell: treat as existing (never offered)
    }

    // ------------------------------------------------------------------ optional features

    private FeatureFacts ReadFeatures()
    {
        var names = OptionalFeatureCatalog.All.Where(p => p.Kind == OptionalPartKind.Feature).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var features = Wmi(@"root\cimv2", "SELECT Name, InstallState FROM Win32_OptionalFeature WHERE InstallState = 1", o => Str(o, "Name"), 90)
            .Where(n => n is not null && names.Contains(n)).Select(n => new OptionalFeatureState(n!, true)).ToList();

        var caps = OptionalFeatureCatalog.All
            .Where(p => p.Kind == OptionalPartKind.Capability && p.Marker is not null && File.Exists(ExpandMarker(p.Marker)))
            .Select(p => p.Name).ToList();

        using var mui = OpenRead(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\MUI\UILanguages");
        var ui = CultureInfo.CurrentUICulture.Name;
        var languages = (mui?.GetSubKeyNames() ?? Array.Empty<string>())
            .OrderBy(l => string.Equals(l, ui, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        return new FeatureFacts(features, caps, languages);
    }

    private string ExpandMarker(string marker)
        => marker.Replace("{windir}", _locations.WindowsDir, StringComparison.OrdinalIgnoreCase)
                 .Replace("{programfiles}", _locations.ProgramFiles, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ network

    private static readonly string[] VpnWords = { "VPN", "WireGuard", "TAP-Windows", "OpenVPN", "Tailscale", "WARP", "Wintun", "ZeroTier", "Fortinet", "Cisco AnyConnect" };

    private static NetworkFacts ReadNetworkBasics()
    {
        var servers = new List<string>();
        bool vpn = false;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (VpnWords.Any(w => nic.Description.Contains(w, StringComparison.OrdinalIgnoreCase) || nic.Name.Contains(w, StringComparison.OrdinalIgnoreCase))) vpn = true;
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var dns in nic.GetIPProperties().DnsAddresses)
            {
                if (dns.AddressFamily != AddressFamily.InterNetwork) continue; // the speed test speaks IPv4 UDP
                var s = dns.ToString();
                if (!servers.Contains(s)) servers.Add(s);
            }
        }
        return new NetworkFacts(servers, null, vpn);
    }

    // ------------------------------------------------------------------ health report facts

    private async Task<HealthFacts> ReadHealthAsync(CancellationToken ct)
    {
        const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        int build = int.TryParse(RegValue(RegistryHive.LocalMachine, nt, "CurrentBuildNumber") as string, out var b) ? b : Environment.OSVersion.Version.Build;
        var product = RegValue(RegistryHive.LocalMachine, nt, "ProductName") as string;
        // Windows 11 still reports "Windows 10" in ProductName; the build number is the truth.
        if (product is not null && build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);

        return new HealthFacts
        {
            OsName = product,
            DisplayVersion = RegValue(RegistryHive.LocalMachine, nt, "DisplayVersion") as string,
            Build = build,
            LastBootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
            UpdatesPausedUntilUtc = IsoDate(RegValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "PauseUpdatesExpiryTime") as string),
            LastUpdateInstallUtc = IsoDate(RegValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install", "LastSuccessTime") as string),
            Disks = Safe(ReadDisks) ?? Array.Empty<DiskHealth>(),
            Gpus = await ReadGpusAsync(ct).ConfigureAwait(false),
            StartupItems = TryCountStartupItems(),
            OneDriveBackedUpFolders = OneDriveFolders(),
            FileHistoryConfigured = Directory.Exists(Path.Combine(_locations.LocalAppData, "Microsoft", "Windows", "FileHistory", "Configuration")),
        };
    }

    private static DateTime? IsoDate(string? s)
        => s is not null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static IReadOnlyList<DiskHealth> ReadDisks()
        => Wmi(@"root\Microsoft\Windows\Storage", "SELECT FriendlyName, MediaType, HealthStatus, Size FROM MSFT_PhysicalDisk",
            o => new DiskHealth(
                Str(o, "FriendlyName") ?? "Disk",
                Prop(o, "MediaType") switch { ushort m when m == 3 => "Hard drive", ushort m when m == 4 => "SSD", _ => "Drive" },
                Prop(o, "HealthStatus") switch { ushort h when h == 0 => "Healthy", ushort h when h == 1 => "Warning", ushort h when h == 2 => "Unhealthy", _ => "Unknown" },
                Prop(o, "Size") switch { ulong u => (long)Math.Min(u, long.MaxValue), _ => 0 }));

    /// <summary>GPU temperature from nvidia-smi only when it's installed (read-only query, no admin).</summary>
    private static async Task<IReadOnlyList<GpuReading>> ReadGpusAsync(CancellationToken ct)
    {
        var exe = ReadOnlyTool.NvidiaSmiPath();
        if (exe is null) return Array.Empty<GpuReading>();
        var output = await ReadOnlyTool.RunAsync(ReadOnlyTool.Query.NvidiaSmiTemperature, exe, ct).ConfigureAwait(false);
        if (output is not { ExitCode: 0 }) return Array.Empty<GpuReading>();
        return NvidiaSmiCsv.Parse(output.Text);
    }

    private int? TryCountStartupItems()
    {
        try { return CountStartupItems(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    private int CountStartupItems()
    {
        int n = 0;
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            using var run = OpenRead(hive, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", view);
            n += run?.GetValueNames().Count(v => v.Length > 0) ?? 0;
        }
        foreach (var folder in new[]
                 {
                     Path.Combine(_locations.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup"),
                     Path.Combine(_locations.ProgramData, "Microsoft", "Windows", "Start Menu", "Programs", "StartUp"),
                 })
        {
            try
            {
                if (Directory.Exists(folder)) n += Directory.EnumerateFiles(folder).Count(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>OneDrive folder backup: KfmFoldersProtectedNow is a bit mask of protected folders (0 or missing = off).</summary>
    private static int? OneDriveFolders()
    {
        using var accounts = OpenRead(RegistryHive.CurrentUser, @"Software\Microsoft\OneDrive\Accounts");
        if (accounts is null) return null;
        int best = 0;
        foreach (var name in accounts.GetSubKeyNames())
        {
            using var k = accounts.OpenSubKey(name, writable: false);
            if (k?.GetValue("KfmFoldersProtectedNow") is int mask && mask > 0) best = Math.Max(best, BitOperations.PopCount((uint)mask));
        }
        return best;
    }
}
