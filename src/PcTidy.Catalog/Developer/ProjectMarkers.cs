namespace PcTidy.Catalog.Developer;

/// <summary>
/// What makes a folder a code project for Developer cleanup (Plan §6 "Code projects", Research "Developer cleanup").
/// Only names are compared: PC Tidy reads folder listings, never file contents, to find projects.
/// </summary>
public static class ProjectMarkers
{
    /// <summary>Exact file or folder names that mark a project root.</summary>
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
        "package.json", "pyproject.toml", "setup.py", "requirements.txt", "Pipfile",
        "Cargo.toml", "go.mod", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts",
        "CMakeLists.txt", "pom.xml", "composer.json", "Gemfile",
    };

    /// <summary>File extensions that mark a project root (solutions and project files).</summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".sln", ".slnx", ".csproj", ".fsproj", ".vbproj", ".vcxproj",
    };

    /// <summary>Version-control folders: never walked into, never touched. Git's own files give the project's age.</summary>
    public static readonly IReadOnlySet<string> VersionControlFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
    };

    /// <summary>
    /// Files inside <c>.git</c> whose modified time moves whenever you commit, check out, stage or fetch.
    /// The newest of these (and of the project's own files) is the project's last activity. Git itself is never run.
    /// </summary>
    public static readonly IReadOnlyList<string> GitActivityFiles = new[] { @"logs\HEAD", "index", "FETCH_HEAD" };

    /// <summary>
    /// Python dependency lists. A <c>.venv</c> can be rebuilt reliably only when one of these sits in the project
    /// (or in the folder that holds the <c>.venv</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> PythonDependencyFiles = new[]
    {
        "requirements.txt", "requirements-dev.txt", "poetry.lock", "Pipfile.lock", "uv.lock", "pdm.lock", "pyproject.toml", "setup.py",
    };

    /// <summary>True when a listed name marks a project root.</summary>
    public static bool IsMarker(string name)
        => Names.Contains(name) || Extensions.Contains(Path.GetExtension(name));
}
