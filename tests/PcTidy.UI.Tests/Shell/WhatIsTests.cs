using PcTidy.App.WhatIs;
using PcTidy.Core;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.UI.Tests.Shell;

/// <summary>A small practice PC (real SafetyGuard over SandboxKnownLocations) shared by the classification table.</summary>
public sealed class WhatIsPc : IDisposable
{
    public WhatIsPc()
    {
        Folder = new ShellTestFolder("whatis");
        Host = PcTidyHost.CreateSandbox(Folder.Root, SandboxProfile.Default);
        var k = Host.Locations;
        foreach (var f in Files(k)) Folder.File(Path.GetRelativePath(Folder.Root, f));
        Directory.CreateDirectory(k.Downloads);
        Directory.CreateDirectory(k.FixedDrives[1]);
        Classifier = new WhatIsClassifier(k, Host.Safety, FixedInstalledApps.FromLocations(k), Host.Settings.DataRoot);
    }

    public ShellTestFolder Folder { get; }
    public PcTidyHost Host { get; }
    public WhatIsClassifier Classifier { get; }
    public IKnownLocations K => Host.Locations;

    public static IEnumerable<string> Files(IKnownLocations k) => new[]
    {
        Path.Combine(k.UserTemp, "setup-1234.tmp"),
        Path.Combine(k.LocalAppData, "Google", "Chrome", "User Data", "Default", "Cache", "Cache_Data", "data_1"),
        Path.Combine(k.LocalAppData, "Google", "Chrome", "User Data", "Default", "Login Data"),
        Path.Combine(k.WindowsDir, "System32", "kernel32.dll"),
        Path.Combine(k.ProgramFiles, "FakeApp", "app.exe"),
        Path.Combine(k.SteamRoot!, "steamapps", "common", "Space Game", "game.exe"),
        Path.Combine(k.CloudRoots[0], "Documents", "notes.txt"),
        Path.Combine(k.Downloads, "Tool-Setup.msi"),
        Path.Combine(k.Downloads, "holiday.jpg"),
        Path.Combine(DataPaths.Quarantine(k.DataRoot), "run-1", "old.tmp"),
        Path.Combine(k.DataRoot, "settings.json"),
        Path.Combine(k.RoamingAppData, "SomeVendor", "config.ini"),
    };

    public void Dispose()
    {
        Host.Dispose();
        Folder.Dispose();
    }
}

/// <summary>"What is this?": what PC Tidy says about each kind of path, and that it never says "safe" for the wrong thing.</summary>
public class WhatIsTests : IClassFixture<WhatIsPc>
{
    private readonly WhatIsPc _pc;
    public WhatIsTests(WhatIsPc pc) => _pc = pc;

    private string P(Func<IKnownLocations, string> f) => f(_pc.K);

    public static IEnumerable<object[]> Table => new[]
    {
        Row(k => Path.Combine(k.UserTemp, "setup-1234.tmp"), WhatIsKind.Temp, WhatIsVerdict.SafeToClean, "cleanup"),
        Row(k => Path.Combine(k.LocalAppData, "Google", "Chrome", "User Data", "Default", "Cache", "Cache_Data", "data_1"), WhatIsKind.Cache, WhatIsVerdict.SafeToClean, "cleanup"),
        Row(k => Path.Combine(k.LocalAppData, "Google", "Chrome", "User Data", "Default", "Login Data"), WhatIsKind.AppData, WhatIsVerdict.DontDelete, null),
        Row(k => Path.Combine(k.WindowsDir, "System32", "kernel32.dll"), WhatIsKind.Windows, WhatIsVerdict.DontDelete, "cleanup"),
        Row(k => Path.Combine(k.ProgramFiles, "FakeApp", "app.exe"), WhatIsKind.App, WhatIsVerdict.DontDelete, "apps"),
        Row(k => Path.Combine(k.SteamRoot!, "steamapps", "common", "Space Game", "game.exe"), WhatIsKind.Game, WhatIsVerdict.DontDelete, "games"),
        Row(k => Path.Combine(k.CloudRoots[0], "Documents", "notes.txt"), WhatIsKind.Cloud, WhatIsVerdict.ThinkFirst, "spacemap"),
        Row(k => Path.Combine(k.Downloads, "Tool-Setup.msi"), WhatIsKind.Personal, WhatIsVerdict.ThinkFirst, "cleanup"),
        Row(k => Path.Combine(k.Downloads, "holiday.jpg"), WhatIsKind.Personal, WhatIsVerdict.ThinkFirst, "spacemap"),
        Row(k => Path.Combine(DataPaths.Quarantine(k.DataRoot), "run-1", "old.tmp"), WhatIsKind.PcTidyQuarantine, WhatIsVerdict.DontDelete, "quarantine"),
        Row(k => Path.Combine(k.DataRoot, "settings.json"), WhatIsKind.PcTidyData, WhatIsVerdict.DontDelete, "settings"),
        Row(k => k.Downloads, WhatIsKind.UserFolder, WhatIsVerdict.DontDelete, "organize"),
        Row(k => k.FixedDrives[1], WhatIsKind.DriveRoot, WhatIsVerdict.DontDelete, "spacemap"),
        Row(k => Path.Combine(k.Downloads, "gone-already.zip"), WhatIsKind.Missing, WhatIsVerdict.NotThere, null),
    };

    private static object[] Row(Func<IKnownLocations, string> path, WhatIsKind kind, WhatIsVerdict verdict, string? section)
        => new object[] { new PathOf(path), kind, verdict, section ?? "" };

    /// <summary>Wraps the path builder so xUnit can show the row (paths depend on the fixture's folder).</summary>
    public sealed record PathOf(Func<IKnownLocations, string> Build)
    {
        public override string ToString() => "path";
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Each_kind_of_path_is_explained_with_the_right_answer(PathOf path, WhatIsKind kind, WhatIsVerdict verdict, string section)
    {
        var report = _pc.Classifier.Classify(P(path.Build));
        Assert.Equal(kind, report.Kind);
        Assert.Equal(verdict, report.Verdict);
        Assert.Equal(section, report.Section ?? "");
        Assert.False(string.IsNullOrWhiteSpace(report.Title));
        Assert.False(string.IsNullOrWhiteSpace(report.Explanation));
        Assert.False(string.IsNullOrWhiteSpace(report.VerdictText));
    }

    [Fact]
    public void The_owner_comes_from_the_installed_app_and_the_game_library()
    {
        Assert.Contains("FakeApp", _pc.Classifier.Classify(P(k => Path.Combine(k.ProgramFiles, "FakeApp", "app.exe"))).Owner);
        Assert.Equal("Space Game (Steam)", _pc.Classifier.Classify(P(k => Path.Combine(k.SteamRoot!, "steamapps", "common", "Space Game", "game.exe"))).Owner);
    }

    [Fact]
    public void An_online_only_OneDrive_file_is_never_called_safe_and_is_not_opened()
    {
        var path = P(k => Path.Combine(k.CloudRoots[0], "Documents", "notes.txt"));
        var report = _pc.Classifier.Classify(path, FileAttributes.Archive | FileAttributes.Offline | (FileAttributes)0x00400000);
        Assert.Equal(WhatIsKind.CloudPlaceholder, report.Kind);
        Assert.Equal(WhatIsVerdict.DontDelete, report.Verdict);
        Assert.Contains("no space on this PC", report.Explanation);
    }

    [Fact]
    public void Nothing_inside_Windows_or_an_app_is_ever_safe_to_clean_except_carve_outs()
    {
        // Every file the fixture made: "Safe to clean" only for known caches and temp files.
        foreach (var f in WhatIsPc.Files(_pc.K))
        {
            var r = _pc.Classifier.Classify(f);
            if (r.Verdict == WhatIsVerdict.SafeToClean) Assert.Contains(r.Kind, new[] { WhatIsKind.Cache, WhatIsKind.Temp });
        }
    }

    [Fact]
    public void Paths_from_Explorer_are_repaired_and_checked()
    {
        Assert.Equal(@"C:\", PcTidy.App.AppArgs.ExplorerPath("C:\""));
        Assert.Equal(@"C:\Users", PcTidy.App.AppArgs.ExplorerPath("\"C:\\Users\""));
        Assert.Null(PcTidy.App.AppArgs.ExplorerPath("relative\\path"));
        Assert.Null(PcTidy.App.AppArgs.ExplorerPath(""));
        var args = PcTidy.App.AppArgs.Parse(new[] { "--whatis", @"C:\Windows" });
        Assert.Equal(@"C:\Windows", args.WhatIs);
    }

    [Fact]
    public void Only_known_commands_with_full_paths_pass_the_pipe()
    {
        Assert.NotNull(PcTidy.App.Shell.ShellCommand.Validate(new("whatis", @"C:\Windows")));
        Assert.Null(PcTidy.App.Shell.ShellCommand.Validate(new("whatis", "notepad.exe")));
        Assert.Null(PcTidy.App.Shell.ShellCommand.Validate(new("delete", @"C:\Windows")));
        Assert.Equal("show", PcTidy.App.Shell.ShellCommand.Validate(new("show", "ignored"))!.Verb);
    }

    [Fact]
    public void Right_click_entries_are_per_user_verbs_with_the_platform_handlers_argument_rules()
    {
        var exe = @"C:\Program Files\PC Tidy\PcTidy.exe"; // per-machine install
        var items = ShellVerbs.WhatIs(exe, enable: true);
        Assert.Equal(new[] { "*", "Directory", @"Directory\Background" }, items.Select(i => i.Args!["on"]));
        Assert.All(items, i =>
        {
            Assert.Equal(Core.Model.ActionKind.ShellVerbSetInstalled, i.Kind);
            Assert.Equal("WhatIsThis", i.Args!["verb"]); // the handler adds "PCTidy." itself and refuses a dot
            Assert.Matches("^[A-Za-z0-9_-]{1,64}$", i.Args["verb"]);
            Assert.StartsWith($"\"{exe}\" --whatis \"%", i.Args["command"]);
            Assert.True(i.Enable);
        });
        Assert.EndsWith("\"%V\"", items[2].Args!["command"]); // folder background passes the folder, not %1
        var tidy = ShellVerbs.TidyFolder(exe, enable: false);
        Assert.Equal(2, tidy.Count);
        Assert.All(tidy, i => { Assert.False(i.Enable); Assert.Equal("TidyFolder", i.Args!["verb"]); });
        Assert.Equal(Core.Model.ActionKind.StartWithWindowsSet, ShellVerbs.StartWithWindows(true).Kind);
    }
}
