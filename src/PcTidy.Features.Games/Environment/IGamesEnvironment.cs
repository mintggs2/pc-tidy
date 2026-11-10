using PcTidy.Catalog.Games;
using PcTidy.Core;

namespace PcTidy.Features.Games.Environment;

/// <summary>A game a launcher registered in the registry (GOG, EA, Ubisoft, Battle.net).</summary>
/// <param name="Id">The launcher's own id (GOG game id), or null.</param>
public sealed record RegistryGame(Launcher Launcher, string Name, string InstallDir, string? Id);

/// <summary>A Microsoft Store / Xbox app game (found with Windows' PackageManager).</summary>
public sealed record StoreGame(string Name, string InstallDir, string PackageFamilyName);

/// <summary>
/// Everything the Games section reads outside plain files: launcher registry keys, Store packages and NVIDIA's
/// capture setting. All read-only. The real PC uses <see cref="WindowsGamesEnvironment"/>; the sandbox never reads the
/// real PC's registry (tests stay deterministic) and uses <see cref="SandboxGamesEnvironment"/>.
/// </summary>
public interface IGamesEnvironment
{
    IReadOnlyList<RegistryGame> RegistryGames();
    IReadOnlyList<StoreGame> StoreGames();
    /// <summary>Raw bytes of HKCU\Software\NVIDIA Corporation\Global\ShadowPlay\NVSPCAPS\DefaultPathW, or null.</summary>
    byte[]? NvidiaCapturePathValue();

    /// <summary>
    /// Per-program compatibility settings (the Properties → Compatibility tab), one per exe path, from the current user and
    /// all users. Read-only; used only to explain a "Run as administrator" tick on a game. Empty when unknown.
    /// </summary>
    IReadOnlyList<CompatibilitySetting> CompatibilitySettings() => Array.Empty<CompatibilitySetting>();
}

/// <summary>One program's Compatibility-tab settings. <paramref name="Flags"/> is the stored text, e.g. "~ RUNASADMIN".</summary>
/// <param name="AllUsers">Set with "Change settings for all users" (machine-wide) rather than for this user only.</param>
public sealed record CompatibilitySetting(string ExePath, string Flags, bool AllUsers)
{
    /// <summary>The "Run this program as an administrator" tick, as a whole word in the stored flags.</summary>
    public bool RunsAsAdmin => Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Any(f => f.Equals("RUNASADMIN", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Sandbox: no registry or Store games; NVIDIA's setting may be simulated by &lt;root&gt;\Sim\nvspcaps-defaultpathw.bin.</summary>
public sealed class SandboxGamesEnvironment : IGamesEnvironment
{
    private readonly IKnownLocations _locations;

    public SandboxGamesEnvironment(IKnownLocations locations) => _locations = locations;

    public IReadOnlyList<RegistryGame> RegistryGames() => Array.Empty<RegistryGame>();

    public IReadOnlyList<StoreGame> StoreGames() => Array.Empty<StoreGame>();

    public byte[]? NvidiaCapturePathValue()
    {
        if (_locations is not SandboxKnownLocations s) return null;
        var file = Path.Combine(s.Root, "Sim", "nvspcaps-defaultpathw.bin");
        try { return File.Exists(file) ? File.ReadAllBytes(file) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
