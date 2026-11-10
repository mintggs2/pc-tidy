using PcTidy.Core;
using PcTidy.Core.Model;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>Rules on a plain PC (one drive, nothing installed, no OneDrive) and with other sync tools.</summary>
public sealed class MinimalProfileTests : IDisposable
{
    private readonly TestSandbox _s = new(SandboxProfile.Minimal);
    public void Dispose() => _s.Dispose();

    [Fact]
    public void Nothing_installed_means_empty_lists_and_no_errors()
    {
        Assert.Empty(_s.K.CloudRoots);
        Assert.Null(_s.K.SteamRoot);
        Assert.Empty(_s.K.SteamLibraries);
        Assert.Null(_s.K.EpicManifestsDir);
        Assert.Single(_s.K.FixedDrives);
        var games = new GameLibraryInfo(_s.K);
        Assert.Empty(games.SteamApps);
        Assert.Empty(games.EpicApps);
    }

    [Fact]
    public void Core_rules_still_hold()
    {
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(_s.K.WindowsDir, "System32", "a.dll"))).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(Path.Combine(_s.K.ProgramFilesX86, "Steam", "steam.exe"))).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(_s.K.Desktop)).Allowed);
        Assert.True(_s.Guard.Check(_s.Quarantine(_s.File(Path.Combine(_s.K.Downloads, "a.zip")))).Allowed);
        Assert.True(_s.Guard.Check(_s.Quarantine(_s.File(Path.Combine(_s.K.WindowsTemp, "a.tmp")), carveOut: CarveOuts.WindowsTemp)).Allowed);
    }

    [Fact]
    public void Local_desktop_is_a_library_not_a_cloud_folder()
    {
        var f = _s.File(Path.Combine(_s.K.Desktop, "shot.png"));
        Assert.False(_s.Guard.IsInCloudRoot(f));
        Assert.True(_s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Desktop, "Shots", "shot.png"))).Allowed);
        Assert.False(_s.Guard.Check(_s.Quarantine(f)).Allowed); // cleanup never junk-flags the Desktop
    }

    [Fact]
    public void Steam_carve_out_is_harmless_without_steam()
    {
        var v = _s.Guard.Check(_s.Quarantine(Path.Combine(_s.K.ProgramFilesX86, "Steam", "dumps", "a.dmp"), carveOut: CarveOuts.SteamCache));
        Assert.False(v.Allowed);
        Assert.True(_s.Guard.Check(_s.Quarantine(_s.File(Path.Combine(_s.K.LocalAppData, "Steam", "htmlcache", "x")), carveOut: CarveOuts.SteamCache)).Allowed);
    }

    [Fact]
    public void Only_drive_is_the_boundary_of_this_pc()
    {
        Assert.False(_s.Guard.ClassifyPath(Path.Combine(_s.Root, "D", "file.txt")).Allowed);
    }
}

public sealed class CustomProfileTests
{
    [Fact]
    public void Onedrive_without_folder_redirection_keeps_desktop_local()
    {
        using var s = new TestSandbox(new SandboxProfile { OneDriveRedirectsFolders = false, SecondDrive = false, UserName = "someone" });
        var desktop = s.File(Path.Combine(s.K.Desktop, "a.txt"));
        var cloud = s.File(Path.Combine(s.K.CloudRoots[0], "b.txt"));
        Assert.False(s.Guard.IsInCloudRoot(desktop));
        Assert.True(s.Guard.IsInCloudRoot(cloud));
        Assert.Contains("someone", s.K.UserProfile);
        Assert.Single(s.K.SteamLibraries);
    }
}

public sealed class SyncFolderTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    [Fact]
    public void Moving_out_of_a_cloud_folder_is_ask_first_with_its_own_reason()
    {
        var notes = _s.Dir(Path.Combine(_s.K.CloudRoots[0], "Notes"));
        var f = _s.File(Path.Combine(notes, "a.txt"));
        var v = _s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Downloads, "a.txt")));
        Assert.False(v.Allowed);
        Assert.Equal(PathProtection.AskFirst, v.Protection);
        Assert.Contains("deletes the cloud copy", v.Reason);
        Assert.Contains("deletes the cloud copy", _s.Guard.Check(_s.Quarantine(f, "spacemap")).Reason);

        _s.Settings.AllowedAskFirstFolders.Add(notes);
        Assert.True(_s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Downloads, "a.txt"))).Allowed);
    }

    [Fact]
    public void Syncthing_folders_are_cloud_folders_and_their_internals_are_never_touched()
    {
        var synced = _s.Dir(Path.Combine(_s.K.Downloads, "Synced"));
        _s.Dir(Path.Combine(synced, ".stfolder"));
        _s.Dir(Path.Combine(synced, ".stversions"));
        var f = _s.File(Path.Combine(synced, "doc.txt"));

        Assert.True(_s.Guard.IsInCloudRoot(f));
        var v = _s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Downloads, "doc.txt")));
        Assert.False(v.Allowed);
        Assert.Equal(PathProtection.AskFirst, v.Protection);
        Assert.False(_s.Guard.Check(_s.Quarantine(synced)).Allowed); // the synced folder itself stays

        _s.Settings.AllowedAskFirstFolders.Add(synced);
        Assert.True(_s.Guard.Check(_s.Move(f, Path.Combine(synced, "Docs", "doc.txt"))).Allowed);
        foreach (var inner in new[] { ".stfolder", ".stversions", @".stversions\doc~20260101.txt", ".stignore" })
        {
            var iv = _s.Guard.Check(_s.Quarantine(Path.Combine(synced, inner)));
            Assert.False(iv.Allowed, inner);
            Assert.Equal(PathProtection.Never, iv.Protection);
        }
    }

    [Fact]
    public void Dropbox_bookkeeping_is_never_touched()
    {
        var f = _s.File(Path.Combine(_s.K.Downloads, "Box", ".dropbox.cache", "x"));
        Assert.False(_s.Guard.Check(_s.Quarantine(f)).Allowed);
    }
}
