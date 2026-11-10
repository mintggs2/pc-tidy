using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Features.PcCare.Model;
using PcTidy.Features.PcCare.Parsing;
using PcTidy.Features.PcCare.Scanning;

namespace PcTidy.Features.PcCare.Source;

/// <summary>
/// Reads PC care's facts from this PC, read-only: WMI, the registry (read access only), the event log, a few files
/// (browser preferences, minidump headers), the WLAN API and two fixed read-only tool listings. Every area is read on its
/// own and any failure there becomes "not available" for that area only.
/// </summary>
public sealed partial class WindowsPcCareSource : IPcCareSource
{
    private readonly IKnownLocations _locations;

    public WindowsPcCareSource(IKnownLocations locations) => _locations = locations;

    public async Task<PcCareSnapshot> ReadAsync(PcCareScanOptions options, IProgress<string>? progress, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        progress?.Report("Checking security…");
        var security = Safe(ReadSecurity);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Reading the crash history…");
        var crashes = Safe(() => ReadCrashes(now, options.CrashDays, ct));
        ct.ThrowIfCancellationRequested();
        progress?.Report("Looking at browsers…");
        var browsers = Safe(ReadBrowsers);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Listing restore points…");
        var restore = Safe(ReadRestore);
        var store = Safe(ReadStore);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Looking for leftovers…");
        var wlan = Safe(ReadWlan);
        var profiles = Safe(ReadProfiles);
        var features = Safe(ReadFeatures);
        ct.ThrowIfCancellationRequested();
        var network = Safe(ReadNetworkBasics);
        progress?.Report("Reading drive health…");
        var health = await SafeAsync(() => ReadHealthAsync(ct)).ConfigureAwait(false);

        WingetFacts? winget = null;
        if (options.CheckAppUpdates)
        {
            progress?.Report("Asking winget for app updates…");
            winget = await SafeAsync(() => ReadWingetAsync(ct)).ConfigureAwait(false);
        }
        if (options.RunDnsTest && network is not null)
        {
            progress?.Report("Timing DNS lookups…");
            network = network with { DnsTest = await SafeAsync(() => DnsSpeedTest.RunAsync(network.CurrentDnsServers, ct)).ConfigureAwait(false) };
        }

        return new PcCareSnapshot
        {
            TakenUtc = now, IsAdmin = IsElevated(), Winget = winget, Security = security, Crashes = crashes, Browsers = browsers,
            Restore = restore, Store = store, Wlan = wlan, Profiles = profiles, Features = features, Network = network, Health = health,
        };
    }

    public async Task<NetworkFacts?> TestDnsAsync(CancellationToken ct)
    {
        var basics = Safe(ReadNetworkBasics);
        if (basics is null) return null;
        return basics with { DnsTest = await DnsSpeedTest.RunAsync(basics.CurrentDnsServers, ct).ConfigureAwait(false) };
    }

    private async Task<WingetFacts> ReadWingetAsync(CancellationToken ct)
    {
        var exe = ReadOnlyTool.WingetPath();
        if (exe is null) return new WingetFacts(WingetState.NotInstalled, Array.Empty<WingetUpgrade>());
        var output = await ReadOnlyTool.RunAsync(ReadOnlyTool.Query.WingetUpgradeList, exe, ct).ConfigureAwait(false);
        return output is null
            ? new WingetFacts(WingetState.Failed, Array.Empty<WingetUpgrade>())
            : WingetTableParser.Parse(output.Text, output.ExitCode);
    }

    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (System.Security.SecurityException) { return false; }
    }

    // ------------------------------------------------------------------ shared read helpers

    /// <summary>Runs one area's reader; anything unexpected there means "not available" for that area only.</summary>
    private static T? Safe<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or IOException
                                       or System.Security.SecurityException or InvalidOperationException or ArgumentException
                                       or FormatException or DllNotFoundException or EntryPointNotFoundException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static async Task<T?> SafeAsync<T>(Func<Task<T>> read) where T : class
    {
        try { return await read().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or IOException
                                       or System.Security.SecurityException or InvalidOperationException or ArgumentException
                                       or FormatException or System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// A read-only WMI query with a timeout. Each row is mapped to a plain value inside <c>using</c>, so no WMI object
    /// (each holds a COM reference) outlives the call. Throws ManagementException (e.g. AccessDenied) for the caller to classify.
    /// </summary>
    internal static List<T> Wmi<T>(string scope, string wql, Func<ManagementBaseObject, T> map, int timeoutSeconds = 20)
    {
        var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(timeoutSeconds), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
        var list = new List<T>();
        using var results = searcher.Get();
        foreach (var o in results)
            using (o)
                list.Add(map(o));
        return list;
    }

    internal static bool IsAccessDenied(Exception ex)
        => ex is UnauthorizedAccessException
           || ex is ManagementException { ErrorCode: ManagementStatus.AccessDenied }
           || ex is COMException { HResult: unchecked((int)0x80070005) };

    internal static string? Str(ManagementBaseObject o, string name) => Prop(o, name)?.ToString();

    internal static object? Prop(ManagementBaseObject o, string name)
    {
        try { return o[name]; }
        catch (ManagementException) { return null; }
    }

    internal static DateTime? WmiDate(object? v)
    {
        if (v is not string s || s.Length < 14) return null;
        try
        {
            var local = ManagementDateTimeConverter.ToDateTime(s);
            return local.Year < 1980 ? null : local.ToUniversalTime();
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Opens a registry key for reading only (64-bit view unless asked). Null when missing or denied.</summary>
    internal static RegistryKey? OpenRead(RegistryHive hive, string path, RegistryView view = RegistryView.Registry64)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            return root.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    internal static object? RegValue(RegistryHive hive, string path, string name)
    {
        using var key = OpenRead(hive, path);
        try { return key?.GetValue(name); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    internal static int? RegInt(RegistryHive hive, string path, string name) => RegValue(hive, path, name) switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var n) => n,
        _ => null,
    };

    /// <summary>Reads a small text file without locking it for others (browsers keep their files open).</summary>
    internal static string? ReadSharedText(string path, long maxBytes = 32L << 20)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > maxBytes) return null;
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
}
