using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>Guard rules for the v3 action kinds.</summary>
public sealed class V3RulesTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private static PlanItem Item(ActionKind kind, string feature = "schedule", string? src = null, string? dst = null, Dictionary<string, string>? args = null, bool? enable = null)
        => new() { Kind = kind, FeatureId = feature, Description = "t", SourcePath = src, DestinationPath = dst, Args = args, Enable = enable };

    [Theory]
    [InlineData("Weekly scan", "weekly", "--scheduled-scan", true)]
    [InlineData("Monthly checkup", "monthly", "--scheduled-scan --checkup", true)]
    [InlineData("Weekly scan", "weekly", "--elevated pipe 1", false)]          // only report-only switches
    [InlineData("..\\..\\Microsoft\\Evil", "weekly", "--scheduled-scan", false)] // no path tricks in the name
    [InlineData("Weekly scan", "every-minute", "--scheduled-scan", false)]
    public void Own_scheduled_tasks_only_run_report_only_switches(string name, string schedule, string arguments, bool allowed)
    {
        var v = _s.Guard.Check(Item(ActionKind.ScheduledTaskCreate, args: new() { ["name"] = name, ["schedule"] = schedule, ["arguments"] = arguments, ["time"] = "10:00" }));
        Assert.Equal(allowed, v.Allowed);
    }

    [Fact]
    public void LaunchApp_refuses_shells_script_hosts_and_windows_programs()
    {
        var app = _s.File(Path.Combine(_s.K.LocalAppData, "Programs", "Chat", "Chat.exe"));
        Assert.True(_s.Guard.Check(Item(ActionKind.LaunchApp, "boost", args: new() { ["exePath"] = app })).Allowed);
        var shell = _s.File(Path.Combine(_s.K.LocalAppData, "Programs", "x", "powershell.exe"));
        Assert.False(_s.Guard.Check(Item(ActionKind.LaunchApp, "boost", args: new() { ["exePath"] = shell })).Allowed);
        var winTool = _s.File(Path.Combine(_s.K.WindowsDir, "System32", "notepad.exe"));
        Assert.False(_s.Guard.Check(Item(ActionKind.LaunchApp, "boost", args: new() { ["exePath"] = winTool })).Allowed);
        Assert.False(_s.Guard.Check(Item(ActionKind.LaunchApp, "boost", args: new() { ["exePath"] = Path.Combine(_s.K.Downloads, "missing.exe") })).Allowed);
    }

    [Fact]
    public void CopyPath_needs_a_readable_source_and_an_allowed_destination()
    {
        var src = _s.File(Path.Combine(_s.K.Downloads, "report.txt"));
        Assert.True(_s.Guard.Check(Item(ActionKind.CopyPath, "aichats", src, Path.Combine(_s.K.Downloads, "Archive", "report.txt"))).Allowed);
        Assert.False(_s.Guard.Check(Item(ActionKind.CopyPath, "aichats", src, Path.Combine(_s.K.WindowsDir, "System32", "report.txt"))).Allowed);
    }

    [Fact]
    public void Free_up_space_only_inside_a_cloud_folder()
    {
        if (_s.K.CloudRoots.Count == 0) return;
        var inCloud = _s.File(Path.Combine(_s.K.CloudRoots[0], "Videos", "trip.mp4"));
        Assert.True(_s.Guard.Check(Item(ActionKind.OneDriveFreeUpSpace, "insights", inCloud)).Allowed);
        var local = _s.File(Path.Combine(_s.K.Downloads, "trip.mp4"));
        Assert.False(_s.Guard.Check(Item(ActionKind.OneDriveFreeUpSpace, "insights", local)).Allowed);
    }

    [Fact]
    public void Quick_access_pin_needs_an_existing_folder()
    {
        var dir = _s.Dir(Path.Combine(_s.K.Downloads, "Projects"));
        Assert.True(_s.Guard.Check(Item(ActionKind.QuickAccessPin, "search", dir)).Allowed);
        Assert.False(_s.Guard.Check(Item(ActionKind.QuickAccessPin, "search", Path.Combine(_s.K.Downloads, "nope"))).Allowed);
    }

    [Fact]
    public void Start_with_windows_needs_an_explicit_on_or_off()
    {
        Assert.True(_s.Guard.Check(Item(ActionKind.StartWithWindowsSet, "shell", enable: true)).Allowed);
        Assert.False(_s.Guard.Check(Item(ActionKind.StartWithWindowsSet, "shell")).Allowed);
    }
}
