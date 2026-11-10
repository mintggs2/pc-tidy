using System.Collections;
using Microsoft.Win32;

namespace PcTidy.Platform;

/// <summary>
/// Environment variables and the admin helper.
/// <list type="bullet">
/// <item><see cref="UnsafeVariables"/>: variables that make the .NET runtime load someone else's code (startup hooks,
/// profilers, extra dependency files). An elevated helper started with one of them would run that code as admin, so
/// PC Tidy doesn't ask for admin rights while one is set for the user, and the helper exits if it finds one.
/// Honest limit: the runtime acts on them before PC Tidy's own code starts, so the helper's own check can only stop
/// what comes after; the check before the admin prompt (and <c>StartupHookSupport=false</c> in the app) is what
/// really keeps them out.</item>
/// <item><see cref="CleanBlock"/>: the fixed environment every program started by the admin helper gets, instead of
/// the user's (whose PATH, PSModulePath or TEMP point into folders any program can change).</item>
/// </list>
/// </summary>
public static class HelperEnvironment
{
    private static readonly string[] Exact =
    {
        "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ADDITIONALPROBINGPATHS",
        "CORECLR_ENABLE_PROFILING", "COR_ENABLE_PROFILING", "COMPlus_EnableProfiling", "DOTNET_EnableProfiling",
        "DOTNET_DbgEnableMiniDump", "COMPlus_DbgEnableMiniDump", "DOTNET_DbgMiniDumpName", "COMPlus_DbgMiniDumpName",
    };

    private static readonly string[] Prefixes = { "CORECLR_PROFILER", "COR_PROFILER", "DOTNET_PROFILER", "COMPlus_Profiler" };

    /// <summary>True for a variable that would make the runtime load code from elsewhere.</summary>
    public static bool IsUnsafe(string name)
        => Exact.Contains(name, StringComparer.OrdinalIgnoreCase) || Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The names of unsafe variables that are set (non-empty), sorted.</summary>
    public static IReadOnlyList<string> UnsafeVariables(IDictionary variables)
        => variables.Keys.Cast<object>().Select(k => k?.ToString() ?? "")
            .Where(k => k.Length > 0 && IsUnsafe(k) && variables[k] is { } v && v.ToString()!.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Unsafe variables the elevated helper would start with: this process's own, plus the user's stored ones
    /// (HKCU\Environment and HKCU\Volatile Environment, which Windows uses to build an elevated process's environment).
    /// </summary>
    public static IReadOnlyList<string> UnsafeForElevation()
    {
        var all = new Hashtable(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) all[e.Key] = e.Value;
        foreach (var key in new[] { "Environment", "Volatile Environment" })
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(key);
                foreach (var name in k?.GetValueNames() ?? Array.Empty<string>())
                    if (IsUnsafe(name) && k!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is { } v) all[name] = v;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return UnsafeVariables(all);
    }

    /// <summary>Plain-English refusal for a list of variable names (null when the list is empty).</summary>
    public static string? Refusal(IReadOnlyList<string> names)
        => names.Count == 0 ? null
            : $"A setting on this PC ({string.Join(", ", names)}) would let another program run inside PC Tidy's admin helper. Remove it, then try again";

    private static string? _childTemp;

    /// <summary>TEMP/TMP for programs the helper starts: a folder in the helper's admin-only store (set at helper start).</summary>
    public static string ChildTempFolder
    {
        get => _childTemp ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        set
        {
            Directory.CreateDirectory(value);
            _childTemp = value;
        }
    }

    /// <summary>
    /// The complete environment for a program the admin helper starts. Machine-wide values come from Windows' own folder
    /// APIs, never from the inherited environment; PATH and PSModulePath only name Windows' own folders; nothing that
    /// tells .NET (DOTNET_, COR_, CORECLR_, COMPlus_) or PowerShell to load extra code is passed on.
    /// </summary>
    public static Dictionary<string, string> CleanBlock(string tempFolder)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Environment.SystemDirectory;
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["windir"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["ComSpec"] = Path.Combine(system32, "cmd.exe"),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD;.VBS;.VBE;.JS;.JSE;.WSF;.WSH;.MSC",
            ["PATH"] = string.Join(';', system32, windows, Path.Combine(system32, "Wbem"), Path.Combine(system32, "WindowsPowerShell", "v1.0")),
            ["PSModulePath"] = Path.Combine(system32, "WindowsPowerShell", "v1.0", "Modules"),
            ["TEMP"] = tempFolder,
            ["TMP"] = tempFolder,
            ["ProgramFiles"] = F(Environment.SpecialFolder.ProgramFiles),
            ["ProgramW6432"] = F(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = F(Environment.SpecialFolder.ProgramFilesX86),
            ["CommonProgramFiles"] = F(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramW6432"] = F(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramFiles(x86)"] = F(Environment.SpecialFolder.CommonProgramFilesX86),
            ["ProgramData"] = F(Environment.SpecialFolder.CommonApplicationData),
            ["ALLUSERSPROFILE"] = F(Environment.SpecialFolder.CommonApplicationData),
            ["PUBLIC"] = Path.GetDirectoryName(F(Environment.SpecialFolder.CommonDocuments)) ?? "",
            ["USERPROFILE"] = F(Environment.SpecialFolder.UserProfile),
            ["APPDATA"] = F(Environment.SpecialFolder.ApplicationData),
            ["LOCALAPPDATA"] = F(Environment.SpecialFolder.LocalApplicationData),
            ["COMPUTERNAME"] = Environment.MachineName,
            ["USERNAME"] = Environment.UserName,
            ["USERDOMAIN"] = Environment.UserDomainName,
            ["OS"] = "Windows_NT",
            ["PROCESSOR_ARCHITECTURE"] = Environment.Is64BitOperatingSystem ? "AMD64" : "x86",
            ["NUMBER_OF_PROCESSORS"] = Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var empty in block.Where(kv => kv.Value.Length == 0).Select(kv => kv.Key).ToList()) block.Remove(empty);
        return block;
    }

    /// <summary>Replaces a start info's whole environment with <paramref name="block"/>.</summary>
    internal static void Apply(System.Diagnostics.ProcessStartInfo psi, IReadOnlyDictionary<string, string> block)
    {
        psi.Environment.Clear();
        foreach (var (k, v) in block) psi.Environment[k] = v;
    }
}
