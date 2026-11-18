using PcTidy.Engine.IO;
using Xunit;

namespace PcTidy.Engine.Tests;

/// <summary>3.3 code review L4: a folder that can't be made never leaves the folders above it behind, empty.</summary>
public class CreateFoldersTests
{
    [Fact]
    public void When_a_nested_folder_cant_be_made_the_ones_made_before_it_are_removed_again()
    {
        using var sb = new TestSandbox();
        Directory.CreateDirectory(sb.Root);
        // "Games" can be made; the next name is longer than any folder name may be, so that one fails.
        var target = Path.Combine(sb.Root, "Games", new string('x', 300), "Call of Duty");

        Assert.ThrowsAny<IOException>(() => FsOps.CreateDirectories(target));
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "Games")));
    }

    [Fact]
    public void Folders_that_already_existed_are_never_removed()
    {
        using var sb = new TestSandbox();
        var games = Path.Combine(sb.Root, "Games");
        Directory.CreateDirectory(games);

        Assert.ThrowsAny<IOException>(() => FsOps.CreateDirectories(Path.Combine(games, "Mods", new string('x', 300))));
        Assert.True(Directory.Exists(games));
        Assert.False(Directory.Exists(Path.Combine(games, "Mods")));
    }
}
