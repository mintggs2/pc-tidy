using System.Globalization;
using PcTidy.Features.Boost.Detection;
using PcTidy.Features.Boost.State;

namespace PcTidy.Features.Boost.Planning;

/// <summary>The plan for one boost (or restore) and the state to save before it runs.</summary>
/// <param name="Plan">Null when there is nothing to change (nothing picked is running).</param>
public sealed record BoostPlanResult(ActionPlan? Plan, BoostState State);

/// <summary>A restore plan and the apps to offer as "Reopen" buttons instead of reopening them automatically.</summary>
public sealed record RestorePlanResult(ActionPlan? Plan, IReadOnlyList<ClosedApp> Offers);

/// <summary>
/// Builds Game boost's plans. Pure: it only reads the snapshot it's given (plus "does this exe still exist").
/// A boost is at most: pause OneDrive (if ticked and running) + politely close each picked app that's running.
/// Windows Update is never changed. Nothing deny-listed, no launcher and nothing belonging to the game is closed.
/// </summary>
public static class BoostPlanner
{
    public const string FeatureId = BoostSettings.FeatureKey;

    /// <summary>Shown instead of any Windows Update change.</summary>
    public const string WindowsUpdateNote =
        "Windows Update isn't changed. Windows Game Mode already holds back driver installs and restart prompts while you play.";

    /// <summary>Game boost never switches power plans (Balanced already gives games full speed).</summary>
    public const string PowerPlanNote =
        "Your power plan isn't changed. Balanced already lets the processor run at full speed in games.";

    /// <summary>
    /// The power note for this CPU. On dual-CCD Ryzen X3D a "maximum performance" plan keeps every core awake and can put
    /// the game on the cores without the extra cache, so the note says to keep Balanced, Game Mode, Game Bar and the
    /// chipset driver (Community tweaks §9.6). Game boost never emits a power-plan step, on any CPU.
    /// </summary>
    public static string PowerNote(Catalog.Tweaks.CpuKind cpu)
        => cpu == Catalog.Tweaks.CpuKind.DualCcdX3D ? Catalog.Tweaks.CpuKinds.DualCcdX3DAdvice + " Game boost never switches the plan." : PowerPlanNote;

    public static BoostPlanResult PlanBoost(GameMatch match, BoostSettings settings, IReadOnlyList<ObservedProcess> processes,
        IReadOnlySet<int> pidsWithWindows, ICommandLineReader commandLines, string windowsDir, DateTime nowUtc)
    {
        var items = new List<PlanItem>();
        var state = new BoostState
        {
            Active = true, GameKey = match.Game.Key, GameName = match.Game.Name, StartedUtc = nowUtc,
        };

        if (settings.PauseOneDrive && IsRunning(processes, "OneDrive"))
        {
            items.Add(new PlanItem
            {
                Kind = ActionKind.RunSystemTool, FeatureId = FeatureId, ToolId = ToolIds.OneDriveShutdown,
                Description = "Pause OneDrive while you play. It starts again when the game closes. Online-only files can't open meanwhile.",
            });
            state.OneDrivePaused = true;
        }

        foreach (var choice in settings.AppsToClose)
        {
            var target = PickProcessToClose(choice, match, processes, pidsWithWindows, windowsDir);
            if (target is null) continue;
            var display = DisplayName(choice);
            var args = new Dictionary<string, string>
            {
                ["pid"] = target.Pid.ToString(CultureInfo.InvariantCulture),
                ["processName"] = target.Name,
            };
            if (target.StartUtc is { } started) args["startedUtc"] = started.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            items.Add(new PlanItem
            {
                Kind = ActionKind.CloseApp, FeatureId = FeatureId, Args = args, Reversible = false,
                Description = settings.ReopenAppsAfter
                    ? $"Close {display} while you play, like clicking its X (never forced). It opens again when the game closes."
                    : $"Close {display} while you play, like clicking its X (never forced).",
            });
            state.ClosedApps.Add(new ClosedApp
            {
                ProcessName = target.Name, DisplayName = display, ExePath = target.ImagePath!,
                Arguments = CommandLineText.SafeArguments(SafeRead(commandLines, target.Pid)),
            });
        }

        var plan = items.Count == 0 ? null : new ActionPlan
        {
            FeatureId = FeatureId, Title = $"Game boost: {match.Game.Name} started", Items = items, CreatedUtc = nowUtc,
        };
        state.BoostRunId = plan?.RunId;
        return new BoostPlanResult(plan, state);
    }

    /// <summary>What was really changed once the boost run finished (skipped steps changed nothing).</summary>
    public static BoostState AfterBoostRun(BoostState intended, RunRecord? run)
    {
        var done = run?.Steps.Where(s => s.Status == StepStatus.Done).Select(s => s.Item).ToList() ?? new List<PlanItem>();
        var closedNames = done.Where(i => i.Kind == ActionKind.CloseApp)
            .Select(i => i.Args?.GetValueOrDefault("processName") ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new BoostState
        {
            Active = true, GameKey = intended.GameKey, GameName = intended.GameName, StartedUtc = intended.StartedUtc,
            BoostRunId = run?.RunId ?? intended.BoostRunId,
            OneDrivePaused = intended.OneDrivePaused && done.Any(i => i.Kind == ActionKind.RunSystemTool && i.ToolId == ToolIds.OneDriveShutdown),
            ClosedApps = intended.ClosedApps.Where(a => closedNames.Contains(a.ProcessName)).ToList(),
            ReopenOffers = intended.ReopenOffers.ToList(),
        };
    }

    /// <summary>
    /// Put things back: start OneDrive if the boost paused it and it isn't running, and reopen closed apps that
    /// aren't running again (unelevated, same exe and arguments). With reopening off, the apps become offers.
    /// </summary>
    public static RestorePlanResult PlanRestore(BoostState state, IReadOnlyList<ObservedProcess> processes, bool reopenApps, DateTime nowUtc)
    {
        var items = new List<PlanItem>();
        var offers = new List<ClosedApp>();
        if (state.OneDrivePaused && !IsRunning(processes, "OneDrive"))
            items.Add(new PlanItem
            {
                Kind = ActionKind.RunSystemTool, FeatureId = FeatureId, ToolId = ToolIds.OneDriveStart,
                Description = "Start OneDrive again. It catches up with your files in the background.",
            });

        foreach (var app in state.ClosedApps)
        {
            if (IsRunning(processes, app.ProcessName) || !ExeExists(app.ExePath)) continue;
            if (reopenApps) items.Add(LaunchItem(app));
            else offers.Add(app);
        }

        var game = state.GameName ?? "the game";
        var plan = items.Count == 0 ? null : new ActionPlan
        {
            FeatureId = FeatureId, Title = $"Game boost: {game} closed, everything put back", Items = items, CreatedUtc = nowUtc,
        };
        return new RestorePlanResult(plan, offers);
    }

    /// <summary>The state once a restore finished: no boost active; apps that didn't reopen are offered as buttons.</summary>
    public static BoostState AfterRestoreRun(BoostState state, RestorePlanResult restore, RunRecord? run)
    {
        var offers = restore.Offers.ToList();
        if (restore.Plan is not null)
        {
            var launched = run?.Steps.Where(s => s.Status is StepStatus.Done && s.Item.Kind == ActionKind.LaunchApp)
                .Select(s => s.Item.Args?.GetValueOrDefault("exePath") ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            offers.AddRange(state.ClosedApps.Where(a => restore.Plan.Items.Any(i => i.Kind == ActionKind.LaunchApp && SameExe(i, a)) && !launched.Contains(a.ExePath)));
        }
        return new BoostState { Active = false, ReopenOffers = offers.GroupBy(a => a.ExePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList() };
    }

    /// <summary>One "Reopen Discord" button: a one-item plan the user runs through the normal confirm.</summary>
    public static ActionPlan PlanReopen(ClosedApp app)
        => new() { FeatureId = FeatureId, Title = $"Reopen {app.DisplayName}", Items = new[] { LaunchItem(app) } };

    private static PlanItem LaunchItem(ClosedApp app)
    {
        var args = new Dictionary<string, string> { ["exePath"] = app.ExePath };
        if (!string.IsNullOrWhiteSpace(app.Arguments)) args["arguments"] = app.Arguments!;
        return new PlanItem
        {
            Kind = ActionKind.LaunchApp, FeatureId = FeatureId, Args = args, Reversible = false,
            Description = $"Open {app.DisplayName} again, as you (not as administrator).",
        };
    }

    private static bool SameExe(PlanItem item, ClosedApp app)
        => string.Equals(item.Args?.GetValueOrDefault("exePath"), app.ExePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The process to ask to close for one picked app: same name, readable path, never deny-listed, never a launcher,
    /// never part of the game, never in the Windows folder. The one with a window first (that's where WM_CLOSE goes),
    /// then the oldest (the main process of multi-process apps).
    /// </summary>
    internal static ObservedProcess? PickProcessToClose(BoostAppChoice choice, GameMatch match, IReadOnlyList<ObservedProcess> processes,
        IReadOnlySet<int> pidsWithWindows, string windowsDir)
    {
        var name = BoostDenyList.Normalize(choice.ProcessName);
        if (name.Length == 0 || BoostDenyList.NeverClose(name)) return null;
        var gamePids = match.Processes.Select(p => p.Pid).ToHashSet();
        return processes
            .Where(p => p.Inspected && p.ImagePath is not null && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Where(p => !gamePids.Contains(p.Pid) && !GameMatcher.IsGameProcess(match.Game, p))
            .Where(p => !GameMatcher.IsUnder(p.ImagePath!, match.Game.InstallDir) && !GameMatcher.IsUnder(p.ImagePath!, windowsDir))
            .OrderByDescending(p => pidsWithWindows.Contains(p.Pid))
            .ThenBy(p => p.StartUtc ?? DateTime.MaxValue)
            .ThenBy(p => p.Pid)
            .FirstOrDefault();
    }

    private static bool IsRunning(IReadOnlyList<ObservedProcess> processes, string name)
        => processes.Any(p => p.Name.Equals(BoostDenyList.Normalize(name), StringComparison.OrdinalIgnoreCase));

    private static string DisplayName(BoostAppChoice choice)
        => string.IsNullOrWhiteSpace(choice.DisplayName) ? BoostDenyList.Normalize(choice.ProcessName) : choice.DisplayName.Trim();

    private static string? SafeRead(ICommandLineReader reader, int pid)
    {
        try { return reader.CommandLine(pid); } catch (Exception) { return null; }
    }

    private static bool ExeExists(string path)
    {
        try { return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
