using PcTidy.Core;

namespace PcTidy.Features.Apps.Inventory;

/// <summary>Where the inventory reads from. Defaults are the real PC; tests swap the registry and Store source.</summary>
public sealed record AppInventorySources
{
    public required IKnownLocations Locations { get; init; }
    public IRegistryReader Registry { get; init; } = WindowsRegistryReader.Instance;
    /// <summary>Null = don't list Store apps (sandbox tests without a fake source).</summary>
    public IStorePackageSource? Store { get; init; } = new WindowsStorePackageSource();
    public IFileWalker Walker { get; init; } = new ManagedFileWalker();

    /// <summary>Real PC sources, except that a sandbox never reads the real Store or registry.</summary>
    public static AppInventorySources For(IKnownLocations locations, IFileWalker? walker = null)
        => locations.IsSandbox
            ? new AppInventorySources { Locations = locations, Registry = new RedirectedRegistryReader(@"Software\PCTidyTest\apps\sandbox"), Store = null, Walker = walker ?? new ManagedFileWalker() }
            : new AppInventorySources { Locations = locations, Walker = walker ?? new ManagedFileWalker() };
}

/// <summary>
/// The list of installed apps: desktop (three Uninstall lists), Store (current user) and Steam/Epic games, with
/// last-used dates from UserAssist / Steam and orphaned entries flagged. Read-only from start to finish.
/// </summary>
public sealed class AppInventory
{
    private readonly AppInventorySources _sources;

    public AppInventory(AppInventorySources sources) => _sources = sources;

    public IKnownLocations Locations => _sources.Locations;

    public Task<IReadOnlyList<InstalledApp>> LoadAsync(CancellationToken ct) => Task.Run(() => Load(ct), ct);

    public IReadOnlyList<InstalledApp> Load(CancellationToken ct)
    {
        var games = LauncherGamesReader.Read(_sources.Locations, ct);
        var steamIds = games.Where(g => g.Source == AppSource.Steam).Select(g => g.LauncherId!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var desktop = new UninstallRegistryReader(_sources.Registry).Read(ct)
            // Steam writes an Uninstall entry per game; the game is already listed from Steam itself.
            .Where(a => a.LauncherId is null || !steamIds.Contains(a.LauncherId))
            .Select(a => a.LauncherId is null ? a : a with { Source = AppSource.Steam, Id = "steam:" + a.LauncherId })
            .Select(MarkOrphan);
        var store = _sources.Store is null ? Array.Empty<InstalledApp>() : StoreAppReader.Read(_sources.Store, ct);

        var all = Deduplicate(desktop.Concat(store).Concat(games)).ToList();
        IReadOnlyList<UserAssistEntry> launches;
        try { launches = new UserAssistReader(_sources.Registry, _sources.Locations).Read(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { launches = Array.Empty<UserAssistEntry>(); }
        return LastUsedMatcher.Apply(all, launches);
    }

    /// <summary>The same app listed twice (64-bit and 32-bit lists, or twice in one) is shown once.</summary>
    private static IEnumerable<InstalledApp> Deduplicate(IEnumerable<InstalledApp> apps)
        => apps.GroupBy(a => a.Source == AppSource.Desktop
                ? $"{a.Name}|{a.Version}|{a.InstallLocation}|{a.UninstallString}".ToUpperInvariant()
                : a.Id.ToUpperInvariant())
            .Select(g => g.First());

    /// <summary>
    /// An entry is orphaned when the folder it says it lives in, or the uninstaller it would run, is gone.
    /// Such entries are "ghosts": the app was removed without telling Windows.
    /// </summary>
    public static InstalledApp MarkOrphan(InstalledApp app)
    {
        if (app.Source != AppSource.Desktop) return app;
        string? why = null;
        if (app.InstallLocation is { Length: > 3 } folder && !SafeDirectoryExists(folder))
            why = "The folder it was installed in is gone.";
        else if (app.MsiProductCode is null && UninstallCommand.ProgramPath(app.UninstallString) is { } program && !SafeFileExists(program))
            why = "Its uninstaller is gone.";
        return why is null ? app : app with { IsOrphan = true, OrphanReason = why };
    }

    /// <summary>
    /// Measures the app's folder (all files below it, links not followed). Null when the folder is unknown, missing,
    /// unreadable, or too broad to belong to one app (a drive, Program Files, Windows, the user folder).
    /// </summary>
    public Task<long?> MeasureAsync(InstalledApp app, CancellationToken ct)
        => Task.Run(() => Measure(app.InstallLocation, ct), ct);

    public long? Measure(string? folder, CancellationToken ct)
    {
        if (folder is null || !IsMeasurable(folder) || !SafeDirectoryExists(folder)) return null;
        try
        {
            // A folder that is itself a link points somewhere else: its size isn't this app's.
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return null;
            var stats = _sources.Walker.Walk(folder, new WalkOptions { IncludeHidden = true, IncludeSystem = true }, _ => true, ct);
            return stats.Files == 0 && stats.Inaccessible > 0 ? null : stats.Bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private bool IsMeasurable(string folder)
    {
        string full;
        try { full = PathUtil.Normalize(folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (full.Length <= 3) return false;
        var k = _sources.Locations;
        var broad = new[] { k.ProgramFiles, k.ProgramFilesX86, k.ProgramData, k.WindowsDir, k.UserProfile, k.LocalAppData, k.RoamingAppData,
            Path.Combine(k.LocalAppData, "Programs"), Path.Combine(k.WindowsDir, "System32"), k.SystemDrive }.Concat(k.FixedDrives);
        return !broad.Any(b => string.Equals(PathUtil.Normalize(b), full, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    internal static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }
}
