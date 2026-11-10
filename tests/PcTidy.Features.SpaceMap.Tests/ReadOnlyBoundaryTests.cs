using System.Text.RegularExpressions;

namespace PcTidy.Features.SpaceMap.Tests;

/// <summary>Space map is a feature project: it only describes changes. Same rule as the solution-wide boundary test.</summary>
public class ReadOnlyBoundaryTests
{
    private static readonly Regex Forbidden = new(
        @"\bFile\.(Delete|Move|Copy|Replace|WriteAll\w*|AppendAll\w*|Create|CreateText|AppendText|SetAttributes|SetCreationTime\w*|SetLastWriteTime\w*|Encrypt|Decrypt)\s*\(" +
        @"|\bDirectory\.(Delete|Move|CreateDirectory|CreateSymbolicLink|SetCreationTime\w*|SetLastWriteTime\w*)\s*\(" +
        @"|\.MoveTo\s*\(|\.Delete\s*\(\s*(\)|true\s*\)|recursive)|FileAccess\.(Write|ReadWrite)|FileMode\.(Create|CreateNew|Append|Truncate|OpenOrCreate)" +
        @"|\.(SetValue|DeleteValue|DeleteSubKey|DeleteSubKeyTree|CreateSubKey)\s*\(|\bProcess\.Start\s*\(|ShellExecute|SHFileOperation|IFileOperation" +
        @"|FSCTL_SET_REPARSE_POINT|FSCTL_DELETE_REPARSE_POINT");

    [Fact]
    public void Space_map_source_never_changes_the_system()
    {
        var src = Path.Combine(TestDir.FindCodeRoot(), "src", "PcTidy.Features.SpaceMap");
        var hits = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, i, line)))
            .Where(x => !x.line.TrimStart().StartsWith("//") && Forbidden.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}: {x.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits));
    }
}
