using System.Runtime.InteropServices;
using System.Text;
using static PcTidy.Features.Speed.Common.NativeMethods;

namespace PcTidy.Features.Speed.Sources;

/// <summary>Sign-in time of this Windows session (WTSQuerySessionInformation, read-only).</summary>
public sealed class WindowsSessionClock : ISessionClock
{
    private readonly Lazy<DateTime?> _signIn = new(ReadSignIn);

    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime? SignInUtc => _signIn.Value;

    private static DateTime? ReadSignIn()
    {
        try
        {
            if (!WTSQuerySessionInformation(WTS_CURRENT_SERVER_HANDLE, WTS_CURRENT_SESSION, WTSSessionInfo, out var buffer, out var bytes) || buffer == IntPtr.Zero)
                return null;
            try
            {
                if (bytes < Marshal.SizeOf<WTSINFOW>()) return null;
                var info = Marshal.PtrToStructure<WTSINFOW>(buffer);
                return info.LogonTime > 0 ? DateTime.FromFileTimeUtc(info.LogonTime) : null;
            }
            finally { WTSFreeMemory(buffer); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Power plans through powrprof (read-only: enumerate, read names, read the personality setting).</summary>
public sealed class WindowsPowerSource : IPowerSource
{
    public IReadOnlyList<PowerScheme> Schemes()
    {
        var list = new List<PowerScheme>();
        try
        {
            for (uint i = 0; i < 64; i++)
            {
                var guid = Guid.Empty;
                uint size = (uint)Marshal.SizeOf<Guid>();
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, ref guid, ref size) != 0) break;
                list.Add(new PowerScheme(guid, FriendlyName(guid) ?? guid.ToString(), Personality(guid)));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No power API (very unusual): the card says "couldn't check".
        }
        return list;
    }

    public Guid? ActiveScheme()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
            try { return Marshal.PtrToStructure<Guid>(ptr); }
            finally { LocalFree(ptr); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    public bool? OnBattery()
    {
        if (!GetSystemPowerStatus(out var status)) return null;
        return status.ACLineStatus switch { 0 => true, 1 => false, _ => null };
    }

    private static string? FriendlyName(Guid scheme)
    {
        uint size = 0;
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0 || size == 0 || size > 4096) return null;
        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return null;
        var name = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>Windows' personality value: 0 = high performance (also Ultimate), 1 = power saver, 2 = balanced.</summary>
    private static PowerPersonality Personality(Guid scheme)
    {
        var sub = NoSubgroupGuid;
        var setting = PowerSchemePersonalityGuid;
        if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var value) != 0) return PowerPersonality.Unknown;
        return value switch { 0 => PowerPersonality.HighPerformance, 1 => PowerPersonality.PowerSaver, 2 => PowerPersonality.Balanced, _ => PowerPersonality.Unknown };
    }
}

/// <summary>
/// Hardware-accelerated GPU scheduling support as the display driver reports it (WDDM 2.7 caps through D3DKMT).
/// Read-only: adapters are opened for a query and closed again.
/// </summary>
public sealed class D3dkmtGpuScheduling : IGpuSchedulingSource
{
    public GpuSchedulingSupport? Read()
    {
        try
        {
            var e = new D3DKMT_ENUMADAPTERS2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0 || e.NumAdapters > 64) return null;
            var itemSize = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
            e.pAdapters = Marshal.AllocHGlobal(itemSize * (int)e.NumAdapters);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return null;
                bool supported = false, enabled = false, any = false;
                for (var i = 0; i < e.NumAdapters; i++)
                {
                    var adapter = Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(e.pAdapters + i * itemSize);
                    var caps = Marshal.AllocHGlobal(sizeof(uint));
                    try
                    {
                        Marshal.WriteInt32(caps, 0);
                        var q = new D3DKMT_QUERYADAPTERINFO { hAdapter = adapter.hAdapter, Type = KMTQAITYPE_WDDM_2_7_CAPS, pPrivateDriverData = caps, PrivateDriverDataSize = sizeof(uint) };
                        if (D3DKMTQueryAdapterInfo(ref q) == 0)
                        {
                            any = true;
                            var bits = (uint)Marshal.ReadInt32(caps);
                            supported |= (bits & 0x1) != 0; // HwSchSupported
                            enabled |= (bits & 0x2) != 0;   // HwSchEnabled
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(caps);
                        var close = new D3DKMT_CLOSEADAPTER { hAdapter = adapter.hAdapter };
                        D3DKMTCloseAdapter(ref close);
                    }
                }
                return any ? new GpuSchedulingSupport(supported, enabled) : null;
            }
            finally { Marshal.FreeHGlobal(e.pAdapters); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>Store app display names for the current user (Windows' PackageManager, read-only).</summary>
public sealed class PackageStoreAppNames : IStoreAppNames
{
    private readonly Dictionary<string, (string, string?)?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public (string Name, string? Publisher)? Find(string packageFamilyName)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(packageFamilyName, out var hit)) return hit;
            (string, string?)? found = null;
            try
            {
                var package = new Windows.Management.Deployment.PackageManager().FindPackagesForUser(string.Empty, packageFamilyName).FirstOrDefault();
                if (package is not null && !string.IsNullOrWhiteSpace(package.DisplayName))
                    found = (package.DisplayName.Trim(), string.IsNullOrWhiteSpace(package.PublisherDisplayName) ? null : package.PublisherDisplayName.Trim());
            }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // Unknown package: the family name is shown instead.
            }
            _cache[packageFamilyName] = found;
            return found;
        }
    }
}

/// <summary>Resolves "@shell32.dll,-8506" style indirect strings (read-only resource lookup).</summary>
public static class IndirectStrings
{
    public static string? Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!value.StartsWith('@')) return value.Trim();
        try
        {
            var buffer = new StringBuilder(512);
            return SHLoadIndirectString(value, buffer, (uint)buffer.Capacity, IntPtr.Zero) == 0 && buffer.Length > 0 ? buffer.ToString().Trim() : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
