using PcTidy.Core;
using PcTidy.Features.Apps.Inventory;
using Xunit;

namespace PcTidy.Features.Apps.Tests;

public sealed class UserAssistTests : IDisposable
{
    private const string ExeGuid = "{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}";
    private const string LnkGuid = "{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}";

    private readonly Sandbox _box = new(SandboxProfile.Minimal);
    private readonly FakeRegistry _reg = new();

    public void Dispose()
    {
        _reg.Dispose();
        _box.Dispose();
    }

    [Theory]
    [InlineData("Hello, World! 123", "Uryyb, Jbeyq! 123")]
    [InlineData("{6D809377-6AF0-444B-8957-A3773F02200E}\\App.exe", "{6Q809377-6NS0-444O-8957-N3773S02200R}\\Ncc.rkr")]
    public void Rot13_round_trips_and_leaves_non_letters_alone(string plain, string encoded)
    {
        Assert.Equal(encoded, UserAssistReader.Rot13(plain));
        Assert.Equal(plain, UserAssistReader.Rot13(encoded));
    }

    [Fact]
    public void Parses_run_count_at_offset_4_and_last_run_at_offset_60()
    {
        var when = new DateTime(2025, 3, 14, 9, 26, 53, DateTimeKind.Utc);
        var parsed = UserAssistReader.Parse(UserAssistData.Record(7, when));
        Assert.NotNull(parsed);
        Assert.Equal(7, parsed!.Value.RunCount);
        Assert.Equal(when, parsed.Value.LastRunUtc);
    }

    [Fact]
    public void Zero_or_impossible_times_are_unknown_and_short_records_are_ignored()
    {
        Assert.Null(UserAssistReader.Parse(UserAssistData.Record(3, null))!.Value.LastRunUtc);
        var bogus = UserAssistData.Record(1, null);
        BitConverter.GetBytes(new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc()).CopyTo(bogus, 60);
        Assert.Null(UserAssistReader.Parse(bogus)!.Value.LastRunUtc);
        var future = UserAssistData.Record(1, null);
        BitConverter.GetBytes(DateTime.UtcNow.AddYears(5).ToFileTimeUtc()).CopyTo(future, 60);
        Assert.Null(UserAssistReader.Parse(future)!.Value.LastRunUtc);
        Assert.Null(UserAssistReader.Parse(new byte[16]));
        var negative = UserAssistData.Record(-5, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(0, UserAssistReader.Parse(negative)!.Value.RunCount);
    }

    [Fact]
    public void Launches_are_matched_to_desktop_apps_by_folder_and_to_store_apps_by_family()
    {
        var editor = _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "Contoso", "Editor"), "editor.exe");
        _reg.UninstallEntry("HKLM64", "ContosoEditor", ("DisplayName", "Contoso Editor"), ("InstallLocation", editor),
            ("UninstallString", $"\"{editor}\\editor.exe\" /uninstall"));
        _reg.UninstallEntry("HKLM64", "NeverRun", ("DisplayName", "Never Run"),
            ("InstallLocation", _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "NeverRun"), "n.exe")));
        _reg.UninstallEntry("HKLM64", "ByShortcut", ("DisplayName", "Shortcut App 3.2 (64-bit)"), ("UninstallString", "MsiExec.exe /X{DDDDDDDD-2222-3333-4444-555555555555}"));
        // No InstallLocation: the folder its uninstaller sits in counts as the app's folder.
        var noLoc = _box.Folder(Path.Combine(_box.Locations.ProgramFiles, "NoLoc"), "uninstall.exe", "noloc.exe");
        _reg.UninstallEntry("HKLM64", "NoLoc", ("DisplayName", "No Location"), ("UninstallString", $"\"{noLoc}\\uninstall.exe\""));
        var noLocRun = new DateTime(2025, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        var editorRun = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var storeRun = new DateTime(2025, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var lnkRun = new DateTime(2025, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        // Windows writes known-folder GUIDs at the start of paths; ProgramFilesX64 here.
        _reg.Key($@"HKCU\{UserAssistReader.UserAssistPath}\{ExeGuid}\Count",
            (UserAssistReader.Rot13(@"{6D809377-6AF0-444B-8957-A3773F02200E}\Contoso\Editor\editor.exe"), UserAssistData.Record(12, editorRun)),
            (UserAssistReader.Rot13("Contoso.PhotoEditor_8wekyb3d8bbwe!App"), UserAssistData.Record(3, storeRun)),
            (UserAssistReader.Rot13(@"{6D809377-6AF0-444B-8957-A3773F02200E}\NoLoc\noloc.exe"), UserAssistData.Record(1, noLocRun)),
            (UserAssistReader.Rot13("UEME_CTLSESSION"), new byte[1612]));
        _reg.Key($@"HKCU\{UserAssistReader.UserAssistPath}\{LnkGuid}\Count",
            (UserAssistReader.Rot13(@"{0139D44E-6AFE-49F2-8690-3DAFCAE6FFB8}\Shortcut App.lnk"), UserAssistData.Record(2, lnkRun)));

        var apps = new AppInventory(new AppInventorySources
        {
            Locations = _box.Locations,
            Registry = _reg.Reader,
            Store = new FakeStore().Add("Contoso.PhotoEditor", "Photo Editor"),
        }).Load(CancellationToken.None);

        var e = apps.Single(a => a.Name == "Contoso Editor");
        Assert.Equal(editorRun, e.LastUsedUtc);
        Assert.Equal(12, e.RunCount);
        Assert.Equal(LastUsedSource.UserAssist, e.LastUsedSource);
        Assert.Equal(storeRun, apps.Single(a => a.Name == "Photo Editor").LastUsedUtc);
        Assert.Equal(lnkRun, apps.Single(a => a.Name == "Shortcut App 3.2 (64-bit)").LastUsedUtc);
        Assert.Equal(noLocRun, apps.Single(a => a.Name == "No Location").LastUsedUtc);
        // No record: "unknown", never a guess.
        var never = apps.Single(a => a.Name == "Never Run");
        Assert.Null(never.LastUsedUtc);
        Assert.Equal("unknown", AppFacts.LastUsedText(never, DateTime.UtcNow));
    }

    [Fact]
    public void Known_folder_guids_resolve_to_this_pcs_folders()
    {
        var reader = new UserAssistReader(_reg.Reader, _box.Locations);
        Assert.Equal(Path.Combine(_box.Locations.ProgramFilesX86, "A", "b.exe"),
            reader.ResolveKnownFolder(@"{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}\A\b.exe"));
        Assert.Equal(@"{00000000-0000-0000-0000-000000000000}\x.exe", reader.ResolveKnownFolder(@"{00000000-0000-0000-0000-000000000000}\x.exe"));
    }
}
