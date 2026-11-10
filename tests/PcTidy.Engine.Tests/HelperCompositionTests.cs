using PcTidy.Core;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.Engine.Tests;

/// <summary>The elevated helper's composition ignores settings that loosen rules and keeps its records out of the user's folder.</summary>
public class HelperCompositionTests
{
    [Fact]
    public void The_helper_drops_loosening_settings_and_keeps_protective_ones()
    {
        var user = new AppSettings
        {
            DataRoot = @"C:\Users\tester\AppData\Local\PC Tidy",
            AllowedAskFirstFolders = new() { @"C:\Users\tester\OneDrive" },
            ExtraProtectedFolders = new() { @"C:\Keep" },
            ScanExclusions = new() { @"C:\Skip" },
        };
        new EngineSettings { MaxRemoveItemsPerRun = int.MaxValue, PerVolumeQuarantine = PerVolumeQuarantine.AllFixedDrives }.SaveTo(user);

        var hardened = PcTidyHost.HardenedSettings(user);

        Assert.Empty(hardened.AllowedAskFirstFolders);
        Assert.Equal(user.ExtraProtectedFolders, hardened.ExtraProtectedFolders);
        Assert.Equal(user.ScanExclusions, hardened.ScanExclusions);
        Assert.Equal(new EngineSettings().MaxRemoveItemsPerRun, EngineSettings.From(hardened).MaxRemoveItemsPerRun);
        Assert.Equal(user.DataRoot, hardened.DataRoot); // still protected by the helper's guard
    }

    [Fact]
    public void Helper_handlers_write_backups_and_logs_into_the_admin_folder()
    {
        using var sb = new TestSandbox();
        var locations = new AdminDataLocations(sb.Locations, @"C:\ProgramData\PC Tidy\Admin");
        Assert.Equal(@"C:\ProgramData\PC Tidy\Admin", locations.DataRoot);
        Assert.Equal(sb.Locations.WindowsTemp, locations.WindowsTemp);
        Assert.Equal(sb.Locations.IsSandbox, locations.IsSandbox);
    }
}
