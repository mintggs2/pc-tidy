using PcTidy.Features.Speed.Common;
using PcTidy.Features.Speed.ContextMenu;

namespace PcTidy.Features.Speed.Tests;

/// <summary>Right-click menu enumeration against a fake class tree at HKCU\Software\PCTidyTest\speed\Classes.</summary>
public sealed class ContextMenuTests : IDisposable
{
    private const string Root = @"Software\PCTidyTest\speed\Classes";
    private const string Blocked = @"Software\PCTidyTest\speed\Blocked";

    private static readonly Guid ArchiverClsid = new("6A1E1B4C-0D3F-4E59-9C1D-2B8F3A5C7E01");
    private static readonly Guid GoneClsid = new("6A1E1B4C-0D3F-4E59-9C1D-2B8F3A5C7E02");
    private static readonly Guid BlockedClsid = new("6A1E1B4C-0D3F-4E59-9C1D-2B8F3A5C7E03");
    private static readonly Guid MicrosoftClsid = new("6A1E1B4C-0D3F-4E59-9C1D-2B8F3A5C7E04");
    private static readonly Guid CoreClsid = new("6A1E1B4C-0D3F-4E59-9C1D-2B8F3A5C7E05");

    private readonly SpeedSandbox _sb = new();
    private readonly string _archiverDll, _editorExe, _msDll, _coreDll;

    public ContextMenuTests()
    {
        _archiverDll = _sb.MakeFile(@"C\Program Files\Archiver\archiver-shell.dll");
        _editorExe = _sb.MakeFile(@"C\Program Files\Editor\editor.exe");
        _msDll = _sb.MakeFile(@"C\Program Files\Microsoft Office\root\office-shell.dll");
        _coreDll = _sb.MakeFile(@"C\Windows\System32\core-shell.dll");

        // *\shellex: a named handler pointing at a CLSID, and a handler key named after the CLSID itself.
        FakeRegistry.SetRaw(Root + @"\*\shellex\ContextMenuHandlers\Archiver", null, ArchiverClsid.ToString("B"));
        FakeRegistry.CreateRaw(Root + @"\Directory\shellex\ContextMenuHandlers\" + ArchiverClsid.ToString("B"));
        FakeRegistry.SetRaw(Root + $@"\CLSID\{ArchiverClsid:B}", null, "Archiver Shell Extension");
        FakeRegistry.SetRaw(Root + $@"\CLSID\{ArchiverClsid:B}\InprocServer32", null, _archiverDll);

        // A handler whose DLL is gone (left behind by an uninstall).
        FakeRegistry.SetRaw(Root + @"\Directory\Background\shellex\ContextMenuHandlers\OldTool", null, GoneClsid.ToString("B"));
        FakeRegistry.SetRaw(Root + $@"\CLSID\{GoneClsid:B}", null, "Old Tool Menu");
        FakeRegistry.SetRaw(Root + $@"\CLSID\{GoneClsid:B}\InprocServer32", null, Path.Combine(_sb.Root, @"C\Program Files\OldTool\gone.dll"));

        // Already blocked.
        FakeRegistry.SetRaw(Root + @"\Folder\shellex\ContextMenuHandlers\Blocked one", null, BlockedClsid.ToString("B"));
        FakeRegistry.SetRaw(Root + $@"\CLSID\{BlockedClsid:B}\InprocServer32", null, _archiverDll);
        FakeRegistry.SetRaw(Blocked, BlockedClsid.ToString("B"), "Blocked by a test");

        // Microsoft app (outside Windows) and Windows core.
        FakeRegistry.SetRaw(Root + @"\AllFilesystemObjects\shellex\ContextMenuHandlers\OfficeShare", null, MicrosoftClsid.ToString("B"));
        FakeRegistry.SetRaw(Root + $@"\CLSID\{MicrosoftClsid:B}\InprocServer32", null, _msDll);
        FakeRegistry.SetRaw(Root + @"\Drive\shellex\ContextMenuHandlers\CoreThing", null, CoreClsid.ToString("B"));
        FakeRegistry.SetRaw(Root + $@"\CLSID\{CoreClsid:B}\InprocServer32", null, _coreDll);

        // Static verbs: one shown, one already disabled with LegacyDisable, one for programs only.
        FakeRegistry.SetRaw(Root + @"\Directory\shell\EditHere", "MUIVerb", "Open with &Editor");
        FakeRegistry.SetRaw(Root + @"\Directory\shell\EditHere\command", null, $"\"{_editorExe}\" \"%V\"");
        FakeRegistry.SetRaw(Root + @"\Directory\Background\shell\EditHere", "MUIVerb", "Open with &Editor");
        FakeRegistry.SetRaw(Root + @"\Directory\Background\shell\EditHere\command", null, $"\"{_editorExe}\" \"%V\"");
        // A modern entry that hands the click to a COM object, and one that names no program at all.
        FakeRegistry.SetRaw(Root + @"\*\shell\ModernVerb", "MUIVerb", "Archive it");
        FakeRegistry.SetRaw(Root + @"\*\shell\ModernVerb\command", "DelegateExecute", ArchiverClsid.ToString("B"));
        FakeRegistry.SetRaw(Root + @"\*\shell\Mystery", "MUIVerb", "Mystery entry");
        FakeRegistry.SetRaw(Root + @"\Directory\Background\shell\Hidden verb", null, "Hidden entry");
        FakeRegistry.SetRaw(Root + @"\Directory\Background\shell\Hidden verb", "LegacyDisable", "");
        FakeRegistry.SetRaw(Root + @"\Directory\Background\shell\Hidden verb\command", null, $"\"{_editorExe}\"");
        FakeRegistry.SetRaw(Root + @"\*\shell\ProgramsOnly", "ProgrammaticAccessOnly", "");
        FakeRegistry.SetRaw(Root + @"\*\shell\ProgramsOnly\command", null, $"\"{_editorExe}\"");
    }

    public void Dispose()
    {
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(Blocked, throwOnMissingSubKey: false);
        FakeRegistry.RemoveEmptyTestRoot();
        _sb.Dispose();
    }

    private ContextMenuScanner Scanner() => new(_ => Env.Fake(
        registry: WindowsRegistryView.Instance,
        files: new FixedFileFacts(new[] { FixedFileFacts.Microsoft(_msDll), FixedFileFacts.Microsoft(_coreDll), FixedFileFacts.Signed(_archiverDll, "Archiver Ltd") }),
        classRoots: new[] { new ClassRoot(RegistryHive.CurrentUser, Root, "you") },
        blocked: new[] { (RegistryHive.CurrentUser, Blocked) }));

    [Fact]
    public void Reader_finds_extensions_and_verbs_with_their_files()
    {
        var env = Env.Fake(registry: WindowsRegistryView.Instance, classRoots: new[] { new ClassRoot(RegistryHive.CurrentUser, Root, "you") }, blocked: new[] { (RegistryHive.CurrentUser, Blocked) });
        var entries = new ContextMenuReader(env, _sb.Locations).Read(CancellationToken.None);

        var archiver = entries.Single(e => e.Clsid == ArchiverClsid);
        Assert.Equal("Archiver Shell Extension", archiver.Name);
        Assert.Equal(_archiverDll, archiver.FilePath);
        Assert.Equal(new[] { "files", "folders" }, archiver.ShowsOn); // one row for one CLSID, wherever it's registered
        Assert.Equal(2, archiver.Keys.Count);
        Assert.False(archiver.IsDisabled);

        Assert.True(entries.Single(e => e.Clsid == BlockedClsid).IsDisabled);
        var edit = entries.Single(e => e.VerbKeys.Any(k => k.EndsWith(@"Directory\shell\EditHere")));
        Assert.Equal("Open with Editor", edit.Name);
        Assert.Equal(_editorExe, edit.FilePath);
        Assert.Equal(2, edit.VerbKeys.Count); // the same entry on folders and the folder background is one row
        Assert.Equal(new[] { "folders", "folder background" }, edit.ShowsOn);
        Assert.Equal(_archiverDll, entries.Single(e => e.Name == "Archive it").FilePath); // DelegateExecute → CLSID → DLL
        Assert.Null(entries.Single(e => e.Name == "Mystery entry").FilePath);
        Assert.True(entries.Single(e => e.VerbKeys.Any(k => k.EndsWith("Hidden verb"))).IsDisabled);
        Assert.DoesNotContain(entries, e => e.VerbKeys.Any(k => k.EndsWith("ProgramsOnly")));
    }

    [Fact]
    public async Task Scanner_groups_entries_flags_missing_files_and_hides_Microsoft_by_default()
    {
        var result = await Scanner().ScanAsync(_sb.Context(hideMicrosoft: true), null, CancellationToken.None);

        var broken = result.Groups.Single(g => g.Id == ContextMenuScanner.BrokenGroup).Items.Single();
        Assert.Equal("Old Tool Menu", broken.Title);
        Assert.Equal("File missing", broken.CurrentState);
        var hide = Assert.Single(broken.Actions);
        Assert.Equal(ActionKind.ShellExtensionSetEnabled, hide.Kind);
        Assert.False(hide.Enable);
        Assert.Equal(GoneClsid, Guid.Parse(hide.Args!["clsid"]));

        var apps = result.Groups.Single(g => g.Id == ContextMenuScanner.AppsGroup).Items;
        Assert.Contains(apps, i => i.Title == "Archiver Shell Extension" && i.OwnerApp == "Archiver Ltd");
        var verb = apps.Single(i => i.Title == "Open with Editor");
        Assert.Equal(2, verb.Actions.Count);
        var verbAction = verb.Actions.Single(a => a.Args!["verbKey"] == Root + @"\Directory\shell\EditHere");
        Assert.Equal("HKCU", verbAction.Args!["hive"]);
        Assert.False(verbAction.Enable);
        Assert.Contains(verb.Actions, a => a.Args!["verbKey"] == Root + @"\Directory\Background\shell\EditHere");
        var mystery = apps.Single(i => i.Title == "Mystery entry");
        Assert.Empty(mystery.Actions);
        Assert.NotNull(mystery.DisabledReason);

        var hidden = result.Groups.Single(g => g.Id == ContextMenuScanner.HiddenGroup).Items;
        Assert.Equal(2, hidden.Count);
        Assert.All(hidden, i => Assert.True(Assert.Single(i.Actions).Enable)); // shown again, never deleted

        Assert.DoesNotContain(result.Groups, g => g.Id == ContextMenuScanner.MicrosoftGroup);
        Assert.Contains(result.Notes, n => n.Contains("Microsoft entries are hidden"));
        Assert.All(result.AllItems, i => Assert.False(i.DefaultSelected));
        Assert.All(result.AllItems.SelectMany(i => i.Actions), a => Assert.True(_sb.Host.Safety.Check(a).Allowed, _sb.Host.Safety.Check(a).Reason));
    }

    [Fact]
    public async Task Microsoft_entries_can_be_shown_but_Windows_core_ones_are_never_offered()
    {
        var result = await Scanner().ScanAsync(_sb.Context(hideMicrosoft: false), null, CancellationToken.None);
        var ms = result.Groups.Single(g => g.Id == ContextMenuScanner.MicrosoftGroup).Items;
        Assert.Equal(2, ms.Count);
        var office = ms.Single(i => i.Detail == _msDll);
        Assert.Single(office.Actions);
        var core = ms.Single(i => i.Detail == _coreDll);
        Assert.Empty(core.Actions);
        Assert.NotNull(core.DisabledReason);
    }
}
