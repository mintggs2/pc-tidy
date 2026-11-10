using System.Text.RegularExpressions;
using Xunit;

namespace PcTidy.Features.Assistant.Tests;

/// <summary>
/// Source checks for the v3 network rule: only the Ollama folder may use networking, the only address written
/// anywhere is 127.0.0.1, and nothing ever asks Ollama to download, create, copy, push or delete a model.
/// </summary>
public class NetworkBoundaryTests
{
    private static readonly string ProjectDir = FindProjectDir();

    private static readonly Regex NetworkApi = new(@"\b(HttpClient|SocketsHttpHandler|HttpClientHandler|HttpRequestMessage|Socket|TcpClient|WebRequest|WebClient|NetworkStream|Dns)\b");
    private static readonly Regex AnyUrl = new(@"\b(?:https?|wss?|ftp)://", RegexOptions.IgnoreCase);
    private static readonly Regex ModelChanging = new(@"api/(pull|push|create|copy|delete|blobs)", RegexOptions.IgnoreCase);

    private static IEnumerable<(string File, int Line, string Text)> CodeLines()
    {
        foreach (var file in Directory.EnumerateFiles(ProjectDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//")) continue;
                yield return (Path.GetRelativePath(ProjectDir, file), i + 1, lines[i]);
            }
        }
    }

    [Fact]
    public void Only_the_Ollama_folder_uses_networking()
    {
        // (The module and page only pass an HttpMessageHandler test seam through; they never send anything.)
        var outside = CodeLines().Where(l => !l.File.StartsWith(@"Ollama\", StringComparison.OrdinalIgnoreCase) && NetworkApi.IsMatch(l.Text)).ToList();
        Assert.True(outside.Count == 0, "Networking outside Ollama\\:\n" + string.Join("\n", outside.Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")));
    }

    [Fact]
    public void The_only_address_in_the_code_is_127_0_0_1()
    {
        var urls = CodeLines().Where(l => AnyUrl.IsMatch(l.Text)).ToList();
        Assert.All(urls, l => Assert.Contains("http://{LoopbackAddress}", l.Text));
        var literals = CodeLines().SelectMany(l => Regex.Matches(l.Text, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b").Select(m => m.Value)).Distinct().ToList();
        Assert.Equal(new[] { "127.0.0.1" }, literals);
    }

    [Fact]
    public void Nothing_asks_Ollama_to_download_or_change_models()
    {
        var hits = CodeLines().Where(l => ModelChanging.IsMatch(l.Text)).ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits.Select(h => $"{h.File}:{h.Line}")));
    }

    [Fact]
    public void Nothing_in_the_assistant_writes_files_or_starts_programs()
    {
        var forbidden = new Regex(@"\bFile\.(Write|Append|Create|Delete|Move|Copy|Replace)|\bDirectory\.(Create|Delete|Move)|\bProcess\.Start|FileMode\.(Create|Append|OpenOrCreate|Truncate)|FileAccess\.(Write|ReadWrite)|\.SetValue\(|\.DeleteValue\(");
        var hits = CodeLines().Where(l => forbidden.IsMatch(l.Text)).ToList();
        Assert.True(hits.Count == 0, string.Join("\n", hits.Select(h => $"{h.File}:{h.Line}: {h.Text.Trim()}")));
    }

    private static string FindProjectDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        if (d == null) throw new InvalidOperationException("PcTidy.sln not found above " + AppContext.BaseDirectory);
        return Path.Combine(d.FullName, "src", "PcTidy.Features.Assistant");
    }
}
