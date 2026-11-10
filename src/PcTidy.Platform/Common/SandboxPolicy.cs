using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// What system handlers may really do when <c>IKnownLocations.IsSandbox</c> is true.
/// Rule: nothing real, except registry writes under <c>HKCU\Software\PCTidyTest</c> (the test area),
/// so handler logic can be proven end-to-end without touching a real setting.
/// </summary>
public static class SandboxPolicy
{
    public const string TestRegistryRoot = @"Software\PCTidyTest";

    public const string NothingChanged = "Simulation sandbox: nothing on this PC was changed.";

    /// <summary>True if the key is HKCU\Software\PCTidyTest or below it.</summary>
    public static bool IsTestRegistryKey(RegistryHive hive, string? keyPath)
    {
        if (hive != RegistryHive.CurrentUser || string.IsNullOrWhiteSpace(keyPath)) return false;
        var k = keyPath.Trim().TrimStart('\\');
        return k.Equals(TestRegistryRoot, StringComparison.OrdinalIgnoreCase)
            || k.StartsWith(TestRegistryRoot + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
