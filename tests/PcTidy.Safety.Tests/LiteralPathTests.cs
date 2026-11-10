using System.Runtime.InteropServices;
using System.Text;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Safety.Paths;
using Xunit;

namespace PcTidy.Safety.Tests;

/// <summary>
/// The guard judges the literal \\?\ path the engine acts on. Regression tests for H1 (a junction named "lnk."
/// let "Downloads\lnk.\victim.dll" through, because the old guard trimmed the dot and looked up "lnk").
/// </summary>
public sealed class LiteralPathTests : IDisposable
{
    private readonly TestSandbox _s = new();
    public void Dispose() => _s.Dispose();

    private string System32 => _s.Dir(Path.Combine(_s.K.WindowsDir, "System32"));

    private SafetyVerdict Q(string path, string feature = "cleanup") => _s.Guard.Check(_s.Quarantine(path, feature));

    private static void Blocked(SafetyVerdict v, string? reasonContains = null)
    {
        Assert.False(v.Allowed, "expected blocked");
        Assert.False(string.IsNullOrWhiteSpace(v.Reason));
        if (reasonContains is not null) Assert.Contains(reasonContains, v.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    private static void Allowed(SafetyVerdict v) => Assert.True(v.Allowed, v.Reason);

    [Theory]
    [InlineData("lnk.")]
    [InlineData("lnk ")]
    [InlineData("lnk. .")]
    public void H1_junction_with_a_trailing_dot_or_space_cannot_smuggle_a_path_through(string linkName)
    {
        var victim = _s.File(Path.Combine(System32, "victim.dll"));
        var link = _s.Junction(Path.Combine(_s.K.Downloads, linkName), System32);
        var smuggled = Path.Combine(link, "victim.dll");
        // Prove the setup: the engine's literal path really reaches System32 through the link.
        Assert.True(File.Exists(TestSandbox.Literal(smuggled)));
        Assert.False(File.Exists(Path.Combine(_s.K.Downloads, linkName.TrimEnd('.', ' '), "victim.dll")));

        foreach (var spelling in new[]
                 {
                     smuggled,
                     TestSandbox.Literal(smuggled),
                     @"\\.\" + smuggled,
                     smuggled.Replace('\\', '/'),
                     Path.Combine(_s.K.Downloads, "sub", "..", linkName, "victim.dll"),
                 })
        {
            Blocked(Q(spelling));
            Blocked(_s.Guard.ClassifyPath(spelling));
            Blocked(_s.Guard.Check(_s.Move(spelling, Path.Combine(_s.K.Downloads, "moved.dll"))));
            Blocked(_s.Guard.Check(_s.Move(spelling, Path.Combine(_s.K.Downloads, "renamed.dll"), kind: ActionKind.RenamePath)));
            Blocked(_s.Guard.Check(_s.Move(_s.File(Path.Combine(_s.K.Downloads, "in.txt")), Path.Combine(link, "in.txt"))));
        }
        // The literal link itself as the last name: the odd-name allowance never skips the link check.
        Blocked(Q(link), "link");
        Blocked(Q(TestSandbox.Literal(link)), "link");
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void Odd_last_names_may_be_quarantined_or_renamed_but_nothing_else()
    {
        foreach (var name in new[] { "bad.", "trail ", "nul", "CON.txt", "com1" })
        {
            var f = _s.OddFile(Path.Combine(_s.K.Downloads, name));
            var fixedName = Path.Combine(_s.K.Downloads, "fixed.txt");
            Allowed(Q(f));
            Allowed(Q(TestSandbox.Literal(f)));
            Allowed(_s.Guard.ClassifyPath(f));
            Allowed(_s.Guard.Check(_s.Move(f, fixedName, kind: ActionKind.RenamePath)));
            Blocked(_s.Guard.Check(_s.Move(f, Path.Combine(_s.K.Downloads, "sub", "x.txt"))));
            Blocked(_s.Guard.Check(_s.Move(_s.File(Path.Combine(_s.K.Downloads, "ok.txt")), f, kind: ActionKind.RenamePath)));
            Blocked(_s.Guard.Check(new PlanItem
            {
                Kind = ActionKind.ArchiveToZip, FeatureId = "organize", Description = "t", SourcePath = f,
                DestinationPath = Path.Combine(_s.K.Downloads, "a.zip"),
            }));
            Blocked(_s.Guard.Check(new PlanItem { Kind = ActionKind.ShredFile, FeatureId = "cleanup", Description = "t", SourcePath = f, Reversible = false }));
        }
    }

    [Fact]
    public void Odd_last_name_still_gets_every_literal_rule()
    {
        // A system file named "sys." is still a system file.
        var sys = _s.OddFile(Path.Combine(_s.K.Downloads, "sys."));
        File.SetAttributes(TestSandbox.Literal(sys), FileAttributes.System);
        Blocked(Q(sys), "system file");
        // Never-touched areas are judged on the literal name too.
        Blocked(Q(_s.OddFile(Path.Combine(System32, "evil."))), "Windows");
        // Odd name needing Windows' normalising is refused (the engine would trim it).
        var odd = _s.OddFile(Path.Combine(_s.K.Downloads, "x."));
        Blocked(Q(odd.Replace('\\', '/')));
        Blocked(Q(Path.Combine(_s.K.Downloads, "sub", "..", "x.")));
        Blocked(Q(@"\\.\" + odd));
    }

    [Fact]
    public void Odd_name_on_the_way_is_refused_even_if_the_folder_is_real()
    {
        var dir = _s.OddDir(Path.Combine(_s.K.Downloads, "folder."));
        _s.OddFile(Path.Combine(dir, "a.txt"));
        Blocked(Q(Path.Combine(dir, "a.txt")), "dot or space");
        Blocked(_s.Guard.ClassifyPath(Path.Combine(dir, "a.txt")));
        var nul = _s.OddDir(Path.Combine(_s.K.Downloads, "nul"));
        Blocked(Q(Path.Combine(nul, "a.txt")), "reserved name");
    }

    [Theory]
    [InlineData("a.txt:secret")]
    [InlineData("a.txt::$DATA")]
    [InlineData("a.txt:secret:$DATA")]
    [InlineData(@"sub::$INDEX_ALLOCATION\a.txt")]
    public void Alternate_data_streams_are_refused(string relative)
    {
        _s.File(Path.Combine(_s.K.Downloads, "a.txt"));
        _s.Dir(Path.Combine(_s.K.Downloads, "sub"));
        var p = Path.Combine(_s.K.Downloads, relative);
        Blocked(Q(p), "stream");
        Blocked(Q(TestSandbox.Literal(p)), "stream");
        Blocked(_s.Guard.ClassifyPath(p));
    }

    [Fact]
    public void Prefixed_spellings_of_an_ordinary_file_are_judged_like_the_plain_path()
    {
        var f = _s.File(Path.Combine(_s.K.Downloads, "plain.txt"));
        Allowed(Q(TestSandbox.Literal(f)));
        // Device form is refused outright: there ".." can climb above the drive (\\.\C:\..\HarddiskVolume3\x = C:\x).
        Blocked(Q(@"\\.\" + f));
        Blocked(Q(@"\\.\" + f[..2] + @"\..\HarddiskVolume3\" + f[3..]));
        Allowed(Q(f.Replace('\\', '/')));
        Allowed(Q(Path.Combine(_s.K.Downloads, "x", "..", "plain.txt")));
        Blocked(Q(@"\??\" + f));
        Blocked(Q(@"\\?\GLOBALROOT\Device\HarddiskVolume1" + f[2..]));
        Blocked(Q(TestSandbox.Literal(Path.Combine(_s.K.Downloads, "x", "..", "plain.txt"))));
    }

    [Fact]
    public void Mixed_separators_and_dot_dot_cannot_climb_into_a_protected_area()
    {
        _s.File(Path.Combine(System32, "kernel32.dll"));
        var depth = _s.K.Downloads[(_s.K.SystemDrive.Length - 1)..].Count(c => c == '\\');
        var up = string.Concat(Enumerable.Repeat(@"..\", depth));
        foreach (var p in new[]
                 {
                     _s.K.Downloads + @"\" + up + @"Windows\System32\kernel32.dll",
                     (_s.K.Downloads + @"\" + up + @"Windows\System32\kernel32.dll").Replace('\\', '/'),
                     _s.K.Downloads + @"/sub\../" + up + @"Windows/System32\kernel32.dll",
                     _s.K.Downloads + @"\" + up + up + up + @"Windows\System32\kernel32.dll", // more ".." only leave this PC's drives
                 })
        {
            Blocked(Q(p));
            Blocked(_s.Guard.ClassifyPath(p));
        }
    }

    [Fact]
    public void Short_8dot3_spelling_of_a_link_parent_still_finds_the_link()
    {
        var parent = _s.Dir(Path.Combine(_s.K.Downloads, "A Long Folder Name Here"));
        _s.File(Path.Combine(System32, "victim.dll"));
        var link = _s.Junction(Path.Combine(parent, "lnk"), System32);
        var shortParent = ShortPath(parent);
        if (shortParent is null || shortParent.Equals(parent, StringComparison.OrdinalIgnoreCase)) return; // 8.3 names off on this volume
        var viaShort = Path.Combine(shortParent, "lnk", "victim.dll");
        Blocked(Q(viaShort), "link");
        Blocked(_s.Guard.ClassifyPath(viaShort), "link");
        Assert.Equal(Path.Combine(link, "victim.dll"), SafePath.Normalize(viaShort).Path, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Short_8dot3_spelling_of_a_protected_folder_is_still_protected()
    {
        var app = _s.Dir(Path.Combine(_s.K.LocalAppData, "Programs", "FakeUserApp", "Long Sub Folder"));
        var f = _s.File(Path.Combine(app, "core.dll"));
        var shortName = ShortPath(f);
        if (shortName is null || shortName.Equals(f, StringComparison.OrdinalIgnoreCase)) return;
        Blocked(Q(shortName), "installed app");
        Blocked(_s.Guard.ClassifyPath(shortName));
    }

    private static string? ShortPath(string path)
    {
        var buf = new StringBuilder(1024);
        var n = GetShortPathName(path, buf, (uint)buf.Capacity);
        return n == 0 ? null : buf.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);
}
