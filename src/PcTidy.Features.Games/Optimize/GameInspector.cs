using System.Text.RegularExpressions;
using PcTidy.Catalog.Games;
using PcTidy.Core;

namespace PcTidy.Features.Games.Optimize;

/// <summary>What a game folder's layout says about the game.</summary>
/// <param name="ProcessNames">The game's own exe names without ".exe" (helpers like crash reporters left out).</param>
/// <param name="UnrealProjects">Candidate Unreal project names (for %LOCALAPPDATA%\&lt;Project&gt;\Saved\Config).</param>
public sealed record GameLayout(GameEngine Engine, string? EngineWhy, AntiCheat AntiCheat, IReadOnlyList<string> ProcessNames, IReadOnlyList<string> UnrealProjects);

/// <summary>
/// Reads a game's install folder listing (names only, 4 levels deep, never file contents, never through links) to
/// detect its engine (catalog <see cref="EngineSignatures"/>), any bundled anti-cheat and its exe names.
/// </summary>
internal static class GameInspector
{
    private const int MaxDepth = 4;
    private const int MaxEntries = 60_000;

    private static readonly IReadOnlyList<(EngineSignature Sig, Regex Pattern)> Engines =
        EngineSignatures.All.Select(s => (s, Compile(s.RelativePattern))).ToList();
    private static readonly IReadOnlyList<(AntiCheat AntiCheat, Regex Pattern)> AntiCheats =
        AntiCheatSignatures.All.Select(a => (a.AntiCheat, Compile(a.RelativePattern))).ToList();
    private static readonly IReadOnlyList<Regex> Helpers = HelperExecutables.Patterns.Select(Compile).ToList();
    private static readonly Regex ShippingExe = new(@"^(?<project>[^\\]+)\\Binaries\\Win64\\(?<exe>[^\\]+)-Win64-Shipping\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PakName = new(@"^(?<name>[^\\-]+)(-Windows\w*)?\.pak$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static GameLayout Inspect(IFileWalker walker, string installDir, CancellationToken ct)
    {
        var relative = List(walker, installDir, ct);

        var scores = new Dictionary<GameEngine, (int Score, string Why)>();
        foreach (var (sig, pattern) in Engines)
            if (relative.Any(r => pattern.IsMatch(r)))
            {
                var current = scores.TryGetValue(sig.Engine, out var s) ? s : (0, sig.Why);
                scores[sig.Engine] = (current.Item1 + sig.Weight, current.Item2);
            }
        var ranked = scores.OrderByDescending(kv => kv.Value.Score).ToList();
        var engine = ranked.Count == 0 || ranked.Count > 1 && ranked[0].Value.Score == ranked[1].Value.Score
            ? GameEngine.Unknown : ranked[0].Key;
        var why = engine == GameEngine.Unknown ? null : ranked[0].Value.Why;

        var antiCheat = AntiCheats.FirstOrDefault(a => relative.Any(r => a.Pattern.IsMatch(r))).AntiCheat;

        var exes = relative.Where(r => r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                       && (!r.Contains('\\') || ShippingExe.IsMatch(r)))
            .Select(r => Path.GetFileNameWithoutExtension(r))
            .Where(n => !Helpers.Any(h => h.IsMatch(n + ".exe") || h.IsMatch(n)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var projects = new List<string>();
        foreach (var r in relative)
        {
            var m = ShippingExe.Match(r);
            if (m.Success) projects.Add(m.Groups["project"].Value);
        }
        foreach (var r in relative.Where(r => r.EndsWith(".pak", StringComparison.OrdinalIgnoreCase)))
        {
            var m = PakName.Match(Path.GetFileName(r));
            if (m.Success) projects.Add(m.Groups["name"].Value);
        }
        projects.Add(Path.GetFileName(installDir.TrimEnd('\\')));

        return new GameLayout(engine, why, antiCheat, exes, projects.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Relative paths of entries up to <see cref="MaxDepth"/> levels down.</summary>
    private static List<string> List(IFileWalker walker, string root, CancellationToken ct)
    {
        var list = new List<string>();
        var prefix = PathUtil.Normalize(root).Length + 1;
        try
        {
            walker.Walk(root, new WalkOptions { MaxDepth = MaxDepth, IncludeHidden = true, IncludeSystem = false }, e =>
            {
                if (list.Count >= MaxEntries) return false;
                if (e.FullPath.Length > prefix) list.Add(e.FullPath[prefix..]);
                return e.IsDirectory && !e.IsReparsePoint;
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* unreadable game folder: Unknown engine */ }
        return list;
    }

    /// <summary>"*" = any characters inside one name; "**" = one or more folders. Case-insensitive, whole path.</summary>
    internal static Regex Compile(string pattern)
    {
        var parts = pattern.Split('\\');
        var rx = string.Join(@"\\", parts.Select(p => p == "**" ? "(?:[^\\\\]+\\\\)*[^\\\\]+" : Regex.Escape(p).Replace(@"\*", @"[^\\]*")));
        return new Regex("^" + rx + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
