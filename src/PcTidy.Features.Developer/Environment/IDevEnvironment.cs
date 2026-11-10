using System.Text.Json;
using PcTidy.Core;

namespace PcTidy.Features.Developer.Environment;

/// <summary>A WSL distribution registered for this user (HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\{GUID}).</summary>
/// <param name="Name">DistributionName.</param>
/// <param name="BasePath">BasePath: the folder holding its virtual disk.</param>
/// <param name="VhdFileName">VhdFileName (usually ext4.vhdx).</param>
/// <param name="Version">1 = WSL 1 (plain folder, no disk to shrink), 2 = WSL 2 (virtual disk).</param>
public sealed record WslDistro(string Name, string BasePath, string VhdFileName, int Version);

/// <summary>
/// Everything Developer cleanup reads outside plain files: PATH, environment variables, WSL's registry list and Docker's
/// disk usage. All read-only. The real PC uses <see cref="WindowsDevEnvironment"/>; the sandbox never reads the real
/// PC (tests stay deterministic) and uses <see cref="SandboxDevEnvironment"/>.
/// </summary>
public interface IDevEnvironment
{
    /// <summary>The folders on PATH, in order.</summary>
    IReadOnlyList<string> SearchPath();
    /// <summary>An environment variable, or null when it isn't set.</summary>
    string? Variable(string name);
    IReadOnlyList<WslDistro> WslDistros();
    /// <summary>
    /// Version of the WSL package (the Microsoft Store / MSI WSL, which has <c>wsl --manage</c>), or null when only the
    /// WSL built into Windows is there (it has no <c>--manage</c>). Read from the file version, never from command output.
    /// </summary>
    Version? WslPackageVersion();
    /// <summary>
    /// Docker Engine's <c>GET /system/df</c> answer (JSON, exact byte sizes), or null when Docker isn't running.
    /// Read-only; it's what <c>docker system df</c> shows, without running the CLI or parsing its text.
    /// </summary>
    Task<string?> DockerDiskUsageJsonAsync(CancellationToken ct);
}

/// <summary>
/// Sandbox: reads &lt;root&gt;\Sim\developer.json when present (paths relative to the sandbox root), otherwise an
/// empty PATH, no WSL and no Docker. Example:
/// <code>{ "path": ["C\\Program Files\\nodejs"], "variables": { "PIP_CACHE_DIR": "D\\pip" },
///   "wsl": [ { "name": "Ubuntu", "basePath": "C\\Users\\tester\\AppData\\Local\\wsl\\ubuntu", "version": 2 } ],
///   "wslVersion": "2.3.26", "dockerDf": "docker-df.json" }</code>
/// </summary>
public sealed class SandboxDevEnvironment : IDevEnvironment
{
    private sealed record SimWsl(string? Name, string? BasePath, string? VhdFileName, int? Version);
    private sealed record SimFile(string[]? Path, Dictionary<string, string>? Variables, SimWsl[]? Wsl, string? DockerDf, string? WslVersion = null);

    private readonly string _root;
    private readonly SimFile _sim;

    public SandboxDevEnvironment(IKnownLocations locations)
    {
        _root = locations is SandboxKnownLocations s ? s.Root : "";
        _sim = Load(_root);
    }

    private static SimFile Load(string root)
    {
        if (root.Length == 0) return new SimFile(null, null, null, null);
        var file = Path.Combine(root, "Sim", "developer.json");
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<SimFile>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SimFile(null, null, null, null)
                : new SimFile(null, null, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SimFile(null, null, null, null);
        }
    }

    private string Full(string p) => Path.IsPathFullyQualified(p) ? p : Path.Combine(_root, p);

    public IReadOnlyList<string> SearchPath() => (_sim.Path ?? Array.Empty<string>()).Select(Full).ToList();

    public string? Variable(string name)
        => _sim.Variables is { } v && v.TryGetValue(name, out var value) ? Full(value) : null;

    public IReadOnlyList<WslDistro> WslDistros()
        => (_sim.Wsl ?? Array.Empty<SimWsl>())
            .Where(w => !string.IsNullOrWhiteSpace(w.Name) && !string.IsNullOrWhiteSpace(w.BasePath))
            .Select(w => new WslDistro(w.Name!, Full(w.BasePath!), w.VhdFileName ?? "ext4.vhdx", w.Version ?? 2))
            .ToList();

    /// <summary>"wslVersion": "2.3.26" in developer.json; missing = only the WSL built into Windows.</summary>
    public Version? WslPackageVersion() => Version.TryParse(_sim.WslVersion, out var v) ? v : null;

    public Task<string?> DockerDiskUsageJsonAsync(CancellationToken ct)
    {
        if (_sim.DockerDf is not { Length: > 0 } name) return Task.FromResult<string?>(null);
        var file = Path.Combine(_root, "Sim", name);
        try { return Task.FromResult<string?>(File.Exists(file) ? File.ReadAllText(file) : null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Task.FromResult<string?>(null); }
    }
}
