using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Features.Apps.Inventory;

namespace PcTidy.Features.Apps.Tests;

/// <summary>A sandbox folder under testbed\apps\ (deleted afterwards). Never touches the real PC.</summary>
public sealed class Sandbox : IDisposable
{
    public static readonly string TestbedRoot = Path.Combine(FindRepoRoot(), "testbed", "apps");

    public Sandbox(SandboxProfile? profile = null)
    {
        Root = Path.Combine(TestbedRoot, Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Locations = new SandboxKnownLocations(Root, profile ?? SandboxProfile.Minimal);
        foreach (var d in new[] { Locations.ProgramFiles, Locations.ProgramFilesX86, Locations.ProgramData, Locations.LocalAppData,
                     Locations.RoamingAppData, Locations.WindowsDir, Locations.Downloads, Locations.DataRoot })
            Directory.CreateDirectory(d);
    }

    public string Root { get; }
    public SandboxKnownLocations Locations { get; }

    /// <summary>Creates a folder (and a small file in it, so it has a size).</summary>
    public string Folder(string path, params string[] files)
    {
        Directory.CreateDirectory(path);
        foreach (var f in files.DefaultIfEmpty("data.bin"))
        {
            var full = Path.Combine(path, f);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[1234]);
        }
        return path;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a handle closing late: the next run's folder name differs anyway */ }
    }

    private static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// A fake registry under HKCU\Software\PCTidyTest\apps\&lt;id&gt; laid out like <see cref="RedirectedRegistryReader"/> expects.
/// The whole key is deleted afterwards.
/// </summary>
public sealed class FakeRegistry : IDisposable
{
    public FakeRegistry()
    {
        Root = $@"Software\PCTidyTest\apps\{Guid.NewGuid():N}";
        Reader = new RedirectedRegistryReader(Root);
    }

    public string Root { get; }
    public RedirectedRegistryReader Reader { get; }

    /// <summary>Creates an Uninstall entry in "HKLM64", "HKLM32" or "HKCU" with the given values (int = DWORD).</summary>
    public void UninstallEntry(string list, string keyName, params (string Name, object Value)[] values)
        => Key($@"{list}\{UninstallRegistryReader.UninstallPath}\{keyName}", values);

    public void Key(string relative, params (string Name, object Value)[] values)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{Root}\{relative}", writable: true)!;
        foreach (var (name, value) in values)
        {
            if (value is int i) key.SetValue(name, i, RegistryValueKind.DWord);
            else if (value is byte[] b) key.SetValue(name, b, RegistryValueKind.Binary);
            else key.SetValue(name, value.ToString()!, RegistryValueKind.String);
        }
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false); }
        catch (UnauthorizedAccessException) { /* nothing else to do */ }
        // Remove the shared parent too when no other test still uses it.
        try
        {
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\PCTidyTest\apps");
            if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                Registry.CurrentUser.DeleteSubKey(@"Software\PCTidyTest\apps", throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException) { }
    }
}

/// <summary>Store packages handed in by the test.</summary>
public sealed class FakeStore : IStorePackageSource
{
    private readonly List<StorePackage> _packages = new();

    public FakeStore Add(string name, string? display = null, bool framework = false, bool resource = false, bool system = false)
    {
        var family = name + "_8wekyb3d8bbwe";
        _packages.Add(new StorePackage(name, $"{name}_1.0.0.0_x64__8wekyb3d8bbwe", family, display ?? name, "Microsoft Corporation", "1.0.0.0",
            null, new DateTime(2024, 5, 1), framework, resource, system, false));
        return this;
    }

    public IReadOnlyList<StorePackage> List(CancellationToken ct) => _packages;
}

public static class UserAssistData
{
    /// <summary>A Windows 7+ 72-byte UserAssist record with a run count and last-run time.</summary>
    public static byte[] Record(int runCount, DateTime? lastRunUtc)
    {
        var data = new byte[72];
        BitConverter.GetBytes(runCount).CopyTo(data, 4);
        BitConverter.GetBytes(lastRunUtc?.ToFileTimeUtc() ?? 0L).CopyTo(data, 60);
        return data;
    }
}
