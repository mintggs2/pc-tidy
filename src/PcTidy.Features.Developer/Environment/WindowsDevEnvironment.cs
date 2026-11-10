using System.IO.Pipes;
using System.Net.Http;
using Microsoft.Win32;

namespace PcTidy.Features.Developer.Environment;

/// <summary>
/// The real PC, read-only: PATH and variables of this process, WSL's per-user registry list, and Docker Engine's
/// local API over its named pipe (a plain HTTP GET, the same data <c>docker system df</c> prints). Nothing is run,
/// nothing is written, and no localized text is parsed.
/// </summary>
public sealed class WindowsDevEnvironment : IDevEnvironment
{
    private const string LxssKey = @"Software\Microsoft\Windows\CurrentVersion\Lxss";
    /// <summary>Docker Desktop's engine pipes (classic name first, then the newer Linux-engine name).</summary>
    private static readonly string[] DockerPipes = { "docker_engine", "dockerDesktopLinuxEngine" };
    private static readonly TimeSpan PipeConnectTimeout = TimeSpan.FromMilliseconds(1500);
    /// <summary>/system/df adds up every image and volume, which can take a while on a big install.</summary>
    private static readonly TimeSpan DockerAnswerTimeout = TimeSpan.FromSeconds(30);

    public IReadOnlyList<string> SearchPath()
        => (System.Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Trim('"'))
            .Where(p => p.Length > 0 && Path.IsPathFullyQualified(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public string? Variable(string name)
    {
        var v = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public IReadOnlyList<WslDistro> WslDistros()
    {
        var list = new List<WslDistro>();
        try
        {
            using var lxss = Registry.CurrentUser.OpenSubKey(LxssKey, writable: false);
            if (lxss is null) return list;
            foreach (var id in lxss.GetSubKeyNames())
            {
                try
                {
                    using var d = lxss.OpenSubKey(id, writable: false);
                    if (d?.GetValue("DistributionName") is not string name || d.GetValue("BasePath") is not string basePath) continue;
                    var vhd = d.GetValue("VhdFileName") as string ?? "ext4.vhdx";
                    var version = d.GetValue("Version") is int v ? v : 2;
                    list.Add(new WslDistro(name, StripLiteralPrefix(basePath), vhd, version));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* one unreadable entry */ }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { /* no list */ }
        return list;
    }

    /// <summary>
    /// The WSL package (Store / MSI, 2.0 and later) installs its own wsl.exe in %ProgramFiles%\WSL, and Windows' built-in
    /// wsl.exe hands over to it. Only its numeric file version is read; no WSL command is run.
    /// </summary>
    public Version? WslPackageVersion()
    {
        try
        {
            var exe = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "WSL", "wsl.exe");
            if (!File.Exists(exe)) return null;
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return info.FileMajorPart > 0 ? new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>WSL writes BasePath as "\\?\C:\..." for some distros.</summary>
    private static string StripLiteralPrefix(string p) => p.StartsWith(@"\\?\", StringComparison.Ordinal) ? p[4..] : p;

    public async Task<string?> DockerDiskUsageJsonAsync(CancellationToken ct)
    {
        foreach (var pipe in DockerPipes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var handler = new SocketsHttpHandler
                {
                    ConnectCallback = async (_, token) =>
                    {
                        var stream = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                        try
                        {
                            await stream.ConnectAsync(PipeConnectTimeout, token).ConfigureAwait(false);
                            return stream;
                        }
                        catch
                        {
                            await stream.DisposeAsync().ConfigureAwait(false);
                            throw;
                        }
                    },
                };
                using var client = new HttpClient(handler) { Timeout = DockerAnswerTimeout };
                // The host name is ignored on a pipe; "docker" is what the Docker CLI sends.
                using var response = await client.GetAsync("http://docker/system/df", ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                // Not running (no pipe), not answering, or no access: try the next pipe, then report "not running".
            }
        }
        return null;
    }
}
