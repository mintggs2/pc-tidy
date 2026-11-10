namespace PcTidy.Features.Speed.Startup;

/// <summary>Where a startup entry lives.</summary>
public enum StartupSource { RunKey, RunOnceKey, StartupFolder, StoreApp, ScheduledTask, Service, Advanced }

/// <summary>One thing that starts with Windows (read-only description).</summary>
public sealed record StartupEntry
{
    public required StartupSource Source { get; init; }
    /// <summary>Entry name as stored (Run value name, shortcut file name, task path, service name).</summary>
    public required string Name { get; init; }
    /// <summary>Raw command line, if any.</summary>
    public string? Command { get; init; }
    /// <summary>File that really runs (program, rundll32's DLL, a script).</summary>
    public string? TargetPath { get; init; }
    public RegistryHive Hive { get; init; } = RegistryHive.CurrentUser;
    /// <summary>"you" or "all users".</summary>
    public string Scope { get; init; } = "you";
    /// <summary>StartupApproved sub-key (Run / Run32 / StartupFolder) for Task-Manager-style toggles; null if not toggleable that way.</summary>
    public string? ApprovedLocation { get; init; }
    /// <summary>Registry key or folder holding the entry, as regedit / Explorer shows it.</summary>
    public string? Where { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTime? DisabledAtUtc { get; init; }
    /// <summary>Task path for scheduled tasks.</summary>
    public string? TaskPath { get; init; }
    public string? DisplayNameHint { get; init; }
    public string? PublisherHint { get; init; }
    /// <summary>Store app startup task state as Windows reports it.</summary>
    public int? StoreState { get; init; }
    public bool ServiceRunning { get; init; }
    /// <summary>Read-only note for Advanced rows ("Not the Windows default").</summary>
    public string? Flag { get; init; }
}

/// <summary>
/// The 12-byte StartupApproved value Task Manager and Settings use (Research §4): first byte even (02, 06) = on,
/// odd (01, 03, 07) = off; bytes 4–11 are the FILETIME it was turned off. No value at all = on (never switched).
/// </summary>
public static class StartupApprovedState
{
    public const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    public const string BaseKeyMachine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static bool IsEnabled(byte[]? value) => value is not { Length: > 0 } || (value[0] & 1) == 0;

    /// <summary>When it was switched off, if it is off and the time is recorded.</summary>
    public static DateTime? DisabledAtUtc(byte[]? value)
    {
        if (IsEnabled(value) || value!.Length < 12) return null;
        var ft = BitConverter.ToInt64(value, 4);
        if (ft <= 0) return null;
        try { return DateTime.FromFileTimeUtc(ft); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}
