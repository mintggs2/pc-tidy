using PcTidy.Features.Speed.Common;

namespace PcTidy.Features.Speed.Startup;

/// <summary>
/// Read-only "Advanced" startup places (Research §4): Winlogon Shell / Userinit (odd values are a malware sign)
/// and policy Run keys. Nothing here is ever offered as a change.
/// </summary>
public sealed class AdvancedReader(SpeedEnvironment env, IKnownLocations locations)
{
    internal const string WinlogonKeyMachine = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    internal const string WinlogonKeyUser = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    internal const string PolicyRunUser = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";
    internal const string PolicyRunMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run";

    public IEnumerable<StartupEntry> All()
    {
        var shell = env.Registry.With(RegistryHive.LocalMachine, WinlogonKeyMachine, k => k.Text("Shell", expand: false), null);
        if (shell is not null)
            yield return Winlogon("Shell", shell, RegistryHive.LocalMachine, WinlogonKeyMachine, IsDefaultShell(shell));
        var userinit = env.Registry.With(RegistryHive.LocalMachine, WinlogonKeyMachine, k => k.Text("Userinit", expand: false), null);
        if (userinit is not null)
            yield return Winlogon("Userinit", userinit, RegistryHive.LocalMachine, WinlogonKeyMachine, IsDefaultUserinit(userinit, locations.WindowsDir));
        // A per-user Shell value replaces Explorer for this account only; Windows doesn't set one.
        var userShell = env.Registry.With(RegistryHive.CurrentUser, WinlogonKeyUser, k => k.Text("Shell", expand: false), null);
        if (!string.IsNullOrWhiteSpace(userShell))
            yield return Winlogon("Shell (your account)", userShell, RegistryHive.CurrentUser, WinlogonKeyUser, isDefault: false);

        foreach (var (hive, key, scope) in new[] { (RegistryHive.CurrentUser, PolicyRunUser, "you"), (RegistryHive.LocalMachine, PolicyRunMachine, "all users") })
        {
            var values = env.Registry.With(hive, key, k => k.Values().Where(v => v.Length > 0).Select(v => (v, k.Text(v, expand: false))).ToList(),
                new List<(string, string?)>());
            foreach (var (name, command) in values)
            {
                if (string.IsNullOrWhiteSpace(command)) continue;
                yield return new StartupEntry
                {
                    Source = StartupSource.Advanced,
                    Name = name,
                    Command = command,
                    TargetPath = CommandLine.Parse(command, locations.WindowsDir, env.SearchPath).TargetFile,
                    Hive = hive,
                    Scope = scope,
                    Where = env.Registry.Describe(hive, key),
                    Flag = "Set by a policy. Remove it with the program that added it, or in Group Policy.",
                };
            }
        }
    }

    private StartupEntry Winlogon(string name, string value, RegistryHive hive, string key, bool isDefault) => new()
    {
        Source = StartupSource.Advanced,
        Name = "Winlogon " + name,
        Command = value,
        TargetPath = CommandLine.Parse(value.Split(',')[0], locations.WindowsDir, env.SearchPath).TargetFile,
        Hive = hive,
        Scope = hive == RegistryHive.CurrentUser ? "you" : "all users",
        Where = env.Registry.Describe(hive, key),
        Flag = isDefault ? null : "Not the Windows default. Old optimizers and some malware change this; a scan with your antivirus is a good idea.",
    };

    /// <summary>Windows' default is "explorer.exe".</summary>
    internal static bool IsDefaultShell(string value)
        => value.Trim().TrimEnd(',').Trim().Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Windows' default is "C:\Windows\system32\userinit.exe," (the trailing comma is normal).</summary>
    internal static bool IsDefaultUserinit(string value, string windowsDir)
    {
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 1) return false;
        var p = Environment.ExpandEnvironmentVariables(parts[0]);
        return p.Equals("userinit.exe", StringComparison.OrdinalIgnoreCase)
            || p.Equals(Path.Combine(windowsDir, "system32", "userinit.exe"), StringComparison.OrdinalIgnoreCase);
    }
}
