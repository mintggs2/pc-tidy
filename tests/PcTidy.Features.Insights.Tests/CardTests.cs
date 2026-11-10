using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Insights.Model;
using PcTidy.Features.Insights.Source;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Features.Insights.Tests;

/// <summary>Detection cards over a sandbox PC (real SafetyGuard via PcTidyHost.CreateSandbox) with injected facts.</summary>
public class CardTests
{
    private static async Task<InsightsReport> ScanAsync(TestbedFolder tb, SandboxProfile profile, FakeSource source, IReadOnlyList<string>? roots = null)
    {
        using var host = PcTidyHost.CreateSandbox(tb.Root, profile);
        var scanner = new InsightsScanner(_ => source, new FixedClock());
        return await scanner.ScanReportAsync(host.CreateScanContext(ScanDepth.Quick, roots), null, CancellationToken.None);
    }

    private static InsightCard Card(InsightsReport r, string id) => r.Find(id) ?? throw new Xunit.Sdk.XunitException("Missing card " + id);

    // ------------------------------------------------------------------ modified Windows

    [Fact]
    public async Task Atlas_markers_and_missing_services_are_reported_as_a_best_guess()
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource
        {
            Identity = new WindowsIdentity
            {
                Build = 26200, ProductName = "Windows 11 Pro",
                OemInformation = new Dictionary<string, string> { ["Model"] = "AtlasOS Playbook" },
                PresentWindowsFolders = new HashSet<string> { "AtlasModules" },
                MissingCoreServices = new[] { "WinDefend" },
            },
        };
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, src), InsightsIds.ModifiedWindows);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.True(card.IsHeuristic);
        Assert.Contains("AtlasOS", card.Headline);
        Assert.Contains(card.Facts, f => f.Value.Contains("Microsoft Defender Antivirus"));
        Assert.Contains("reinstalling Windows", card.Explanation);
        Assert.Empty(card.Items); // information only
    }

    [Fact]
    public async Task Stock_windows_shows_no_signs_and_unrelated_words_dont_match()
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource { Identity = new WindowsIdentity { Build = 26100, OemInformation = new Dictionary<string, string> { ["Manufacturer"] = "Atlasian Computers" } } };
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, src), InsightsIds.ModifiedWindows);
        Assert.Equal(InsightStatus.Good, card.Status);
    }

    // ------------------------------------------------------------------ reliability and memory test

    [Fact]
    public async Task Stability_index_shows_now_average_and_trend()
    {
        using var tb = new TestbedFolder();
        var samples = Enumerable.Range(0, 30).Select(d => new StabilitySample(FixedClock.Now.AddDays(-29 + d), d < 20 ? 9.0 : 3.0)).ToList();
        var src = new FakeSource { Stability = ReadOutcome<IReadOnlyList<StabilitySample>>.Ok(samples) };
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, src), InsightsIds.Reliability);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.Contains("getting worse", card.Headline);
        Assert.Contains(card.Facts, f => f.Label == "Lowest in 30 days");
    }

    [Theory]
    [InlineData(ReadProblem.NeedsAdmin, InsightStatus.NeedsAdmin)]
    [InlineData(ReadProblem.NotAvailable, InsightStatus.NotAvailable)]
    public async Task Stability_index_unavailable_is_said_plainly(ReadProblem problem, InsightStatus expected)
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource { Stability = new ReadOutcome<IReadOnlyList<StabilitySample>>(null, problem) };
        Assert.Equal(expected, Card(await ScanAsync(tb, SandboxProfile.Minimal, src), InsightsIds.Reliability).Status);
    }

    [Theory]
    [InlineData(1202, MemoryTestOutcome.ErrorsFound, InsightStatus.Attention)]
    [InlineData(1201, MemoryTestOutcome.NoErrors, InsightStatus.Good)]
    [InlineData(1103, MemoryTestOutcome.Cancelled, InsightStatus.Info)]
    public async Task Memory_test_result_drives_the_card(int eventId, MemoryTestOutcome outcome, InsightStatus expected)
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource { MemoryTests = ReadOutcome<IReadOnlyList<MemoryTestResult>>.Ok(new[] { new MemoryTestResult(FixedClock.Now.AddDays(-2), outcome, eventId) }) };
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, src), InsightsIds.MemoryTest);
        Assert.Equal(expected, card.Status);
        Assert.Empty(card.Links); // mdsched is a program: instructions only, never launched
    }

    [Fact]
    public async Task No_memory_test_yet_gives_steps_only()
    {
        using var tb = new TestbedFolder();
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, new FakeSource()), InsightsIds.MemoryTest);
        Assert.Equal(InsightStatus.Info, card.Status);
        Assert.Contains(card.Steps, s => s.Contains("Windows Memory Diagnostic"));
        Assert.Empty(card.Items);
    }

    // ------------------------------------------------------------------ backup

    [Fact]
    public async Task OneDrive_folder_sync_alone_is_explained_as_not_a_backup()
    {
        using var tb = new TestbedFolder();
        var card = Card(await ScanAsync(tb, SandboxProfile.Default, new FakeSource()), InsightsIds.Backup);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.Equal("Only sync found, no backup", card.Headline);
        Assert.Contains("Sync is not a backup", card.Explanation);
    }

    [Fact]
    public async Task Backup_states_nothing_recent_and_backup_app()
    {
        using var tb = new TestbedFolder();
        Assert.Equal("No backup found", Card(await ScanAsync(tb, SandboxProfile.Minimal, new FakeSource()), InsightsIds.Backup).Headline);

        var recent = new FakeSource { Backup = new BackupFacts { FileHistoryConfigured = true, FileHistoryLastBackupUtc = FixedClock.Now.AddDays(-3) } };
        var good = Card(await ScanAsync(tb, SandboxProfile.Minimal, recent), InsightsIds.Backup);
        Assert.Equal(InsightStatus.Good, good.Status);
        Assert.Contains("3 days ago", good.Headline);

        var old = new FakeSource { Backup = new BackupFacts { FileHistoryConfigured = true, FileHistoryLastBackupUtc = FixedClock.Now.AddDays(-90) } };
        Assert.Equal(InsightStatus.Attention, Card(await ScanAsync(tb, SandboxProfile.Minimal, old), InsightsIds.Backup).Status);

        var app = new FakeSource { Identity = new WindowsIdentity { Build = 26100, InstalledApps = new[] { "Veeam Agent for Microsoft Windows", "Arqade Viewer" } } };
        var withApp = Card(await ScanAsync(tb, SandboxProfile.Minimal, app), InsightsIds.Backup);
        Assert.Equal(InsightStatus.Good, withApp.Status);
        Assert.DoesNotContain(withApp.Facts, f => f.Value.Contains("Arqade")); // "Arq" matches whole words only
    }

    // ------------------------------------------------------------------ hosts file

    [Fact]
    public async Task Hosts_audit_flags_Microsoft_blocks_and_points_to_Tweaks()
    {
        using var tb = new TestbedFolder();
        using (var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal))
        {
            var etc = Path.Combine(host.Locations.WindowsDir, "System32", "drivers", "etc");
            Directory.CreateDirectory(etc);
            File.WriteAllLines(Path.Combine(etc, "hosts"), new[]
            {
                "# Copyright (c) Microsoft Corp.", "#\t127.0.0.1       localhost",
                "0.0.0.0 fe2.update.microsoft.com", "0.0.0.0 ads.example.com", "192.168.1.20 nas.local   # my NAS",
            });
        }
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, new FakeSource()), InsightsIds.Hosts);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.Contains("Windows Update", card.Headline);
        Assert.Equal(InsightsIds.TweaksModule, card.Navigate?.ModuleId);
        Assert.Equal(3, card.Facts.Count);
        Assert.Empty(card.Items); // the fix belongs to Tweaks' bad-tweak finder
    }

    [Fact]
    public async Task Missing_hosts_file_is_not_available_not_an_error()
    {
        using var tb = new TestbedFolder();
        Assert.Equal(InsightStatus.NotAvailable, Card(await ScanAsync(tb, SandboxProfile.Minimal, new FakeSource()), InsightsIds.Hosts).Status);
    }

    // ------------------------------------------------------------------ OneDrive free up space

    [Fact]
    public async Task OneDrive_offers_big_old_downloaded_files_unticked_and_allowed_by_the_real_guard()
    {
        using var tb = new TestbedFolder();
        string oneDrive;
        using (var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default))
        {
            oneDrive = host.Locations.CloudRoots[0];
            TestbedFolder.BigOldFile(Path.Combine(oneDrive, "Videos", "holiday-2019.mov"), 80L << 20, FixedClock.Now.AddYears(-2));
            TestbedFolder.BigOldFile(Path.Combine(oneDrive, "Recent.mov"), 80L << 20, FixedClock.Now.AddDays(-5));      // too new
            TestbedFolder.BigOldFile(Path.Combine(oneDrive, "small.txt"), 1L << 20, FixedClock.Now.AddYears(-2));        // too small
            var pinned = TestbedFolder.BigOldFile(Path.Combine(oneDrive, "Keep.iso"), 80L << 20, FixedClock.Now.AddYears(-2));
            File.SetAttributes(pinned, File.GetAttributes(pinned) | (FileAttributes)0x00080000);                         // "Always keep on this device"
        }
        var card = Card(await ScanAsync(tb, SandboxProfile.Default, new FakeSource()), InsightsIds.OneDriveFreeUp);

        var row = Assert.Single(card.Items);
        Assert.Equal("holiday-2019.mov", row.Title);
        Assert.Null(row.DisabledReason);
        Assert.False(row.DefaultSelected);
        Assert.Equal(Badge.Check, row.Badge);
        var action = Assert.Single(row.Actions);
        Assert.Equal(ActionKind.OneDriveFreeUpSpace, action.Kind);
        Assert.True(action.Reversible);
        Assert.Contains("80 MB", card.Headline); // the headline counts exactly what's offered
        Assert.Contains(card.Facts, f => f.Value == "1 file");     // the pinned file is mentioned, not offered
        Assert.Contains(card.Steps, s => s.Contains("Free up space"));
    }

    [Fact]
    public async Task OneDrive_card_is_not_available_without_OneDrive()
    {
        using var tb = new TestbedFolder();
        Assert.Equal(InsightStatus.NotAvailable, Card(await ScanAsync(tb, SandboxProfile.Minimal, new FakeSource()), InsightsIds.OneDriveFreeUp).Status);
    }

    // ------------------------------------------------------------------ compress old folders

    [Fact]
    public async Task Old_compressible_folders_are_offered_opt_in_media_folders_are_not()
    {
        using var tb = new TestbedFolder();
        IKnownLocations loc;
        using (var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal))
        {
            loc = host.Locations;
            var old = FixedClock.Now.AddYears(-3);
            TestbedFolder.BigOldFile(Path.Combine(loc.Downloads, "Old reports", "2021.csv"), 200L << 20, old);
            TestbedFolder.BigOldFile(Path.Combine(loc.Downloads, "Old reports", "logs", "server.log"), 150L << 20, old);
            TestbedFolder.BigOldFile(Path.Combine(loc.Downloads, "Old videos", "trip.mp4"), 400L << 20, old);
            TestbedFolder.BigOldFile(Path.Combine(loc.Downloads, "Fresh data", "now.csv"), 400L << 20, FixedClock.Now.AddDays(-10));
        }
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, new FakeSource().WithNtfs(loc)), InsightsIds.CompressFolders);

        var row = Assert.Single(card.Items);
        Assert.Equal("Old reports", row.Title);
        Assert.False(row.DefaultSelected);
        Assert.Null(row.DisabledReason);
        var action = Assert.Single(row.Actions);
        Assert.Equal((ActionKind.RunSystemTool, ToolIds.CompactLzx, true), (action.Kind, action.ToolId, action.Reversible));
        Assert.Equal(Path.Combine(loc.Downloads, "Old reports"), action.Args!["folder"]);
        Assert.Equal(row.SizeBytes, action.ExpectedSize);
    }

    [Fact]
    public async Task Compress_card_needs_an_NTFS_drive()
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource();
        src.FileSystems["X:\\"] = "FAT32";
        Assert.Equal(InsightStatus.NotAvailable, Card(await ScanAsync(tb, SandboxProfile.Minimal, src), InsightsIds.CompressFolders).Status);
    }

    // ------------------------------------------------------------------ Dev Drive

    [Fact]
    public async Task Dev_drive_advisor_needs_windows_11_and_dev_folders()
    {
        using var tb = new TestbedFolder();
        var win10 = new FakeSource { Identity = new WindowsIdentity { Build = 19045 } };
        Assert.Equal(InsightStatus.NotAvailable, Card(await ScanAsync(tb, SandboxProfile.Minimal, win10), InsightsIds.DevDrive).Status);
        Assert.Equal(InsightStatus.NotAvailable, Card(await ScanAsync(tb, SandboxProfile.Minimal, new FakeSource()), InsightsIds.DevDrive).Status); // no code

        using (var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal))
            Directory.CreateDirectory(Path.Combine(host.Locations.UserProfile, "source", "app", ".git"));
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, new FakeSource()), InsightsIds.DevDrive);
        Assert.Equal(InsightStatus.Info, card.Status);
        Assert.Contains("1 code project", card.Headline);
        Assert.Contains(card.Links, l => l.Target == "ms-settings:disksandvolumes");
        Assert.Contains(card.Steps, s => s.Contains("Keep Defender on"));
        Assert.Empty(card.Items); // never creates volumes

        var early22H2 = new FakeSource { Identity = new WindowsIdentity { Build = 22621, Ubr = 1000 } };
        Assert.Equal(InsightStatus.NotAvailable, Card(await ScanAsync(tb, SandboxProfile.Minimal, early22H2), InsightsIds.DevDrive).Status);
    }

    // ------------------------------------------------------------------ hardware detection

    [Fact]
    public async Task Display_stuck_at_60_hz_is_spotted()
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource { Displays = { new DisplayFacts("Gaming monitor", 60, 144, 2560, 1440), new DisplayFacts("Office monitor", 60, 60, 1920, 1080) } };
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, src), InsightsIds.RefreshRate);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.Contains("144 Hz", card.Headline);
        Assert.Contains(card.Links, l => l.Target == "ms-settings:display-advanced");
    }

    [Fact]
    public async Task Memory_below_rated_speed_is_spotted()
    {
        using var tb = new TestbedFolder();
        var src = new FakeSource { Memory = ReadOutcome<MemoryFacts>.Ok(new MemoryFacts(new[] { new MemoryModule("DIMM1", 16L << 30, 6000, 4800), new MemoryModule("DIMM2", 16L << 30, 6000, 4800) }, 4)) };
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, src), InsightsIds.Ram);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.Contains("4", card.Headline);
        Assert.True(card.IsHeuristic);
    }

    [Fact]
    public async Task Games_on_the_hard_disk_point_to_move_to_another_drive()
    {
        using var tb = new TestbedFolder();
        using (var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Default))
        {
            foreach (var lib in host.Locations.SteamLibraries)
                Directory.CreateDirectory(Path.Combine(lib, "steamapps", "common", lib.Contains(Path.Combine(tb.Root, "D")) ? "Big Open World" : "Small Puzzle"));
        }
        var card = Card(await ScanAsync(tb, SandboxProfile.Default, new FakeSource()), InsightsIds.GamesOnHdd);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.Contains(card.Facts, f => f.Label == "Big Open World");
        Assert.DoesNotContain(card.Facts, f => f.Label == "Small Puzzle");
        Assert.Equal(InsightsIds.DriveMoveModule, card.Navigate?.ModuleId);
    }

    [Theory]
    [InlineData(14_318_180L, InsightStatus.Attention, true)]
    [InlineData(10_000_000L, InsightStatus.Good, false)]
    [InlineData(3_579_545L, InsightStatus.Info, true)]
    public async Task Timer_check_reads_the_QPC_frequency(long frequency, InsightStatus expected, bool linksTweaks)
    {
        using var tb = new TestbedFolder();
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, new FakeSource { Timer = new TimerFacts(frequency, true) }), InsightsIds.Timer);
        Assert.Equal(expected, card.Status);
        Assert.Equal(linksTweaks, card.Navigate?.ModuleId == InsightsIds.TweaksModule);
    }

    [Fact]
    public async Task Vanguard_card_reports_secure_boot_off_and_tpm_needing_admin()
    {
        using var tb = new TestbedFolder();
        var off = new FakeSource { Security = new SecurityFacts { SecureBoot = TriState.No, TpmPresent = TriState.Yes, TpmVersion = "2.0", MemoryIntegrity = TriState.Yes } };
        var card = Card(await ScanAsync(tb, SandboxProfile.Minimal, off), InsightsIds.Vanguard);
        Assert.Equal(InsightStatus.Attention, card.Status);
        Assert.StartsWith("Secure Boot is off", card.Headline);

        var admin = new FakeSource { Security = new SecurityFacts { SecureBoot = TriState.Yes, TpmNeedsAdmin = true } };
        var needs = Card(await ScanAsync(tb, SandboxProfile.Minimal, admin), InsightsIds.Vanguard);
        Assert.Equal(InsightStatus.NeedsAdmin, needs.Status);
        Assert.Contains(needs.Facts, f => f.Label == "TPM" && f.Value == "Needs admin to check");
    }
}
