using System.Globalization;
using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// v3.1 fix-it tools: restart one allow-listed service, reset Windows Update's components, register Windows Installer
/// again, and install one of Microsoft's runtimes with winget. None of them changes a start type, and none deletes
/// anything: the Windows Update reset only renames its two fixed folders (the one place PC Tidy changes something in the
/// Windows folder, with the paths built here, never from a plan).
/// </summary>
public sealed partial class SystemToolHandler
{
    /// <summary>
    /// Stop, then start. Windows stops the running services that depend on it first (and the ones that depend on those),
    /// so all of them are recorded before anything is stopped. Whatever happens next (the stop times out, Windows refuses
    /// it, the user cancels), a <c>finally</c> starts the service again if it was running, then every dependent that was
    /// running. The message says what really happened: once a stop was asked for, it never says nothing changed.
    /// </summary>
    private async Task<StepRecord> RunServiceRestartAsync(StepRecord step, SystemToolPlan plan, CancellationToken ct)
    {
        var name = plan.ServiceName!;
        var label = SystemToolCatalog.RestartableServices[name];
        var before = _backend.QueryService(name);
        if (before is null) return step.Skipped($"The {label} service isn't on this PC.");
        if (before.Disabled)
            return step.Skipped($"The {label} service is turned off (its startup type is Disabled), so it can't start. PC Tidy doesn't change " +
                                "startup types here. If an old tweak turned it off, the bad-tweak finder can set it back to Windows' default.");

        var dependents = before.Running ? RunningDependentsDeep(name, before) : Array.Empty<string>();
        var stopped = !before.Running; // nothing to stop when it wasn't running
        var mainRunning = false;
        IReadOnlyList<string> notStarted;
        try
        {
            if (before.Running)
            {
                try { stopped = await _backend.StopServiceAsync(name, ServiceWait, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    stopped = false; // Windows refused the stop, or a dependent didn't stop in time: started again below
                }
            }
            // From here on the service is stopped: started again even if the user cancels.
            if (stopped) mainRunning = await TryStartAsync(name).ConfigureAwait(false);
        }
        finally
        {
            // Always: the service itself if it was running before, then every dependent that was running.
            if (before.Running && !mainRunning) mainRunning = await TryStartAsync(name).ConfigureAwait(false);
            notStarted = await StartAllAsync(dependents).ConfigureAwait(false);
        }

        var note = NotStartedNote(notStarted);
        if (!stopped)
        {
            var others = dependents.Count == 0 ? "" : " Windows may have stopped the services that depend on it first; PC Tidy started them again.";
            return mainRunning
                ? step.Failed($"Windows couldn't stop the {label} service in time, so it wasn't restarted. It's running now.{others}{note}")
                : step.Failed($"Windows couldn't stop the {label} service in time, and it isn't running now. Restarting your PC starts it again.{others}{note}");
        }
        if (!mainRunning)
            return step.Failed($"Windows stopped the {label} service but it didn't start again in time. Restarting your PC starts it again.{note}");
        var done = before.Running ? $"Restarted the {label} service." : $"Started the {label} service (it wasn't running).";
        return step.Done(done + note);
    }

    /// <summary>
    /// Every running service that depends on <paramref name="name"/>, directly or through another one (closest first).
    /// Stopping a service makes Windows stop all of these, so all of them are started again afterwards.
    /// </summary>
    private IReadOnlyList<string> RunningDependentsDeep(string name, ServiceRunState state)
    {
        var all = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var queue = new Queue<string>();
        foreach (var d in state.RunningDependents)
            if (seen.Add(d)) { all.Add(d); queue.Enqueue(d); }
        while (queue.Count > 0 && all.Count < 256)
        {
            ServiceRunState? s;
            try { s = _backend.QueryService(queue.Dequeue()); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { s = null; }
            if (s is null) continue;
            foreach (var d in s.RunningDependents)
                if (seen.Add(d)) { all.Add(d); queue.Enqueue(d); }
        }
        return all;
    }

    /// <summary>Starts one service; never cancelled. False when it didn't start (or Windows refused).</summary>
    private async Task<bool> TryStartAsync(string service)
    {
        try { return await _backend.StartServiceAsync(service, ServiceWait, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Microsoft's "reset Windows Update components" steps (verify against Microsoft Learn): stop BITS, wuauserv,
    /// CryptSvc and msiserver; rename %windir%\SoftwareDistribution and %windir%\System32\catroot2 to *.old-&lt;stamp&gt;;
    /// start again exactly the services that were running. If a service won't stop, nothing is renamed. The services
    /// are always started again, even after an error or a cancel.
    /// </summary>
    private async Task<StepRecord> RunWindowsUpdateResetAsync(StepRecord step, SystemToolPlan plan, CancellationToken ct)
    {
        var services = plan.Services!;
        var states = new Dictionary<string, ServiceRunState>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var s in services)
        {
            if (_backend.QueryService(s) is { } state) states[s] = state;
            else missing.Add(s);
        }
        if (missing.Count > 0)
            return step.Skipped($"Some of Windows Update's services aren't on this PC ({string.Join(", ", missing)}), so PC Tidy changed nothing.");

        // Both folders must be exactly %windir%\SoftwareDistribution and %windir%\System32\catroot2, and each one that is
        // there must be a real folder (not a link to somewhere else). Checked before anything is stopped.
        var folders = SystemToolCatalog.WindowsUpdateResetFolders(_backend);
        if (WindowsUpdateFolderProblem(folders) is { } folderProblem) return step.Skipped(folderProblem);

        // What to start again afterwards: the services that were running, then services that depend on them (directly or
        // through another one) and were running.
        var startAgain = services.Where(s => states[s].Running).ToList();
        foreach (var dependent in services.Where(s => states[s].Running).SelectMany(s => RunningDependentsDeep(s, states[s])))
            if (!startAgain.Contains(dependent, StringComparer.OrdinalIgnoreCase)) startAgain.Add(dependent);

        string? wouldNotStop = null;
        var renamed = new List<string>();
        var notRenamed = new List<string>();
        var absent = new List<string>();
        IReadOnlyList<string> notStarted;
        try
        {
            foreach (var s in services.Where(s => states[s].Running))
            {
                ct.ThrowIfCancellationRequested();
                bool stoppedIt;
                try { stoppedIt = await _backend.StopServiceAsync(s, ServiceWait, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { stoppedIt = false; }
                if (!stoppedIt) { wouldNotStop = s; break; }
            }
            if (wouldNotStop is null)
            {
                var stamp = _backend.LocalNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                foreach (var folder in folders)
                {
                    if (!_backend.DirectoryExists(folder)) { absent.Add(Path.GetFileName(folder)); continue; }
                    // Checked again right before the rename: a folder that became a link is left alone.
                    if (!_backend.IsPlainDirectory(folder)) { notRenamed.Add(Path.GetFileName(folder)); continue; }
                    var target = $"{folder}.old-{stamp}";
                    try
                    {
                        _backend.RenameFolder(folder, target);
                        renamed.Add(target);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        notRenamed.Add(Path.GetFileName(folder));
                    }
                }
            }
        }
        finally
        {
            notStarted = await StartAllAsync(startAgain).ConfigureAwait(false);
        }

        var startNote = NotStartedNote(notStarted);
        if (wouldNotStop is not null)
            return step.Failed($"Windows couldn't stop {wouldNotStop} in time, so PC Tidy renamed nothing. The services were started again as they were.{startNote}");
        if (renamed.Count == 0 && notRenamed.Count == 0)
            return step.Skipped($"Windows Update's folders aren't there, so there's nothing to reset.{startNote}");
        if (renamed.Count == 0)
            return step.Failed($"Windows was still using {string.Join(" and ", notRenamed)}, so PC Tidy couldn't rename it. Nothing was changed. " +
                               $"Restart your PC and try again.{startNote}");

        var partly = notRenamed.Count > 0 ? $" {string.Join(" and ", notRenamed)} was in use, so it was left as it was." : "";
        var gone = absent.Count > 0 ? $" ({string.Join(" and ", absent)} wasn't there.)" : "";
        return step.Done($"Reset Windows Update components. The old folders were kept as {string.Join(" and ", renamed)}. Windows makes fresh ones " +
                         $"the next time it checks for updates.{partly}{gone} Restart your PC, then check for updates. You can delete the .old folders " +
                         $"once updates work again.{startNote}")
            .WithUndo((RenamedFoldersData, string.Join("|", renamed)));
    }

    /// <summary>
    /// msiexec /unregister, then msiexec /regserver. The second command always runs, so Windows Installer is never left
    /// unregistered because the first one reported a problem or the user cancelled in between.
    /// </summary>
    private async Task<StepRecord> RunMsiReregisterAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        await _backend.RunAsync(plan.Commands[0], plan.Timeout, log, ct).ConfigureAwait(false);
        var register = await _backend.RunAsync(plan.Commands[1], plan.Timeout, log, CancellationToken.None).ConfigureAwait(false);
        if (register.TimedOut || register.ExitCode != 0)
            return WithLog(step.Failed($"Windows Installer couldn't be registered again (code {register.ExitCode}). " +
                                       "Open Terminal as administrator and run: msiexec /regserver"), log);
        return WithLog(step.Done("Registered Windows Installer again. Try the install or uninstall again."), log);
    }

    private async Task<StepRecord> RunWingetInstallAsync(StepRecord step, SystemToolPlan plan, string log, CancellationToken ct)
    {
        var name = plan.Runtime!.Name;
        var result = await _backend.RunAsync(plan.Commands[0], plan.Timeout, log, ct).ConfigureAwait(false);
        if (result.TimedOut) return WithLog(step.Failed($"winget is still installing {name}. It was left to finish on its own."), log);
        return WithLog(result.ExitCode switch
        {
            0 => step.Done($"Installed {name}."),
            SystemToolCatalog.WingetAlreadyInstalled or SystemToolCatalog.WingetNoNewerVersion => step.Skipped($"{name} is already installed and up to date. Nothing to do."),
            3010 or SystemToolCatalog.WingetRestartToFinish => step.Done($"Installed {name}. Restart your PC to finish."),
            SystemToolCatalog.WingetRestartFirst => step.Failed($"Windows needs a restart before {name} can be installed. Restart your PC, then try again."),
            var code => step.Failed($"winget couldn't install {name} (code 0x{code:X8}). Details are in the log."),
        }, log);
    }

    /// <summary>
    /// Null when the reset's folders are exactly %windir%\SoftwareDistribution and %windir%\System32\catroot2 (a full,
    /// local path to the Windows folder) and each one that exists is a real folder, not a link (reparse point).
    /// Otherwise why PC Tidy changes nothing.
    /// </summary>
    internal string? WindowsUpdateFolderProblem(IReadOnlyList<string> folders)
    {
        const string NotFound = "PC Tidy couldn't find the Windows folder the usual way, so it changed nothing.";
        var windir = _backend.WindowsDirectory;
        if (string.IsNullOrWhiteSpace(windir) || windir.Any(char.IsControl) || !Path.IsPathFullyQualified(windir) || windir.StartsWith(@"\\", StringComparison.Ordinal))
            return NotFound;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(windir)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return NotFound; }
        if (!string.Equals(full, Path.TrimEndingDirectorySeparator(windir), StringComparison.OrdinalIgnoreCase)) return NotFound;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(_backend.SystemDirectory), Path.Combine(full, "System32"), StringComparison.OrdinalIgnoreCase)) return NotFound;

        var expected = new[] { Path.Combine(full, "SoftwareDistribution"), Path.Combine(full, "System32", "catroot2") };
        if (folders.Count != expected.Length || folders.Where((f, i) => !string.Equals(f, expected[i], StringComparison.OrdinalIgnoreCase)).Any())
            return NotFound;
        foreach (var folder in folders)
            if (_backend.DirectoryExists(folder) && !_backend.IsPlainDirectory(folder))
                return $"{Path.GetFileName(folder)} isn't a normal folder (it's a link to somewhere else), so PC Tidy changed nothing.";
        return null;
    }

    /// <summary>Starts each service; never cancelled (a service PC Tidy stopped is always started again). Returns the ones that didn't start.</summary>
    private async Task<IReadOnlyList<string>> StartAllAsync(IEnumerable<string> services)
    {
        var failed = new List<string>();
        foreach (var s in services)
        {
            try
            {
                if (!await _backend.StartServiceAsync(s, ServiceWait, CancellationToken.None).ConfigureAwait(false)) failed.Add(s);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                failed.Add(s);
            }
        }
        return failed;
    }

    private static string NotStartedNote(IReadOnlyList<string> notStarted)
        => notStarted.Count == 0 ? "" : $" These services didn't start again: {string.Join(", ", notStarted)}. Restarting your PC starts them.";
}
