using PcTidy.Features.Drivers.Analysis;

namespace PcTidy.Features.Drivers.Tests;

public class BackupFolderTests
{
    [Fact]
    public void Only_dated_folders_with_driver_packages_are_listed_newest_first()
    {
        using var tb = new TestbedFolder();
        var root = BackupFolders.Root(tb.Root);
        void Make(string name, bool withInf)
        {
            var pkg = Path.Combine(root, name, "pkg.inf_amd64_fixture");
            Directory.CreateDirectory(pkg);
            File.WriteAllText(Path.Combine(pkg, withInf ? "pkg.inf" : "readme.txt"), "fixture");
        }
        Make("2026-09-30", true);
        Make("2026-11-10", true);
        Make("2026-11-10-2", true);
        Make("2026-10-01", false);   // nothing to restore
        Make("not-a-date", true);    // not made by PC Tidy

        var list = BackupFolders.List(root);

        Assert.Equal(new[] { "2026-11-10-2", "2026-11-10", "2026-09-30" }, list.Select(b => Path.GetFileName(b.Folder)));
        Assert.All(list, b => Assert.Equal(1, b.PackageCount));
        Assert.Equal(new DateTime(2026, 11, 10), list[0].CreatedLocal);
    }

    [Fact]
    public void A_missing_backups_folder_lists_nothing()
        => Assert.Empty(BackupFolders.List(Path.Combine(Path.GetTempPath(), "pctidy-no-such-folder-" + Guid.NewGuid().ToString("N"))));

    [Fact]
    public void Backup_folder_is_dated_under_the_data_folder()
        => Assert.Equal(Path.Combine(@"C:\Data", "backups", "drivers", "2026-11-10"), BackupFolders.ForDate(@"C:\Data", new DateTime(2026, 11, 10, 23, 59, 0)));
}
