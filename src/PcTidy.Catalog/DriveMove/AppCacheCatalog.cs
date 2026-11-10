namespace PcTidy.Catalog.DriveMove;

/// <summary>Which of the user's base folders a cache path is relative to (so the same table works on any PC and in the sandbox).</summary>
public enum CacheBase { UserProfile, LocalAppData, RoamingAppData, ProgramData }

/// <summary>A folder relative to one of the user's base folders.</summary>
public sealed record CacheFolder(CacheBase Base, string RelativePath);

/// <summary>The supported way to give a tool's data a new home (Research.md §7 "App caches with a supported new-location setting").</summary>
public enum RelocationMechanism
{
    /// <summary>PC Tidy edits the tool's own config file (EditTextFile: backed up first, undone from History).</summary>
    ConfigFile,
    /// <summary>The tool reads a per-user environment variable. Shown as steps: PC Tidy doesn't change environment variables.</summary>
    EnvironmentVariable,
    /// <summary>The app has its own "location" setting. Shown as steps.</summary>
    AppSetting,
    /// <summary>WSL's own <c>wsl --manage &lt;distro&gt; --move &lt;folder&gt;</c> (needs WSL stopped first).</summary>
    WslMove,
}

/// <summary>Text formats PC Tidy knows how to change one setting in (see the feature's ConfigText).</summary>
public enum ConfigFormat
{
    /// <summary>npm / pnpm .npmrc: <c>key=value</c> lines.</summary>
    NpmRc,
    /// <summary>pip.ini: <c>[global]</c> section, <c>key = value</c>.</summary>
    PipIni,
    /// <summary>NuGet.Config: <c>&lt;configuration&gt;&lt;config&gt;&lt;add key value/&gt;</c>.</summary>
    NuGetConfig,
    /// <summary>Maven settings.xml: <c>&lt;settings&gt;&lt;localRepository&gt;</c>.</summary>
    MavenSettings,
}

/// <summary>The tool's own per-user config file and the setting that says where its data lives.</summary>
/// <param name="Section">INI section (pip: "global"); null for other formats.</param>
public sealed record ConfigFileTarget(CacheBase Base, string RelativePath, ConfigFormat Format, string Key, string? Section = null);

/// <summary>
/// One tool whose big data folder can be given a new home the tool itself supports.
/// </summary>
/// <param name="Id">Stable id ("npm", "pip", ...).</param>
/// <param name="Name">Short display name ("npm cache").</param>
/// <param name="What">One plain sentence: what the folder holds.</param>
/// <param name="DefaultFolders">Where the data lives when nothing was changed (first existing wins).</param>
/// <param name="LocationVariables">Environment variables that, when set, already point somewhere else (first set wins).</param>
/// <param name="Mechanism">The supported way to move it.</param>
/// <param name="Config">For <see cref="RelocationMechanism.ConfigFile"/>: the file and setting.</param>
/// <param name="Variable">For <see cref="RelocationMechanism.EnvironmentVariable"/>: the variable that moves it.</param>
/// <param name="Steps">What to do, in plain words. "{target}" is replaced by the suggested new folder.</param>
/// <param name="Warning">Anything to know first (null when nothing).</param>
/// <param name="CloseFirst">Process names (no .exe) that must be closed first.</param>
/// <param name="OldCopyRebuildable">True when the old folder is a pure cache the tool refills on demand, so it may go to quarantine after the move.</param>
/// <param name="Source">Where the mechanism is documented.</param>
public sealed record AppCacheTool(
    string Id,
    string Name,
    string What,
    IReadOnlyList<CacheFolder> DefaultFolders,
    IReadOnlyList<string> LocationVariables,
    RelocationMechanism Mechanism,
    ConfigFileTarget? Config,
    string? Variable,
    string Steps,
    string? Warning,
    IReadOnlyList<string> CloseFirst,
    bool OldCopyRebuildable,
    string Source);

/// <summary>
/// The app-cache relocation table (Plan §8 "10 · Move to another drive", Research.md §7). Only mechanisms the tool's own
/// documentation supports are listed; anything else is not offered. Browser caches are cleaned, never moved, so they're absent.
/// </summary>
public static class AppCacheCatalog
{
    private static readonly string[] None = Array.Empty<string>();

    private static CacheFolder L(string rel) => new(CacheBase.LocalAppData, rel);
    private static CacheFolder R(string rel) => new(CacheBase.RoamingAppData, rel);
    private static CacheFolder U(string rel) => new(CacheBase.UserProfile, rel);
    private static CacheFolder P(string rel) => new(CacheBase.ProgramData, rel);

    /// <summary>Wording shared by every environment-variable tool.</summary>
    public const string VariableSteps =
        "Close the app, move the folder to {target}, then add a user variable {variable} = {target} " +
        "(Start → \"Edit environment variables for your account\" → New). The app finds it there next time.";

    public static readonly IReadOnlyList<AppCacheTool> All = new AppCacheTool[]
    {
        new("npm", "npm cache", "Packages npm downloaded. npm fills a new cache by itself as it needs packages.",
            new[] { L("npm-cache") }, new[] { "npm_config_cache" },
            RelocationMechanism.ConfigFile, new ConfigFileTarget(CacheBase.UserProfile, ".npmrc", ConfigFormat.NpmRc, "cache"), null,
            "Your .npmrc gets the line cache={target}. npm uses the new folder from the next install.",
            null, None, OldCopyRebuildable: true, "https://docs.npmjs.com/cli/using-npm/config#cache"),

        new("pnpm", "pnpm store", "Every package pnpm installed, shared by your projects.",
            new[] { L(@"pnpm\store") }, new[] { "npm_config_store_dir" },
            RelocationMechanism.ConfigFile, new ConfigFileTarget(CacheBase.UserProfile, ".npmrc", ConfigFormat.NpmRc, "store-dir"), null,
            "Your .npmrc gets the line store-dir={target}. pnpm uses the new store from the next install.",
            "pnpm links packages from its store into each project. With the store on another drive than your projects, pnpm copies instead of linking, which uses more space.",
            None, OldCopyRebuildable: false, "https://pnpm.io/npmrc#store-dir"),

        new("yarn", "Yarn cache", "Packages Yarn downloaded. Yarn fills a new cache by itself.",
            new[] { L(@"Yarn\Cache") }, new[] { "YARN_CACHE_FOLDER" },
            RelocationMechanism.EnvironmentVariable, null, "YARN_CACHE_FOLDER", VariableSteps,
            null, None, OldCopyRebuildable: true, "https://classic.yarnpkg.com/en/docs/cli/cache"),

        new("pip", "pip cache", "Python packages pip downloaded. pip fills a new cache by itself.",
            new[] { L(@"pip\Cache") }, new[] { "PIP_CACHE_DIR" },
            RelocationMechanism.ConfigFile, new ConfigFileTarget(CacheBase.RoamingAppData, @"pip\pip.ini", ConfigFormat.PipIni, "cache-dir", "global"), null,
            "Your pip.ini gets cache-dir = {target} in its [global] section. pip uses it from the next install.",
            null, None, OldCopyRebuildable: true, "https://pip.pypa.io/en/stable/topics/configuration/"),

        new("nuget", "NuGet packages", ".NET packages NuGet downloaded for your projects. NuGet downloads them again into the new folder when needed.",
            new[] { U(@".nuget\packages") }, new[] { "NUGET_PACKAGES" },
            RelocationMechanism.ConfigFile, new ConfigFileTarget(CacheBase.RoamingAppData, @"NuGet\NuGet.Config", ConfigFormat.NuGetConfig, "globalPackagesFolder"), null,
            "Your NuGet.Config gets globalPackagesFolder = {target}. Visual Studio and dotnet use it from the next restore.",
            null, new[] { "devenv" }, OldCopyRebuildable: true,
            "https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders"),

        new("gradle", "Gradle home", "Gradle's downloaded libraries, wrappers and build caches.",
            new[] { U(".gradle") }, new[] { "GRADLE_USER_HOME" },
            RelocationMechanism.EnvironmentVariable, null, "GRADLE_USER_HOME", VariableSteps,
            "The Gradle home also holds your own gradle.properties, so move the whole folder rather than starting empty.",
            new[] { "java", "studio64" }, OldCopyRebuildable: false, "https://docs.gradle.org/current/userguide/directory_layout.html"),

        new("maven", "Maven repository", "Java libraries Maven downloaded. Maven downloads them again into the new folder when needed.",
            new[] { U(@".m2\repository") }, None,
            RelocationMechanism.ConfigFile, new ConfigFileTarget(CacheBase.UserProfile, @".m2\settings.xml", ConfigFormat.MavenSettings, "localRepository"), null,
            "Your Maven settings.xml gets <localRepository>{target}</localRepository>. Maven uses it from the next build.",
            null, None, OldCopyRebuildable: true, "https://maven.apache.org/settings.html"),

        new("android-sdk", "Android SDK", "Android build tools, platforms and system images.",
            new[] { L(@"Android\Sdk") }, new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" },
            RelocationMechanism.AppSetting, null, null,
            "Close Android Studio, move the folder to {target}, then open Android Studio → Settings → Languages & Frameworks → Android SDK → Edit (next to \"Android SDK Location\") and pick {target}.",
            "If you set ANDROID_HOME yourself, change it to {target} too.", new[] { "studio64" }, OldCopyRebuildable: false,
            "https://developer.android.com/tools/variables"),

        new("android-avd", "Android emulators", "Your Android emulator disks (AVDs).",
            new[] { U(@".android\avd") }, new[] { "ANDROID_AVD_HOME" },
            RelocationMechanism.EnvironmentVariable, null, "ANDROID_AVD_HOME", VariableSteps,
            "Close every running emulator first.", new[] { "qemu-system-x86_64", "emulator" }, OldCopyRebuildable: false,
            "https://developer.android.com/tools/variables"),

        new("go-modules", "Go module cache", "Go modules downloaded for your projects. Go downloads them again when needed.",
            new[] { U(@"go\pkg\mod") }, new[] { "GOMODCACHE" },
            RelocationMechanism.EnvironmentVariable, null, "GOMODCACHE",
            "Run go env -w GOMODCACHE={target} in a terminal (or add a user variable GOMODCACHE = {target}). Go uses it from the next build.",
            null, None, OldCopyRebuildable: true, "https://go.dev/ref/mod#module-cache"),

        new("go-build", "Go build cache", "Compiled Go packages. Go rebuilds them when needed.",
            new[] { L("go-build") }, new[] { "GOCACHE" },
            RelocationMechanism.EnvironmentVariable, null, "GOCACHE",
            "Run go env -w GOCACHE={target} in a terminal (or add a user variable GOCACHE = {target}). Go uses it from the next build.",
            null, None, OldCopyRebuildable: true, "https://pkg.go.dev/cmd/go#hdr-Build_and_test_caching"),

        new("cargo", "Cargo home", "Rust crates Cargo downloaded, plus the cargo tools you installed.",
            new[] { U(".cargo") }, new[] { "CARGO_HOME" },
            RelocationMechanism.EnvironmentVariable, null, "CARGO_HOME", VariableSteps,
            "Cargo's own programs live in its bin folder, which is on your PATH: change that PATH entry to {target}\\bin too.",
            new[] { "cargo", "rust-analyzer" }, OldCopyRebuildable: false, "https://doc.rust-lang.org/cargo/guide/cargo-home.html"),

        new("rustup", "Rust toolchains", "The Rust compilers rustup installed.",
            new[] { U(".rustup") }, new[] { "RUSTUP_HOME" },
            RelocationMechanism.EnvironmentVariable, null, "RUSTUP_HOME", VariableSteps,
            null, new[] { "cargo", "rustc", "rust-analyzer" }, OldCopyRebuildable: false, "https://rust-lang.github.io/rustup/environment-variables.html"),

        new("ollama", "Ollama models", "The AI models you downloaded with Ollama (often many GB each).",
            new[] { U(@".ollama\models") }, new[] { "OLLAMA_MODELS" },
            RelocationMechanism.EnvironmentVariable, null, "OLLAMA_MODELS",
            "Quit Ollama (tray icon → Quit), move the folder to {target}, then add a user variable OLLAMA_MODELS = {target} (Start → \"Edit environment variables for your account\" → New) and start Ollama again.",
            null, new[] { "ollama", "ollama app" }, OldCopyRebuildable: false, "https://github.com/ollama/ollama/blob/main/docs/faq.md"),

        new("huggingface", "Hugging Face cache", "AI models and datasets downloaded by Hugging Face tools.",
            new[] { U(@".cache\huggingface") }, new[] { "HF_HOME" },
            RelocationMechanism.EnvironmentVariable, null, "HF_HOME", VariableSteps,
            null, None, OldCopyRebuildable: false, "https://huggingface.co/docs/huggingface_hub/package_reference/environment_variables"),

        new("wsl", "WSL Linux disks", "The virtual disk of each Linux distribution you installed with WSL.",
            Array.Empty<CacheFolder>(), None,
            RelocationMechanism.WslMove, null, null,
            "Stop WSL, then run wsl --manage {distro} --move {target} in a terminal. WSL moves the disk itself and keeps everything inside.",
            "Needs a recent WSL from the Microsoft Store (run wsl --update first if --manage isn't known).", None, OldCopyRebuildable: false,
            "https://learn.microsoft.com/windows/wsl/basic-commands"),

        new("docker", "Docker Desktop disk", "Docker Desktop's images, containers and volumes, in one virtual disk.",
            new[] { L(@"Docker\wsl") }, None,
            RelocationMechanism.AppSetting, null, null,
            "Open Docker Desktop → Settings → Resources → Advanced → Disk image location → Browse, pick {target}, then Apply & restart. Docker moves the disk itself.",
            null, None, OldCopyRebuildable: false, "https://docs.docker.com/desktop/settings-and-maintenance/settings/"),

        new("epic-vault", "Epic vault cache", "Unreal Engine marketplace downloads the Epic Games Launcher keeps.",
            new[] { P(@"Epic\EpicGamesLauncher\VaultCache") }, None,
            RelocationMechanism.AppSetting, null, null,
            "Open the Epic Games Launcher → Settings → Edit Vault Cache Location, and pick {target}.",
            null, None, OldCopyRebuildable: false, "Epic Games Launcher settings"),

        new("adobe-media-cache", "Adobe media cache", "Preview and audio files Premiere Pro and After Effects make while you edit. They're rebuilt when needed.",
            new[] { R(@"Adobe\Common\Media Cache Files"), R(@"Adobe\Common\Media Cache") }, None,
            RelocationMechanism.AppSetting, null, null,
            "In Premiere Pro or After Effects open Edit → Preferences → Media Cache, click Browse next to the location and pick {target}.",
            null, new[] { "Adobe Premiere Pro", "AfterFX" }, OldCopyRebuildable: false,
            "https://helpx.adobe.com/premiere-pro/using/preferences.html"),
    };

    public static AppCacheTool? Find(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
