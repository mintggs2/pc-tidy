using Microsoft.Win32;
using PcTidy.Core;

namespace PcTidy.App.WhatIs;

/// <summary>An installed app as Windows lists it (Settings → Apps).</summary>
public sealed record InstalledApp(string Name, string? Publisher, string InstallLocation);

/// <summary>Which installed app a path belongs to.</summary>
public interface IInstalledApps
{
    IReadOnlyList<InstalledApp> All { get; }
}

public static class InstalledAppsExtensions
{
    /// <summary>The app whose install folder holds the path (the deepest match wins: "Steam\steamapps" beats "Steam").</summary>
    public static InstalledApp? OwnerOf(this IInstalledApps apps, string path)
        => apps.All.Where(a => PathUtil.IsUnder(path, a.InstallLocation))
                   .OrderByDescending(a => a.InstallLocation.Length)
                   .FirstOrDefault();

    /// <summary>A likely owner for an AppData folder name ("Google" → "Google Chrome"), by name or publisher.</summary>
    public static InstalledApp? Guess(this IInstalledApps apps, string folderName)
    {
        if (folderName.Length < 3) return null;
        return apps.All.FirstOrDefault(a => a.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase))
            ?? apps.All.FirstOrDefault(a => a.Name.StartsWith(folderName + " ", StringComparison.OrdinalIgnoreCase))
            ?? apps.All.FirstOrDefault(a => a.Publisher is { } p && p.StartsWith(folderName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The Uninstall registry (current user, machine 64-bit and 32-bit), read once, read-only. Entries without an
/// install folder, system components and updates are left out.
/// </summary>
public sealed class UninstallRegistryApps : IInstalledApps
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly Lazy<IReadOnlyList<InstalledApp>> _all = new(Read);

    public IReadOnlyList<InstalledApp> All => _all.Value;

    private static IReadOnlyList<InstalledApp> Read()
    {
        var list = new List<InstalledApp>();
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uninstall.OpenSubKey(name);
                        if (k is null || k.GetValue("SystemComponent") is int sc && sc == 1 || k.GetValue("ParentKeyName") is not null) continue;
                        if (k.GetValue("DisplayName") is not string display || display.Trim().Length == 0) continue;
                        if (k.GetValue("InstallLocation") is not string loc || !IsFolder(loc.Trim().Trim('"'))) continue;
                        list.Add(new InstalledApp(display.Trim(), (k.GetValue("Publisher") as string)?.Trim(), PathUtil.Normalize(loc.Trim().Trim('"'))));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return list.DistinctBy(a => (a.Name, a.InstallLocation)).ToList();
    }

    private static bool IsFolder(string p)
    {
        try { return p.Length > 3 && Path.IsPathFullyQualified(p) && Directory.Exists(p); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return false; }
    }
}

/// <summary>Practice PC / demo / tests: the sandbox's app folders (IKnownLocations.AppInstallDirs) as apps.</summary>
public sealed class FixedInstalledApps : IInstalledApps
{
    public FixedInstalledApps(IEnumerable<InstalledApp> apps) => All = apps.ToList();

    public IReadOnlyList<InstalledApp> All { get; }

    public static FixedInstalledApps FromLocations(IKnownLocations k)
        => new(k.AppInstallDirs.Select(d => new InstalledApp(Path.GetFileName(PathUtil.Normalize(d)), "Sample publisher", PathUtil.Normalize(d))));
}
