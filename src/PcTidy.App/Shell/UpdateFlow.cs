using System.Reflection;
using PcTidy.App.Updates;
using PcTidy.Core;

namespace PcTidy.App.Shell;

/// <summary>What the user answered on the "PC Tidy X.Y.Z is available" notice.</summary>
public enum UpdateNoticeChoice { Later, UpdateNow, Skip }

/// <summary>
/// The window side of the updater (<see cref="WindowUpdateUi"/> in the app, a fake in tests): dialogs, the progress
/// overlay, settings and what counts as busy. <see cref="UpdateFlow"/> holds the rules and never touches WPF itself.
/// </summary>
public interface IUpdateUi
{
    AppSettings Settings { get; }
    void SaveSettings();

    /// <summary>
    /// Why an update can't be offered or installed now (null = it can): a run, an undo, a scan, or an open dialog.
    /// <paramref name="holdingDialogGate"/>: the caller holds the one-dialog-at-a-time gate itself, so that doesn't count.
    /// </summary>
    string? BusyReason(bool holdingDialogGate = false);

    /// <summary>The window is on screen, not minimised, and no progress overlay is up.</summary>
    bool ReadyForNotice { get; }

    /// <summary>
    /// Shows the notice for <paramref name="offer"/> once the dialog gate is free, but only if <paramref name="stillWanted"/>
    /// is still true then (checked while holding the gate). Null = not shown.
    /// </summary>
    Task<UpdateNoticeChoice?> ShowNoticeIfAsync(UpdateOffer offer, Version current, bool practice, Func<bool> stillWanted);

    /// <summary>"Install PC Tidy X.Y.Z?" with <paramref name="body"/>; true = Install and close PC Tidy.</summary>
    Task<bool> ConfirmInstallAsync(string title, string body);

    void ShowMessage(string title, string message, bool isError = false);
    CancellationToken ShowProgress(string title, bool canStop, string stopText = "Stop after this step");
    void ReportProgress(double fraction);
    void HideProgress();
    void OpenInExplorer(string path);
    /// <summary>Closes PC Tidy so Setup can replace its files.</summary>
    void Quit();
    /// <summary>Runs <paramref name="work"/> on the UI thread.</summary>
    Task RunOnUiAsync(Func<Task> work);
}

/// <summary>Everything the updater reaches outside PC Tidy: the network, the install kind, signatures and starting Setup. Tests swap each.</summary>
public sealed record UpdateEnvironment
{
    /// <summary>practice → the checker (real: <see cref="UpdateChecker.Create"/>; tests: a fake HTTP handler).</summary>
    public Func<bool, Version, UpdateChecker> CreateChecker { get; init; } = UpdateChecker.Create;
    public Func<InstallKind> DetectKind { get; init; } = () => UpdateAssets.Detect(OwnProcesses.FindUninstaller(), InstallFolder.Current);
    public Func<SignatureInfo> OwnSignature { get; init; } = Authenticode.Own;
    /// <summary>The installer's signature, read through the held handle (null = the real Authenticode check).</summary>
    public Func<FileStream, string, SignatureInfo>? CheckSignature { get; init; }
    public Func<string, SignatureInfo> CheckFileSignature { get; init; } = Authenticode.Check;
    /// <summary>Starts the checked Setup (tests: a fake that records it and starts nothing).</summary>
    public IProcessStarter Starter { get; init; } = RealProcessStarter.Instance;
    public Func<string> AppFolder { get; init; } = () => InstallFolder.Current;
    public TimeSpan StartDelay { get; init; } = UpdatePolicy.StartDelay;
    /// <summary>How often the automatic notice looks again while PC Tidy is busy.</summary>
    public TimeSpan BusyPoll { get; init; } = TimeSpan.FromSeconds(20);
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    public static readonly UpdateEnvironment Real = new();
}

/// <summary>
/// The updater as the user sees it ("can you make it auto update the program if you have old program of it").
/// <list type="number">
/// <item>Check: automatically at most once a day, a while after start and off the UI thread (Settings › Updates ›
/// "Check for updates automatically", on by default), or with "Check for updates". Offline or any problem is only noted
/// quietly in Settings by the automatic check; "Check for updates" says what happened.</item>
/// <item>Notice: "PC Tidy X.Y.Z is available" with the release notes as plain text and Update now / Later / Skip this
/// version. Never shown or installed while a run, a scan, an undo or another dialog is going (<see cref="IUpdateUi.BusyReason"/>).
/// The notice waits for the dialog gate and then checks again (busy, a version skipped meanwhile, the switch switched
/// off), so a notice queued behind "Undo this run?" never opens on top of the undo (3.1.1 review M3). Only one notice
/// at a time: the automatic and the manual check share one "offering" flag, and a version already shown this session
/// isn't shown again by the automatic check.</item>
/// <item>Update now: downloads the exact file for this kind of install and checks it against the release's SHA256SUMS.txt
/// (<see cref="UpdateChecker"/>). Installed copies: after one more OK, and one more busy check, PC Tidy Setup starts from
/// the locked, checked file (<see cref="VerifiedInstaller"/>, trust rule in <see cref="UpdateTrust"/>) and PC Tidy
/// closes so Setup can replace its files. Portable copies: the zip is unpacked into a new folder
/// (<see cref="PortableUpdate"/>), shown in Explorer, and the user starts PC Tidy from there. Nothing is ever installed
/// silently.</item>
/// </list>
/// Demo and practice mode never go online: checks are answered by <see cref="PracticeReleaseHandler"/> and Update now
/// only says what would happen.
/// </summary>
public sealed class UpdateFlow
{
    private readonly IUpdateUi _ui;
    private readonly UpdateEnvironment _env;
    private int _offering;
    private string? _shownThisSession;

    public UpdateFlow(IUpdateUi ui, bool practice, UpdateEnvironment? environment = null, Version? current = null)
    {
        _ui = ui;
        IsPractice = practice;
        _env = environment ?? UpdateEnvironment.Real;
        Current = current ?? CurrentVersion;
    }

    /// <summary>The running PC Tidy's version as X.Y.Z.</summary>
    public static Version CurrentVersion { get; } = ReleaseVersion.Current(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3));

    /// <summary>The version this flow compares with (the running one; tests pass their own).</summary>
    public Version Current { get; }

    /// <summary>Demo or practice PC: no network, nothing downloaded or started.</summary>
    public bool IsPractice { get; }

    /// <summary>Raised on the UI thread after a check or a skip changed what Settings shows.</summary>
    public event Action? StateChanged;

    /// <summary>True while a notice is waiting for, or holding, the screen (or its download and install are going).</summary>
    public bool IsOffering => Volatile.Read(ref _offering) != 0;

    private AppSettings S => _ui.Settings;

    /// <summary>Why an update can't be offered or installed now (null = it can).</summary>
    public string? BusyReason() => _ui.BusyReason();

    // ---------------------------------------------------------------- automatic check

    /// <summary>
    /// Real PC only: removes old downloads, then starts the once-a-day check in the background (demo and practice mode
    /// never check by themselves).
    /// </summary>
    public void StartAutomatic()
    {
        if (IsPractice) return;
        var dataRoot = S.DataRoot;
        _ = Task.Run(() => CleanOldDownloads(dataRoot));
        _ = Task.Run(AutomaticAsync);
    }

    /// <summary>
    /// 3.1.1 review L9: installers and zips in updates\ for this version or older are deleted once this (newer) PC Tidy
    /// has started; so are leftover partial downloads. Newer downloads and the unpacked portable folders stay.
    /// </summary>
    public void CleanOldDownloads(string dataRoot)
    {
        try { new UpdateStore(dataRoot).RemoveOld(Current, _env.UtcNow()); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { CrashHandler.Write(ex, "Removing old update downloads", terminating: false); }
    }

    /// <summary>The automatic check (tests call it directly; the app through <see cref="StartAutomatic"/>).</summary>
    public async Task AutomaticAsync()
    {
        try
        {
            await Task.Delay(_env.StartDelay).ConfigureAwait(false);
            bool enabled = false, onboarded = false;
            DateTime? last = null;
            await _ui.RunOnUiAsync(() =>
            {
                (enabled, last, onboarded) = (ShellPrefs.Get(S).AutoUpdateCheck, ShellState.Get(S).LastUpdateCheckUtc, S.OnboardingDone);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            // First start: the welcome screens come first; the check waits for the next start.
            if (!onboarded || !UpdatePolicy.AutoCheckDue(enabled, IsPractice, last, _env.UtcNow())) return;
            UpdateCheck result;
            using (var checker = _env.CreateChecker(IsPractice, Current))
                result = await checker.CheckAsync(CancellationToken.None).ConfigureAwait(false);
            await _ui.RunOnUiAsync(async () =>
            {
                Record(result);
                if (!UpdatePolicy.ShouldNotify(result, ShellState.Get(S).SkippedUpdateVersion, manual: false)) return;
                await OfferWhenFreeAsync(result);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { CrashHandler.Write(ex, "Automatic update check", terminating: false); }
    }

    /// <summary>
    /// The automatic notice: waits until nothing is going on, then offers. If something got busy between the wait and
    /// the dialog gate, it waits again. It gives up when the version was skipped, the switch was turned off, or the
    /// notice for this version was already shown this session (by "Check for updates", say).
    /// </summary>
    private async Task OfferWhenFreeAsync(UpdateCheck result)
    {
        var version = result.Offer!.VersionText;
        while (true)
        {
            while (!_ui.ReadyForNotice || _ui.BusyReason() is not null) await Task.Delay(_env.BusyPoll);
            if (!AutomaticStillWanted(version)) return;
            var outcome = await OfferAsync(result, manual: false);
            if (outcome != OfferOutcome.NotShownBusy) return;
        }
    }

    private bool AutomaticStillWanted(string version)
        => ShellPrefs.Get(S).AutoUpdateCheck
           && !string.Equals(ShellState.Get(S).SkippedUpdateVersion, version, StringComparison.Ordinal)
           && !string.Equals(_shownThisSession, version, StringComparison.Ordinal);

    private void Record(UpdateCheck result)
    {
        var note = result.State == UpdateState.Available ? $"PC Tidy {result.Offer!.VersionText} is available." : result.Message;
        ShellState.Update(S, s => s with { LastUpdateCheckUtc = _env.UtcNow(), LastUpdateNote = note });
        _ui.SaveSettings();
        StateChanged?.Invoke();
    }

    // ---------------------------------------------------------------- "Check for updates"

    public async Task CheckNowAsync()
    {
        // One notice at a time: a notice (automatic or manual) is already up, or its download or install is going.
        if (IsOffering) return;
        _ui.ShowProgress("Checking for updates…", canStop: false);
        UpdateCheck result;
        try
        {
            using var checker = _env.CreateChecker(IsPractice, Current);
            result = await Task.Run(() => checker.CheckAsync(CancellationToken.None));
        }
        finally { _ui.HideProgress(); }
        Record(result);
        if (result.State != UpdateState.Available)
        {
            _ui.ShowMessage(result.State == UpdateState.UpToDate ? "No update" : "Update check", result.Message, isError: false);
            return;
        }
        if (await OfferAsync(result, manual: true) == OfferOutcome.NotShownBusy && _ui.BusyReason() is { } why)
            _ui.ShowMessage(FlowHost.OneTaskTitle, $"PC Tidy {result.Offer!.VersionText} is available. {why}. PC Tidy shows updates only when nothing else is going on, so try again when it's done.");
    }

    // ---------------------------------------------------------------- the notice

    public enum OfferOutcome { Shown, NotShownBusy, NotShownSkippedOrOff, AlreadyOffering }

    /// <summary>"PC Tidy X.Y.Z is available": release notes as plain text, Update now / Later / Skip this version.</summary>
    public Task OfferAsync(UpdateCheck result) => OfferAsync(result, manual: true);

    public async Task<OfferOutcome> OfferAsync(UpdateCheck result, bool manual)
    {
        if (result.Offer is not { } offer) return OfferOutcome.NotShownSkippedOrOff;
        // One notice at a time, whichever check found it.
        if (Interlocked.CompareExchange(ref _offering, 1, 0) != 0) return OfferOutcome.AlreadyOffering;
        try
        {
            string? busyAtGate = null;
            bool StillWanted()
            {
                busyAtGate = _ui.BusyReason(holdingDialogGate: true);
                return UpdatePolicy.StillOffer(busyAtGate, ShellState.Get(S).SkippedUpdateVersion, offer.VersionText, ShellPrefs.Get(S).AutoUpdateCheck, manual)
                       && (manual || !string.Equals(_shownThisSession, offer.VersionText, StringComparison.Ordinal));
            }
            var choice = await _ui.ShowNoticeIfAsync(offer, Current, IsPractice, StillWanted);
            if (choice is null) return busyAtGate is not null ? OfferOutcome.NotShownBusy : OfferOutcome.NotShownSkippedOrOff;
            _shownThisSession = offer.VersionText;
            if (choice == UpdateNoticeChoice.Skip)
            {
                ShellState.Update(S, s => s with { SkippedUpdateVersion = offer.VersionText });
                _ui.SaveSettings();
                StateChanged?.Invoke();
                _ui.ShowMessage("Version skipped", $"PC Tidy won't offer {offer.VersionText} again by itself. A newer version will still be offered, and \"Check for updates\" in Settings always shows what's there.");
            }
            else if (choice == UpdateNoticeChoice.UpdateNow) await UpdateNowAsync(result);
            return OfferOutcome.Shown;
        }
        finally { Volatile.Write(ref _offering, 0); }
    }

    // ---------------------------------------------------------------- Update now

    private async Task UpdateNowAsync(UpdateCheck result)
    {
        var offer = result.Offer!;
        if (BusyReason() is { } why)
        {
            _ui.ShowMessage(FlowHost.OneTaskTitle, $"{why}. PC Tidy updates only when nothing else is going on, so nothing was downloaded. Try again when it's done.");
            return;
        }
        var kind = IsPractice ? InstallKind.Installed : await Task.Run(_env.DetectKind);
        var name = UpdateAssets.ExpectedName(kind, offer.Version);
        if (IsPractice)
        {
            _ui.ShowMessage("Practice mode", $"Here PC Tidy would download {name} and {UpdateAssets.SumsName} from PC Tidy's GitHub release {offer.Tag}, check the SHA-256, then start PC Tidy Setup and close (a portable copy unpacks into a new folder instead). In practice mode nothing is downloaded or started.");
            return;
        }

        var store = new UpdateStore(S.DataRoot);
        var stop = _ui.ShowProgress($"Downloading PC Tidy {offer.VersionText}…", canStop: true, stopText: "Stop the download");
        var progress = new Progress<double>(_ui.ReportProgress);
        UpdateDownload download;
        try
        {
            using var checker = _env.CreateChecker(false, Current);
            download = await Task.Run(() => checker.DownloadAsync(result, kind, store, progress, stop));
        }
        finally { _ui.HideProgress(); }
        if (download.Problem == UpdateChecker.StoppedMessage) { _ui.ShowMessage("Download stopped", download.Problem); return; }
        if (!download.Ok)
        {
            _ui.ShowMessage("Update not downloaded", (download.Problem ?? "The download didn't finish.") + $" All releases: {ReleaseSource.ReleasesPage.AbsoluteUri}", isError: true);
            return;
        }
        if (kind == InstallKind.Installed) await InstallAsync(download, store);
        else await UnpackAsync(download, store);
    }

    private async Task InstallAsync(UpdateDownload download, UpdateStore store)
    {
        var release = download.Release!;
        var path = download.Path!;
        var own = await Task.Run(_env.OwnSignature);
        VerifiedInstaller verified;
        // From here the file is held open with FileShare.Read (no writes, renames or deletes) until Setup has started.
        try { verified = await Task.Run(() => VerifiedInstaller.Open(path, release.Sha256, own, release, _env.CheckSignature)); }
        catch (InvalidOperationException ex)
        {
            store.Discard(path);
            _ui.ShowMessage("Update refused", ex.Message + " The download was deleted. Nothing was installed.", isError: true);
            return;
        }
        using (verified)
        {
            var body = "The download matches the checksum in SHA256SUMS.txt of the same GitHub release. " + UpdateTrust.Describe(own, verified.Signature) +
                       "\n\nPC Tidy closes and PC Tidy Setup opens. Choose Update there. Windows asks for permission once, because PC Tidy is installed for everyone on this PC. " +
                       "Your settings, history, quarantine and PC Tidy Rescue stay as they are, and Setup can start PC Tidy again at the end.";
            if (!await _ui.ConfirmInstallAsync($"Install PC Tidy {ReleaseVersion.Show(release.Version)}?", body)) return;
            // The last busy check, right before Setup starts: something may have started while the question was open.
            if (BusyReason() is { } why)
            {
                _ui.ShowMessage(FlowHost.OneTaskTitle, $"{why}. PC Tidy updates only when nothing else is going on, so Setup wasn't started. Try again when it's done.");
                return;
            }
            try
            {
                verified.Start(_env.Starter);
                _ui.Quit(); // Setup refuses to replace the files of a PC Tidy that is still open
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                _ui.ShowMessage("Update not started", ex.Message, isError: true);
            }
        }
    }

    private async Task UnpackAsync(UpdateDownload download, UpdateStore store)
    {
        var release = download.Release!;
        var path = download.Path!;
        _ui.ShowProgress($"Unpacking PC Tidy {ReleaseVersion.Show(release.Version)}…", canStop: false);
        string target;
        try
        {
            var appFolder = _env.AppFolder();
            target = await Task.Run(() =>
            {
                using var zip = VerifiedInstaller.OpenArchive(path, release.Sha256); // locked and re-hashed while it is unpacked
                return PortableUpdate.UnpackIntoNewFolder(zip.Stream, appFolder, store.Folder, release.Version);
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // UnpackIntoNewFolder already removed a half-made folder; any other surprise is still only a message (review L6).
            _ui.ShowMessage("Update not unpacked", ex is InvalidOperationException ? ex.Message : "The zip couldn't be unpacked: " + ex.Message + " Your current PC Tidy wasn't changed.", isError: true);
            return;
        }
        finally
        {
            _ui.HideProgress();
            store.Discard(path); // the zip isn't needed any more either way
        }

        // A signed PC Tidy also checks the new program's publisher (an unsigned beta relies on the release checks above).
        var own = await Task.Run(_env.OwnSignature);
        if (own.State == SignatureState.Valid)
        {
            var exe = Path.Combine(target, "PcTidy.exe");
            var signature = await Task.Run(() => _env.CheckFileSignature(exe));
            if (UpdateTrust.Problem(own, signature) is { } problem)
            {
                try { Directory.Delete(target, recursive: true); } // boundary-ok: only the new folder PC Tidy just created
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                _ui.ShowMessage("Update refused", problem + " The new folder was removed. Your current PC Tidy wasn't changed.", isError: true);
                return;
            }
        }

        _ui.OpenInExplorer(target);
        _ui.ShowMessage($"PC Tidy {ReleaseVersion.Show(release.Version)} is ready",
            $"It's in a new folder:\n{target}\n\nStart PC Tidy from the new folder (PcTidy.exe); your settings carry over. Close this one first. " +
            "PC Tidy never starts anything from the download by itself. Once you're happy with the new version, you can delete the old folder.");
    }
}
