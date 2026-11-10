using PcTidy.Catalog.Games;

namespace PcTidy.Features.Games.Library;

/// <summary>One installed game, from any launcher. Read-only facts; nothing here is ever changed by PC Tidy.</summary>
public sealed record InstalledGame
{
    public required Launcher Launcher { get; init; }
    /// <summary>The launcher's id: Steam app id, Epic AppName, GOG game id, Store package family name...</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string InstallDir { get; init; }
    /// <summary>Size in bytes (0 = unknown).</summary>
    public long SizeBytes { get; init; }
    /// <summary>When the launcher last saw it played (Steam only records this).</summary>
    public DateTime? LastPlayedUtc { get; init; }
    /// <summary>Last install/update time (Steam's LastUpdated), used for "never played since it was installed".</summary>
    public DateTime? InstalledUtc { get; init; }
    /// <summary>Steam library or other folder the game lives in.</summary>
    public string? LibraryPath { get; init; }
    /// <summary>False while a launcher is still downloading/updating it.</summary>
    public bool FullyInstalled { get; init; } = true;
    /// <summary>Process names (no .exe) the launcher lists for it (Epic manifests).</summary>
    public IReadOnlyList<string> ProcessNames { get; init; } = Array.Empty<string>();

    public string Key => $"{Launcher}:{Id}".ToLowerInvariant();

}
