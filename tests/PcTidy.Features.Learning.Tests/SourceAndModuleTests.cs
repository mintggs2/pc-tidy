using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Learning.Advisor;
using PcTidy.Features.Learning.Model;
using PcTidy.Features.Learning.Sources;
using PcTidy.Features.Learning.UI;
using PcTidy.Hosting;

namespace PcTidy.Features.Learning.Tests;

public class SourceTests : IDisposable
{
    private const string TestKey = @"Software\PCTidyTest\learning";
    private readonly TestFolder _box = new("sources");

    public void Dispose()
    {
        _box.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(TestKey, throwOnMissingSubKey: false); } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Record(int count, DateTime? lastRun)
    {
        var data = new byte[72];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), count);
        if (lastRun is { } t) BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(60), t.ToFileTimeUtc());
        return data;
    }

    [Fact]
    public void UserAssist_is_read_from_a_fake_tree_and_keeps_only_apps()
    {
        var root = $@"{TestKey}\{Guid.NewGuid():N}";
        var loc = new SandboxKnownLocations(_box.Root, SandboxProfile.Minimal);
        var when = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        using (var count = Registry.CurrentUser.CreateSubKey($@"{root}\{UserAssistSource.UserAssistPath}\{{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}}\Count"))
        {
            count.SetValue(UserAssistSource.Rot13(Path.Combine(loc.ProgramFiles, "Editor", "editor.exe")), Record(7, when), RegistryValueKind.Binary);
            count.SetValue(UserAssistSource.Rot13("{6D809377-6AF0-444B-8957-A3773F02200E}\\Paint\\paint.exe"), Record(2, null), RegistryValueKind.Binary);
            count.SetValue(UserAssistSource.Rot13(Path.Combine(loc.WindowsDir, "System32", "notepad.exe")), Record(9, when), RegistryValueKind.Binary);
            count.SetValue(UserAssistSource.Rot13(Path.Combine(loc.Downloads, "SomeSetup.exe")), Record(1, when), RegistryValueKind.Binary);
            count.SetValue(UserAssistSource.Rot13("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"), Record(3, when), RegistryValueKind.Binary);
            count.SetValue("UEME_CTLSESSION", new byte[16], RegistryValueKind.Binary);
        }
        var usage = new UserAssistSource(new RedirectedRegistryKeyReader(root), loc).Read(CancellationToken.None);
        Assert.Equal(3, usage.Count);
        var editor = Assert.Single(usage, u => u.Name == "editor");
        Assert.Equal((7, when), (editor.RunCount, editor.LastRunUtc));
        var paint = Assert.Single(usage, u => u.Name == "paint");
        Assert.Equal(Path.Combine(loc.ProgramFiles, "Paint", "paint.exe"), paint.Key);
        Assert.Null(paint.LastRunUtc);
        Assert.Contains(usage, u => u.Name == "ZuneMusic");
    }

    [Fact]
    public void No_UserAssist_key_means_nothing()
        => Assert.Empty(new UserAssistSource(new RedirectedRegistryKeyReader($@"{TestKey}\missing"), new SandboxKnownLocations(_box.Root)).Read(CancellationToken.None));

    [Fact]
    public void Steam_last_played_is_read_from_manifests()
    {
        var loc = new SandboxKnownLocations(_box.Root, SandboxProfile.Default);
        var apps = Path.Combine(loc.SteamLibraries[0], "steamapps");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, "appmanifest_10.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"10\"\n\t\"name\"\t\t\"Old Game\"\n\t\"LastPlayed\"\t\t\"1700000000\"\n}\n");
        File.WriteAllText(Path.Combine(apps, "appmanifest_20.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"20\"\n\t\"name\"\t\t\"Unplayed\"\n\t\"LastPlayed\"\t\t\"0\"\n}\n");
        File.WriteAllText(Path.Combine(apps, "appmanifest_30.acf"), "broken");
        var games = new SteamPlayedSource(loc).Read(CancellationToken.None);
        Assert.Equal(2, games.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime, games.Single(g => g.Key == "steam:10").LastRunUtc);
        Assert.Null(games.Single(g => g.Key == "steam:20").LastRunUtc); // 0 = unknown, never "never played"

        Assert.Empty(new SteamPlayedSource(new SandboxKnownLocations(_box.Root, SandboxProfile.Minimal)).Read(CancellationToken.None));
    }

    /// <summary>A minimal .lnk: header + LinkInfo with a local base path (ANSI, and optionally Unicode).</summary>
    private static byte[] Link(string target, bool unicode)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x4C); w.Write(new byte[16]); w.Write(0x2u); // header size, CLSID, flags = HasLinkInfo
        w.Write(new byte[0x4C - 24]);
        var ansi = Encoding.Latin1.GetBytes(target + "\0");
        var wide = Encoding.Unicode.GetBytes(target + "\0");
        int header = unicode ? 0x24 : 0x1C;
        int baseOffset = header, suffixOffset = baseOffset + ansi.Length;
        int wideOffset = suffixOffset + 1, wideSuffix = wideOffset + wide.Length;
        int size = unicode ? wideSuffix + 2 : suffixOffset + 1;
        w.Write(size); w.Write(header); w.Write(0x1u); w.Write(0); w.Write(baseOffset); w.Write(0); w.Write(suffixOffset);
        if (unicode) { w.Write(wideOffset); w.Write(wideSuffix); }
        w.Write(ansi); w.Write((byte)0);
        if (unicode) { w.Write(wide); w.Write((short)0); }
        return ms.ToArray();
    }

    [Fact]
    public void Recent_shortcuts_are_parsed_to_their_targets()
    {
        Assert.Equal(@"D:\Projects\plan.txt", WindowsRecentFolders.TargetOf(Link(@"D:\Projects\plan.txt", unicode: false)));
        Assert.Equal(@"D:\Café\Ünïcode", WindowsRecentFolders.TargetOf(Link(@"D:\Café\Ünïcode", unicode: true)));
        Assert.Null(WindowsRecentFolders.TargetOf(new byte[10]));
        Assert.Null(WindowsRecentFolders.TargetOf(Link("relative\\x", unicode: false)));
    }

    [Fact]
    public void Recent_list_gives_folder_visits()
    {
        var loc = new SandboxKnownLocations(_box.Root, SandboxProfile.Minimal);
        var recent = Path.Combine(loc.RoamingAppData, "Microsoft", "Windows", "Recent");
        Directory.CreateDirectory(recent);
        var project = Path.Combine(_box.Root, "D", "Projects");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "plan.txt"), "x");
        File.WriteAllBytes(Path.Combine(recent, "plan.txt.lnk"), Link(Path.Combine(project, "plan.txt"), unicode: true));
        File.WriteAllBytes(Path.Combine(recent, "gone.lnk"), Link(Path.Combine(_box.Root, "nowhere", "x.txt"), unicode: false));
        var visits = new WindowsRecentFolders(loc).Read(null, CancellationToken.None);
        var v = Assert.Single(visits);
        Assert.Equal(project, v.Folder);
        Assert.Equal(VisitSource.WindowsRecent, v.Source);
        Assert.Empty(new WindowsRecentFolders(loc).Read(DateTime.UtcNow.AddMinutes(1), CancellationToken.None));
    }
}

public class ModuleTests : IDisposable
{
    private readonly TestFolder _box = new("module");

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Module_describes_the_section()
    {
        var m = new LearningModule();
        Assert.Equal(("learned", "Learned", NavGroup.You, "Lightbulb24", Release.V3), (m.Id, m.Title, m.Group, m.Icon, m.Release));
        Assert.Empty(m.Scanners);
        Assert.False(string.IsNullOrWhiteSpace(m.Summary));
    }

    [Fact]
    public void Module_uses_the_registered_advisor_or_makes_its_own()
    {
        using var host = PcTidyHost.CreateSandbox(_box.Root, SandboxProfile.Minimal);
        var module = new LearningModule();
        var own = module.AdvisorFor(host);
        Assert.Same(own, module.AdvisorFor(host));
        Assert.Equal(Path.Combine(host.Settings.DataRoot, "learning", "learned.db"), own.Store.DatabasePath);

        var registered = LearningAdvisor.Create(host);
        host.Add<ILearningAdvisor>(registered);
        Assert.Same(registered, module.AdvisorFor(host));
    }

    [Fact]
    public void Page_builds_and_lists_cards_by_section()
    {
        using var host = PcTidyHost.CreateSandbox(_box.Root, SandboxProfile.Minimal);
        var advisor = LearningAdvisor.Create(host);
        for (int i = 0; i < 3; i++) advisor.RecordDecision("cleanup", "dumps", LearningDecision.Reject, groupTitle: "Crash reports");
        advisor.RecordFolderVisit(((SandboxKnownLocations)host.Locations).Videos);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                // A real dispatcher, so the page's awaits come back to this (UI) thread like in the app.
                SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
                var model = new LearnedViewModel(host, advisor) { Dialogs = new NoDialogs() };
                var view = new LearnedView(model);
                var load = model.LoadAsync();
                var frame = new System.Windows.Threading.DispatcherFrame();
                load.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                load.GetAwaiter().GetResult();
                Assert.Equal(new[] { "Things you usually skip" }, model.Rows.OfType<SectionRow>().Select(r => r.Title).Where(t => t.Contains("skip")));
                var card = Assert.Single(model.Rows.OfType<LearnedCardModel>(), c => c.Item.Kind == LearnedKind.Skip);
                Assert.False(card.CanEdit);
                Assert.Equal("Turn off", card.OffText);
                Assert.False(model.IsEmpty);
                Assert.Contains("Learning on this PC since", model.Subtitle);
                Assert.Contains("more day", model.WatchingNote);
                Assert.NotNull(view.Content);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private sealed class NoDialogs : ILearnedDialogs
    {
        public Task<bool> ConfirmForgetAsync() => Task.FromResult(true);
        public Task<(string Name, string Destination)?> EditRuleAsync(string name, string destination) => Task.FromResult<(string, string)?>(null);
        public void Show(string title, string message) { }
    }
}
