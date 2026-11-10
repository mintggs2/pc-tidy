using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Platform.Tests;

/// <summary>Whitelist validation and sandbox behaviour only. These tests never run any Windows tool.</summary>
public class SystemToolCatalogTests
{
    public static IEnumerable<object[]> ValidItems() => new[]
    {
        new object[] { Steps.Tool(ToolIds.DeliveryOptimizationClear) },
        new object[] { Steps.Tool(ToolIds.DismAnalyzeComponentStore) },
        new object[] { Steps.Tool(ToolIds.DismStartComponentCleanup) },
        new object[] { Steps.Tool(ToolIds.CleanmgrHandler, new() { ["handler"] = "Previous Installations" }) },
        new object[] { Steps.Tool(ToolIds.CleanmgrHandler, new() { ["handler"] = "thumbnail cache" }) },
        new object[] { Steps.Tool(ToolIds.CleanmgrHandler, new() { ["handler"] = "Update Cleanup" }) },
        new object[] { Steps.Tool(ToolIds.HibernateType, new() { ["type"] = "off", ["undoType"] = "full" }) },
        new object[] { Steps.Tool(ToolIds.HibernateType, new() { ["type"] = "Reduced" }) },
    };

    [Theory]
    [MemberData(nameof(ValidItems))]
    public void Whitelisted_tools_resolve_to_absolute_system32_executables_only(PlanItem item)
    {
        Assert.True(SystemToolCatalog.TryResolve(item, out var plan, out var problem), problem);
        Assert.NotEmpty(plan.Commands);
        foreach (var c in plan.Commands)
        {
            Assert.True(Path.IsPathFullyQualified(c.ExePath));
            Assert.StartsWith(Environment.SystemDirectory, c.ExePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(c.Arguments, a => a.Contains("ResetBase", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Dism_runs_in_english_and_never_resets_the_base()
    {
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.DismStartComponentCleanup), out var plan, out _));
        var args = plan.Commands.Single().Arguments;
        Assert.Contains("/English", args);
        Assert.Contains("/StartComponentCleanup", args);
        Assert.False(plan.Reversible);
    }

    [Fact]
    public void Cleanmgr_uses_the_fixed_sageset_slot_and_canonical_handler_name()
    {
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.CleanmgrHandler, new() { ["handler"] = " thumbnail CACHE " }), out var plan, out _));
        Assert.Equal("Thumbnail Cache", plan.CleanmgrHandler);
        Assert.Equal(new[] { $"/sagerun:{SystemToolCatalog.CleanmgrSageSet}" }, plan.Commands.Single().Arguments);
    }

    [Fact]
    public void Hibernate_keeps_the_undo_target()
    {
        Assert.True(SystemToolCatalog.TryResolve(Steps.Tool(ToolIds.HibernateType, new() { ["type"] = "off", ["undoType"] = "Full" }), out var plan, out _));
        Assert.Equal("off", plan.HibernateTarget);
        Assert.Equal("full", plan.HibernateUndoTarget);
        Assert.True(plan.Reversible);
        var undoStep = Steps.Of(Steps.Tool(ToolIds.HibernateType, new() { ["type"] = "off", ["undoType"] = "full" }), status: StepStatus.Done);
        Assert.Equal("full", SystemToolHandler.HibernateUndoTarget(undoStep));
        var recorded = undoStep with { UndoData = new() { [SystemToolHandler.PriorHibernateEnabledData] = "1", [SystemToolHandler.PriorHiberFileTypeData] = "1" } };
        Assert.Equal("reduced", SystemToolHandler.HibernateUndoTarget(recorded)); // the real prior value wins
    }

    public static IEnumerable<object[]> RefusedItems() => new[]
    {
        new object[] { Steps.Tool(ToolIds.SfcScan, new() { ["mode"] = "/offbootdir" }) },
        new object[] { Steps.Tool(ToolIds.WingetUpgrade, new() { ["id"] = "x & calc" }) },
        new object[] { Steps.Tool("cmd.exe") },
        new object[] { Steps.Tool("") },
        new object[] { Steps.Tool(ToolIds.DismStartComponentCleanup, new() { ["extra"] = "/ResetBase" }) },
        new object[] { Steps.Tool(ToolIds.CleanmgrHandler, new() { ["handler"] = "Temporary Files" }) },
        new object[] { Steps.Tool(ToolIds.CleanmgrHandler, new() { ["handler"] = "Update Cleanup\" & calc" }) },
        new object[] { Steps.Tool(ToolIds.CleanmgrHandler) },
        new object[] { Steps.Tool(ToolIds.HibernateType, new() { ["type"] = "off /?" }) },
        new object[] { Steps.Tool(ToolIds.HibernateType, new() { ["type"] = "off", ["undoType"] = "maybe" }) },
        new object[] { Steps.Tool(ToolIds.DeliveryOptimizationClear, new() { ["command"] = "Remove-Item C:\\" }) },
        new object[] { Steps.RegSet(@"Software\x", "y", RegistryKind.DWord, "1") },
    };

    [Theory]
    [MemberData(nameof(RefusedItems))]
    public void Anything_off_the_whitelist_is_refused(PlanItem item)
    {
        Assert.False(SystemToolCatalog.TryResolve(item, out _, out var problem));
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void Dism_analysis_reads_the_english_labels()
    {
        const string output = "Deployment Image Servicing and Management tool\r\n\r\nActual Size of Component Store : 8.21 GB\r\n" +
                              "Number of Reclaimable Packages : 2\r\nComponent Store Cleanup Recommended : Yes\r\nThe operation completed successfully.\r\n";
        var facts = DismAnalysis.Parse(output);
        Assert.Equal("8.21 GB", facts[DismAnalysis.ActualSize]);
        Assert.Equal("2", facts[DismAnalysis.Reclaimable]);
        Assert.Equal("Yes", facts[DismAnalysis.CleanupRecommended]);
    }
}

public class SystemToolHandlerSandboxTests
{
    [Theory]
    [MemberData(nameof(SystemToolCatalogTests.ValidItems), MemberType = typeof(SystemToolCatalogTests))]
    public async Task Sandbox_returns_the_command_that_would_run_and_runs_nothing(PlanItem item)
    {
        using var sandbox = new TestSandbox();
        var handler = new SystemToolHandler();

        var result = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(elevated: true), default);

        Assert.Equal(StepStatus.Simulated, result.Status);
        Assert.Contains("Would run:", result.Message);
        Assert.Contains(Environment.SystemDirectory, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(sandbox.Locations.DataRoot, "logs")));
    }

    [Fact]
    public async Task Simulate_mode_and_undo_change_nothing()
    {
        using var sandbox = new TestSandbox();
        var handler = new SystemToolHandler();
        var dism = Steps.Tool(ToolIds.DismStartComponentCleanup);

        var simulated = await handler.SimulateAsync(Steps.Of(dism), sandbox.Context(), default);
        var undo = await handler.UndoAsync(Steps.Of(dism, status: StepStatus.Done), sandbox.Context(), default);
        var hibernateUndo = await handler.UndoAsync(Steps.Of(Steps.Tool(ToolIds.HibernateType, new() { ["type"] = "off", ["undoType"] = "full" }), status: StepStatus.Done), sandbox.Context(elevated: true), default);

        Assert.Equal(StepStatus.Simulated, simulated.Status);
        Assert.Contains("can't be undone", simulated.Message);
        Assert.Equal(StepStatus.Skipped, undo.Status);
        Assert.Equal(StepStatus.Simulated, hibernateUndo.Status);
        Assert.Contains("full", hibernateUndo.Message);
    }

    [Fact]
    public async Task Refused_tool_is_skipped_even_outside_simulation()
    {
        using var sandbox = new TestSandbox();
        var result = await new SystemToolHandler().ExecuteAsync(Steps.Of(Steps.Tool("format.exe")), sandbox.Context(elevated: true), default);
        Assert.Equal(StepStatus.Skipped, result.Status);
    }
}

public class RecycleBinTests
{
    [Fact]
    public void Size_query_is_read_only_and_works()
    {
        var info = RecycleBin.Query();
        Assert.NotNull(info);
        Assert.True(info!.Bytes >= 0 && info.Items >= 0);
    }

    [Fact]
    public async Task Sandbox_never_empties_the_real_bin_and_undo_says_it_cannot()
    {
        using var sandbox = new TestSandbox();
        var handler = new RecycleBinHandler();
        var item = new PlanItem { Kind = ActionKind.EmptyRecycleBin, FeatureId = "test", Description = "test", Reversible = false };

        var result = await handler.ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        var undo = await handler.UndoAsync(Steps.Of(item, status: StepStatus.Done), sandbox.Context(), default);

        Assert.Equal(StepStatus.Simulated, result.Status);
        Assert.Contains("Would empty the Recycle Bin", result.Message);
        Assert.Equal(StepStatus.Skipped, undo.Status);
        Assert.False(await handler.WasCompletedAsync(Steps.Of(item), sandbox.Context(), default));
    }
}

public class OpenExternalTests
{
    [Theory]
    [InlineData("ms-settings:startupapps", ExternalTargetKind.SettingsPage)]
    [InlineData("ms-settings:storagesense", ExternalTargetKind.SettingsPage)]
    [InlineData("windowsdefender:", ExternalTargetKind.DefenderPage)]
    [InlineData("windowsdefender://threat", ExternalTargetKind.DefenderPage)]
    [InlineData("steam://open/downloads", ExternalTargetKind.SteamScreen)]
    [InlineData("https://learn.microsoft.com/windows/", ExternalTargetKind.WebPage)]
    public void Allowed_targets(string target, ExternalTargetKind kind)
    {
        Assert.True(OpenExternalHandler.TryValidate(target, out var k, out var problem), problem);
        Assert.Equal(kind, k);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:pw@example.com")]
    [InlineData("https://example.com/a b")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("steam://run/440")]
    [InlineData("steam://open/../x")]
    [InlineData("ms-settings:x\" && calc")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("\\\\server\\share")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void Refused_targets(string target)
        => Assert.False(OpenExternalHandler.TryValidate(target, out _, out _));

    [Fact]
    public async Task Existing_folder_is_allowed_but_only_simulated_in_the_sandbox()
    {
        using var sandbox = new TestSandbox();
        Assert.True(OpenExternalHandler.TryValidate(sandbox.Root, out var kind, out _));
        Assert.Equal(ExternalTargetKind.Folder, kind);
        Assert.False(OpenExternalHandler.TryValidate(Path.Combine(sandbox.Root, "missing"), out _, out _));

        var item = new PlanItem { Kind = ActionKind.OpenExternal, FeatureId = "test", Description = "test", Args = new() { ["target"] = sandbox.Root } };
        var result = await new OpenExternalHandler().ExecuteAsync(Steps.Of(item), sandbox.Context(), default);
        Assert.Equal(StepStatus.Simulated, result.Status);
    }
}
