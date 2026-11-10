using PcTidy.Setup.Windows;

namespace PcTidy.Setup.Install;

/// <summary>
/// Install and uninstall as the user sees them, with one admin prompt each:
/// <list type="number">
/// <item>The window (normal rights) explains the prompt and gathers the choices.</item>
/// <item>The admin part runs in a copy of Setup started with admin rights (<see cref="ISetupElevation"/>), with only
/// fixed switches: it decides every folder itself (always %ProgramFiles%\PC Tidy), so nothing the user's programs pass
/// in can point it elsewhere. If PC Tidy Setup already has admin rights it runs in-process.</item>
/// <item>Then the user's own part runs with normal rights (<see cref="UserSetup"/>, <see cref="Uninstaller.RemoveUserParts"/>),
/// so it changes this user's data and settings, not an admin account's.</item>
/// </list>
/// </summary>
public sealed class SetupFlow
{
    private readonly InstallEnvironment _env;
    private readonly ISetupElevation _elevation;

    public SetupFlow(InstallEnvironment env, ISetupElevation elevation)
    {
        _env = env;
        _elevation = elevation;
    }

    public const string ElevatedInstallSwitch = "--install-elevated";
    public const string ElevatedUninstallSwitch = "--uninstall-elevated";

    internal static IReadOnlyList<string> InstallArguments(InstallRequest request)
    {
        var args = new List<string> { ElevatedInstallSwitch };
        if (request.DesktopShortcut) args.Add("--desktop");
        if (!request.StartMenuShortcuts) args.Add("--no-start-menu");
        return args;
    }

    public async Task<SetupResult> InstallAsync(InstallRequest request, IProgress<SetupProgress>? progress = null, CancellationToken ct = default)
    {
        var previous = new Registration(_env.Registry).FindExisting();
        SetupResult machine;
        if (_elevation.IsElevated)
        {
            machine = await new Installer(_env).InstallAsync(request, progress, ct);
        }
        else
        {
            progress?.Report(new SetupProgress(0.05, "Waiting for Windows' permission to install in Program Files…"));
            var code = await _elevation.RunElevatedAsync(InstallArguments(request), ct);
            machine = FromInstallExitCode(code, previous is not null, previous?.Version);
        }
        if (!machine.Ok) return machine;

        progress?.Report(new SetupProgress(0.98, "Finishing for your account…"));
        var notes = machine.Notes.Concat(new UserSetup(_env).AfterInstall()).ToList();
        progress?.Report(new SetupProgress(1, "Done."));
        return machine with { Notes = notes };
    }

    public async Task<SetupResult> UninstallAsync(bool keepData, string? runningUninstaller, IProgress<SetupProgress>? progress = null, CancellationToken ct = default)
    {
        var request = new UninstallRequest { KeepData = keepData, RunningUninstaller = runningUninstaller };
        // While the app is still there: remove its Windows notification registration (per-user, normal rights).
        // Best effort: if it can't run, the leftover registration is harmless and points at a missing program.
        if (!_elevation.IsElevated && _env.Processes.FindRunning(_env.InstallDir).Count == 0)
            _env.Processes.RunQuiet(_env.AppPath, "--unregister-toasts", TimeSpan.FromSeconds(15));
        SetupResult machine;
        if (_elevation.IsElevated)
        {
            machine = await Task.Run(() => new Uninstaller(_env).RemoveProgram(request, progress), ct);
        }
        else
        {
            progress?.Report(new SetupProgress(0.05, "Waiting for Windows' permission to remove PC Tidy from Program Files…"));
            machine = FromUninstallExitCode(await _elevation.RunElevatedAsync(new[] { ElevatedUninstallSwitch }, ct));
            // The admin copy only answers with an exit code; this note is what it would have said.
            if (machine.Ok && runningUninstaller is not null && FolderSafety.IsSameOrUnder(runningUninstaller, _env.InstallDir))
                machine = machine with { Notes = new[] { $"The uninstaller itself ({runningUninstaller}) is removed when you restart your PC." } };
        }
        // Declined or blocked: nothing of the user's is touched either, so trying again later finds everything as it was.
        if (!machine.Ok && machine.Notes.Count == 0 && !IsPartlyRemoved(machine)) return machine;

        var user = await Task.Run(() => new Uninstaller(_env).RemoveUserParts(request, progress), ct);
        var notes = machine.Notes.Concat(user.Notes).ToList();
        var ok = machine.Ok && user.Ok;
        return new SetupResult(ok, ok ? "PC Tidy was removed." : machine.Ok ? user.Message : machine.Message, notes);
    }

    private static bool IsPartlyRemoved(SetupResult r) => r.Message == PartlyRemoved;

    private const string PartlyRemoved = "PC Tidy was removed, but some of its files couldn't be (probably in use). You can delete them yourself.";

    /// <summary>The admin copy only answers with an exit code: this is what it would have said.</summary>
    internal static SetupResult FromInstallExitCode(int code, bool isUpdate, string? previousVersion = null) => code switch
    {
        Program.ExitRolledBack => SetupResult.Fail($"The update couldn't finish, so PC Tidy{(string.IsNullOrEmpty(previousVersion) ? "" : " " + previousVersion)} was put back as it was. Try again in a few minutes.")
            with { Outcome = SetupOutcome.RolledBack },
        Program.ExitNeedsRepair => SetupResult.Fail("The update couldn't finish and some of PC Tidy's files couldn't be put back, so PC Tidy may not start. Run PC Tidy Setup again to repair it.")
            with { Outcome = SetupOutcome.NeedsRepair },
        Program.ExitOk => new SetupResult(true, isUpdate ? "PC Tidy was updated." : "PC Tidy is installed.", Array.Empty<string>()),
        Program.ExitDeclined => SetupResult.Fail("Windows didn't give permission, so nothing was installed. PC Tidy needs it once, to install in Program Files."),
        Program.ExitDamaged => SetupResult.Fail("This installer is damaged or incomplete. Please download PC Tidy Setup again."),
        Program.ExitAppOpen => SetupResult.Fail("PC Tidy is open. Close it, then try again."),
        Program.ExitAppOpenElsewhere => OpenElsewhere(),
        Program.ExitUnsafe => SetupResult.Fail(UnsafeMessage()),
        Program.ExitUntrusted => SetupResult.Fail("This copy of PC Tidy Setup wasn't built as a release, so it can't install with admin rights. Please download PC Tidy Setup again."),
        // A fresh install that fails is removed again. An update that fails after replacing files answers
        // ExitRolledBack or ExitNeedsRepair, so a plain failure here never replaced anything.
        _ => SetupResult.Fail(isUpdate
            ? "Setup couldn't finish the update. The installed PC Tidy was left as it was."
            : "Setup couldn't finish installing in Program Files. Nothing was left behind there, and your data and settings weren't touched."),
    };

    /// <summary>The admin copy only says "another account"; the generic text, since this window may not see that copy.</summary>
    private static SetupResult OpenElsewhere()
        => SetupResult.Fail(RunningApp.OpenMessage(new[] { new RunningApp(0, "PcTidy", "") { InOtherSession = true } })) with { Outcome = SetupOutcome.AppOpenElsewhere };

    /// <summary>Why the admin part was refused, naming the setting when it is still there.</summary>
    internal static string UnsafeMessage()
        => (ElevationGuard.BeforePrompt() ?? "Something on this PC would let another program run inside Setup's admin part, so Setup stopped.")
           + " Nothing was changed.";

    internal static SetupResult FromUninstallExitCode(int code) => code switch
    {
        Program.ExitOk => new SetupResult(true, "PC Tidy was removed.", Array.Empty<string>()),
        Program.ExitDeclined => SetupResult.Fail("Windows didn't give permission, so PC Tidy wasn't removed. Nothing was changed."),
        Program.ExitAppOpen => SetupResult.Fail("PC Tidy is open. Close it, then try again."),
        Program.ExitAppOpenElsewhere => OpenElsewhere(),
        Program.ExitLeftovers => new SetupResult(false, PartlyRemoved, Array.Empty<string>()),
        Program.ExitUnsafe => SetupResult.Fail(UnsafeMessage()),
        _ => SetupResult.Fail("PC Tidy couldn't be removed from Program Files. Nothing else was changed."),
    };
}
