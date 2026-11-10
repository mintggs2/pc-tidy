using System.Diagnostics;
using System.Text;

namespace PcTidy.Features.PcCare.Source;

/// <summary>
/// Runs the two read-only listings PC care needs, with fixed argument lists (nothing else can be passed in):
/// <c>winget upgrade --include-unknown --disable-interactivity</c> (lists updates; installs nothing and accepts no terms)
/// and <c>nvidia-smi --query-gpu=... --format=csv,noheader,nounits</c> (reads GPU temperature). Every change is a PlanItem
/// carried out by PcTidy.Platform.
/// </summary>
internal static class ReadOnlyTool
{
    public enum Query { WingetUpgradeList, NvidiaSmiTemperature }

    public sealed record Output(int ExitCode, string Text);

    private static readonly IReadOnlyDictionary<Query, string[]> Commands = new Dictionary<Query, string[]>
    {
        [Query.WingetUpgradeList] = new[] { "upgrade", "--include-unknown", "--disable-interactivity" },
        [Query.NvidiaSmiTemperature] = new[] { "--query-gpu=name,temperature.gpu,fan.speed", "--format=csv,noheader,nounits" },
    };

    private static readonly IReadOnlyDictionary<Query, TimeSpan> Timeouts = new Dictionary<Query, TimeSpan>
    {
        [Query.WingetUpgradeList] = TimeSpan.FromSeconds(120),
        [Query.NvidiaSmiTemperature] = TimeSpan.FromSeconds(15),
    };

    /// <summary>winget is an app execution alias in the user's WindowsApps folder.</summary>
    public static string? WingetPath()
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(p) ? p : null;
    }

    public static string? NvidiaSmiPath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The tool's output, or null when it's missing, can't start or times out.</summary>
    public static async Task<Output?> RunAsync(Query query, string exe, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, // boundary-ok: no shell; one of the fixed read-only listings above runs directly
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true, // closed straight away, so a prompt can never wait for an answer
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in Commands[query]) psi.ArgumentList.Add(a);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeouts[query]);
        Process? process = null;
        try
        {
            process = Process.Start(psi); // boundary-ok: read-only listing from the fixed table above
            if (process is null) return null;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return new Output(process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
            process?.Dispose();
        }
    }
}
