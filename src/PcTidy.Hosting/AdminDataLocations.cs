using PcTidy.Core;

namespace PcTidy.Hosting;

/// <summary>
/// The PC's locations as the elevated helper's handlers see them: everything as usual, except that the data folder
/// (where handlers put registry backups and tool logs) is the helper's admin-only folder, never the user-writable one.
/// The helper's SafetyGuard keeps the real locations, so the user's data folder stays protected.
/// </summary>
internal sealed class AdminDataLocations(IKnownLocations inner, string adminDataRoot) : IKnownLocations
{
    public string DataRoot => adminDataRoot;

    public string UserProfile => inner.UserProfile;
    public string LocalAppData => inner.LocalAppData;
    public string RoamingAppData => inner.RoamingAppData;
    public string ProgramData => inner.ProgramData;
    public string WindowsDir => inner.WindowsDir;
    public string SystemDrive => inner.SystemDrive;
    public string ProgramFiles => inner.ProgramFiles;
    public string ProgramFilesX86 => inner.ProgramFilesX86;
    public string UserTemp => inner.UserTemp;
    public string WindowsTemp => inner.WindowsTemp;
    public string Desktop => inner.Desktop;
    public string Documents => inner.Documents;
    public string Downloads => inner.Downloads;
    public string Pictures => inner.Pictures;
    public string Videos => inner.Videos;
    public string Music => inner.Music;
    public IReadOnlyList<string> CloudRoots => inner.CloudRoots;
    public string? SteamRoot => inner.SteamRoot;
    public IReadOnlyList<string> SteamLibraries => inner.SteamLibraries;
    public string? EpicManifestsDir => inner.EpicManifestsDir;
    public IReadOnlyList<string> GameInstallDirs => inner.GameInstallDirs;
    public IReadOnlyList<string> AppInstallDirs => inner.AppInstallDirs;
    public string ClaudeDir => inner.ClaudeDir;
    public string CodexDir => inner.CodexDir;
    public IReadOnlyList<string> FixedDrives => inner.FixedDrives;
    public bool IsSandbox => inner.IsSandbox;
}
