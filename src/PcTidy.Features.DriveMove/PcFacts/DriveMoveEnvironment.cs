using Microsoft.Win32;
using PcTidy.Core;

namespace PcTidy.Features.DriveMove.PcFacts;

/// <summary>One installed WSL Linux distribution (read from the registry, never changed).</summary>
/// <param name="BasePath">Folder that holds its ext4.vhdx disk.</param>
public sealed record WslDistro(string Name, string BasePath);

/// <summary>
/// Facts about this PC outside the file system that the section reads: environment variables, OneDrive's backed-up
/// folders and WSL distributions. Read-only. The sandbox uses <see cref="EmptyDriveMoveEnvironment"/> (or a test's own),
/// so tests never depend on the real PC.
/// </summary>
public interface IDriveMoveEnvironment
{
    /// <summary>A per-user (then process) environment variable, or null when it isn't set.</summary>
    string? Variable(string name);

    /// <summary>OneDrive folders of every signed-in account (HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder).</summary>
    IReadOnlyList<string> OneDriveFolders();

    /// <summary>Installed WSL distributions (HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\*).</summary>
    IReadOnlyList<WslDistro> WslDistros();
}

/// <summary>Nothing set, nothing installed: what a fresh sandbox looks like.</summary>
public sealed class EmptyDriveMoveEnvironment : IDriveMoveEnvironment
{
    public static readonly EmptyDriveMoveEnvironment Instance = new();
    public string? Variable(string name) => null;
    public IReadOnlyList<string> OneDriveFolders() => Array.Empty<string>();
    public IReadOnlyList<WslDistro> WslDistros() => Array.Empty<WslDistro>();
}

/// <summary>The real PC, read through the registry and the process environment only (never localized command output).</summary>
public sealed class WindowsDriveMoveEnvironment : IDriveMoveEnvironment
{
    private const string OneDriveAccounts = @"Software\Microsoft\OneDrive\Accounts";
    private const string Lxss = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public string? Variable(string name)
    {
        try
        {
            var value = System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                        ?? System.Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : System.Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; }
    }

    public IReadOnlyList<string> OneDriveFolders()
        => ReadSubKeys(OneDriveAccounts, key => key.GetValue("UserFolder") as string is { Length: > 0 } folder ? folder : null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<WslDistro> WslDistros()
        => ReadSubKeys(Lxss, key =>
            key.GetValue("DistributionName") is string name && name.Length > 0 && key.GetValue("BasePath") is string basePath && basePath.Length > 0
                ? new WslDistro(name, StripLongPrefix(System.Environment.ExpandEnvironmentVariables(basePath)))
                : null).ToList();

    /// <summary>Reads one value from every subkey of an HKCU key (read-only handles; unreadable keys are skipped).</summary>
    private static IEnumerable<T> ReadSubKeys<T>(string path, Func<RegistryKey, T?> read) where T : class
    {
        var list = new List<T>();
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(path, writable: false);
            if (parent is null) return list;
            foreach (var name in parent.GetSubKeyNames())
            {
                try
                {
                    using var sub = parent.OpenSubKey(name, writable: false);
                    if (sub is not null && read(sub) is { } value) list.Add(value);
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return list;
    }

    private static string StripLongPrefix(string p) => p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;
}

/// <summary>Helper for tests and sandboxes: fixed answers.</summary>
public sealed class FixedDriveMoveEnvironment : IDriveMoveEnvironment
{
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> OneDrive { get; } = new();
    public List<WslDistro> Wsl { get; } = new();

    public string? Variable(string name) => Variables.TryGetValue(name, out var v) ? v : null;
    public IReadOnlyList<string> OneDriveFolders() => OneDrive;
    public IReadOnlyList<WslDistro> WslDistros() => Wsl;
}

internal static class PathText
{
    /// <summary>Normalised full path, or null for anything that isn't a usable absolute path.</summary>
    public static string? TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.IsPathFullyQualified(path) ? PathUtil.Normalize(path) : null; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
