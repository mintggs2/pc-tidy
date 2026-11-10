using PcTidy.Features.Schedule.Common;

namespace PcTidy.Features.Schedule.Metrics;

/// <summary>
/// Counts what starts with Windows, the same places Task Manager's Startup tab looks: the Run keys (user, machine,
/// 32-bit machine) and the two Startup folders, minus entries switched off in Task Manager (StartupApproved, where an
/// odd first byte means "disabled"). Read-only; the Startup folders come from <see cref="IKnownLocations"/>, so a
/// practice PC counts its own folders. Pass <c>registry: null</c> there so the real registry isn't mixed in.
/// </summary>
public sealed class StartupCounter : IStartupCountSource
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private readonly IKnownLocations _locations;
    private readonly IRegistryReader? _registry;

    public StartupCounter(IKnownLocations locations, IRegistryReader? registry)
    {
        _locations = locations;
        _registry = registry;
    }

    public int? Count(CancellationToken ct)
    {
        int count = 0;
        if (_registry is { } reg)
        {
            count += CountRun(reg, RegistryHive.CurrentUser, Run, "Run");
            count += CountRun(reg, RegistryHive.LocalMachine, Run, "Run");
            count += CountRun(reg, RegistryHive.LocalMachine, Run32, "Run32");
        }
        ct.ThrowIfCancellationRequested();
        count += CountFolder(StartupFolder(_locations.RoamingAppData, "Startup"), RegistryHive.CurrentUser);
        count += CountFolder(StartupFolder(_locations.ProgramData, "StartUp"), RegistryHive.LocalMachine);
        return count;
    }

    private int CountRun(IRegistryReader reg, RegistryHive hive, string key, string approvedKey)
        => reg.ValueNames(hive, key).Count(name => name.Length > 0 && !IsDisabled(reg, hive, approvedKey, name));

    private int CountFolder(string folder, RegistryHive hive)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            return Directory.EnumerateFiles(folder)
                .Select(Path.GetFileName)
                .Count(name => name is not null
                               && !name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                               && !(_registry is { } reg && IsDisabled(reg, hive, "StartupFolder", name)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool IsDisabled(IRegistryReader reg, RegistryHive hive, string approvedKey, string name)
        => reg.Read(hive, Approved + approvedKey, name).Data is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;

    private static string StartupFolder(string root, string leaf)
        => Path.Combine(root, "Microsoft", "Windows", "Start Menu", "Programs", leaf);
}
