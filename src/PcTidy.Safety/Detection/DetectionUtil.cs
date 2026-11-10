using Microsoft.Win32;
using PcTidy.Safety.Paths;

namespace PcTidy.Safety.Detection;

/// <summary>Small helpers shared by the read-only detectors. None of them throw.</summary>
internal static class DetectionUtil
{
    /// <summary>Cleans a path found in a config file or the registry: quotes, env vars, slashes. Null if unusable.</summary>
    public static string? CleanPath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().Trim('"').Trim();
        if (s.Length == 0) return null;
        try { s = Environment.ExpandEnvironmentVariables(s); } catch (ArgumentException) { return null; }
        return SafePath.TryNormalize(s);
    }

    public static bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    /// <summary>A single folder name with no separators or ".." (so a config file can't point outside its library).</summary>
    public static bool IsPlainFolderName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.IndexOfAny(new[] { '\\', '/', ':' }) < 0
           && name.Trim('.', ' ').Length > 0
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Opens a registry key read-only; null if it (or the hive view) doesn't exist or access is denied.</summary>
    public static RegistryKey? OpenKey(RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            return baseKey.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static string? ReadString(RegistryKey? key, string name)
    {
        try { return key?.GetValue(name) as string; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static string[] SubKeyNames(RegistryKey? key)
    {
        try { return key?.GetSubKeyNames() ?? Array.Empty<string>(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return Array.Empty<string>(); }
    }

    /// <summary>Reads one string value from every subkey of <paramref name="parentPath"/> (e.g. each game under a launcher key).</summary>
    public static IEnumerable<string> ReadFromSubKeys(RegistryHive hive, RegistryView view, string parentPath, params string[] valueNames)
    {
        using var parent = OpenKey(hive, view, parentPath);
        foreach (var name in SubKeyNames(parent))
        {
            RegistryKey? sub;
            try { sub = parent!.OpenSubKey(name, writable: false); }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { continue; }
            using (sub)
            {
                foreach (var v in valueNames)
                {
                    var value = ReadString(sub, v);
                    if (!string.IsNullOrWhiteSpace(value)) { yield return value; break; }
                }
            }
        }
    }

    /// <summary>Runs a detector; any unexpected failure means "nothing found", never a crash.</summary>
    public static IReadOnlyList<string> Safe(Func<IEnumerable<string>> detector) => SafeList(detector);

    public static IReadOnlyList<T> SafeList<T>(Func<IEnumerable<T>> detector)
    {
        try { return detector().ToList(); }
        catch { return Array.Empty<T>(); }
    }
}
