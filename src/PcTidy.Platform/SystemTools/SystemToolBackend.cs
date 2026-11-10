using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>One boot setting as the BCD store holds it (read through WMI, never from bcdedit's localized text).</summary>
public sealed record BcdElementValue(bool IsBoolean, ulong Value);

/// <summary>Result of installing Windows Update driver updates (WUA OperationResultCode: 2 ok, 3 ok with errors, 4 failed, 5 aborted).</summary>
public sealed record UpdateInstallResult(int Requested, int Found, int ResultCode, bool RebootRequired);

/// <summary>Result of DiRollbackDriver.</summary>
public sealed record DriverRollbackResult(bool Ok, int Error, bool RebootRequired);

/// <summary>Is a device connected? (Configuration Manager, never pnputil's localized text.)</summary>
public enum DevicePresence { Present, NotPresent, Missing, Unknown }

/// <summary>Restore point space on one drive (WMI Win32_ShadowStorage / Win32_ShadowCopy / Win32_Volume).</summary>
public sealed record ShadowStorageFacts(long VolumeBytes, long UsedBytes, long MaxBytes, int Snapshots);

/// <summary>
/// Everything <see cref="SystemToolHandler"/> needs from Windows, behind one interface: finding programs, running them,
/// and the few documented APIs it calls directly. Real: <see cref="WindowsSystemToolBackend"/>. Tests: a fake with
/// <see cref="TouchesRealSystem"/> = false that records what would run.
/// </summary>
public interface ISystemToolBackend
{
    bool TouchesRealSystem { get; }

    // ---- where things are (read-only)
    string LocalAppData { get; }
    string RoamingAppData { get; }
    string ProgramFiles { get; }
    string ProgramData { get; }
    string UserProfile { get; }
    bool FileExists(string path);
    bool DirectoryExists(string path);
    IReadOnlyList<string> SubDirectories(string path);
    /// <summary>The folders on PATH (process environment).</summary>
    IReadOnlyList<string> SearchPath();
    string? Variable(string name);

    // ---- programs
    Task<ProcessResult> RunAsync(ToolCommand command, TimeSpan timeout, string? logPath, CancellationToken ct);
    /// <summary>Starts a program that keeps running (OneDrive /background) without waiting.</summary>
    void Launch(ToolCommand command);
    bool IsProcessRunning(string processName);
    void OpenUri(string uri);

    // ---- state read before / after a tool
    HibernationState? ReadHibernation();
    bool CleanmgrHandlerExists(string handler);
    /// <summary>Selects exactly one Disk Cleanup handler in PC Tidy's sageset slot; Dispose puts every flag back.</summary>
    IDisposable SelectCleanmgrHandler(string handler);
    /// <summary>The element on {current}, or null if it isn't set. <paramref name="readable"/> = false when the store couldn't be read.</summary>
    BcdElementValue? ReadBcdElement(uint elementType, out bool readable);
    /// <summary>TCP receive window auto-tuning level name (disabled|highlyrestricted|restricted|normal|experimental), or null.</summary>
    string? ReadAutotuningLevel();
    DriveMedia MediaOf(char driveLetter);
    /// <summary>Whether the device is connected now, only known as a "ghost" entry, unknown to Windows, or couldn't be checked.</summary>
    DevicePresence DeviceState(string instanceId);
    /// <summary>Restore point space on the drive (no shadow storage yet = 0 used, 0 snapshots), or null if it couldn't be read.</summary>
    ShadowStorageFacts? ReadShadowStorage(char driveLetter);

    // ---- documented APIs
    bool FlushDnsCache();
    Task<UpdateInstallResult> InstallDriverUpdatesAsync(IReadOnlyList<string> updateIds, CancellationToken ct);
    DriverRollbackResult RollbackDriver(string instanceId);
    Task<RestorePointInfo> CreateRestorePointAsync(IKnownLocations locations, string description, CancellationToken ct);
}

public sealed class WindowsSystemToolBackend : ISystemToolBackend
{
    public static readonly WindowsSystemToolBackend Instance = new();

    public bool TouchesRealSystem => true;

    public string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IReadOnlyList<string> SubDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    public IReadOnlyList<string> SearchPath()
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"')).Where(p => PathArgs.IsPlainLocal(p, allowDriveRoot: true)).ToList();

    public string? Variable(string name) => Environment.GetEnvironmentVariable(name);

    public Task<ProcessResult> RunAsync(ToolCommand command, TimeSpan timeout, string? logPath, CancellationToken ct)
        => ProcessRunner.RunAsync(command.ExePath, command.Arguments, timeout, logPath, ct);

    public void Launch(ToolCommand command) => ProcessRunner.Launch(command.ExePath, command.Arguments);

    public bool IsProcessRunning(string processName)
    {
        var running = Process.GetProcessesByName(processName);
        foreach (var p in running) p.Dispose();
        return running.Length > 0;
    }

    public void OpenUri(string uri)
    {
        using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    public HibernationState? ReadHibernation() => WindowsToolState.ReadHibernation();
    public bool CleanmgrHandlerExists(string handler) => WindowsToolState.CleanmgrHandlerExists(handler);

    public IDisposable SelectCleanmgrHandler(string handler)
    {
        var priors = WindowsToolState.PrepareCleanmgrFlags(handler);
        return new Restorer(() => WindowsToolState.RestoreCleanmgrFlags(priors));
    }

    private sealed class Restorer(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>{current}: the boot entry Windows is running from.</summary>
    private const string CurrentBootEntry = "{fa926493-6f1c-4193-a414-58f0b2456d1e}";

    public BcdElementValue? ReadBcdElement(uint elementType, out bool readable)
    {
        readable = false;
        try
        {
            var scope = new ManagementScope(@"root\WMI");
            using var store = new ManagementObject(scope, new ManagementPath("BcdStore.FilePath=\"\""), null);
            using var openIn = store.GetMethodParameters("OpenObject");
            openIn["Id"] = CurrentBootEntry;
            using var opened = store.InvokeMethod("OpenObject", openIn, null);
            if (opened?["ReturnValue"] is not true) return null;
            using var entry = new ManagementObject(scope, new ManagementPath($"BcdObject.Id=\"{CurrentBootEntry}\",StoreFilePath=\"\""), null);
            using var getIn = entry.GetMethodParameters("GetElement");
            getIn["Type"] = elementType;
            using var got = entry.InvokeMethod("GetElement", getIn, null);
            readable = true;
            if (got?["ReturnValue"] is not true || got["Element"] is not ManagementBaseObject element) return null;
            using (element)
            {
                if (element.Properties.Cast<PropertyData>().Any(p => p.Name == "Boolean")) return new BcdElementValue(true, element["Boolean"] is true ? 1UL : 0UL);
                if (element.Properties.Cast<PropertyData>().Any(p => p.Name == "Integer") && ulong.TryParse(Convert.ToString(element["Integer"], System.Globalization.CultureInfo.InvariantCulture), out var n))
                    return new BcdElementValue(false, n);
                readable = false; // a type PC Tidy can't put back exactly
                return null;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            readable = false;
            return null;
        }
    }

    public string? ReadAutotuningLevel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT SettingName, AutoTuningLevelLocal FROM MSFT_NetTCPSetting");
            using var results = searcher.Get();
            string? any = null;
            foreach (ManagementObject mo in results)
            {
                using (mo)
                {
                    var name = Convert.ToByte(mo["AutoTuningLevelLocal"] ?? byte.MaxValue) switch
                    {
                        0 => "disabled", 1 => "highlyrestricted", 2 => "restricted", 3 => "normal", 4 => "experimental", _ => null,
                    };
                    if (name is null) continue;
                    if ((mo["SettingName"] as string)?.Equals("Internet", StringComparison.OrdinalIgnoreCase) == true) return name;
                    any ??= name;
                }
            }
            return any;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public DriveMedia MediaOf(char driveLetter)
    {
        try { return new WmiVolumeOptimizer().MediaOf(driveLetter); }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException) { return DriveMedia.Unknown; }
    }

    public DevicePresence DeviceState(string instanceId)
    {
        try
        {
            var present = CM_Locate_DevNodeW(out _, instanceId, CmLocateDevnodeNormal);
            if (present == CrSuccess) return DevicePresence.Present;
            if (present != CrNoSuchDevnode) return DevicePresence.Unknown;
            var known = CM_Locate_DevNodeW(out _, instanceId, CmLocateDevnodePhantom);
            return known == CrSuccess ? DevicePresence.NotPresent : known == CrNoSuchDevnode ? DevicePresence.Missing : DevicePresence.Unknown;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return DevicePresence.Unknown; }
    }

    public ShadowStorageFacts? ReadShadowStorage(char driveLetter)
    {
        try
        {
            string? deviceId = null;
            long capacity = 0;
            using (var volumes = new ManagementObjectSearcher(@"root\cimv2", $"SELECT DeviceID, Capacity FROM Win32_Volume WHERE DriveLetter = '{char.ToUpperInvariant(driveLetter)}:'"))
            using (var found = volumes.Get())
            {
                foreach (ManagementObject v in found)
                    using (v)
                    {
                        deviceId = v["DeviceID"] as string;
                        capacity = Convert.ToInt64(v["Capacity"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
                    }
            }
            if (deviceId is null || VolumeGuid(deviceId) is not { } guid) return null;

            long used = 0, max = 0;
            using (var storage = new ManagementObjectSearcher(@"root\cimv2", "SELECT Volume, UsedSpace, MaxSpace FROM Win32_ShadowStorage"))
            using (var found = storage.Get())
            {
                foreach (ManagementObject s in found)
                    using (s)
                    {
                        if (s["Volume"] is not string reference || !string.Equals(VolumeGuid(reference), guid, StringComparison.OrdinalIgnoreCase)) continue;
                        used += Convert.ToInt64(s["UsedSpace"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture);
                        max = Math.Max(max, Convert.ToInt64(s["MaxSpace"] ?? 0L, System.Globalization.CultureInfo.InvariantCulture));
                    }
            }

            var snapshots = 0;
            using (var copies = new ManagementObjectSearcher(@"root\cimv2", "SELECT VolumeName FROM Win32_ShadowCopy"))
            using (var found = copies.Get())
            {
                foreach (ManagementObject c in found)
                    using (c)
                        if (c["VolumeName"] is string name && string.Equals(VolumeGuid(name), guid, StringComparison.OrdinalIgnoreCase)) snapshots++;
            }
            return new ShadowStorageFacts(capacity, used, max, snapshots);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>"Volume{guid}" from a device id or a WMI reference to one.</summary>
    private static string? VolumeGuid(string text)
    {
        var start = text.IndexOf("Volume{", StringComparison.OrdinalIgnoreCase);
        var end = start < 0 ? -1 : text.IndexOf('}', start);
        return end > start ? text[start..(end + 1)] : null;
    }

    public bool FlushDnsCache() => DnsFlushResolverCache() != 0;

    public Task<UpdateInstallResult> InstallDriverUpdatesAsync(IReadOnlyList<string> updateIds, CancellationToken ct) => Task.Run(() =>
    {
        var wanted = new HashSet<string>(updateIds, StringComparer.OrdinalIgnoreCase);
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session") ?? throw new InvalidOperationException("Windows Update isn't available."))!;
        session.ClientApplicationID = "PC Tidy";
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic found = searcher.Search("IsInstalled=0 and Type='Driver'");
        dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        var count = 0;
        foreach (dynamic update in found.Updates)
        {
            // Only exactly the updates the user ticked, and never one that would need a licence click-through.
            if (wanted.Contains((string)update.Identity.UpdateID) && (bool)update.EulaAccepted) { collection.Add(update); count++; }
        }
        if (count == 0) return new UpdateInstallResult(wanted.Count, 0, 0, false);
        ct.ThrowIfCancellationRequested();
        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = collection;
        downloader.Download();
        dynamic installer = session.CreateUpdateInstaller();
        installer.Updates = collection;
        dynamic result = installer.Install();
        return new UpdateInstallResult(wanted.Count, count, (int)result.ResultCode, (bool)result.RebootRequired);
    }, ct);

    public DriverRollbackResult RollbackDriver(string instanceId)
    {
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) return new DriverRollbackResult(false, Marshal.GetLastWin32Error(), false);
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data)) return new DriverRollbackResult(false, Marshal.GetLastWin32Error(), false);
            if (!DiRollbackDriver(set, ref data, IntPtr.Zero, RollbackFlagNoUi, out var reboot)) return new DriverRollbackResult(false, Marshal.GetLastWin32Error(), false);
            return new DriverRollbackResult(true, 0, reboot);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    public Task<RestorePointInfo> CreateRestorePointAsync(IKnownLocations locations, string description, CancellationToken ct)
        => new RestorePointService(locations).EnsureAsync(description, ct);

    private const uint RollbackFlagNoUi = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    private const uint CmLocateDevnodeNormal = 0x0, CmLocateDevnodePhantom = 0x1;
    private const int CrSuccess = 0x0, CrNoSuchDevnode = 0xD;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("dnsapi.dll")] private static extern int DnsFlushResolverCache();
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);
    [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwndParent, uint flags, ref SP_DEVINFO_DATA data);
    [DllImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DiRollbackDriver(IntPtr set, ref SP_DEVINFO_DATA data, IntPtr hwndParent, uint flags, [MarshalAs(UnmanagedType.Bool)] out bool needReboot);
}
