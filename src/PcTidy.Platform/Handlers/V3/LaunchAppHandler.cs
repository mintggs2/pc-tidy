using System.Diagnostics;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>Starts a user program without admin rights. Real: Process.Start, or Explorer's ShellExecute when PC Tidy is elevated.</summary>
public interface IAppLauncher
{
    bool TouchesRealSystem { get; }
    bool FileExists(string path);
    void Launch(string exePath, string arguments);
}

public sealed class UnelevatedAppLauncher : IAppLauncher
{
    public bool TouchesRealSystem => true;
    public bool FileExists(string path) => File.Exists(path);

    public void Launch(string exePath, string arguments)
    {
        var directory = Path.GetDirectoryName(exePath)!;
        if (ProcessElevation.IsElevated)
        {
            // Never hand admin rights on: Explorer starts it with the signed-in user's normal token.
            ShellInterop.ShellExecuteAsExplorer(exePath, arguments, directory);
            return;
        }
        using var _ = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false, Arguments = arguments, WorkingDirectory = directory });
    }
}

/// <summary>
/// LaunchApp. Args: "exePath" (an existing .exe outside the Windows folder, not a shell, script host or system tool),
/// optional "arguments" (one string, at most 1,024 characters, handed to Windows as-is; never through a shell).
/// Starts the program unelevated as the signed-in user. Never from the admin helper. Not reversible (close it yourself).
/// </summary>
public sealed class LaunchAppHandler(IAppLauncher launcher) : SystemActionHandler
{
    /// <summary>Programs never started this way (a trailing * matches a prefix).</summary>
    internal static readonly string[] NeverLaunched =
    {
        "cmd", "powershell", "powershell_ise", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec", "reg", "regedit",
        "schtasks", "sc", "bcdedit", "netsh", "wmic", "bash", "wsl", "wslhost", "conhost", "certutil", "bitsadmin", "installutil", "msbuild",
        "cmstp", "odbcconf", "forfiles", "pcalua", "hh", "control", "mmc", "taskkill", "vssadmin", "diskpart", "format", "cipher", "PcTidy*",
    };

    public LaunchAppHandler() : this(new UnelevatedAppLauncher()) { }

    protected override ActionKind Kind => ActionKind.LaunchApp;
    protected override bool TouchesRealSystem => launcher.TouchesRealSystem;
    protected override bool PerUserOnly(PlanItem item) => true;

    private static string Exe(PlanItem item) => item.Arg("exePath").Trim();
    private static string Arguments(PlanItem item) => item.Arg("arguments").Trim();

    protected override string? Validate(PlanItem item)
    {
        var exe = Exe(item);
        if (!PathArgs.IsPlainLocal(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "This step doesn't name a program on this PC.";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (PathUtil.IsUnder(exe, windows)) return "PC Tidy doesn't start Windows' own tools.";
        var name = Path.GetFileNameWithoutExtension(exe);
        if (NeverLaunched.Any(p => p.EndsWith('*') ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) : name.Equals(p, StringComparison.OrdinalIgnoreCase)))
            return "PC Tidy never starts command shells, script hosts or system tools.";
        var args = Arguments(item);
        if (args.Length > 1024 || args.Any(char.IsControl)) return "The program's settings are not valid.";
        return null;
    }

    protected override string Describe(PlanItem item) => $"Would start {Exe(item)} {Arguments(item)}".TrimEnd() + " as you, without admin rights";

    protected override Task<StepRecord> RunAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
    {
        var exe = Exe(step.Item);
        if (!launcher.FileExists(exe)) return Task.FromResult(step.Skipped($"{Path.GetFileName(exe)} isn't on this PC any more."));
        launcher.Launch(exe, Arguments(step.Item));
        return Task.FromResult(step.Done($"Started {Path.GetFileNameWithoutExtension(exe)}."));
    }

    protected override Task<StepRecord> RevertAsync(StepRecord step, HandlerContext ctx, CancellationToken ct)
        => Task.FromResult(step.Skipped("Close the app yourself if you don't need it. Nothing else was changed."));
}
