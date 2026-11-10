using Microsoft.Win32;

namespace PcTidy.Safety.Rules;

/// <summary>What the guard could learn about a service's program.</summary>
public enum ServiceImageState
{
    /// <summary>The service's ImagePath was read.</summary>
    Found,
    /// <summary>No such service on this PC.</summary>
    Missing,
    /// <summary>The service exists but its ImagePath couldn't be read (or is empty).</summary>
    Unreadable,
}

/// <param name="UnderWindows">The program lives in the Windows folder: one of Windows' own services.</param>
public sealed record ServiceImage(ServiceImageState State, bool UnderWindows, string? ImagePath = null);

/// <summary>Where a service's program lives. Read-only; the guard uses it so only the Services section changes Windows' own services (SVC-1).</summary>
public interface IServiceImageSource
{
    ServiceImage Find(string serviceName);
}

/// <summary>
/// Reads <c>HKLM\SYSTEM\CurrentControlSet\Services\&lt;name&gt;\ImagePath</c> (64-bit view, read-only, no scan) and
/// says whether the program is under <c>%WINDIR%</c>. Understands the forms Windows stores: quoted paths with
/// arguments, <c>%SystemRoot%\…</c>, <c>\SystemRoot\…</c>, <c>\??\C:\…</c> and paths relative to the Windows folder
/// (<c>System32\drivers\x.sys</c>).
/// </summary>
public sealed class RegistryServiceImages : IServiceImageSource
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    public static RegistryServiceImages Instance { get; } = new();

    private readonly string _windowsDir;

    public RegistryServiceImages(string? windowsDir = null)
        => _windowsDir = (windowsDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd('\\');

    public ServiceImage Find(string serviceName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(ServicesKey + "\\" + serviceName, writable: false);
            if (key is null) return new ServiceImage(ServiceImageState.Missing, false);
            if (key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string raw || string.IsNullOrWhiteSpace(raw))
                return new ServiceImage(ServiceImageState.Unreadable, false);
            return new ServiceImage(ServiceImageState.Found, IsUnderWindows(raw, _windowsDir), raw);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return new ServiceImage(ServiceImageState.Unreadable, false);
        }
    }

    /// <summary>True when the program an ImagePath names is inside <paramref name="windowsDir"/>.</summary>
    public static bool IsUnderWindows(string imagePath, string windowsDir)
    {
        var p = Environment.ExpandEnvironmentVariables(imagePath.Trim());
        if (p.StartsWith('"'))
        {
            p = p[1..];
            var close = p.IndexOf('"');
            if (close >= 0) p = p[..close];
        }
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) return true;
        // No drive and no leading slash: Windows reads it relative to the Windows folder (drivers, some services).
        if (p.Length > 0 && !(p.Length >= 2 && p[1] == ':') && p[0] is not '\\' and not '/') return true;
        var win = windowsDir.TrimEnd('\\') + "\\";
        return p.StartsWith(win, StringComparison.OrdinalIgnoreCase);
    }
}
