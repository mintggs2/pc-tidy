namespace PcTidy.Core;

/// <summary>
/// Fake PC laid out under one sandbox folder (the testbed). Every path PC Tidy uses points inside it,
/// so features can be run end-to-end without touching the real PC. Layout matches PcTidy.TestbedGen:
///   &lt;root&gt;\C\Windows, &lt;root&gt;\C\Program Files, &lt;root&gt;\C\Users\tester\..., &lt;root&gt;\D\..., &lt;root&gt;\Data (PC Tidy data)
/// </summary>
/// <summary>Simulated PC configuration for sandbox tests (PC Tidy must work on any PC).</summary>
public sealed record SandboxProfile
{
    /// <summary>Owner-like default: two drives, Steam + Epic, Desktop/Documents/Pictures redirected into OneDrive.</summary>
    public static readonly SandboxProfile Default = new();
    /// <summary>Plain laptop-style PC: one drive, no games, no OneDrive.</summary>
    public static readonly SandboxProfile Minimal = new() { SecondDrive = false, Steam = false, Epic = false, OneDrive = false, OneDriveRedirectsFolders = false };

    public bool SecondDrive { get; init; } = true;
    public bool Steam { get; init; } = true;
    public bool Epic { get; init; } = true;
    public bool OneDrive { get; init; } = true;
    public bool OneDriveRedirectsFolders { get; init; } = true;
    public string UserName { get; init; } = "tester";
}

public sealed class SandboxKnownLocations : IKnownLocations
{
    public string Root { get; }
    public SandboxProfile Profile { get; }

    public SandboxKnownLocations(string root) : this(root, SandboxProfile.Default) { }

    public SandboxKnownLocations(string root, SandboxProfile profile)
    {
        Profile = profile;
        Root = PathUtil.Normalize(root);
        var c = Path.Combine(Root, "C");
        var d = Path.Combine(Root, "D");
        SystemDrive = c + "\\";
        UserProfile = Path.Combine(c, "Users", profile.UserName);
        LocalAppData = Path.Combine(UserProfile, "AppData", "Local");
        RoamingAppData = Path.Combine(UserProfile, "AppData", "Roaming");
        ProgramData = Path.Combine(c, "ProgramData");
        WindowsDir = Path.Combine(c, "Windows");
        ProgramFiles = Path.Combine(c, "Program Files");
        ProgramFilesX86 = Path.Combine(c, "Program Files (x86)");
        UserTemp = Path.Combine(LocalAppData, "Temp");
        WindowsTemp = Path.Combine(WindowsDir, "Temp");
        var oneDrive = Path.Combine(UserProfile, "OneDrive");
        CloudRoots = profile.OneDrive ? new[] { oneDrive } : Array.Empty<string>();
        // Default profile: Desktop/Documents/Pictures redirected into OneDrive (common Windows 11 setup); Downloads/Videos/Music local.
        var redirected = profile.OneDrive && profile.OneDriveRedirectsFolders;
        Desktop = Path.Combine(redirected ? oneDrive : UserProfile, "Desktop");
        Documents = Path.Combine(redirected ? oneDrive : UserProfile, "Documents");
        Pictures = Path.Combine(redirected ? oneDrive : UserProfile, "Pictures");
        Downloads = Path.Combine(UserProfile, "Downloads");
        Videos = Path.Combine(UserProfile, "Videos");
        Music = Path.Combine(UserProfile, "Music");

        var games = new List<string>();
        if (profile.Steam)
        {
            SteamRoot = Path.Combine(ProgramFilesX86, "Steam");
            var libs = new List<string> { SteamRoot };
            if (profile.SecondDrive) libs.Add(Path.Combine(d, "SteamLibrary"));
            SteamLibraries = libs;
            games.AddRange(libs.Select(l => Path.Combine(l, "steamapps", "common")));
        }
        else SteamLibraries = Array.Empty<string>();
        if (profile.Epic)
        {
            EpicManifestsDir = Path.Combine(ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
            games.Add(Path.Combine(ProgramFiles, "Epic Games"));
        }
        GameInstallDirs = games;
        AppInstallDirs = new[] { Path.Combine(ProgramFiles, "FakeApp"), Path.Combine(LocalAppData, "Programs", "FakeUserApp") };
        ClaudeDir = Path.Combine(UserProfile, ".claude");
        CodexDir = Path.Combine(UserProfile, ".codex");
        DataRoot = Path.Combine(Root, "Data");
        FixedDrives = profile.SecondDrive ? new[] { c + "\\", d + "\\" } : new[] { c + "\\" };
    }

    public string UserProfile { get; }
    public string LocalAppData { get; }
    public string RoamingAppData { get; }
    public string ProgramData { get; }
    public string WindowsDir { get; }
    public string SystemDrive { get; }
    public string ProgramFiles { get; }
    public string ProgramFilesX86 { get; }
    public string UserTemp { get; }
    public string WindowsTemp { get; }
    public string Desktop { get; }
    public string Documents { get; }
    public string Downloads { get; }
    public string Pictures { get; }
    public string Videos { get; }
    public string Music { get; }
    public IReadOnlyList<string> CloudRoots { get; }
    public string? SteamRoot { get; }
    public IReadOnlyList<string> SteamLibraries { get; }
    public string? EpicManifestsDir { get; }
    public IReadOnlyList<string> GameInstallDirs { get; }
    public IReadOnlyList<string> AppInstallDirs { get; }
    public string ClaudeDir { get; }
    public string CodexDir { get; }
    public string DataRoot { get; }
    public IReadOnlyList<string> FixedDrives { get; }
    public bool IsSandbox => true;
}
