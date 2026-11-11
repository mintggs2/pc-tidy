using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using PcTidy.App.Pages;
using PcTidy.App.Shell;
using PcTidy.App.Updates;
using PcTidy.Core;
using PcTidy.UI.Flow;
using Xunit;

namespace PcTidy.UI.Tests.Shell;

/// <summary>
/// 3.1.1 review fixes for the updater, end to end with a fake GitHub (fake HTTP handler), a fake window and a fake
/// process starter: no network, no dialog, no installer is ever started. M3 (the notice never opens during or right
/// after a run, undo or scan; re-checks once it has the dialog gate; no stacked notices), the busy check right before
/// Setup starts, L6, L7, L8 and L9. Versions and file contents are invented.
/// </summary>
public partial class UpdateTests
{
    /// <summary>The window side, faked: one dialog at a time like FlowHost, and a busy state the test sets.</summary>
    private sealed class FakeUi : IUpdateUi
    {
        public FakeUi(string dataRoot) => Settings = new AppSettings { DataRoot = dataRoot, OnboardingDone = true };

        public AppSettings Settings { get; }
        public SemaphoreSlim DialogGate { get; } = new(1, 1);
        /// <summary>A run, an undo or a scan (null = free).</summary>
        public volatile string? Busy;
        public List<UpdateOffer> Notices { get; } = new();
        public Queue<UpdateNoticeChoice> Answers { get; } = new();
        /// <summary>When set, the notice stays open until this completes (to show two checks at once).</summary>
        public TaskCompletionSource<UpdateNoticeChoice>? HoldNotice;
        public bool ConfirmAnswer = true;
        public Action? OnConfirm;
        public List<string> Messages { get; } = new();
        public int Quits;
        public int Saves;

        public void SaveSettings() => Interlocked.Increment(ref Saves);

        public string? BusyReason(bool holdingDialogGate = false)
            => Busy ?? (!holdingDialogGate && DialogGate.CurrentCount == 0 ? UpdatePolicy.DialogOpenReason : null);

        public bool ReadyForNotice => true;

        public async Task<UpdateNoticeChoice?> ShowNoticeIfAsync(UpdateOffer offer, Version current, bool practice, Func<bool> stillWanted)
        {
            await DialogGate.WaitAsync();
            try
            {
                if (!stillWanted()) return null;
                lock (Notices) Notices.Add(offer);
                if (HoldNotice is { } hold) return await hold.Task;
                return Answers.Count > 0 ? Answers.Dequeue() : UpdateNoticeChoice.Later;
            }
            finally { DialogGate.Release(); }
        }

        public Task<bool> ConfirmInstallAsync(string title, string body)
        {
            OnConfirm?.Invoke();
            return Task.FromResult(ConfirmAnswer);
        }

        public void ShowMessage(string title, string message, bool isError = false) { lock (Messages) Messages.Add(title + ": " + message); }
        public CancellationToken ShowProgress(string title, bool canStop, string stopText = "Stop after this step") => CancellationToken.None;
        public void ReportProgress(double fraction) { }
        public void HideProgress() { }
        public void OpenInExplorer(string path) { }
        public void Quit() => Interlocked.Increment(ref Quits);
        public Task RunOnUiAsync(Func<Task> work) => work();
        public int NoticeCount { get { lock (Notices) return Notices.Count; } }
    }

    private sealed class FakeStarter : IProcessStarter
    {
        public List<ProcessStartInfo> Started { get; } = new();
        public void Start(ProcessStartInfo info) => Started.Add(info);
    }

    private static UpdateEnvironment Env(HttpMessageHandler handler, FakeStarter starter, InstallKind kind = InstallKind.Installed, string? appFolder = null) => new()
    {
        CreateChecker = (_, v) => new UpdateChecker(handler, v),
        DetectKind = () => kind,
        OwnSignature = () => SignatureInfo.NotSigned,
        CheckSignature = (_, _) => SignatureInfo.NotSigned,
        CheckFileSignature = _ => SignatureInfo.NotSigned,
        Starter = starter,
        AppFolder = () => appFolder ?? @"C:\Invented\PC Tidy",
        StartDelay = TimeSpan.Zero,
        BusyPoll = TimeSpan.FromMilliseconds(20),
    };

    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("The condition wasn't met in time.");
            await Task.Delay(10);
        }
    }

    // ------------------------------------------------------------------ the whole way, with fakes

    [Fact]
    public async Task Check_notice_download_confirm_and_Setup_start_end_to_end_with_fakes()
    {
        using var folder = new ShellTestFolder("flow-e2e");
        var handler = Release();
        var starter = new FakeStarter();
        var ui = new FakeUi(folder.Root);
        ui.Answers.Enqueue(UpdateNoticeChoice.UpdateNow);
        var flow = new UpdateFlow(ui, practice: false, Env(handler, starter), Current);

        await flow.CheckNowAsync();

        var notice = Assert.Single(ui.Notices);
        Assert.Equal("1.2.0", notice.VersionText);
        var start = Assert.Single(starter.Started);
        Assert.Equal(Path.Combine(folder.Root, "updates", SetupName), start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Empty(start.ArgumentList); // Setup's own window, never silent
        Assert.Equal(1, ui.Quits);
        Assert.All(handler.Requests, r => Assert.Contains(r.RequestUri!.Host, new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com" }));
        Assert.Contains("1.2.0", ShellState.Get(ui.Settings).LastUpdateNote);
    }

    [Fact]
    public async Task Setup_isnt_started_when_something_got_busy_while_the_install_question_was_open()
    {
        using var folder = new ShellTestFolder("flow-busy-last");
        var starter = new FakeStarter();
        var ui = new FakeUi(folder.Root) { OnConfirm = null };
        ui.Answers.Enqueue(UpdateNoticeChoice.UpdateNow);
        ui.OnConfirm = () => ui.Busy = "Available when the undo finishes"; // "Undo" was clicked behind the question
        var flow = new UpdateFlow(ui, practice: false, Env(Release(), starter), Current);

        await flow.CheckNowAsync();

        Assert.Empty(starter.Started);
        Assert.Equal(0, ui.Quits);
        Assert.Contains(ui.Messages, m => m.Contains("Setup wasn't started", StringComparison.Ordinal) && m.Contains("undo", StringComparison.Ordinal));
        Assert.False(flow.IsOffering);
    }

    [Fact]
    public async Task Nothing_is_downloaded_when_busy_right_after_Update_now()
    {
        using var folder = new ShellTestFolder("flow-busy-dl");
        var handler = Release();
        var starter = new FakeStarter();
        var ui = new FakeUi(folder.Root);
        ui.Answers.Enqueue(UpdateNoticeChoice.UpdateNow);
        var flow = new UpdateFlow(ui, practice: false, Env(handler, starter), Current);
        var check = await new UpdateChecker(handler, Current).CheckAsync(CancellationToken.None);
        int before = handler.Requests.Count;
        ui.Busy = "Available when Clean finishes";

        // Busy: the notice isn't even shown, and nothing is downloaded.
        Assert.Equal(UpdateFlow.OfferOutcome.NotShownBusy, await flow.OfferAsync(check, manual: true));
        Assert.Empty(ui.Notices);
        Assert.Equal(before, handler.Requests.Count);
        Assert.Empty(starter.Started);
    }

    // ------------------------------------------------------------------ M3: never during or right after a run, undo or scan

    [Fact]
    public async Task A_notice_waiting_behind_Undo_this_run_doesnt_open_on_top_of_the_undo()
    {
        using var folder = new ShellTestFolder("flow-gate");
        var ui = new FakeUi(folder.Root);
        var flow = new UpdateFlow(ui, practice: false, Env(Release(), new FakeStarter()), Current);
        var check = await new UpdateChecker(Release(), Current).CheckAsync(CancellationToken.None);

        // "Undo this run?" is open (it holds the dialog gate); the notice queues behind it.
        await ui.DialogGate.WaitAsync();
        var offer = flow.OfferAsync(check, manual: false);
        await Task.Delay(50);
        Assert.False(offer.IsCompleted);
        // The user clicks Undo: the undo starts as the question closes.
        ui.Busy = "Available when the undo finishes";
        ui.DialogGate.Release();

        Assert.Equal(UpdateFlow.OfferOutcome.NotShownBusy, await offer);
        Assert.Empty(ui.Notices);
        Assert.False(flow.IsOffering);
    }

    [Fact]
    public async Task The_automatic_notice_waits_out_a_dialog_and_an_undo_then_shows_once()
    {
        using var folder = new ShellTestFolder("flow-auto");
        var ui = new FakeUi(folder.Root);
        var flow = new UpdateFlow(ui, practice: false, Env(Release(), new FakeStarter()), Current);

        await ui.DialogGate.WaitAsync(); // a question is open
        var auto = flow.AutomaticAsync();
        await Task.Delay(150);
        Assert.Empty(ui.Notices); // an open dialog counts as busy
        ui.Busy = "Available when the undo finishes";
        ui.DialogGate.Release();
        await Task.Delay(150);
        Assert.Empty(ui.Notices); // the undo is running

        ui.Busy = null;
        await auto.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(ui.Notices);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task A_version_skipped_or_the_switch_turned_off_while_waiting_isnt_shown(bool skip, bool switchOff)
    {
        using var folder = new ShellTestFolder("flow-skip");
        var ui = new FakeUi(folder.Root) { Busy = "Available when the scan finishes" };
        var flow = new UpdateFlow(ui, practice: false, Env(Release(), new FakeStarter()), Current);
        var auto = flow.AutomaticAsync();
        await Until(() => ShellState.Get(ui.Settings).LastUpdateCheckUtc is not null); // checked; now waiting for the scan

        if (skip) ShellState.Update(ui.Settings, s => s with { SkippedUpdateVersion = "1.2.0" });
        if (switchOff) ShellPrefs.Set(ui.Settings, ShellPrefs.Get(ui.Settings) with { AutoUpdateCheck = false });
        ui.Busy = null;
        await auto.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(ui.Notices);
    }

    [Fact]
    public async Task The_automatic_and_the_manual_notice_never_stack()
    {
        using var folder = new ShellTestFolder("flow-stack");
        var ui = new FakeUi(folder.Root) { HoldNotice = new TaskCompletionSource<UpdateNoticeChoice>() };
        var flow = new UpdateFlow(ui, practice: false, Env(Release(), new FakeStarter()), Current);

        var auto = flow.AutomaticAsync();
        await Until(() => ui.NoticeCount == 1);
        Assert.True(flow.IsOffering);
        await flow.CheckNowAsync(); // "Check for updates" while the automatic notice is open: nothing new
        Assert.Equal(1, ui.NoticeCount);
        ui.HoldNotice.SetResult(UpdateNoticeChoice.Later);
        await auto.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(flow.IsOffering);

        // A manual notice answered "Later", then the automatic check finds the same version: not shown again this session.
        using var folder2 = new ShellTestFolder("flow-stack2");
        var ui2 = new FakeUi(folder2.Root);
        var flow2 = new UpdateFlow(ui2, practice: false, Env(Release(), new FakeStarter()), Current);
        await flow2.CheckNowAsync();
        Assert.Single(ui2.Notices);
        ShellState.Update(ui2.Settings, s => s with { LastUpdateCheckUtc = null });
        await flow2.AutomaticAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(ui2.Notices);
    }

    [Fact]
    public void An_open_dialog_counts_as_busy_and_the_gate_re_check_follows_the_rules()
    {
        var gate = new RunGate();
        Assert.Equal(UpdatePolicy.DialogOpenReason, UpdatePolicy.BusyReason(gate, false, false, false, dialogOpen: true));
        Assert.Null(UpdatePolicy.BusyReason(gate, false, false, false, dialogOpen: false));
        Assert.Equal(RunGate.BusyReason, UpdatePolicy.BusyReason(gate, true, false, false, dialogOpen: true)); // a run says so first

        Assert.True(UpdatePolicy.StillOffer(null, null, "1.2.0", autoCheckOn: true, manual: false));
        Assert.False(UpdatePolicy.StillOffer("busy", null, "1.2.0", autoCheckOn: true, manual: false));
        Assert.False(UpdatePolicy.StillOffer("busy", null, "1.2.0", autoCheckOn: true, manual: true)); // never while busy, even by hand
        Assert.False(UpdatePolicy.StillOffer(null, "1.2.0", "1.2.0", autoCheckOn: true, manual: false));
        Assert.False(UpdatePolicy.StillOffer(null, null, "1.2.0", autoCheckOn: false, manual: false));
        Assert.True(UpdatePolicy.StillOffer(null, "1.2.0", "1.2.0", autoCheckOn: false, manual: true)); // "Check for updates" shows a skipped one
    }

    // ------------------------------------------------------------------ L6: any unpack problem cleans up

    [Fact]
    public void A_zip_with_an_unsupported_packing_method_is_a_plain_problem_and_leaves_no_folder()
    {
        using var folder = new ShellTestFolder("flow-lzma");
        var app = folder.Folder("PC Tidy 1.0.0");
        var updates = Path.Combine(folder.Root, "Data", "updates");
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(z.CreateEntry("PC Tidy 1.2.0/PcTidy.exe", CompressionLevel.NoCompression).Open());
            w.Write("invented program bytes");
        }
        var bytes = ms.ToArray();
        // Mark the entry as LZMA (method 14), which .NET can't unpack: the local header (offset 8) and the central directory (offset 10).
        for (int i = 0; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 3 && bytes[i + 3] == 4) bytes[i + 8] = 14;
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 1 && bytes[i + 3] == 2) bytes[i + 10] = 14;
        }

        var ex = Assert.Throws<InvalidOperationException>(() => PortableUpdate.UnpackIntoNewFolder(new MemoryStream(bytes), app, updates, new Version(1, 2, 0)));
        Assert.Contains("wasn't changed", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(folder.Root, "PC Tidy 1.2.0")));
        Assert.False(Directory.Exists(Path.Combine(updates, "PC Tidy 1.2.0")));
    }

    // ------------------------------------------------------------------ L7: the path check compares where names really lead

    [Fact]
    public void The_installer_name_is_compared_by_file_id_and_final_path_not_by_spelling()
    {
        using var folder = new ShellTestFolder("flow-path");
        var f = folder.File(@"updates\" + SetupName, "MZ invented installer");
        var copy = folder.File(@"elsewhere\" + SetupName, "MZ invented installer");
        var sha = Hash(File.ReadAllBytes(f));
        using var held = VerifiedInstaller.Open(f, sha, SignatureInfo.NotSigned, Proof(sha), (_, _) => SignatureInfo.NotSigned);

        Assert.True(held.SameFileAsHeld(f));
        Assert.True(held.SameFileAsHeld(f.ToUpperInvariant()));
        Assert.True(held.SameFileAsHeld(Path.Combine(folder.Root, "updates", ".", "..", "updates", SetupName)));
        Assert.False(held.SameFileAsHeld(copy)); // same bytes, another file
        Assert.False(held.SameFileAsHeld(Path.Combine(folder.Root, "missing.exe"))); // fail-closed

        var starter = new FakeStarter();
        held.Start(starter);
        Assert.Equal(f, Assert.Single(starter.Started).FileName);
        Assert.Contains(ReleaseSource.ReleasesPage.AbsoluteUri, VerifiedInstaller.CantConfirmMessage);
    }

    // ------------------------------------------------------------------ L9: old downloads go

    [Fact]
    public void Old_installers_and_zips_go_once_a_newer_PC_Tidy_runs_and_nothing_else_is_touched()
    {
        using var folder = new ShellTestFolder("flow-clean");
        var store = new UpdateStore(folder.Root);
        string F(string name, string content = "x") => folder.File(Path.Combine("updates", name), content);
        var now = new DateTime(2030, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var gone = new[] { F("PC-Tidy-Setup-0.9.0.exe"), F("PC-Tidy-Setup-1.0.0.exe"), F("PC-Tidy-1.0.0-portable.zip"), F("PC-Tidy-Setup-0.9.5.exe.partial"), F("PC-Tidy-Setup-1.2.0.exe.partial") };
        File.SetLastWriteTimeUtc(gone[^1], now.AddDays(-1)); // a newer download that was abandoned long ago
        var stay = new[] { F("PC-Tidy-Setup-1.2.0.exe"), F("PC-Tidy-1.3.0-portable.zip.partial"), F("notes.txt"), F("PC-Tidy-Setup-1.0.0.exe.bak"), F("pc-tidy-setup-0.1.0.exe2") };
        File.SetLastWriteTimeUtc(stay[1], now.AddMinutes(-5));
        var unpacked = folder.Folder(@"updates\PC Tidy 1.0.0");
        File.WriteAllText(Path.Combine(unpacked, "PcTidy.exe"), "the portable copy that may be running");

        Assert.Equal(gone.Length, store.RemoveOld(new Version(1, 0, 0), now));
        Assert.All(gone, g => Assert.False(File.Exists(g), g));
        Assert.All(stay, s => Assert.True(File.Exists(s), s));
        Assert.True(File.Exists(Path.Combine(unpacked, "PcTidy.exe")));
        Assert.Equal(0, new UpdateStore(Path.Combine(folder.Root, "no-such-folder")).RemoveOld(new Version(1, 0, 0), now));
    }

    [Fact]
    public void Starting_a_newer_version_cleans_the_updates_folder_through_the_flow()
    {
        using var folder = new ShellTestFolder("flow-clean2");
        var old = folder.File(@"updates\PC-Tidy-Setup-1.0.0.exe");
        var ui = new FakeUi(folder.Root);
        new UpdateFlow(ui, practice: false, Env(new FakeHandler(), new FakeStarter()), Current).CleanOldDownloads(folder.Root);
        Assert.False(File.Exists(old));
    }

    // ------------------------------------------------------------------ L8: the privacy wording

    [Fact]
    public void The_welcome_text_mentions_the_update_check_and_upgraders_get_a_one_time_notice()
    {
        Assert.Contains("update check", HelpContent.Privacy);
        Assert.Contains("version number", HelpContent.Privacy);
        Assert.Equal("PC Tidy now checks GitHub once a day for updates (only its version number is sent). Turn it off in Settings.", HelpContent.UpdateCheckNotice);

        Assert.True(UpdatePolicy.ShowUpdateCheckNotice(onboardingDone: true, seen: false, autoCheckOn: true, practice: false)); // updated from 3.1.0
        Assert.False(UpdatePolicy.ShowUpdateCheckNotice(onboardingDone: true, seen: true, autoCheckOn: true, practice: false)); // once only
        Assert.False(UpdatePolicy.ShowUpdateCheckNotice(onboardingDone: false, seen: false, autoCheckOn: true, practice: false)); // new: the welcome says it
        Assert.False(UpdatePolicy.ShowUpdateCheckNotice(onboardingDone: true, seen: false, autoCheckOn: false, practice: false));
        Assert.False(UpdatePolicy.ShowUpdateCheckNotice(onboardingDone: true, seen: false, autoCheckOn: true, practice: true));
        Assert.False(new ShellState().UpdateCheckNoticeSeen);
    }
}
