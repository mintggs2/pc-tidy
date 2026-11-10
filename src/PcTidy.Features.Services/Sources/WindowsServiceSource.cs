using System.Runtime.InteropServices;
using Microsoft.Win32;
using static PcTidy.Features.Services.Sources.NativeMethods;

namespace PcTidy.Features.Services.Sources;

/// <summary>
/// Lists every service and driver through the Service Control Manager (EnumServicesStatusEx) and reads each one's
/// configuration (QueryServiceConfig / QueryServiceConfig2: start type, delayed flag, triggers, description,
/// dependencies). Handles are opened with query rights only; nothing here can change a service. When Windows won't
/// let a standard account read a service's configuration, its registry key is read instead (also read-only).
/// Descriptions are shown, never parsed.
/// </summary>
public sealed class WindowsServiceSource : IServiceSource
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public IReadOnlyList<ServiceRecord> ReadAll(ICollection<string> problems, CancellationToken ct)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
        {
            problems.Add("Windows didn't allow listing services, so this list is empty.");
            return Array.Empty<ServiceRecord>();
        }
        try
        {
            var list = new List<ServiceRecord>();
            using var lm = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            var unreadable = 0;
            foreach (var basic in Enumerate(scm, SERVICE_WIN32 | SERVICE_DRIVER))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var record = Configure(scm, basic, lm, out var fromScm);
                    if (!fromScm) unreadable++;
                    list.Add(record);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException)
                {
                    unreadable++;
                    list.Add(basic);
                }
            }
            if (unreadable > 0)
                problems.Add($"{unreadable} service{(unreadable == 1 ? "'s" : "s'")} settings were read from the registry because Windows only shows them to admins.");
            AddTemplates(list, lm);
            return list;
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Name, display name, type, state and process id of every service (one SCM call per 64 KB).</summary>
    private static IEnumerable<ServiceRecord> Enumerate(IntPtr scm, uint types)
    {
        uint resume = 0;
        var size = 64 * 1024;
        var itemSize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESSW>();
        var results = new List<ServiceRecord>();
        for (var round = 0; round < 64; round++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var ok = EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, types, SERVICE_STATE_ALL, buffer, (uint)size, out var needed, out var count, ref resume, null);
                var error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != ERROR_MORE_DATA) break;
                for (var i = 0; i < count; i++)
                {
                    var e = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESSW>(buffer + i * itemSize);
                    var name = Marshal.PtrToStringUni(e.lpServiceName);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var s = e.ServiceStatusProcess;
                    results.Add(new ServiceRecord
                    {
                        Name = name,
                        DisplayName = Marshal.PtrToStringUni(e.lpDisplayName) ?? name,
                        ServiceType = (int)s.dwServiceType,
                        Start = -1,
                        State = MapState(s.dwCurrentState),
                        ProcessId = (int)s.dwProcessId,
                    });
                }
                if (ok) break;
                // More data: grow the buffer if one entry didn't fit, then continue from the resume handle.
                if (count == 0) size = (int)Math.Min(Math.Max(needed, (uint)size * 2), 4u * 1024 * 1024);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return results;
    }

    private static ServiceRecord Configure(IntPtr scm, ServiceRecord basic, RegistryKey lm, out bool fromScm)
    {
        using var key = lm.OpenSubKey($@"{ServicesKey}\{basic.Name}");
        var serviceDll = ReadServiceDll(key);
        var svc = OpenService(scm, basic.Name, SERVICE_QUERY_CONFIG);
        if (svc == IntPtr.Zero)
        {
            fromScm = false;
            return FromRegistry(basic, key, serviceDll);
        }
        try
        {
            var config = ReadConfig(svc);
            if (config is null)
            {
                fromScm = false;
                return FromRegistry(basic, key, serviceDll);
            }
            fromScm = true;
            var c = config.Value;
            return basic with
            {
                ServiceType = (int)c.Type,
                Start = (int)c.Start,
                ImagePath = c.BinaryPath,
                Account = c.Account,
                DependsOn = c.Dependencies,
                DisplayName = string.IsNullOrWhiteSpace(c.DisplayName) ? basic.DisplayName : c.DisplayName!,
                Description = ReadConfig2(svc, SERVICE_CONFIG_DESCRIPTION, b => Marshal.PtrToStringUni(Marshal.PtrToStructure<SERVICE_DESCRIPTIONW>(b).lpDescription)),
                DelayedAutostart = ReadConfig2Value(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, b => Marshal.PtrToStructure<SERVICE_DELAYED_AUTO_START_INFO>(b).fDelayedAutostart) ?? false,
                TriggerStart = (ReadConfig2Value(svc, SERVICE_CONFIG_TRIGGER_INFO, b => Marshal.PtrToStructure<SERVICE_TRIGGER_INFO>(b).cTriggers) ?? 0) > 0,
                ServiceDll = serviceDll,
            };
        }
        finally { CloseServiceHandle(svc); }
    }

    private readonly record struct Config(uint Type, uint Start, string? BinaryPath, string? Account, string? DisplayName, IReadOnlyList<string> Dependencies);

    private static Config? ReadConfig(IntPtr svc)
    {
        QueryServiceConfig(svc, IntPtr.Zero, 0, out var needed);
        if (needed == 0 || needed > 64 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfig(svc, buffer, needed, out _)) return null;
            var c = Marshal.PtrToStructure<QUERY_SERVICE_CONFIGW>(buffer);
            return new Config(c.dwServiceType, c.dwStartType, Text(c.lpBinaryPathName), Text(c.lpServiceStartName), Text(c.lpDisplayName), MultiString(c.lpDependencies));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? ReadConfig2(IntPtr svc, uint level, Func<IntPtr, string?> read)
    {
        QueryServiceConfig2(svc, level, IntPtr.Zero, 0, out var needed);
        if (needed == 0 || needed > 64 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfig2(svc, level, buffer, needed, out _)) return null;
            var s = read(buffer);
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static T? ReadConfig2Value<T>(IntPtr svc, uint level, Func<IntPtr, T> read) where T : struct
    {
        QueryServiceConfig2(svc, level, IntPtr.Zero, 0, out var needed);
        if (needed == 0 || needed > 64 * 1024) return null;
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            return QueryServiceConfig2(svc, level, buffer, needed, out _) ? read(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Fallback for services a standard account can't query: the same values from the service's registry key.</summary>
    private static ServiceRecord FromRegistry(ServiceRecord basic, RegistryKey? key, string? serviceDll)
    {
        if (key is null) return basic with { ServiceDll = serviceDll };
        var deps = key.GetValue("DependOnService") as string[] ?? Array.Empty<string>();
        var groups = (key.GetValue("DependOnGroup") as string[] ?? Array.Empty<string>()).Select(g => "+" + g);
        return basic with
        {
            Start = key.GetValue("Start") is int start ? start : -1,
            DelayedAutostart = key.GetValue("DelayedAutostart") is int d && d != 0,
            TriggerStart = HasSubKey(key, "TriggerInfo"),
            ImagePath = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            Account = key.GetValue("ObjectName") as string,
            DependsOn = deps.Concat(groups).Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
            ServiceDll = serviceDll,
        };
    }

    /// <summary>
    /// Per-user services: Windows lists the copies ("CDPUserSvc_1a2b3c"), which it recreates at every sign-in, but a
    /// start-type change only sticks on the template key ("CDPUserSvc"). Templates are added from the registry (read-only).
    /// </summary>
    private static void AddTemplates(List<ServiceRecord> list, RegistryKey lm)
    {
        var known = new HashSet<string>(list.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var instance in list.Where(s => (s.ServiceType & 0x80) != 0).ToList())
        {
            var cut = instance.Name.LastIndexOf('_');
            if (cut <= 0) continue;
            var template = instance.Name[..cut];
            if (!known.Add(template)) continue;
            try
            {
                using var key = lm.OpenSubKey($@"{ServicesKey}\{template}");
                if (key is null || key.GetValue("Type") is not int type || (type & 0x40) == 0) continue;
                var display = instance.DisplayName;
                var suffix = instance.Name[cut..];
                if (display.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) display = display[..^suffix.Length];
                list.Add(FromRegistry(new ServiceRecord { Name = template, DisplayName = display, ServiceType = type, Start = -1, State = ServiceRunState.Unknown },
                    key, ReadServiceDll(key)) with { Description = instance.Description });
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not readable: the per-user copies are still listed (shown only).
            }
        }
    }

    private static string? ReadServiceDll(RegistryKey? key)
    {
        if (key is null) return null;
        try
        {
            using var parameters = key.OpenSubKey("Parameters");
            return (parameters?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    ?? key.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames)) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static bool HasSubKey(RegistryKey key, string name)
    {
        try
        {
            using var sub = key.OpenSubKey(name);
            return sub is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string? Text(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        var s = Marshal.PtrToStringUni(p);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>A double-null-terminated list ("a\0b\0\0"), as SCM returns dependencies.</summary>
    private static IReadOnlyList<string> MultiString(IntPtr p)
    {
        var list = new List<string>();
        if (p == IntPtr.Zero) return list;
        for (var guard = 0; guard < 512; guard++)
        {
            var s = Marshal.PtrToStringUni(p);
            if (string.IsNullOrEmpty(s)) break;
            list.Add(s);
            p += (s.Length + 1) * 2;
        }
        return list;
    }

    private static ServiceRunState MapState(uint state) => state switch
    {
        1 => ServiceRunState.Stopped,
        2 => ServiceRunState.Starting,
        3 => ServiceRunState.Stopping,
        4 => ServiceRunState.Running,
        5 => ServiceRunState.Resuming,
        6 => ServiceRunState.Pausing,
        7 => ServiceRunState.Paused,
        _ => ServiceRunState.Unknown,
    };
}
