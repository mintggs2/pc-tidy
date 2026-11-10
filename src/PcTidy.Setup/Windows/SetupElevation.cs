using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace PcTidy.Setup.Windows;

/// <summary>
/// Runs the admin part of Setup (copying into Program Files, the HKLM entry, all-users shortcuts) in a copy of this
/// program started with admin rights, and waits for its exit code. Real: Verb=runas (Windows' admin prompt).
/// Tests: a fake that runs the admin part in-process with the test environment.
/// </summary>
public interface ISetupElevation
{
    /// <summary>True when this process already has admin rights (then nothing is started).</summary>
    bool IsElevated { get; }

    /// <summary>The admin copy's exit code; <see cref="Program.ExitDeclined"/> when the prompt was refused.</summary>
    Task<int> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

public sealed class RunAsSetupElevation : ISetupElevation
{
    public bool IsElevated
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public async Task<int> RunElevatedAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Can't tell where Setup is running from.");
        // Only fixed switches are passed (no paths): the admin copy decides every folder itself.
        if (arguments.Any(a => a.Length == 0 || !a.StartsWith("--", StringComparison.Ordinal) || a.Any(c => c is ' ' or '"')))
            throw new ArgumentException("Only plain switches are passed to the admin part of Setup.", nameof(arguments));
        // Never ask for admin rights while the user's environment would load another program's code into the admin copy.
        if (ElevationGuard.BeforePrompt() is not null) return Program.ExitUnsafe;
        var psi = new ProcessStartInfo(self)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', arguments),
            WorkingDirectory = Environment.SystemDirectory,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return Program.ExitFailed;
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 1223 /* cancelled */ or 5 /* access denied */)
        {
            return Program.ExitDeclined;
        }
    }
}
