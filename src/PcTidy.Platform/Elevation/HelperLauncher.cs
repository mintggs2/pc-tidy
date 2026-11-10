using System.ComponentModel;
using System.Diagnostics;
using static PcTidy.Platform.NativeMethods;

namespace PcTidy.Platform;

/// <summary>
/// The admin prompt was cancelled or refused. Admin steps are then Skipped with this friendly reason.
/// Messages are short reasons without a full stop; callers add what was skipped.
/// </summary>
public sealed class ElevationDeclinedException(string message) : Exception(message);

/// <summary>The admin helper didn't start, didn't connect, or stopped. Message is plain English.</summary>
public sealed class ElevationUnavailableException(string message) : Exception(message);

/// <summary>
/// PC Tidy didn't ask for admin rights because it wouldn't be safe (a portable copy, or a setting that would let
/// another program into the helper). Message is a complete plain-English sentence, shown as is.
/// </summary>
public sealed class ElevationRefusedException(string message) : Exception(message);

/// <summary>A started helper process (id may be unknown). Disposing releases the process object we hold, not the process.</summary>
internal sealed record HelperProcess(int? ProcessId, Func<bool> HasExited, IDisposable? Handle = null) : IDisposable
{
    public void Dispose() => Handle?.Dispose();
}

/// <summary>Starts the admin helper. Real: <see cref="RunAsLauncher"/>. Tests: an in-process fake (no UAC ever).</summary>
internal interface IHelperLauncher
{
    /// <summary>Blocks while Windows shows the admin prompt. Throws <see cref="ElevationDeclinedException"/> if refused.</summary>
    HelperProcess Launch(string pipeName, int parentPid);
}

/// <summary>Relaunches this exe (<see cref="Environment.ProcessPath"/>) with <c>--elevated &lt;pipe&gt; &lt;pid&gt;</c> and Verb=runas.</summary>
internal sealed class RunAsLauncher : IHelperLauncher
{
    public HelperProcess Launch(string pipeName, int parentPid)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            throw new ElevationUnavailableException("PC Tidy couldn't find its own program file to start the admin helper");

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"{ElevationProtocol.ElevatedSwitch} {pipeName} {parentPid}",
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            var process = Process.Start(psi);
            if (process is null) return new HelperProcess(null, () => false);
            return new HelperProcess(process.Id, () =>
            {
                try { return process.HasExited; }
                catch { return false; }
            }, process);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            throw new ElevationDeclinedException("You said no to the admin prompt");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_ACCESS_DENIED)
        {
            throw new ElevationDeclinedException("Windows didn't give admin rights on this account");
        }
        catch (Win32Exception)
        {
            throw new ElevationUnavailableException("PC Tidy's admin helper couldn't start");
        }
    }
}
