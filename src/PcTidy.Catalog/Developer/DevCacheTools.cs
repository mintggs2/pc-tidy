namespace PcTidy.Catalog.Developer;

/// <summary>Where a cache folder lives (resolved at runtime for this PC; nothing is hard-coded to one machine).</summary>
public enum CacheBase
{
    LocalAppData,
    RoamingAppData,
    UserProfile,
    UserTemp,
    ProgramFiles,
    /// <summary>The root of every fixed drive (pnpm keeps a store per drive: <c>X:\.pnpm-store</c>).</summary>
    EachFixedDriveRoot,
    /// <summary>The folder named by <see cref="CacheLocation.Variable"/> (when that environment variable is set).</summary>
    Variable,
}

/// <summary>One candidate cache folder.</summary>
/// <param name="Variable">Environment variable that moves this cache somewhere else (it wins when set).</param>
/// <param name="VariableSubPath">Sub-folder of the variable's folder (GRADLE_USER_HOME → caches).</param>
public sealed record CacheLocation(CacheBase Base, string RelativePath, string? Variable = null, string VariableSubPath = "");

/// <summary>
/// A package manager whose download cache PC Tidy can ask to clean with the tool's OWN command
/// (<c>ToolIds.DevCacheClean</c>, Args["tool"] = <see cref="Id"/>). PC Tidy never deletes these folders itself.
/// </summary>
/// <param name="Id">The exact Args["tool"] value the SafetyGuard accepts: npm|pnpm|yarn|pip|nuget-http|gradle.</param>
/// <param name="Executables">File names looked for on PATH and in the tool's usual install folders.</param>
/// <param name="KnownInstallFolders">Usual install folders, relative to a base (checked read-only when PATH has no hit).</param>
/// <param name="Command">The command the tool runs, shown so you know exactly what happens.</param>
/// <param name="FreesEverything">
/// True: the command empties the cache, so its whole size is freed. False: the tool decides what is unused
/// (npm verify, pnpm prune, Gradle's old versions), so no amount is promised.
/// </param>
/// <param name="Redownload">Plain-English note on what gets downloaded again.</param>
/// <param name="CloseApps">Apps that must be closed first (process names without .exe).</param>
public sealed record DevCacheTool(
    string Id,
    string Name,
    IReadOnlyList<string> Executables,
    IReadOnlyList<CacheLocation> KnownInstallFolders,
    IReadOnlyList<CacheLocation> Caches,
    string Command,
    bool FreesEverything,
    string Redownload,
    IReadOnlyList<string> CloseApps);

/// <summary>
/// The package caches (Research "Developer cleanup": npm cache verify over clean --force; pnpm store prune; yarn cache
/// clean; pip cache purge; dotnet nuget locals http-cache|temp|plugins-cache --clear; Gradle --stop then old caches).
/// </summary>
public static class DevCacheTools
{
    public static readonly IReadOnlyList<DevCacheTool> All = new DevCacheTool[]
    {
        new("npm", "npm", new[] { "npm.cmd", "npm.exe" },
            new[] { new CacheLocation(CacheBase.ProgramFiles, "nodejs"), new CacheLocation(CacheBase.RoamingAppData, "npm") },
            new[] { new CacheLocation(CacheBase.LocalAppData, "npm-cache", "npm_config_cache") },
            "npm cache verify", false,
            "npm checks its download cache and removes anything it no longer needs. Packages you use again are downloaded when a project installs them.",
            Array.Empty<string>()),
        new("pnpm", "pnpm", new[] { "pnpm.cmd", "pnpm.exe" },
            new[] { new CacheLocation(CacheBase.LocalAppData, "pnpm") },
            new[] { new CacheLocation(CacheBase.LocalAppData, @"pnpm\store", "PNPM_STORE_DIR"), new CacheLocation(CacheBase.EachFixedDriveRoot, ".pnpm-store") },
            "pnpm store prune", false,
            "pnpm removes packages that no project uses any more. Projects that need them download them again on install.",
            Array.Empty<string>()),
        new("yarn", "Yarn", new[] { "yarn.cmd", "yarn.exe" },
            new[] { new CacheLocation(CacheBase.RoamingAppData, "npm") },
            new[] { new CacheLocation(CacheBase.LocalAppData, @"Yarn\Cache", "YARN_CACHE_FOLDER"), new CacheLocation(CacheBase.LocalAppData, @"Yarn\Berry\cache") },
            "yarn cache clean", true,
            "Yarn empties its download cache. Each project downloads its packages again the next time you install.",
            Array.Empty<string>()),
        new("pip", "pip", new[] { "pip.exe", "pip3.exe" },
            Array.Empty<CacheLocation>(),
            new[] { new CacheLocation(CacheBase.LocalAppData, @"pip\cache", "PIP_CACHE_DIR") },
            "pip cache purge", true,
            "pip empties its download cache. Packages are downloaded again the next time you install them. Installed packages keep working.",
            Array.Empty<string>()),
        new("nuget-http", "NuGet (download cache)", new[] { "dotnet.exe", "nuget.exe" },
            new[] { new CacheLocation(CacheBase.ProgramFiles, "dotnet") },
            new[]
            {
                new CacheLocation(CacheBase.LocalAppData, @"NuGet\v3-cache", "NUGET_HTTP_CACHE_PATH"),
                new CacheLocation(CacheBase.LocalAppData, @"NuGet\plugins-cache", "NUGET_PLUGINS_CACHE_PATH"),
                new CacheLocation(CacheBase.UserTemp, "NuGetScratch"),
            },
            "dotnet nuget locals http-cache, temp and plugins-cache --clear", true,
            "NuGet empties its download cache. Your projects' installed packages stay, so nothing has to be downloaded until a package changes.",
            new[] { "devenv" }),
        new("gradle", "Gradle", new[] { "gradle.bat", "gradle.exe" },
            Array.Empty<CacheLocation>(),
            new[] { new CacheLocation(CacheBase.UserProfile, @".gradle\caches", "GRADLE_USER_HOME", "caches") },
            "gradle --stop, then remove caches of older Gradle versions", false,
            "Gradle's daemon stops and caches of Gradle versions you no longer use are removed. The next build downloads what it needs.",
            new[] { "java", "studio64" }),
    };

    /// <summary>
    /// NuGet's global packages folder. Clearing it makes EVERY .NET project download all its packages again, so it
    /// is only offered in Expert mode with Visual Studio closed (and as a quarantine move, so it can be put back).
    /// </summary>
    public static readonly CacheLocation NuGetGlobalPackages = new(CacheBase.UserProfile, @".nuget\packages", "NUGET_PACKAGES");

    public static readonly IReadOnlyList<string> NuGetGlobalPackagesCloseApps = new[] { "devenv", "MSBuild", "dotnet", "rider64" };

    public static DevCacheTool? Find(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
