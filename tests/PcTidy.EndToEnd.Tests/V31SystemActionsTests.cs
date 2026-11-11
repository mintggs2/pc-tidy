using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.EndToEnd.Tests;

/// <summary>
/// The v3.1 system actions through the real composition root on a practice PC: every one has a handler, passes the
/// real SafetyGuard when built as the features will build them, previews what it would do, and in the practice PC
/// changes nothing (no service, device, boot setting or winget call reaches this PC).
/// </summary>
public class V31SystemActionsTests
{
    private static string NewRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        var root = Path.Combine(d!.FullName, "testbed", "e2e", "v31-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static PlanItem Tool(string id, Dictionary<string, string>? args = null, bool admin = true, bool systemChange = false) => new()
    {
        Kind = ActionKind.RunSystemTool, FeatureId = "pccare", Description = id, ToolId = id, Args = args,
        NeedsAdmin = admin, IsSystemChange = systemChange, Reversible = false,
    };

    private static IReadOnlyList<PlanItem> Items() => new[]
    {
        Tool(ToolIds.ServiceRestart, new() { ["name"] = "WlanSvc" }),
        Tool(ToolIds.WindowsUpdateReset, systemChange: true),
        Tool(ToolIds.MsiReregister),
        Tool(ToolIds.WingetInstall, new() { ["id"] = "Microsoft.VCRedist.2015+.x64" }, admin: false),
        Tool(ToolIds.BcdDeleteValue, new() { ["name"] = "safeboot" }, systemChange: true) with { Reversible = true },
        new PlanItem
        {
            Kind = ActionKind.DeviceSetEnabled, FeatureId = "pccare", Description = "Turn the camera back on", Enable = true,
            NeedsAdmin = true, IsSystemChange = true, Args = new() { ["instanceId"] = @"USB\VID_1234&PID_ABCD\5&2B3C4D5E&0&1" },
        },
    };

    [Fact]
    public async Task Every_v31_action_has_one_handler_passes_the_guard_and_only_previews_on_a_practice_pc()
    {
        var root = NewRoot();
        try
        {
            using var host = PcTidyHost.CreateSandbox(root);
            foreach (var item in Items())
            {
                Assert.Single(host.Handlers, h => h.CanHandle(item));
                var verdict = host.Safety.Check(item);
                Assert.True(verdict.Allowed, $"{item.Description}: {verdict.Reason}");
            }

            var plan = new ActionPlan { FeatureId = "pccare", Title = "Fix-it", Items = Items() };
            var preview = await host.Executor.ExecuteAsync(plan, new ExecuteOptions { Mode = ExecutionMode.Simulate }, null, CancellationToken.None);
            Assert.All(preview.Steps, s => Assert.True(s.Status == StepStatus.Simulated, $"{s.Item.Description}: {s.Status} {s.Message}"));
            Assert.Contains(preview.Steps, s => s.Message!.Contains("licence terms"));
            Assert.Contains(preview.Steps, s => s.Message!.Contains("/enable-device"));

            var real = await host.Executor.ExecuteAsync(plan with { RunId = ActionPlan.NewRunId() + "r" }, new ExecuteOptions { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) },
                null, CancellationToken.None);
            Assert.DoesNotContain(real.Steps, s => s.Status == StepStatus.Done); // a practice PC never changes the real one
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Dangerous_variants_are_refused_by_the_real_guard()
    {
        var root = NewRoot();
        try
        {
            using var host = PcTidyHost.CreateSandbox(root);
            var refused = new[]
            {
                Tool(ToolIds.ServiceRestart, new() { ["name"] = "WinDefend" }),
                Tool(ToolIds.WindowsUpdateReset, new() { ["folder"] = @"C:\Windows" }, systemChange: true),
                Tool(ToolIds.WingetInstall, new() { ["id"] = "Contoso.Tool" }, admin: false),
                Tool(ToolIds.BcdDeleteValue, new() { ["name"] = "nx" }),
                new PlanItem
                {
                    Kind = ActionKind.DeviceSetEnabled, FeatureId = "pccare", Description = "off", Enable = false,
                    NeedsAdmin = true, IsSystemChange = true, Args = new() { ["instanceId"] = @"USB\VID_1234&PID_ABCD\5&2B3C4D5E&0&1" },
                },
                new PlanItem
                {
                    Kind = ActionKind.DeviceSetEnabled, FeatureId = "pccare", Description = "disk", Enable = true,
                    NeedsAdmin = true, IsSystemChange = true, Args = new() { ["instanceId"] = @"SCSI\DISK&VEN_CONTOSO\4&1A2B&0&000000", ["class"] = "DiskDrive" },
                },
            };
            foreach (var item in refused) Assert.False(host.Safety.Check(item).Allowed, item.Description);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }
}
