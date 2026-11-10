using System.IO.Compression;
using Microsoft.Data.Sqlite;
using PcTidy.App.Portability;
using PcTidy.App.Shell;
using PcTidy.Core;
using Xunit;

namespace PcTidy.UI.Tests.Shell;

/// <summary>Export / import: round trip, the preview of what changes, and the safety flags (all in testbed\shell\).</summary>
public class PortabilityTests
{
    private sealed class FileStore : ISettingsStore
    {
        public void Save(AppSettings settings) => settings.Save();
        public void SetDataFolder(AppSettings settings, string newRoot) => throw new NotSupportedException();
    }

    private static AppSettings Settings(string dataRoot) => new()
    {
        DataRoot = dataRoot,
        QuarantineDays = 14,
        ExtraProtectedFolders = { Path.Combine(dataRoot, "Projects") },
        ScanExclusions = { Path.Combine(dataRoot, "VMs") },
    };

    private static void MakeLearningDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE meta(k TEXT, v TEXT); INSERT INTO meta VALUES('started','2026-11-01');";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Export_then_read_gives_back_every_setting_rule_and_the_learned_data()
    {
        using var folder = new ShellTestFolder("export");
        var data = folder.Folder("Data");
        var s = Settings(data);
        s.SetFeature("organize", new { Rules = new[] { new { Name = "Invoices", Pattern = "*.pdf" } } });
        MakeLearningDb(DataPaths.LearnedDb(data));
        var store = new SettingsPackageStore(data);

        var path = store.Export(s, new[] { @"C:\", @"D:\" }, "1.0.0", includeLearning: true);
        Assert.StartsWith(Path.Combine(data, "exports"), path);
        Assert.EndsWith(".pctidy", path);
        Assert.Empty(Directory.GetFiles(store.ExportsFolder, "*.db")); // the learning snapshot was cleaned up

        var back = SettingsPackage.Read(path);
        Assert.Equal(14, back.Settings.QuarantineDays);
        Assert.Equal(s.ExtraProtectedFolders, back.Settings.ExtraProtectedFolders);
        Assert.True(back.Settings.Features.ContainsKey("organize"));
        Assert.True(back.Manifest.HasLearning);
        Assert.NotNull(back.LearningDb);
        Assert.Equal(new[] { @"C:\", @"D:\" }, back.Manifest.Drives);
    }

    [Fact]
    public void The_preview_lists_differences_and_starts_risky_ones_unticked()
    {
        using var folder = new ShellTestFolder("preview");
        var data = folder.Folder("Data");
        var drives = new[] { folder.Root + "\\" };
        var mine = Settings(data);
        var theirs = Settings(data);
        theirs.QuarantineDays = 3;                                                    // shorter: less protection
        theirs.Theme = "Dark";                                                        // harmless
        theirs.ExtraProtectedFolders.Clear();                                         // removing protection
        theirs.AllowedAskFirstFolders.Add(Path.Combine(data, "OneDrive", "Pictures"));// allowing OneDrive
        theirs.ScanExclusions.Add(@"Q:\Somewhere\Else");                              // not on this PC
        var shell = ShellPrefs.Get(theirs) with { WhatIsMenu = true, QuietFrom = "21:00" };
        ShellPrefs.Set(theirs, shell);
        var package = new LoadedPackage(new PackageManifest(), theirs, null);

        var changes = ImportPreview.Compare(mine, package, drives, learningExistsHere: false);

        var days = changes.Single(c => c.Name.StartsWith("Days to keep"));
        Assert.True(days.Loosens);
        Assert.False(days.Include);
        Assert.True(changes.Single(c => c.Name == "Theme").Include);
        var removed = changes.Single(c => c.Name == "Extra protected folder removed");
        Assert.True(removed.Loosens);
        Assert.False(removed.Include);
        var allowed = changes.Single(c => c.Name.StartsWith("OneDrive folder"));
        Assert.True(allowed.Loosens);
        Assert.False(allowed.Include);
        Assert.Equal("Less protection", allowed.Flag);
        var off = changes.Single(c => c.After == @"Q:\Somewhere\Else");
        Assert.Equal("Not on this PC", off.Flag);
        Assert.False(off.Include);
        // The right-click menu is THIS PC's state: listed, never applied. Quiet hours (same blob) are offered.
        Assert.True(changes.Single(c => c.Name == "Right-click menu entries").InfoOnly);
        // Every changed setting of a section is its own row (EXP-1), named after the setting.
        var shellRow = changes.Single(c => c.Area == "Alerts, quiet hours and updates" && c.Name == "Quiet from");
        Assert.True(shellRow.Include);
        Assert.Equal("21:00", shellRow.After);

        // Apply what's ticked: harmless rows only, plus the user's own choice.
        allowed.Include = true;
        ImportPreview.Apply(mine, changes);
        Assert.Equal("Dark", mine.Theme);
        Assert.Equal(14, mine.QuarantineDays);
        Assert.Single(mine.ExtraProtectedFolders);
        Assert.Single(mine.AllowedAskFirstFolders);
        Assert.DoesNotContain(@"Q:\Somewhere\Else", mine.ScanExclusions);
        var after = ShellPrefs.Get(mine);
        Assert.False(after.WhatIsMenu);
        Assert.Equal("21:00", after.QuietFrom);
    }

    [Fact]
    public void Import_backs_up_the_current_settings_first_and_saves_once()
    {
        using var folder = new ShellTestFolder("import");
        var data = folder.Folder("Data");
        var mine = Settings(data);
        mine.Save();
        var theirs = Settings(data);
        theirs.Theme = "Light";
        var package = new LoadedPackage(new PackageManifest(), theirs, null);
        var changes = ImportPreview.Compare(mine, package, new[] { folder.Root + "\\" }, false);

        var result = new SettingsPackageStore(data).Import(mine, new FileStore(), changes, package);

        Assert.Null(result.Problem);
        Assert.Equal(1, result.Applied);
        Assert.NotNull(result.BackupPath);
        Assert.Matches(@"settings-\d{8}-\d{6}\.json$", result.BackupPath!);
        Assert.StartsWith(DataPaths.Backups(data), result.BackupPath);
        Assert.Equal("System", System.Text.Json.JsonDocument.Parse(File.ReadAllText(result.BackupPath!)).RootElement.GetProperty("Theme").GetString());
        Assert.Equal("Light", AppSettings.Load(data).Theme);
    }

    [Fact]
    public void Imported_learned_data_replaces_this_pcs_only_when_ticked_and_keeps_a_copy()
    {
        using var folder = new ShellTestFolder("learn");
        var data = folder.Folder("Data");
        var mine = Settings(data);
        var store = new SettingsPackageStore(data);
        MakeLearningDb(store.LearningFile);
        var before = File.ReadAllBytes(store.LearningFile);
        var other = folder.File("other.db", "");
        File.Delete(other);
        MakeLearningDb(other);
        var package = new LoadedPackage(new PackageManifest { HasLearning = true }, Settings(data), File.ReadAllBytes(other));

        var changes = ImportPreview.Compare(mine, package, new[] { folder.Root + "\\" }, learningExistsHere: true);
        var row = changes.Single(c => c.IsLearning);
        Assert.False(row.Include); // this PC already learned things: replacing them is the user's call
        row.Include = true;
        var result = store.Import(mine, new FileStore(), changes, package);

        Assert.True(result.LearningReplaced, result.Problem);
        Assert.Single(Directory.GetFiles(DataPaths.Backups(data), "learned-*.db"));
        Assert.Equal(before, File.ReadAllBytes(Directory.GetFiles(DataPaths.Backups(data), "learned-*.db")[0]));
    }

    [Theory]
    [InlineData("extra.txt")]
    [InlineData("../evil.json")]
    public void A_package_with_anything_unexpected_inside_is_refused(string extra)
    {
        using var folder = new ShellTestFolder("tamper");
        var path = Path.Combine(folder.Root, "bad.pctidy");
        using (var fs = File.Create(path))
        {
            SettingsPackage.Write(fs, new AppSettings { DataRoot = folder.Root }, new[] { @"C:\" }, "1.0.0", null, DateTime.UtcNow);
        }
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) zip.CreateEntry(extra);
        var ex = Assert.Throws<PackageException>(() => SettingsPackage.Read(path));
        Assert.Contains("never has", ex.Message);
    }

    [Fact]
    public void A_file_that_isnt_a_package_gets_a_friendly_reason()
    {
        using var folder = new ShellTestFolder("notzip");
        var path = folder.File("x.pctidy", "hello");
        Assert.Throws<PackageException>(() => SettingsPackage.Read(path));
    }

    [Theory]
    [InlineData(@"C:\Users\x", true)]
    [InlineData(@"Q:\Users\x", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"relative", false)]
    public void Paths_are_on_this_pc_only_on_its_fixed_drives(string path, bool expected)
        => Assert.Equal(expected, ImportPreview.OnThisPc(path, new[] { @"C:\", @"D:\" }));
}
