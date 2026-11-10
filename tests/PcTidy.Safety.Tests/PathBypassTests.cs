using System.Runtime.InteropServices;
using System.Text;
using PcTidy.Core;
using PcTidy.Safety.Paths;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>Crafted spellings of a protected path must classify exactly like the plain path.</summary>
public sealed class PathBypassTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Theory]
    [InlineData(@"C:\Windows\Temp\..\System32", @"C:\Windows\System32")]
    [InlineData(@"c:/windows", @"C:\Windows")]
    [InlineData(@"\\?\C:\WINDOWS", @"C:\Windows")]
    [InlineData(@"\\?\C:\Windows\System32\", @"C:\Windows\System32")]
    [InlineData(@"C:\\Windows\\\System32\", @"C:\Windows\System32")]
    [InlineData(@"C:\Windows\.\System32\.\drivers\..", @"C:\Windows\System32")]
    [InlineData(@"C:/Windows\System32/drivers\..", @"C:\Windows\System32")]
    [InlineData(@"C:\..\..\Windows", @"C:\Windows")]
    public void Spellings_normalize_to_one_canonical_path(string raw, string expected)
    {
        if (!Win.Equals(@"C:\Windows", StringComparison.OrdinalIgnoreCase)) return; // the literals assume Windows on C:
        Assert.Equal(expected, SafePath.Normalize(raw).Path, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\.\C:\Windows\System32")]
    [InlineData(@"\\.\C:\Windows/System32")]
    [InlineData(@"\\.\C:\..\HarddiskVolume3\Windows\System32")] // re-review exploit: ".." climbs above the drive
    public void Device_form_paths_are_refused(string raw) => Assert.Null(SafePath.Normalize(raw).Path);

    [Theory]
    [InlineData(@"Windows\System32")]
    [InlineData(@"C:Windows")]
    [InlineData(@"\Windows")]
    [InlineData(@"C:\Windows\*")]
    [InlineData(@"C:\Windows\Sys?em32")]
    [InlineData(@"C:\a<b")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\Win\0dows")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"\\?\UNC\server\share\x")]
    [InlineData(@"\\localhost\C$\Windows")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume3\Windows")]
    [InlineData(@"\\.\GLOBALROOT\Device\HarddiskVolume3\Windows")]
    [InlineData(@"\??\C:\Windows")]                                  // NT spelling: the engine doesn't take it
    [InlineData(@"\\.\CON")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"CON")]
    [InlineData(@"  C:\Windows")]                                    // never trimmed
    // Trailing dots/spaces are never trimmed: on the way to something they're refused outright.
    [InlineData(@"C:\Windows.\System32")]
    [InlineData(@"C:\Windows \System32")]
    [InlineData(@"C:\Windows...\System32")]
    [InlineData(@"C:\Windows\System32. ")]                           // odd last name: only rename/quarantine sources may have one
    // Alternate data streams, anywhere after the drive.
    [InlineData(@"C:\Windows\System32\config\SAM:hidden:$DATA")]
    [InlineData(@"C:\Windows\Temp\..::$INDEX_ALLOCATION\System32")]
    [InlineData(@"C:\Users\x\a.txt::$DATA")]
    [InlineData(@"\\?\C:\Users\x\a.txt:secret")]
    // \\?\ is literal: "." / ".." / "/" / empty segments can't be real names there.
    [InlineData(@"\\?\C:\Windows\Temp\..\System32")]
    [InlineData(@"\\?\C:\Windows\.\System32")]
    [InlineData(@"\\?\C:/Windows")]
    [InlineData(@"\\?\C:\Windows\\System32")]
    // Reserved device names as a folder on the way.
    [InlineData(@"C:\CON\x.txt")]
    [InlineData(@"C:\x\NUL\y")]
    [InlineData(@"C:\x\com1.txt\y")]
    [InlineData(@"C:\x\LPT9\y")]
    [InlineData(@"C:\x\CONOUT$\y")]
    public void Unclear_or_network_paths_are_refused(string raw)
    {
        Assert.False(SafePath.Normalize(raw).Ok);
    }

    [Theory]
    [InlineData(@"C:\x\lnk.", true)]
    [InlineData(@"C:\x\trail ", true)]
    [InlineData(@"C:\x\nul", true)]
    [InlineData(@"C:\x\COM1.txt", true)]
    [InlineData(@"C:\x\lnk.\", true)]
    [InlineData(@"\\?\C:\x\lnk.", true)]
    [InlineData(@"C:/x/lnk.", false)]          // needs Windows' rules, which would trim the dot
    [InlineData(@"C:\y\..\x\lnk.", false)]
    [InlineData(@"C:\x\\lnk.", false)]
    [InlineData(@"\\.\C:\x\lnk.", false)]
    [InlineData(@"C:\x\lnk.\y", false)]        // not the last name
    public void Odd_last_name_is_kept_literally_only_where_the_engine_uses_it_as_is(string raw, bool ok)
    {
        var r = SafePath.Parse(raw, allowOddLeaf: true);
        Assert.Equal(ok, r.Ok);
        if (ok)
        {
            Assert.True(r.OddLeaf);
            Assert.EndsWith("\\" + raw.TrimEnd('\\').Split('\\')[^1], r.Path, StringComparison.Ordinal);
        }
        Assert.False(SafePath.Parse(raw, allowOddLeaf: false).Ok); // only rename/quarantine sources ever ask for it
    }

    [Fact]
    public void Volume_guid_path_maps_to_its_drive_letter()
    {
        var drive = Path.GetPathRoot(Win)!;
        var buf = new StringBuilder(64);
        Assert.True(GetVolumeNameForVolumeMountPoint(drive, buf, (uint)buf.Capacity));
        var volume = buf.ToString(); // \\?\Volume{guid}\
        var normalized = SafePath.Normalize(volume + Win[3..]).Path;
        Assert.Equal(Win, normalized, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Short_8dot3_names_expand_to_the_long_name()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var shortName = ShortPath(pf);
        if (shortName is null || shortName.Equals(pf, StringComparison.OrdinalIgnoreCase)) return; // 8.3 names disabled on this volume
        Assert.Equal(pf, SafePath.Normalize(shortName).Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(pf + @"\NotThere\x.dll", SafePath.Normalize(shortName + @"\NotThere\x.dll").Path, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sandbox_short_name_cannot_bypass_a_rule()
    {
        var app = _s.Dir(Path.Combine(_s.K.ProgramFiles, "Some Long Application Name"));
        var file = _s.File(Path.Combine(app, "important.dll"));
        var shortName = ShortPath(file);
        if (shortName is null || shortName.Equals(file, StringComparison.OrdinalIgnoreCase)) return; // 8.3 disabled on the testbed drive
        Assert.False(_s.Guard.ClassifyPath(shortName).Allowed);
        Assert.Equal(file, SafePath.Normalize(shortName).Path, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Crafted_sandbox_spellings_are_blocked_like_the_plain_path()
    {
        var sys = Path.Combine(_s.K.WindowsDir, "System32");
        _s.Dir(sys);
        var spellings = new[]
        {
            Path.Combine(_s.K.WindowsTemp, "..", "System32"),
            sys.Replace('\\', '/'),
            @"\\?\" + sys.ToUpperInvariant(),
            sys.ToLowerInvariant() + ".",
            sys + "::$INDEX_ALLOCATION",
            _s.K.Downloads + @"\..\..\..\..\Windows\System32",
        };
        foreach (var p in spellings)
        {
            var v = _s.Guard.Check(_s.Quarantine(p, carveOut: CarveOuts.WindowsTemp));
            Assert.False(v.Allowed, p);
            Assert.False(_s.Guard.ClassifyPath(p).Allowed, p);
        }
    }

    [Fact]
    public void Folder_boundaries_are_respected_for_settings()
    {
        var projects = _s.Dir(Path.Combine(_s.K.Downloads, "Projects"));
        var projects2 = _s.Dir(Path.Combine(_s.K.Downloads, "Projects2"));
        _s.Settings.ExtraProtectedFolders.Add(projects);
        _s.Settings.ScanExclusions.Add(projects);
        Assert.False(_s.Guard.ClassifyPath(Path.Combine(projects, "a.txt")).Allowed);
        Assert.True(_s.Guard.ClassifyPath(_s.File(Path.Combine(projects2, "a.txt"))).Allowed);
        Assert.True(_s.Guard.IsExcludedFromScans(Path.Combine(projects, "x")));
        Assert.False(_s.Guard.IsExcludedFromScans(Path.Combine(projects2, "x")));
        Assert.False(_s.Guard.IsExcludedFromScans(projects + "2"));
    }

    [Fact]
    public void Scan_excluded_folders_are_left_alone()
    {
        var keep = _s.Dir(Path.Combine(_s.K.Downloads, "Archive"));
        var f = _s.File(Path.Combine(keep, "a.txt"));
        _s.Settings.ScanExclusions.Add(keep.ToLowerInvariant() + "\\");
        var v = _s.Guard.Check(_s.Quarantine(f));
        Assert.False(v.Allowed);
        Assert.Contains("excluded", v.Reason);
    }

    private static string? ShortPath(string path)
    {
        var buf = new StringBuilder(1024);
        var n = GetShortPathName(path, buf, (uint)buf.Capacity);
        return n == 0 ? null : buf.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, uint size);
}

/// <summary>ClassifyPath's cached fast path must give the same answer as the full evaluation.</summary>
public sealed class ClassifyFastPathTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    [Fact]
    public void Fast_and_full_classification_agree()
    {
        var link = _s.Junction(Path.Combine(_s.K.Downloads, "Link"), _s.Dir(Path.Combine(_s.K.Downloads, "Target")));
        var paths = new[]
        {
            _s.File(Path.Combine(_s.K.Downloads, "a.txt")),
            Path.Combine(_s.K.WindowsDir, "System32", "x.dll"),
            Path.Combine(_s.K.SteamLibraries[0], "steamapps", "common", "G", "g.pak"),
            Path.Combine(_s.K.CloudRoots[0], "Notes", "n.txt"),
            Path.Combine(_s.K.Videos, "v.mp4"),
            Path.Combine(link, "inside.txt"),
            Path.Combine(link, "deeper", "inside.txt"),
            Path.Combine(_s.K.ClaudeDir, "settings.json"),
            Path.Combine(_s.K.DataRoot, "runs", "r.json"),
        };
        foreach (var p in paths)
        {
            var fast = _s.Guard.ClassifyPath(p);
            var full = _s.Guard.Check(_s.Quarantine(p, feature: "spacemap-unlisted"));
            Assert.Equal(full.Allowed, fast.Allowed);
            Assert.Equal(full.Protection, fast.Protection);
            Assert.Equal(fast, _s.Guard.ClassifyPath(p)); // second call (cached parent) is identical
        }
    }
}
