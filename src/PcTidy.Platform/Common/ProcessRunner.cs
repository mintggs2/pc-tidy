using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PcTidy.Platform;

/// <summary>Outcome of running a Windows tool. Success is judged by the exit code, never by its (localized) text.</summary>
public sealed record ProcessResult(int ExitCode, bool TimedOut, string Output);

/// <summary>
/// Runs a fixed executable by absolute path with an argument list: no shell, no window, output captured.
/// Only Platform code calls this, and only with paths it built (or verified) itself.
/// When PC Tidy runs with admin rights (the admin helper), every program it starts gets
/// <see cref="HelperEnvironment.CleanBlock"/> instead of the user's environment.
/// </summary>
internal static class ProcessRunner
{
    static ProcessRunner() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Task<ProcessResult> RunAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, string? logPath, CancellationToken ct)
        => RunCoreAsync(exePath, arguments, null, timeout, logPath, null, ct);

    /// <summary>
    /// For vendor uninstallers only: the argument text is handed to Windows exactly as the app registered it
    /// (CreateProcess, never a shell), because many uninstallers parse their own command line.
    /// </summary>
    public static Task<ProcessResult> RunRawAsync(string exePath, string rawArguments, TimeSpan timeout, string? logPath, CancellationToken ct)
        => RunCoreAsync(exePath, Array.Empty<string>(), rawArguments, timeout, logPath, null, ct);

    /// <summary>
    /// Like <see cref="RunRawAsync"/>, for a program about to run with admin rights: the program file is opened first
    /// with read-only sharing (nobody can change, rename or delete it while that handle is open), <paramref name="recheck"/>
    /// (the folder-permission check) runs while it is held, and the handle is closed only once the process has started.
    /// A non-null answer from <paramref name="recheck"/> stops it with <see cref="UnsafeProgramException"/>.
    /// </summary>
    public static Task<ProcessResult> RunRawVerifiedAsync(string exePath, string rawArguments, Func<string?> recheck, TimeSpan timeout, string? logPath, CancellationToken ct)
        => RunCoreAsync(exePath, Array.Empty<string>(), rawArguments, timeout, logPath, recheck, ct);

    /// <summary>True when programs started from this process get the clean environment (the admin helper).</summary>
    internal static bool UsesCleanEnvironment => ProcessElevation.IsElevated;

    private static void PrepareEnvironment(ProcessStartInfo psi)
    {
        if (UsesCleanEnvironment) HelperEnvironment.Apply(psi, HelperEnvironment.CleanBlock(HelperEnvironment.ChildTempFolder));
    }

    /// <summary>Starts a program that keeps running (OneDrive /background) without waiting for it.</summary>
    public static void Launch(string exePath, IReadOnlyList<string> arguments)
    {
        if (!Path.IsPathFullyQualified(exePath) || !File.Exists(exePath))
            throw new FileNotFoundException("This program isn't on this PC.", exePath);
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exePath)! };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        PrepareEnvironment(psi);
        using var _ = Process.Start(psi);
    }

    private static async Task<ProcessResult> RunCoreAsync(string exePath, IReadOnlyList<string> arguments, string? raw, TimeSpan timeout, string? logPath,
        Func<string?>? recheck, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(exePath) || !File.Exists(exePath))
            throw new FileNotFoundException("This Windows tool isn't on this PC.", exePath);

        // Console tools write in the OEM code page; this only matters for the log file, never for decisions.
        var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = oem,
            StandardErrorEncoding = oem,
            WorkingDirectory = Environment.SystemDirectory,
        };
        if (raw is not null) psi.Arguments = raw;
        else foreach (var a in arguments) psi.ArgumentList.Add(a);
        PrepareEnvironment(psi);

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        ct.ThrowIfCancellationRequested();
        if (recheck is null)
        {
            process.Start();
        }
        else
        {
            // Read-only sharing: while this handle is open nobody can write, rename or delete the program file.
            using var hold = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.None);
            if (recheck() is { } unsafeReason) throw new UnsafeProgramException(unsafeReason);
            process.Start();
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Once started, system tools (DISM, cleanmgr) are allowed to finish: killing them mid-way is worse than waiting.
        using var timeoutCts = new CancellationTokenSource(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }

        string text;
        lock (output) text = output.ToString();
        var shown = raw ?? string.Join(' ', arguments);
        if (logPath is not null) WriteLog(logPath, exePath, shown, timedOut ? null : process.ExitCode, text);
        return new ProcessResult(timedOut ? -1 : process.ExitCode, timedOut, text);
    }

    private static void WriteLog(string logPath, string exePath, string arguments, int? exitCode, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var header = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {exePath} {arguments}{Environment.NewLine}" +
                         $"exit code: {(exitCode?.ToString(CultureInfo.InvariantCulture) ?? "still running (timed out)")}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(logPath, header + text + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing log never fails the step.
        }
    }
}

/// <summary>The program (or its folder) could be changed by an account without admin rights, so it wasn't started.</summary>
public sealed class UnsafeProgramException(string reason) : Exception(reason);
