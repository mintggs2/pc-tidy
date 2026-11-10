using System.Text.RegularExpressions;
using System.Windows;
using PcTidy.Catalog.Tweaks;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Tweaks.Reading;
using PcTidy.Features.Tweaks.UI;
using PcTidy.Hosting;
using Xunit;
using CoreHive = PcTidy.Core.Model.RegistryHive;

namespace PcTidy.Features.Tweaks.Tests;

public class ModuleAndPageTests
{
    [Fact]
    public void Module_matches_the_section_contract()
    {
        var m = new TweaksModule();
        Assert.Equal("tweaks", m.Id);
        Assert.Equal("Tweaks", m.Title);
        Assert.Equal(NavGroup.SpeedUp, m.Group);
        Assert.Equal("Wrench24", m.Icon);
        Assert.Equal(Release.V2, m.Release);
        Assert.Equal(new[] { "tweaks", "tweaks.badtweaks" }, m.Scanners.Select(s => s.FeatureId).ToArray());
        Assert.True(m.TweaksScanner.IncludedInOptimizeAll);
        Assert.All(m.Scanners, s => Assert.Equal(Release.V2, s.Release));
    }

    [Fact]
    public void Feature_code_never_changes_the_system_directly()
    {
        var src = Path.Combine(Path.GetDirectoryName(TestFolder.TestbedRoot)!, "..", "src", "PcTidy.Features.Tweaks");
        var forbidden = new Regex(@"\b(File\.(Delete|Move|Copy|Replace|WriteAll\w*|AppendAll\w*|Create)|Directory\.(Delete|Move|CreateDirectory)|\.MoveTo\(|Process\.Start|ShellExecute)|\.(SetValue|DeleteValue|DeleteSubKey\w*|CreateSubKey)\s*\(|FileAccess\.(Write|ReadWrite)");
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && forbidden.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}").ToList();
        Assert.Empty(hits);
    }

    private sealed class Services(PcTidyHost host, IFlowHost flow) : IServiceProvider
    {
        public object? GetService(Type t) => t == typeof(IFlowHost) ? flow : host.GetService(t);
    }

    [Fact]
    public void Page_loads_scans_ticks_a_preset_and_hands_the_plan_to_the_shell()
    {
        using var reg = new TestRegistry("ui");
        using var folder = new TestFolder("ui");
        reg.Lm(@"SOFTWARE\Policies\Microsoft\Windows Defender", "DisableAntiSpyware", 1);
        reg.Text(CoreHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "VRROptimizeEnable=1;");
        using var host = PcTidyHost.CreateSandbox(Path.Combine(folder.Path, "pc"), SandboxProfile.Minimal);
        var flow = new FakeFlowHost();
        var module = new TweaksModule(ctx => TweakEnvironment.Sandbox(ctx.Locations, ctx.System, reg.Root));

        Sta.Run(() =>
        {
            var page = (FrameworkElement)module.CreatePage(new Services(host, flow))!;
            page.Measure(new Size(1000, 760));
            page.Arrange(new Rect(0, 0, 1000, 760));
            var vm = (TweaksViewModel)page.DataContext;
            Assert.False(vm.HasScanned);

            Sta.Pump(vm.ScanCommand.ExecuteAsync(null));
            Assert.True(vm.HasScanned);
            Assert.Contains(vm.Rows, r => r is TweakHeaderRow);
            page.UpdateLayout();

            // Preset: only the proven, non-opt-in Safe gaming rows get ticked.
            var safeGaming = vm.Presets.Single(p => p.Preset == TweakPreset.SafeGaming);
            vm.ApplyPresetCommand.Execute(safeGaming);
            var ticked = vm.AllRows.Where(r => r.IsChecked).Select(r => r.Tweak?.Id).ToList();
            Assert.Contains("gaming.windowed-optimizations", ticked);
            Assert.DoesNotContain("gaming.pointer-precision-off", ticked);
            Assert.All(vm.AllRows.Where(r => r.IsChecked), r => Assert.Equal(TweakPreset.SafeGaming, r.Preset));
            Assert.True(vm.PreviewCommand.CanExecute(null));

            // Placebo rows can't be ticked.
            var placebo = vm.AllRows.First(r => r.Tweak?.Verdict == TweakVerdict.Placebo);
            placebo.IsChecked = true;
            Assert.False(placebo.IsChecked);

            // Find bad tweaks tab: the Defender policy is there, never pre-ticked.
            vm.Tab = TweaksViewModel.FinderTab;
            var defender = vm.Rows.OfType<TweakItemRow>().Single(r => r.Item.Id == "badtweaks.security.defender-off");
            Assert.False(defender.IsChecked);
            defender.IsChecked = true;
            page.UpdateLayout();

            Sta.Pump(vm.PreviewCommand.ExecuteAsync(null));
            Assert.Equal(new[] { "tweaks", "tweaks.badtweaks" }, flow.Reviewed.Select(p => p.FeatureId).ToArray());
            var tweakPlan = flow.Reviewed[0];
            Assert.Contains(tweakPlan.Items, i => i.Registry?.Data == "VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;");
            Assert.All(flow.Reviewed.SelectMany(p => p.Items), i => Assert.True(host.Safety.Check(i).Allowed || i.Kind == ActionKind.ServiceSetStartType, i.Description));

            // Memory integrity is framed as "keep it on": information only, no tick box, no FPS pitch.
            var integrity = vm.AllRows.Single(r => r.Tweak?.Id == "gaming.memory-integrity");
            Assert.Equal("Memory integrity: keep it on", integrity.Title);
            Assert.Equal(Visibility.Hidden, integrity.CheckBoxVisibility);
            Assert.False(integrity.IsSelectable);
            Assert.DoesNotContain("FPS", integrity.Item.Reason);
            Assert.DoesNotContain(integrity.Tags, t => t.Text.StartsWith("Risk", StringComparison.Ordinal));

            // Expert mode asks first (same question as Settings): saying no keeps it off.
            vm.Tab = TweaksViewModel.TweaksTab;
            var confirm = vm.ConfirmExpertMode;
            vm.ConfirmExpertMode = () => Task.FromResult(false);
            vm.ExpertMode = true;
            Sta.Pump(Task.Delay(25));
            Assert.False(host.Settings.ExpertMode);
            Assert.False(vm.ExpertMode);

            // Saying yes stores it, unlocks expert rows after a rescan and keeps the user's ticks.
            for (var i = 0; i < 200 && vm.IsScanning; i++) Sta.Pump(Task.Delay(25));
            vm.ApplyPresetCommand.Execute(safeGaming);
            var tickedBefore = vm.AllRows.Where(r => r.IsChecked && r.SectionId == TweakPlanFeatures.Tweaks).Select(r => r.Item.Id).ToList();
            Assert.NotEmpty(tickedBefore);
            vm.ConfirmExpertMode = () => Task.FromResult(true);
            vm.ExpertMode = true; // starts a rescan of the tweaks
            Sta.Pump(Task.Delay(25));
            Assert.True(host.Settings.ExpertMode);
            for (var i = 0; i < 200 && vm.IsScanning; i++) Sta.Pump(Task.Delay(25));
            Assert.False(vm.IsScanning);
            Assert.Contains(vm.AllRows, r => r.Tweak?.IsExpertOnly == true && r.IsSelectable);
            Assert.All(tickedBefore, id => Assert.True(vm.AllRows.Single(r => r.Item.Id == id).IsChecked, id));
            Assert.DoesNotContain(vm.AllRows, r => r.IsDisabled && r.Status.Text == "Available");
            vm.ConfirmExpertMode = confirm;

            // Source links go through the shell as an OpenExternal step.
            var withSource = vm.AllRows.First(r => r.Sources.Count > 0);
            withSource.OpenLinkCommand.Execute(withSource.Sources[0]);
            Sta.Pump(Task.Delay(50));
            var link = Assert.Single(flow.Approved);
            Assert.Equal(ActionKind.OpenExternal, Assert.Single(link.Items).Kind);
            Assert.True(host.Safety.Check(link.Items[0]).Allowed);
        });
    }
}
