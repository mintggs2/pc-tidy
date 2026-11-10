using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Source;

/// <summary>
/// Reads driver information from the real PC. Everything here is read-only and works without admin:
/// pnputil enumerations (<see cref="PnpUtilReader"/>), WMI queries, a Windows Update search (search only, never install),
/// the Uninstall registry, and the size of driver-store folders. Each part fails soft (null / empty).
/// </summary>
public sealed class WindowsDriverSource : IDriverSource
{
    private static readonly TimeSpan WindowsUpdateTimeout = TimeSpan.FromMinutes(3);

    public Task<string?> GetDriverPackagesXmlAsync(CancellationToken ct) => PnpUtilReader.ReadXmlAsync(PnpUtilReader.Query.DriverPackages, ct);
    public Task<string?> GetDevicesXmlAsync(CancellationToken ct) => PnpUtilReader.ReadXmlAsync(PnpUtilReader.Query.Devices, ct);
    public Task<string?> GetProblemDevicesXmlAsync(CancellationToken ct) => PnpUtilReader.ReadXmlAsync(PnpUtilReader.Query.Problems, ct);

    /// <summary>With /properties for last-seen dates; older builds without /properties fall back to the plain list.</summary>
    public async Task<string?> GetDisconnectedDevicesXmlAsync(CancellationToken ct)
        => await PnpUtilReader.ReadXmlAsync(PnpUtilReader.Query.DisconnectedWithProperties, ct).ConfigureAwait(false)
           ?? await PnpUtilReader.ReadXmlAsync(PnpUtilReader.Query.Disconnected, ct).ConfigureAwait(false);

    /// <summary>Typed presence and dates from the Configuration Manager (see <see cref="DevNodeReader"/>).</summary>
    public Task<IReadOnlyDictionary<string, DeviceFacts>> GetDeviceFactsAsync(IReadOnlyCollection<string> instanceIds, CancellationToken ct)
        => Task.Run(() => DevNodeReader.Read(instanceIds, ct), ct);

    // ------------------------------------------------------------------ WMI

    public Task<IReadOnlyList<WmiProblemDevice>> GetWmiProblemDevicesAsync(CancellationToken ct) => Task.Run(() =>
        Query("SELECT PNPDeviceID, Name, PNPClass, ConfigManagerErrorCode, HardwareID FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0",
            o => o["PNPDeviceID"] is string { Length: > 0 } id
                ? new WmiProblemDevice(id, o["Name"] as string ?? "", o["PNPClass"] as string ?? "",
                    Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0, System.Globalization.CultureInfo.InvariantCulture),
                    (o["HardwareID"] as string[]) ?? Array.Empty<string>())
                : null, ct), ct);

    public Task<IReadOnlyList<WmiSignedDriver>> GetWmiSignedDriversAsync(CancellationToken ct) => Task.Run(() =>
        Query("SELECT DeviceID, DeviceName, DeviceClass, DriverProviderName, InfName, DriverVersion, DriverDate, IsSigned, Signer FROM Win32_PnPSignedDriver",
            o => o["DeviceID"] is string { Length: > 0 } id
                ? new WmiSignedDriver(id, o["DeviceName"] as string ?? "", o["DeviceClass"] as string ?? "",
                    o["DriverProviderName"] as string ?? "", o["InfName"] as string ?? "", o["DriverVersion"] as string ?? "",
                    CimDate(o["DriverDate"] as string), o["IsSigned"] as bool? ?? false, o["Signer"] as string ?? "")
                : null, ct), ct);

    public Task<BoardInfo?> GetBoardAsync(CancellationToken ct) => Task.Run(() =>
        Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard",
            o => new BoardInfo(o["Manufacturer"] as string ?? "", o["Product"] as string ?? "", o["Version"] as string ?? ""), ct)
            .FirstOrDefault(), ct);

    /// <summary>Runs a WMI query, mapping each row while it is still open; any WMI failure gives an empty list.</summary>
    private static IReadOnlyList<T> Query<T>(string wql, Func<ManagementBaseObject, T?> map, CancellationToken ct) where T : class
    {
        var rows = new List<T>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(wql),
                new System.Management.EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(60) });
            using var results = searcher.Get();
            foreach (var o in results)
            {
                ct.ThrowIfCancellationRequested();
                using (o)
                {
                    if (map(o) is { } row) rows.Add(row);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or TimeoutException)
        {
            // WMI broken or blocked: the section shows what pnputil gave.
        }
        return rows;
    }

    private static DateTime? CimDate(string? cim)
    {
        if (string.IsNullOrWhiteSpace(cim)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(cim).Date; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or ArgumentOutOfRangeException) { return null; }
    }

    // ------------------------------------------------------------------ Windows Update (search only)

    /// <summary>
    /// Windows Update Agent search for driver updates that aren't installed ("IsInstalled=0 and Type='Driver'").
    /// Search is read-only and works as a normal user. Installing is a separate, confirmed, admin plan step.
    /// </summary>
    public async Task<DriverUpdateSearch> SearchWindowsUpdateDriversAsync(CancellationToken ct)
    {
        var search = Task.Run(SearchWindowsUpdate);
        var finished = await Task.WhenAny(search, Task.Delay(WindowsUpdateTimeout, ct)).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (finished != search)
        {
            _ = search.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe a late failure
            return DriverUpdateSearch.Unavailable("Windows Update took too long to answer. Try again later.");
        }
        return await search.ConfigureAwait(false);
    }

    private static DriverUpdateSearch SearchWindowsUpdate()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: false);
            if (type is null) return DriverUpdateSearch.Unavailable("Windows Update isn't available on this PC.");
            dynamic session = Activator.CreateInstance(type)!;
            session.ClientApplicationID = "PC Tidy";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");
            var offers = new List<DriverUpdateOffer>();
            int count = result.Updates.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic u = result.Updates.Item(i);
                if ((bool)u.IsHidden) continue;
                string id = u.Identity.UpdateID;
                offers.Add(new DriverUpdateOffer
                {
                    UpdateId = id,
                    Title = (string?)u.Title ?? "Driver update",
                    DriverClass = (string?)u.DriverClass ?? "",
                    Model = (string?)u.DriverModel ?? "",
                    Manufacturer = (string?)u.DriverManufacturer ?? "",
                    Provider = (string?)u.DriverProvider ?? "",
                    DriverDate = ToDate(u.DriverVerDate),
                    HardwareId = (string?)u.DriverHardwareID ?? "",
                    DownloadBytes = ToLong(u.MaxDownloadSize),
                    IsOptional = (bool)u.BrowseOnly,
                });
            }
            return new DriverUpdateSearch(offers, null);
        }
        catch (COMException ex)
        {
            return DriverUpdateSearch.Unavailable(WindowsUpdateProblem(ex.HResult));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidComObjectException)
        {
            return DriverUpdateSearch.Unavailable("Windows Update couldn't be checked from PC Tidy. You can still check in Settings.");
        }
    }

    /// <summary>Friendly text for common WUA error codes (never the raw code alone).</summary>
    internal static string WindowsUpdateProblem(int hresult) => unchecked((uint)hresult) switch
    {
        0x8024402C or 0x80072EE7 or 0x80072EFD or 0x80072EE2 or 0x8024401C => "Windows Update couldn't be reached. Check your internet connection and try again.",
        0x8024002E or 0x80240025 or 0x8024500C => "Windows Update is turned off or managed by your organization on this PC.",
        0x80070422 => "The Windows Update service is turned off, so driver updates can't be checked.",
        _ => "Windows Update couldn't be checked right now. You can still check in Settings > Windows Update.",
    };

    private static DateTime? ToDate(object? value) => value is DateTime d && d.Year > 1601 ? d.Date : null;

    private static long ToLong(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException) { return 0; }
    }

    // ------------------------------------------------------------------ Uninstall registry (read-only)

    public Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(CancellationToken ct) => Task.Run(() =>
    {
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        var apps = new List<InstalledApp>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(uninstall, writable: false);
                if (key is null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var sub = key.OpenSubKey(name, writable: false);
                        if (sub?.GetValue("DisplayName") is string display && display.Length > 0)
                            apps.Add(new InstalledApp(display, sub.GetValue("Publisher") as string ?? ""));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return (IReadOnlyList<InstalledApp>)apps;
    }, ct);

    // ------------------------------------------------------------------ driver store (read-only)

    /// <summary>Total size of the package's FileRepository folder. Reparse points are never followed.</summary>
    public Task<long?> GetPackageSizeAsync(string publishedName, CancellationToken ct) => Task.Run(() =>
    {
        var folder = DriverStoreFolder(publishedName);
        if (folder is null) return (long?)null;
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(folder));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos())
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is FileInfo f) total += f.Length;
                    else if (entry is DirectoryInfo d) pending.Push(d);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
        }
        return total;
    }, ct);

    /// <summary>
    /// Newest "added to the driver store" time of any third-party package: the creation time of %windir%\INF\oem*.inf
    /// (Windows copies the INF there when a package is published). Null if the folder can't be read.
    /// </summary>
    public Task<DateTime?> GetLastDriverChangeUtcAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            DateTime? newest = null;
            foreach (var f in new DirectoryInfo(inf).EnumerateFiles("oem*.inf"))
            {
                ct.ThrowIfCancellationRequested();
                var t = f.CreationTimeUtc > f.LastWriteTimeUtc ? f.CreationTimeUtc : f.LastWriteTimeUtc;
                if (newest is null || t > newest) newest = t;
            }
            return newest;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }, ct);

    private static string? DriverStoreFolder(string publishedName)
    {
        if (!PcTidy.Catalog.Drivers.DriverClassRules.IsOemInf(publishedName)) return null;
        var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", publishedName);
        var buffer = new StringBuilder(1024);
        if (!NativeMethods.SetupGetInfDriverStoreLocationW(inf, IntPtr.Zero, null, buffer, buffer.Capacity, out _)) return null;
        var dir = Path.GetDirectoryName(buffer.ToString());
        return dir is not null && Directory.Exists(dir) ? dir : null;
    }

    private static class NativeMethods
    {
        /// <summary>Read-only: returns the driver-store path of a published INF.</summary>
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupGetInfDriverStoreLocationW(string fileName, IntPtr alternatePlatformInfo, string? localeName,
            StringBuilder returnBuffer, int returnBufferSize, out int requiredSize);
    }
}
