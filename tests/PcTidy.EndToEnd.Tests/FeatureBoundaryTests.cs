using System.Text.RegularExpressions;
using Xunit;

namespace PcTidy.EndToEnd.Tests;

/// <summary>
/// Enforces the core safety architecture: feature projects only DESCRIBE changes (PlanItems).
/// Only PcTidy.Engine and PcTidy.Platform may change files, the registry, services or run tools.
/// This scans feature source code for forbidden calls and fails the build if any appear.
/// </summary>
public class FeatureBoundaryTests
{
    private static readonly string SrcRoot = FindSrcRoot();

    // Forbidden in feature projects. Read APIs (File.ReadAll*, File.Exists, OpenRead...) are fine.
    private static readonly (string Name, Regex Pattern)[] Forbidden =
    {
        ("File.Delete", new Regex(@"\bFile\.Delete\s*\(")),
        ("File.Move", new Regex(@"\bFile\.Move\s*\(")),
        ("File.Copy", new Regex(@"\bFile\.Copy\s*\(")),
        ("File.Replace", new Regex(@"\bFile\.Replace\s*\(")),
        ("File.Write*", new Regex(@"\bFile\.(WriteAll\w*|AppendAll\w*|Create|CreateText|AppendText|SetAttributes|SetCreationTime\w*|SetLastWriteTime\w*|Encrypt|Decrypt)\s*\(")),
        ("Directory.Delete/Move/Create", new Regex(@"\bDirectory\.(Delete|Move|CreateDirectory|CreateSymbolicLink|SetCreationTime\w*|SetLastWriteTime\w*)\s*\(")),
        ("FileInfo/DirectoryInfo mutation", new Regex(@"\.MoveTo\s*\(|\.Delete\s*\(\s*(\)|true\s*\)|recursive)")),
        ("FileStream for writing", new Regex(@"FileAccess\.(Write|ReadWrite)|FileMode\.(Create|CreateNew|Append|Truncate|OpenOrCreate)")),
        ("Registry writes", new Regex(@"\.(SetValue|DeleteValue|DeleteSubKey|DeleteSubKeyTree|CreateSubKey)\s*\(")),
        ("Process.Start", new Regex(@"\bProcess\.Start\s*\(")),
        ("ShellExecute", new Regex(@"ShellExecute|SHFileOperation|IFileOperation|SHEmptyRecycleBin\s*\(")),
        ("Junction/reparse writes", new Regex(@"FSCTL_SET_REPARSE_POINT|FSCTL_DELETE_REPARSE_POINT")),
    };

    // Known-safe exceptions: settings persistence is done by the shell, not features.
    private static readonly Regex AllowLine = new(@"//\s*boundary-ok:", RegexOptions.IgnoreCase);

    public static IEnumerable<object[]> FeatureProjects()
        => Directory.GetDirectories(SrcRoot, "PcTidy.Features.*").Select(d => new object[] { Path.GetFileName(d) });

    [Theory]
    [MemberData(nameof(FeatureProjects))]
    public void Feature_projects_never_change_the_system_directly(string project)
    {
        var dir = Path.Combine(SrcRoot, project);
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("///") || trimmed.StartsWith("*")) continue;
                if (AllowLine.IsMatch(line)) continue;
                foreach (var (name, pattern) in Forbidden)
                    if (pattern.IsMatch(line))
                        violations.Add($"{Path.GetRelativePath(SrcRoot, file)}:{i + 1}: {name}: {trimmed}");
            }
        }
        Assert.True(violations.Count == 0,
            "Feature projects must only produce PlanItems. Forbidden calls found:\n" + string.Join("\n", violations));
    }

    private static string FindSrcRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        if (d == null) throw new InvalidOperationException("Could not find PcTidy.sln above " + AppContext.BaseDirectory);
        return Path.Combine(d.FullName, "src");
    }
}
