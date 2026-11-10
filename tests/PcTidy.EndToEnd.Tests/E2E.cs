using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Hosting;
using PcTidy.TestbedGen;
using PcTidy.TestbedGen.Manifest;

namespace PcTidy.EndToEnd.Tests;

/// <summary>
/// One generated fake PC per test under testbed\e2e\, with the real composition root (sandbox mode).
/// Proves the full chain: scan -> plan -> safety -> engine -> undo, and that undo restores the tree exactly.
/// </summary>
public sealed class E2E : IDisposable
{
    public static readonly string TestbedRoot = Path.Combine(FindCodeRoot(), "testbed", "e2e");

    public string Root { get; }
    public TestbedInfo Info { get; }
    public PcTidyHost Host { get; }
    public TestbedManifest Before { get; }

    public E2E(string name, SandboxProfile? profile = null)
    {
        Root = Path.Combine(TestbedRoot, $"{name}-{Guid.NewGuid().ToString("N")[..6]}");
        Info = TestbedBuilder.Generate(Root, TestbedSize.Small, profile ?? SandboxProfile.Default);
        Host = PcTidyHost.CreateSandbox(Root, profile ?? SandboxProfile.Default);
        Before = TestbedBuilder.Manifest(Root);
    }

    public ScanContext Context(ScanDepth depth = ScanDepth.Quick, IReadOnlyList<string>? roots = null, string? preset = null)
        => Host.CreateScanContext(depth, roots, preset);

    public static readonly ExecuteOptions Real = new()
    {
        Mode = ExecutionMode.Real,
        ContinueWithoutRestorePoint = _ => Task.FromResult(true),
    };

    public Task<RunRecord> Run(ActionPlan plan, ExecutionMode mode = ExecutionMode.Real)
        => Host.Executor.ExecuteAsync(plan, Real with { Mode = mode }, null, CancellationToken.None);

    public Task<UndoReport> Undo(string runId) => Host.Undo.UndoRunAsync(runId, null, CancellationToken.None);

    /// <summary>Differences vs. the starting tree, ignoring PC Tidy's own data and quarantine folders.</summary>
    public ManifestDiff DiffFromStart()
    {
        var diff = TestbedBuilder.Compare(Before, TestbedBuilder.Manifest(Root));
        return new ManifestDiff(diff.Differences.Where(d => !IsPcTidyOwn(d.Path)).ToList());
    }

    public IReadOnlyList<ManifestDifference> CanaryViolations(CanaryTier tier)
        => TestbedBuilder.CanaryViolations(Before, TestbedBuilder.Manifest(Root), Info.Canaries.Where(c => tier == CanaryTier.Default || c.Tier == CanaryTier.Never));

    private static bool IsPcTidyOwn(string relativePath)
    {
        var p = relativePath.Replace('/', '\\');
        return p.StartsWith("Data\\", StringComparison.OrdinalIgnoreCase) || p.Equals("Data", StringComparison.OrdinalIgnoreCase)
            || p.Contains("PC Tidy Quarantine", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Host.Dispose();
        try { TestbedBuilder.Delete(Root); } catch { /* best effort: testbed is disposable */ }
    }

    private static string FindCodeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found");
    }
}
