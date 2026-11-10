using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.App.Composition.Demo;

/// <summary>
/// The demo / design-time composition: in-memory fakes and sample data so the shell runs (and can be
/// screenshotted) before the real engine is wired. Writes nothing to disk.
/// </summary>
public static class DemoComposition
{
    /// <summary>Folder the demo pretends is the PC (paths only; nothing is created there).</summary>
    public static string DemoRoot => Path.Combine(Path.GetTempPath(), "PC Tidy Demo");

    public static AppSettings CreateSettings() => new()
    {
        // Shown on screen (onboarding, settings): a generic path, never the real user name. Nothing is written there.
        DataRoot = @"C:\Users\tester\AppData\Local\PC Tidy",
        OnboardingDone = false,
        SuggestSimulationOnFirstUse = true,
    };

    /// <summary>Where a crash log would go in demo mode (a real, writable temp folder).</summary>
    public static string CrashLogRoot => Path.Combine(DemoRoot, "Data");

    public static DemoRunJournal? Journal { get; private set; }
    public static DemoQuarantineStore? Quarantine { get; private set; }

    public static void Register(AppServices services, AppSettings settings)
    {
        // "--demo-minimal": a plain one-drive PC with no OneDrive or games (checks the "any PC" layouts).
        bool minimal = services.Args.Raw.Any(a => a.Equals("--demo-minimal", StringComparison.OrdinalIgnoreCase));
        var locations = new SandboxKnownLocations(DemoRoot, minimal ? SandboxProfile.Minimal : SandboxProfile.Default);
        var journal = new DemoRunJournal();
        var quarantine = new DemoQuarantineStore();
        var restorePoints = new DemoRestorePointService();
        DemoData.SeedHistory(journal, quarantine);
        Journal = journal;
        Quarantine = quarantine;

        services.Add<IKnownLocations>(locations);
        services.Add<ISystemInfo>(new DemoSystemInfo(minimal));
        services.Add<ISafetyGuard>(new DemoSafetyGuard());
        services.Add<IInUseChecker>(new DemoInUseChecker());
        services.Add<IRunJournal>(journal);
        services.Add<IQuarantineStore>(quarantine);
        services.Add<IRestorePointService>(restorePoints);
        services.Add<IExecutor>(new DemoExecutor(journal, quarantine, restorePoints));
        services.Add<IUndoEngine>(new DemoUndoEngine(journal, quarantine));

        foreach (var m in DemoModules.All())
        {
            if (minimal && m.Id == "games") continue; // no games on a plain PC
            services.AddModule(m);
        }
        // The demo shows the whole planned rail; the real app shows only the current release.
        services.CurrentRelease = Release.V3;
    }
}
