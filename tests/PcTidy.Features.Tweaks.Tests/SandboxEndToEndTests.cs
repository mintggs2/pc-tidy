using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Tweaks.BadTweaks;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Hosting;
using PcTidy.TestbedGen;
using Xunit;
using CoreHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Tweaks.Tests;

/// <summary>
/// The whole path with the real SafetyGuard, Executor, platform RegistryHandler and UndoEngine
/// (PcTidyHost.CreateSandbox): scan → plan → apply → undo → the registry is exactly as before,
/// including values and keys that didn't exist. Everything stays inside HKCU\Software\PCTidyTest.
/// </summary>
public class SandboxEndToEndTests
{
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string Defender = @"SOFTWARE\Policies\Microsoft\Windows Defender";
    private const string Profile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string ClassicMenuClsid = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    [Fact]
    public async Task Tweaks_apply_and_undo_back_to_the_exact_prior_state()
    {
        using var reg = new TestRegistry("e2e");
        using var folder = new TestFolder("e2e");
        reg.Cu(Adv, "HideFileExt", 1);                                   // existed (Windows default)
        reg.Cu(Adv, "Hidden", 2);                                        // key exists, LaunchTo does not
        reg.Text(CoreHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "1");
        reg.Text(CoreHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "6");
        // MouseThreshold2, the classic-menu CLSID key and the HKLM CEIP policy don't exist at all.

        using var host = PcTidyHost.CreateSandbox(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        var module = new TweaksModule(ctx => TweakEnvironment.Sandbox(ctx.Locations, ctx.System, reg.Root));
        var ctx = host.CreateScanContext(ScanDepth.Quick);
        var outcome = await module.TweaksScanner.ScanRowsAsync(ctx, null, CancellationToken.None);
        string[] picked = { "ui.file-extensions", "ui.open-this-pc", "ui.classic-context-menu", "gaming.pointer-precision-off", "privacy.ceip-off" };
        var items = outcome.Rows.Where(r => picked.Contains(r.Tweak.Id)).Select(r => r.Item).ToList();
        Assert.Equal(picked.Length, items.Count);
        Assert.All(items, i => Assert.True(i.DisabledReason is null && i.Actions.Count > 0, i.Id));

        var plan = PlanBuilder.FromSelection("tweaks", "Tweaks", items);
        Assert.All(plan.Items, i => Assert.True(host.Safety.Check(i).Allowed, $"{i.Description}: {host.Safety.Check(i).Reason}"));
        var run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions(), null, CancellationToken.None);
        Assert.True(run.Status == RunStatus.Completed, $"{run.Status}: {string.Join("; ", run.Steps.Select(s => s.Status + " " + s.Message))}");

        Assert.Equal(0, reg.Get(CoreHive.CurrentUser, Adv, "HideFileExt"));
        Assert.Equal(1, reg.Get(CoreHive.CurrentUser, Adv, "LaunchTo"));
        Assert.Equal("", reg.Get(CoreHive.CurrentUser, ClassicMenuClsid + @"\InprocServer32", ""));
        Assert.Equal("0", reg.Get(CoreHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2"));
        Assert.Equal(0, reg.Get(CoreHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable"));

        // A fresh scan now shows them as already applied.
        var after = await module.TweaksScanner.ScanRowsAsync(ctx, null, CancellationToken.None);
        Assert.All(after.Rows.Where(r => picked.Contains(r.Tweak.Id)), r => Assert.Equal("Already applied", r.Item.CurrentState));

        var undo = await host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        Assert.True(undo.Failed == 0, string.Join("; ", undo.Messages));

        Assert.Equal(1, reg.Get(CoreHive.CurrentUser, Adv, "HideFileExt"));
        Assert.Equal(2, reg.Get(CoreHive.CurrentUser, Adv, "Hidden"));
        Assert.False(reg.HasValue(CoreHive.CurrentUser, Adv, "LaunchTo"));                     // "wasn't there" → not there
        Assert.False(reg.KeyExists(CoreHive.CurrentUser, ClassicMenuClsid));                   // created keys removed
        Assert.Equal("1", reg.Get(CoreHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed"));
        Assert.Equal("6", reg.Get(CoreHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1"));
        Assert.False(reg.HasValue(CoreHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2"));
        Assert.False(reg.KeyExists(CoreHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\SQMClient"));
    }

    [Fact]
    public async Task Bad_tweak_repairs_apply_and_undo_with_the_real_guard()
    {
        using var reg = new TestRegistry("e2e");
        using var folder = new TestFolder("e2e");
        reg.Lm(Defender, "DisableAntiSpyware", 1);
        reg.Lm(Defender, "DisableAntiVirus", 1);
        reg.Service("WinDefend", start: 4);
        reg.Lm(Profile, "SystemResponsiveness", 100);

        var tb = TestbedBuilder.Generate(Path.Combine(folder.Path, "pc"), TestbedSize.Small, SandboxProfile.Minimal);
        try { await RepairAndUndo(reg, tb); }
        finally { TestbedBuilder.Delete(tb.Root); } // the testbed has read-only files and junctions: use its own cleaner
    }

    private static async Task RepairAndUndo(TestRegistry reg, TestbedInfo tb)
    {
        var roblox = Path.Combine(tb.Locations.LocalAppData, "Roblox", "Versions", "version-1", "ClientSettings", "ClientAppSettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(roblox)!);
        File.WriteAllText(roblox, "{\"FFlagFpsBoost\": true}");
        var before = TestbedBuilder.Manifest(tb.Root);

        using var host = PcTidyHost.CreateSandbox(tb.Root, SandboxProfile.Minimal);
        var scanner = new BadTweakScanner(ctx => TweakEnvironment.Sandbox(ctx.Locations, ctx.System, reg.Root));
        var result = await scanner.ScanAsync(host.CreateScanContext(ScanDepth.Quick), null, CancellationToken.None);
        var defenderItem = result.AllItems.Single(i => i.Id == "badtweaks.security.defender-off");
        var mmcss = result.AllItems.Single(i => i.Id == "badtweaks.scheduler.system-responsiveness");
        var fastFlags = result.AllItems.Single(i => i.Id == "badtweaks.games.roblox-fastflags");

        var plan = PlanBuilder.FromSelection("tweaks.badtweaks", "Find bad tweaks", new[] { defenderItem, mmcss, fastFlags });
        var run = await host.Executor.ExecuteAsync(plan, new ExecuteOptions(), null, CancellationToken.None);
        Assert.False(reg.HasValue(CoreHive.LocalMachine, Defender, "DisableAntiSpyware"));
        Assert.False(reg.HasValue(CoreHive.LocalMachine, Defender, "DisableAntiVirus"));
        Assert.Equal(20, reg.Get(CoreHive.LocalMachine, Profile, "SystemResponsiveness"));
        Assert.False(File.Exists(roblox));
        // The WinDefend start-type step never touches a real service in the sandbox (and today the guard refuses it: see vault page).
        Assert.Contains(run.Steps, s => s.Item.Kind == ActionKind.ServiceSetStartType && s.Status != StepStatus.Done);

        var undo = await host.Undo.UndoRunAsync(run.RunId, null, CancellationToken.None);
        // Undo never switches Defender off again (UNDO-1): the two "Defender off" policies stay removed, and it says so.
        Assert.Equal(2, undo.Failed);
        Assert.Equal(2, undo.Messages.Count(m => m.Contains("didn't put this Microsoft Defender policy back")));
        Assert.False(reg.HasValue(CoreHive.LocalMachine, Defender, "DisableAntiSpyware"));
        Assert.False(reg.HasValue(CoreHive.LocalMachine, Defender, "DisableAntiVirus"));
        Assert.Equal(100, reg.Get(CoreHive.LocalMachine, Profile, "SystemResponsiveness"));
        Assert.True(File.Exists(roblox));
        // Identical apart from PC Tidy's own data folder (run journal, emptied quarantine slot).
        var diff = TestbedBuilder.Compare(before, TestbedBuilder.Manifest(tb.Root));
        var outsideData = diff.Differences.Where(d => !d.Path.StartsWith("Data", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(outsideData.Count == 0, diff.Format());
    }
}
