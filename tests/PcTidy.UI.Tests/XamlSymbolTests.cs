using System.Text.RegularExpressions;
using Xunit;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace PcTidy.UI.Tests;

/// <summary>
/// A symbol name that WPF-UI doesn't have makes the XAML throw while the page loads (a whole section then fails to
/// open). Every symbol named in any XAML file under src\ must be a real <see cref="SymbolRegular"/>.
/// </summary>
public class XamlSymbolTests
{
    private static readonly Regex[] Uses =
    {
        new(@"\{ui:SymbolIcon\s+(?:Symbol=)?(?<name>\w+)", RegexOptions.Compiled),
        new(@"\b(?:Symbol|Icon|Glyph)\s*=\s*""(?<name>[A-Z][A-Za-z]*\d+)""", RegexOptions.Compiled),
        new(@"<ui:SymbolIcon[^>]*\bSymbol\s*=\s*""(?<name>\w+)""", RegexOptions.Compiled),
    };

    private static string SrcRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) return Path.Combine(d.FullName, "src");
        throw new InvalidOperationException("Tests must run from inside the PC Tidy repository.");
    }

    [Fact]
    public void Every_symbol_named_in_xaml_exists_in_WPF_UI()
    {
        var files = Directory.EnumerateFiles(SrcRoot(), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(files);

        var bad = new List<string>();
        int checkedCount = 0;
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                foreach (var use in Uses)
                    foreach (Match m in use.Matches(lines[i]))
                    {
                        checkedCount++;
                        var name = m.Groups["name"].Value;
                        if (!Enum.TryParse<SymbolRegular>(name, ignoreCase: false, out _))
                            bad.Add($"{Path.GetRelativePath(SrcRoot(), file)}:{i + 1} {name}");
                    }
        }
        Assert.True(checkedCount > 50, "The XAML scan found almost no symbols: the patterns no longer match.");
        Assert.True(bad.Count == 0, "Symbols WPF-UI doesn't have:\n" + string.Join("\n", bad.Distinct()));
    }
}
