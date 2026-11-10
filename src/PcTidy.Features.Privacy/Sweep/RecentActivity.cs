namespace PcTidy.Features.Privacy.Sweep;

/// <summary>What one recent-activity list holds on this PC: its files (each quarantined on its own) and what was skipped.</summary>
public sealed record RecentList(string Folder, IReadOnlyList<FileEntry> Files, int Skipped);

/// <summary>
/// Windows' own recent-activity lists for the signed-in user, under <c>%APPDATA%\Microsoft\Windows\Recent</c>:
/// the Recent files shortcuts (*.lnk), and the jump lists behind taskbar/Start right-click menus
/// (AutomaticDestinations\*.automaticDestinations-ms, CustomDestinations\*.customDestinations-ms).
/// Read-only listing (no recursion, no links followed, no cloud placeholders, nothing with the System attribute);
/// every file must also pass <see cref="ISafetyGuard.ClassifyPath"/>, otherwise it's counted as skipped.
/// </summary>
public static class RecentActivity
{
    /// <summary>File Explorer's own jump list: Quick access (frequent folders, recent files, and the folders you pinned).</summary>
    public const string QuickAccessFile = "f01b4d95cf55d32a.automaticDestinations-ms";

    public static string RecentFolder(IKnownLocations k) => Path.Combine(k.RoamingAppData, "Microsoft", "Windows", "Recent");
    public static string AutomaticDestinations(IKnownLocations k) => Path.Combine(RecentFolder(k), "AutomaticDestinations");
    public static string CustomDestinations(IKnownLocations k) => Path.Combine(RecentFolder(k), "CustomDestinations");

    /// <summary>The Recent files shortcuts (directly in the Recent folder).</summary>
    public static RecentList RecentFiles(ScanContext ctx, CancellationToken ct)
        => List(ctx, RecentFolder(ctx.Locations), e => e.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase), ct);

    /// <summary>Every app's jump list except File Explorer's Quick access one.</summary>
    public static RecentList JumpLists(ScanContext ctx, CancellationToken ct)
    {
        var auto = List(ctx, AutomaticDestinations(ctx.Locations),
            e => e.Name.EndsWith(".automaticDestinations-ms", StringComparison.OrdinalIgnoreCase) && !IsQuickAccess(e.Name), ct);
        var custom = List(ctx, CustomDestinations(ctx.Locations), e => e.Name.EndsWith(".customDestinations-ms", StringComparison.OrdinalIgnoreCase), ct);
        return new RecentList(AutomaticDestinations(ctx.Locations), auto.Files.Concat(custom.Files).ToList(), auto.Skipped + custom.Skipped);
    }

    /// <summary>File Explorer's Quick access history (one file, listed on its own because it also holds your pins).</summary>
    public static RecentList QuickAccess(ScanContext ctx, CancellationToken ct)
        => List(ctx, AutomaticDestinations(ctx.Locations), e => IsQuickAccess(e.Name), ct);

    private static bool IsQuickAccess(string name) => name.Equals(QuickAccessFile, StringComparison.OrdinalIgnoreCase);

    private static RecentList List(ScanContext ctx, string folder, Func<FileEntry, bool> wanted, CancellationToken ct)
    {
        var files = new List<FileEntry>();
        int skipped = 0;
        if (!Directory.Exists(folder)) return new RecentList(folder, files, 0);
        ctx.Walker.Walk(folder, new WalkOptions { Recurse = false, IncludeHidden = true, IncludeSystem = true, MaxDepth = 1 }, e =>
        {
            ct.ThrowIfCancellationRequested();
            if (e.IsDirectory || !wanted(e)) return false;
            // Odd entries are left alone: links, online-only files, Windows' own system-flagged files.
            if (e.IsReparsePoint || e.IsCloudPlaceholder || e.IsSystem) { skipped++; return false; }
            if (!ctx.Safety.ClassifyPath(e.FullPath).Allowed) { skipped++; return false; }
            files.Add(e);
            return false;
        }, ct);
        return new RecentList(folder, files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }
}
