using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Hosting;
using PcTidy.TestbedGen;
using PcTidy.TestbedGen.Manifest;

namespace PcTidy.Rescue.Tests;

/// <summary>
/// One generated fake PC per test under testbed\rescue\. The "app" side runs plans through the real composition
/// root in sandbox mode; Rescue then opens the same folder independently, like it would after the app crashed.
/// </summary>
public sealed class RescueBed : IDisposable
{
    public static readonly string TestbedRoot = Path.Combine(FindCodeRoot(), "testbed", "rescue");

    public string Root { get; }
    public TestbedManifest Before { get; }
    public SandboxKnownLocations Locations { get; }

    public RescueBed(string name)
    {
        Root = Path.Combine(TestbedRoot, $"{name}-{Guid.NewGuid().ToString("N")[..6]}");
        Locations = TestbedBuilder.Generate(Root, TestbedSize.Small).Locations;
        Before = TestbedBuilder.Manifest(Root);
    }

    public string Downloads(string name) => Path.Combine(Locations.Downloads, name);

    /// <summary>A small realistic plan: quarantine three old installers, sort three files into category folders.</summary>
    public ActionPlan QuarantineAndMovePlan(bool includeMoves = true)
    {
        var items = new List<PlanItem>();
        foreach (var n in new[] { "7z2408-x64.msi", "ChromeSetup.exe", "Firefox Installer.exe" })
            items.Add(Quarantine(Downloads(n)));
        if (includeMoves)
        {
            items.Add(Move(Downloads("DSC_0042.JPG"), Downloads(@"Pictures\DSC_0042.JPG")));
            items.Add(Move(Downloads("IMG_20250614_101500.jpg"), Downloads(@"Pictures\IMG_20250614_101500.jpg")));
            items.Add(Move(Downloads("Artist - Song.mp3"), Downloads(@"Music\Artist - Song.mp3")));
        }
        return new ActionPlan { FeatureId = "organize", Title = "Tidy Downloads", Items = items };
    }

    private static PlanItem Quarantine(string path)
    {
        var fi = new FileInfo(path);
        Assert.True(fi.Exists, "testbed file missing: " + path);
        return new PlanItem
        {
            Kind = ActionKind.QuarantinePath, FeatureId = "organize", Description = $"Move '{fi.Name}' to quarantine",
            SourcePath = path, ExpectedSize = fi.Length, ExpectedLastWriteUtc = fi.LastWriteTimeUtc,
        };
    }

    private static PlanItem Move(string from, string to)
    {
        var fi = new FileInfo(from);
        Assert.True(fi.Exists, "testbed file missing: " + from);
        return new PlanItem
        {
            Kind = ActionKind.MovePath, FeatureId = "organize", Description = $"Move '{fi.Name}' to {Path.GetFileName(Path.GetDirectoryName(to))}",
            SourcePath = from, DestinationPath = to, ExpectedSize = fi.Length, ExpectedLastWriteUtc = fi.LastWriteTimeUtc,
        };
    }

    public static readonly ExecuteOptions Real = new() { Mode = ExecutionMode.Real, ContinueWithoutRestorePoint = _ => Task.FromResult(true) };

    /// <summary>Runs the plan as the app would, then shuts that "app" down (journal locks released).</summary>
    public async Task<RunRecord> RunAsApp(ActionPlan plan, ExecutionMode mode = ExecutionMode.Real)
    {
        using var host = PcTidyHost.CreateSandbox(Root);
        try { return await host.Executor.ExecuteAsync(plan, Real with { Mode = mode }, null, CancellationToken.None); }
        finally { (host.Journal as IDisposable)?.Dispose(); }
    }

    /// <summary>
    /// Leaves the run in the journal as "Running" with nobody holding it: exactly what a crash or power cut leaves.
    /// <paramref name="finishFirst"/> = the changes were all made before the crash (else none were).
    /// </summary>
    public async Task<string> CrashMidRun(ActionPlan plan, bool finishFirst)
    {
        using var host = PcTidyHost.CreateSandbox(Root);
        try
        {
            if (finishFirst)
            {
                var run = await host.Executor.ExecuteAsync(plan, Real, null, CancellationToken.None);
                await host.Journal.SaveRunAsync(run with { Status = RunStatus.Running, FinishedUtc = null }, CancellationToken.None);
            }
            else
            {
                await host.Journal.CreateAsync(plan, ExecutionMode.Real, CancellationToken.None);
            }
            return plan.RunId;
        }
        finally { (host.Journal as IDisposable)?.Dispose(); } // the "process died": its run lock is gone
    }

    public RescueService OpenRescue() => RescueService.Open(RescueTarget.ForSandbox(Root));

    /// <summary>Differences vs. the starting tree, ignoring PC Tidy's own data folder (journal, quarantine, logs).</summary>
    public ManifestDiff DiffFromStart()
    {
        var diff = TestbedBuilder.Compare(Before, TestbedBuilder.Manifest(Root));
        return new ManifestDiff(diff.Differences.Where(d => !IsPcTidyOwn(d.Path)).ToList());
    }

    public void AssertIdenticalToStart()
    {
        var diff = DiffFromStart();
        Assert.True(diff.Identical, "The tree isn't back to how it was:\n" + diff.Format(40));
    }

    private static bool IsPcTidyOwn(string relativePath)
    {
        var p = relativePath.Replace('/', '\\');
        return p.Equals("Data", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Data\\", StringComparison.OrdinalIgnoreCase)
            || p.Contains("PC Tidy Quarantine", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { TestbedBuilder.Delete(Root); } catch { /* best effort: the testbed is disposable */ }
    }

    private static string FindCodeRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found");
    }
}
