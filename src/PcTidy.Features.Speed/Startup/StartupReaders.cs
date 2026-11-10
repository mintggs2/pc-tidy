using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.Sources;

namespace PcTidy.Features.Speed.Startup;

/// <summary>
/// Reads every place things start from (Research §4): Run / RunOnce (per-user, machine, 32-bit), both Startup
/// folders, Store app startup tasks, logon/boot scheduled tasks outside \Microsoft, and auto-start services.
/// Read-only throughout.
/// </summary>
public sealed class StartupReaders(SpeedEnvironment env, IKnownLocations locations)
{
    private const string RunKeyUser = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyUser = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunKeyMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunKeyMachine32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyMachine32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";
    internal const string StoreTasksKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    internal const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    private sealed record RunLocation(RegistryHive Hive, string Key, string? Approved, bool Once, string Scope);

    private static readonly RunLocation[] RunLocations =
    {
        new(RegistryHive.CurrentUser, RunKeyUser, "Run", false, "you"),
        new(RegistryHive.LocalMachine, RunKeyMachine, "Run", false, "all users"),
        new(RegistryHive.LocalMachine, RunKeyMachine32, "Run32", false, "all users (32-bit)"),
        new(RegistryHive.CurrentUser, RunOnceKeyUser, null, true, "you"),
        new(RegistryHive.LocalMachine, RunOnceKeyMachine, null, true, "all users"),
        new(RegistryHive.LocalMachine, RunOnceKeyMachine32, null, true, "all users (32-bit)"),
    };

    public IEnumerable<StartupEntry> All(CancellationToken ct)
    {
        foreach (var e in RunKeys()) yield return e;
        ct.ThrowIfCancellationRequested();
        foreach (var e in StartupFolders()) yield return e;
        ct.ThrowIfCancellationRequested();
        foreach (var e in StoreApps()) yield return e;
        ct.ThrowIfCancellationRequested();
        foreach (var e in Tasks(ct)) yield return e;
        ct.ThrowIfCancellationRequested();
        foreach (var e in Services()) yield return e;
    }

    // ------------------------------------------------------------------ Run / RunOnce

    public IEnumerable<StartupEntry> RunKeys()
    {
        foreach (var loc in RunLocations)
        {
            var values = env.Registry.With(loc.Hive, loc.Key, k => k.Values().Where(v => v.Length > 0)
                .Select(v => (Name: v, Command: k.Text(v, expand: false))).ToList(), new List<(string, string?)>());
            foreach (var (name, command) in values)
            {
                if (string.IsNullOrWhiteSpace(command)) continue;
                var parsed = CommandLine.Parse(command, locations.WindowsDir, env.SearchPath);
                var approved = loc.Approved is null ? null : Approved(loc.Hive, loc.Approved, name);
                yield return new StartupEntry
                {
                    Source = loc.Once ? StartupSource.RunOnceKey : StartupSource.RunKey,
                    Name = name,
                    Command = command,
                    TargetPath = parsed.TargetFile,
                    Hive = loc.Hive,
                    Scope = loc.Scope,
                    ApprovedLocation = loc.Approved,
                    Where = env.Registry.Describe(loc.Hive, loc.Key),
                    Enabled = StartupApprovedState.IsEnabled(approved),
                    DisabledAtUtc = StartupApprovedState.DisabledAtUtc(approved),
                };
            }
        }
    }

    private byte[]? Approved(RegistryHive hive, string location, string name)
        => env.Registry.With(hive, (hive == RegistryHive.CurrentUser ? StartupApprovedState.BaseKey : StartupApprovedState.BaseKeyMachine) + "\\" + location,
            k => k.Bytes(name), null);

    // ------------------------------------------------------------------ Startup folders

    public IEnumerable<StartupEntry> StartupFolders()
    {
        var user = Path.Combine(locations.RoamingAppData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
        var common = Path.Combine(locations.ProgramData, "Microsoft", "Windows", "Start Menu", "Programs", "StartUp");
        foreach (var e in Folder(user, RegistryHive.CurrentUser, "you")) yield return e;
        foreach (var e in Folder(common, RegistryHive.LocalMachine, "all users")) yield return e;
    }

    private IEnumerable<StartupEntry> Folder(string folder, RegistryHive hive, string scope)
    {
        List<FileInfo> files;
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0) yield break;
            files = dir.EnumerateFiles().Where(f => !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                && (f.Attributes & FileAttributes.ReparsePoint) == 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }

        foreach (var f in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            string? target = f.FullName, args = null;
            if (f.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var link = ShortcutReader.Read(f.FullName);
                target = link?.TargetPath;
                args = link?.Arguments;
            }
            var approved = Approved(hive, "StartupFolder", f.Name);
            yield return new StartupEntry
            {
                Source = StartupSource.StartupFolder,
                Name = f.Name,
                Command = target is null ? null : (target.Contains(' ') ? $"\"{target}\"" : target) + (args is null ? "" : " " + args),
                TargetPath = target,
                Hive = hive,
                Scope = scope,
                ApprovedLocation = "StartupFolder",
                Where = folder,
                Enabled = StartupApprovedState.IsEnabled(approved),
                DisabledAtUtc = StartupApprovedState.DisabledAtUtc(approved),
                DisplayNameHint = Path.GetFileNameWithoutExtension(f.Name),
            };
        }
    }

    // ------------------------------------------------------------------ Store apps

    /// <summary>
    /// Store app startup tasks: <c>...\AppModel\SystemAppData\&lt;family&gt;\&lt;task&gt;\State</c>
    /// (0 off, 1 turned off by you, 2 on, 3 off by policy, 4 on by policy). Shown read-only: Windows' Settings switches them.
    /// </summary>
    public IEnumerable<StartupEntry> StoreApps()
    {
        var families = env.Registry.With(RegistryHive.CurrentUser, StoreTasksKey, k => k.SubKeys(), Array.Empty<string>());
        foreach (var family in families)
        {
            var path = StoreTasksKey + "\\" + family;
            var tasks = env.Registry.With(RegistryHive.CurrentUser, path,
                k => k.SubKeys().Select(t => (Task: t, State: k.Child(t) is { } c ? Using(c, x => x.Dword("State")) : null))
                    .Where(t => t.State is not null).ToList(), new List<(string, int?)>());
            foreach (var (task, state) in tasks)
            {
                var names = env.StoreApps.Find(family);
                yield return new StartupEntry
                {
                    Source = StartupSource.StoreApp,
                    Name = task,
                    Hive = RegistryHive.CurrentUser,
                    Scope = "you",
                    Where = env.Registry.Describe(RegistryHive.CurrentUser, path + "\\" + task),
                    Enabled = state is 2 or 4,
                    StoreState = state,
                    DisplayNameHint = names?.Name ?? FamilyToName(family),
                    PublisherHint = names?.Publisher,
                };
            }
        }
    }

    private static T Using<T>(Microsoft.Win32.RegistryKey key, Func<Microsoft.Win32.RegistryKey, T> read)
    {
        using (key) return read(key);
    }

    /// <summary>"SpotifyAB.SpotifyMusic_zpdnekdrzrea0" → "SpotifyMusic".</summary>
    internal static string FamilyToName(string family)
    {
        var name = family.Split('_')[0];
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    // ------------------------------------------------------------------ scheduled tasks

    public IEnumerable<StartupEntry> Tasks(CancellationToken ct)
    {
        foreach (var t in env.Tasks.StartupTasks(ct))
        {
            if (t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)) continue;
            var command = t.Command is null ? null : (t.Command.Contains(' ') && !t.Command.StartsWith('"') ? $"\"{t.Command}\"" : t.Command) + (string.IsNullOrWhiteSpace(t.Arguments) ? "" : " " + t.Arguments);
            yield return new StartupEntry
            {
                Source = StartupSource.ScheduledTask,
                Name = t.Path,
                TaskPath = t.Path,
                Command = command,
                TargetPath = command is null ? null : CommandLine.Parse(command, locations.WindowsDir, env.SearchPath).TargetFile,
                Hive = RegistryHive.LocalMachine,
                Scope = t.AtBoot ? "the PC (at boot)" : "sign-in",
                Where = "Task Scheduler: " + t.Path,
                Enabled = t.Enabled,
                DisplayNameHint = t.Path[(t.Path.LastIndexOf('\\') + 1)..],
                PublisherHint = t.Author,
            };
        }
    }

    // ------------------------------------------------------------------ services

    /// <summary>Automatic, non-driver services (eligibility is decided by <see cref="ServiceRules"/>).</summary>
    public IEnumerable<StartupEntry> Services()
    {
        var names = env.Registry.With(RegistryHive.LocalMachine, ServicesKey, k => k.SubKeys(), Array.Empty<string>());
        foreach (var name in names)
        {
            var info = env.Registry.With(RegistryHive.LocalMachine, ServicesKey + "\\" + name, k => ReadService(k), null);
            if (info is null) continue;
            var (type, start, delayed, imagePath, display, serviceDll) = info.Value;
            if (!ServiceRules.IsAutoStartProcessService(type, start)) continue;
            var parsed = CommandLine.Parse(imagePath, locations.WindowsDir, env.SearchPath);
            var target = serviceDll is not null ? CommandLine.NormalizePath(serviceDll, locations.WindowsDir, env.SearchPath) : parsed.TargetFile;
            yield return new StartupEntry
            {
                Source = StartupSource.Service,
                Name = name,
                Command = imagePath,
                TargetPath = target,
                Hive = RegistryHive.LocalMachine,
                Scope = "the PC",
                Where = env.Registry.Describe(RegistryHive.LocalMachine, ServicesKey + "\\" + name),
                Enabled = true,
                DisplayNameHint = env.IndirectString(display) ?? name,
                ServiceRunning = env.ServiceRunning(name) == true,
                Flag = delayed ? "Automatic (delayed start)" : "Automatic",
            };
        }
    }

    private static (int Type, int Start, bool Delayed, string? ImagePath, string? Display, string? ServiceDll)? ReadService(Microsoft.Win32.RegistryKey k)
    {
        if (k.Dword("Type") is not { } type || k.Dword("Start") is not { } start) return null;
        string? dll = null;
        using (var p = k.Child("Parameters")) dll = p?.Text("ServiceDll", expand: true);
        return (type, start, k.Dword("DelayedAutostart") is { } d && d != 0, k.Text("ImagePath", expand: true), k.Text("DisplayName"), dll);
    }
}
