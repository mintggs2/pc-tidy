using System.Xml;
using Xunit;

namespace PcTidy.EndToEnd.Tests;

/// <summary>
/// Windows refuses to start a program whose embedded app.manifest isn't valid XML ("side-by-side configuration is
/// incorrect"). A double dash inside an XML comment is enough to break it, and no unit test runs the real exe.
/// </summary>
public class ManifestTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PcTidy.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found above the test folder.");
    }

    [Fact]
    public void Every_app_manifest_is_valid_xml()
    {
        var manifests = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.manifest", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(manifests);
        foreach (var path in manifests)
        {
            var doc = new XmlDocument();
            var ex = Record.Exception(() => doc.Load(path));
            Assert.True(ex is null, $"{path} is not valid XML: {ex?.Message}");
        }
    }
}
