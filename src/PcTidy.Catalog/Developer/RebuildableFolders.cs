namespace PcTidy.Catalog.Developer;

/// <summary>What kind of rebuildable folder a rule finds (drives the wording and which tool rebuilds it).</summary>
public enum RebuildKind { NodeModules, DotNetBin, DotNetObj, VisualStudioCache, CargoTarget, PythonBytecode, PythonVenv, BuildOutput }

/// <summary>How sure PC Tidy is that a folder only holds things a tool recreates.</summary>
public enum RebuildConfidence
{
    /// <summary>The tool's own marker is inside (project.assets.json, CACHEDIR.TAG, CMakeCache.txt...): offered as Safe.</summary>
    Certain,
    /// <summary>It sits next to the right project file but carries no proof: offered as Check (never ticked for you).</summary>
    Likely,
}

/// <summary>
/// One kind of folder that a build tool or package manager recreates, flagged only next to its project marker
/// (Research "Developer cleanup": node_modules+package.json, bin/obj+*.csproj, .vs+*.sln, target+Cargo.toml,
/// __pycache__, .venv+pyvenv.cfg, build next to build.gradle*/CMakeLists.txt/package.json).
/// The folder names here must stay within the SafetyGuard's rebuildable list for feature "developer".
/// </summary>
/// <param name="Id">Stable id, also used in group/item ids.</param>
/// <param name="FolderName">Exact folder name (case-insensitive).</param>
/// <param name="SiblingMarkers">
/// At least one of these must sit in the same folder as the candidate ("package.json", "*.csproj").
/// Empty = anywhere inside a project (only used for <c>__pycache__</c>, whose contents prove what it is).
/// </param>
/// <param name="ProofInside">Names inside the candidate that make it <see cref="RebuildConfidence.Certain"/>.</param>
/// <param name="ProofRequired">True: without proof the folder is not offered at all (.venv needs pyvenv.cfg).</param>
/// <param name="OnlyExtensions">If set, every file directly inside must have one of these extensions (or it isn't offered).</param>
/// <param name="AlwaysCheck">True: never Safe, even with proof (it holds your IDE state or your own environment).</param>
/// <param name="Label">Short plain name for titles ("Packages (node_modules)").</param>
/// <param name="Reason">What it is and how it comes back. Shown on the row.</param>
/// <param name="CloseApps">Apps that hold it open while you work (process names without .exe).</param>
public sealed record RebuildableFolderRule(
    string Id,
    string FolderName,
    RebuildKind Kind,
    IReadOnlyList<string> SiblingMarkers,
    IReadOnlyList<string> ProofInside,
    bool ProofRequired,
    IReadOnlyList<string>? OnlyExtensions,
    bool AlwaysCheck,
    string Label,
    string Reason,
    IReadOnlyList<string> CloseApps)
{
    /// <summary>True if <paramref name="name"/> matches one of the patterns (exact name, or "*.ext").</summary>
    public static bool MatchesPattern(string name, string pattern)
        => pattern.StartsWith("*.", StringComparison.Ordinal)
            ? name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)
            : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);

    /// <summary>Does the candidate's folder (listed by <paramref name="siblingNames"/>) hold this rule's marker?</summary>
    public bool HasSiblingMarker(IEnumerable<string> siblingNames)
        => SiblingMarkers.Count == 0 || siblingNames.Any(n => SiblingMarkers.Any(p => MatchesPattern(n, p)));
}

/// <summary>The rebuildable-folder table. First matching rule wins (so CMake's proof is tried before the package.json fallback).</summary>
public static class RebuildableFolders
{
    private static readonly string[] DotNetProjects = { "*.csproj", "*.fsproj", "*.vbproj" };
    private static readonly string[] VisualStudio = { "devenv" };
    private static readonly string[] None = Array.Empty<string>();

    public static readonly IReadOnlyList<RebuildableFolderRule> All = new RebuildableFolderRule[]
    {
        new("node-modules", "node_modules", RebuildKind.NodeModules, new[] { "package.json" }, None, false, null, false,
            "Packages (node_modules)",
            "Packages npm, pnpm or Yarn installed for this project. Running 'npm install' (or pnpm/yarn install) downloads them again. Your code isn't touched.",
            None),
        new("dotnet-obj", "obj", RebuildKind.DotNetObj, DotNetProjects, new[] { "project.assets.json" }, false, null, false,
            ".NET build files (obj)",
            "Temporary files from building this .NET project. The next build recreates them. Your code isn't touched.",
            VisualStudio),
        new("dotnet-bin", "bin", RebuildKind.DotNetBin, DotNetProjects, new[] { "Debug", "Release" }, false, null, false,
            ".NET build output (bin)",
            "The compiled program from building this .NET project. The next build makes it again. Your code isn't touched.",
            VisualStudio),
        new("visual-studio", ".vs", RebuildKind.VisualStudioCache, new[] { "*.sln", "*.slnx" }, None, false, null, true,
            "Visual Studio cache (.vs)",
            "Visual Studio's own cache for this solution. It's rebuilt when you open the solution, but Visual Studio forgets your open tabs and breakpoints.",
            VisualStudio),
        new("cargo-target", "target", RebuildKind.CargoTarget, new[] { "Cargo.toml" }, new[] { "CACHEDIR.TAG" }, false, null, false,
            "Rust build output (target)",
            "Everything Cargo built for this Rust project. 'cargo build' makes it again. Your code isn't touched.",
            None),
        new("python-bytecode", "__pycache__", RebuildKind.PythonBytecode, None, None, false, new[] { ".pyc" }, false,
            "Python bytecode (__pycache__)",
            "Pre-compiled copies of Python files. Python recreates them the next time the code runs. Your code isn't touched.",
            None),
        new("python-venv", ".venv", RebuildKind.PythonVenv, None, new[] { "pyvenv.cfg" }, true, null, true,
            "Python environment (.venv)",
            "This project's private Python environment with its installed packages.",
            None),
        new("gradle-build", "build", RebuildKind.BuildOutput, new[] { "build.gradle", "build.gradle.kts" }, None, false, null, false,
            "Gradle build output (build)",
            "Everything Gradle built for this project. The next build makes it again. Your code isn't touched.",
            None),
        new("cmake-build", "build", RebuildKind.BuildOutput, new[] { "CMakeLists.txt" }, new[] { "CMakeCache.txt" }, false, null, false,
            "CMake build folder (build)",
            "CMake's build folder for this project. Configuring and building again recreates it. Your code isn't touched.",
            None),
        new("node-build", "build", RebuildKind.BuildOutput, new[] { "package.json" }, None, false, null, true,
            "Build output (build)",
            "Usually what this project's build script produced. Some tools keep icons or settings in a 'build' folder, so have a look first.",
            None),
    };

    /// <summary>Every folder name the table can flag (these are the names the SafetyGuard lets "developer" quarantine).</summary>
    public static readonly IReadOnlySet<string> FolderNames = All.Select(r => r.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The rules for one folder name, in priority order.</summary>
    public static IEnumerable<RebuildableFolderRule> For(string folderName) => All.Where(r => r.FolderName.Equals(folderName, StringComparison.OrdinalIgnoreCase));
}
