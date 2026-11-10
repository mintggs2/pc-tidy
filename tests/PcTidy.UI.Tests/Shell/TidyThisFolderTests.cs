using PcTidy.App.Shell;
using PcTidy.Core;
using PcTidy.Hosting;
using Xunit;

namespace PcTidy.UI.Tests.Shell;

/// <summary>
/// Explorer's "Tidy this folder" asks the REAL safety guard about the folder's contents, so the user folders and
/// drive roots it exists for are accepted, while folders whose contents are protected are still refused.
/// </summary>
public class TidyThisFolderTests
{
    [Fact]
    public void User_folders_cloud_roots_and_drive_roots_can_be_tidied()
    {
        using var t = new ShellTestFolder("tidyfolder");
        var host = PcTidyHost.CreateSandbox(t.Root, SandboxProfile.Default);
        var k = host.Locations;
        foreach (var folder in new[] { k.Downloads, k.Desktop, k.Documents, k.Pictures })
        {
            Directory.CreateDirectory(folder);
            // The guard does protect the folder itself (it never moves), which is what the old check asked about.
            Assert.Equal(PcTidy.Core.Model.PathProtection.Never, host.Safety.ClassifyPath(folder).Protection);
            Assert.Null(ShellNavigation.ContentsRefusal(host.Safety, folder));
        }
        foreach (var root in k.CloudRoots.Concat(k.FixedDrives))
        {
            Directory.CreateDirectory(root);
            Assert.Null(ShellNavigation.ContentsRefusal(host.Safety, root));
        }
    }

    [Theory]
    [InlineData(false, null, true)]   // protection off for the Windows drive
    [InlineData(true, 1, true)]       // a policy turns System Restore off
    [InlineData(true, null, false)]
    [InlineData(null, null, false)]   // can't tell: no notice
    public void System_Protection_notice_rule(bool? on, int? policy, bool off)
        => Assert.Equal(off, SystemProtectionNotice.IsOff(on, policy));

    [Fact]
    public void System_Protection_notice_never_shows_in_the_sandbox_or_demo()
        => Assert.False(SystemProtectionNotice.ShouldShow(isSandboxOrDemo: true));

    [Fact]
    public void A_folder_whose_contents_are_protected_is_still_refused()
    {
        using var t = new ShellTestFolder("tidyfolder");
        var host = PcTidyHost.CreateSandbox(t.Root, SandboxProfile.Default);
        Directory.CreateDirectory(host.Locations.WindowsDir);
        var refusal = ShellNavigation.ContentsRefusal(host.Safety, host.Locations.WindowsDir);
        Assert.False(string.IsNullOrWhiteSpace(refusal));
    }
}
