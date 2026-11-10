using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Boost;
using PcTidy.Features.Boost.Detection;
using PcTidy.Features.Boost.Games;
using PcTidy.Features.Boost.State;

namespace PcTidy.Features.Boost.Tests;

/// <summary>A sandbox folder under testbed\boost, deleted afterwards.</summary>
public sealed class TestFolder : IDisposable
{
    public TestFolder()
    {
        Root = Path.Combine(RepoRoot(), "testbed", "boost", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string relative, string content = "x")
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !System.IO.File.Exists(Path.Combine(d.FullName, "PcTidy.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("PcTidy.sln not found");
    }
}

/// <summary>A fake process list. Records every OpenProcess access mask requested, per pid.</summary>
public sealed class FakeProcessQuery : IProcessQuery
{
    private readonly object _gate = new();
    public List<(int Pid, string Name, int Session, string? Path, DateTime? Start)> Processes { get; } = new();
    public HashSet<int> WindowPids { get; } = new();
    public List<(int Pid, uint Access)> Opened { get; } = new();
    public int CurrentSessionId { get; set; } = 1;

    public FakeProcessQuery Add(int pid, string name, string? path, int session = 1, DateTime? start = null, bool window = false)
    {
        lock (_gate)
        {
            Processes.Add((pid, name, session, path, start ?? new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc).AddSeconds(pid)));
            if (window) WindowPids.Add(pid);
        }
        return this;
    }

    public void Remove(string name)
    {
        lock (_gate) Processes.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ProcessEntry> List()
    {
        lock (_gate) return Processes.Select(p => new ProcessEntry(p.Pid, p.Name, p.Session)).ToList();
    }

    public ProcessDetails? Query(int pid, uint desiredAccess)
    {
        lock (_gate)
        {
            Opened.Add((pid, desiredAccess));
            var p = Processes.FirstOrDefault(x => x.Pid == pid);
            return p.Name is null ? null : new ProcessDetails(p.Path, p.Start);
        }
    }

    public IReadOnlySet<int> PidsWithWindows() { lock (_gate) return WindowPids.ToHashSet(); }
}

public sealed class FakeSteam(int? id = null) : ISteamStatus
{
    public int? Id { get; set; } = id;
    public int? RunningAppId() => Id;
}

public sealed class FakeGames(params BoostGame[] games) : IBoostGameSource
{
    public IReadOnlyList<BoostGame> Games() => games;
}

public sealed class FakeCommandLines : ICommandLineReader
{
    public Dictionary<int, string> Lines { get; } = new();
    public List<int> Asked { get; } = new();
    public string? CommandLine(int pid) { Asked.Add(pid); return Lines.GetValueOrDefault(pid); }
}

/// <summary>Records plans; every step "Done" unless <see cref="StepResult"/> says otherwise.</summary>
public sealed class FakeFlowHost : IFlowHost
{
    public List<ActionPlan> Approved { get; } = new();
    public List<ActionPlan> Reviewed { get; } = new();
    public Func<PlanItem, StepStatus> StepResult { get; set; } = _ => StepStatus.Done;
    /// <summary>Called when a plan is about to run (e.g. to check what's already on disk).</summary>
    public Action<ActionPlan>? BeforeRun { get; set; }
    public bool ReturnNull { get; set; }

    public Task<RunRecord?> RunApprovedAsync(ActionPlan plan, CancellationToken ct)
    {
        BeforeRun?.Invoke(plan);
        Approved.Add(plan);
        return Task.FromResult(ReturnNull ? null : Record(plan));
    }

    public Task<RunRecord?> ReviewAndRunAsync(ActionPlan plan, CancellationToken ct)
    {
        Reviewed.Add(plan);
        return Task.FromResult<RunRecord?>(Record(plan));
    }

    public Task<RunRecord?> SimulateAsync(ActionPlan plan, CancellationToken ct) => Task.FromResult<RunRecord?>(null);
    public void OpenInExplorer(string path) { }
    public void ShowMessage(string title, string message, bool isError = false) { }

    private RunRecord Record(ActionPlan plan) => new()
    {
        RunId = plan.RunId, FeatureId = plan.FeatureId, Title = plan.Title, Status = RunStatus.Completed,
        Steps = plan.Items.Select((item, i) => new StepRecord { Index = i, Item = item, Status = StepResult(item) }).ToList(),
    };
}

/// <summary>A small fake PC for the service: one Steam game, OneDrive, Discord and Spotify running.</summary>
public sealed class BoostRig : IDisposable
{
    public const int GamePid = 5000, OneDrivePid = 600, DiscordPid = 700, SpotifyPid = 800, SteamPid = 900;

    public TestFolder Folder { get; } = new();
    public FakeProcessQuery Processes { get; } = new();
    public FakeFlowHost Flow { get; } = new();
    public FakeSteam Steam { get; } = new();
    public FakeCommandLines CommandLines { get; } = new();
    public BoostSettings Settings { get; } = new();
    public BoostGame Game { get; }
    public string DiscordExe { get; }
    public string SpotifyExe { get; }
    public string GameExe { get; }
    public string WindowsDir { get; }
    public BoostStateStore Store { get; }

    public BoostRig()
    {
        var gameDir = Path.Combine(Folder.Root, "D", "SteamLibrary", "steamapps", "common", "Space Game");
        GameExe = Folder.File(@"D\SteamLibrary\steamapps\common\Space Game\SpaceGame.exe");
        DiscordExe = Folder.File(@"C\Users\tester\AppData\Local\Discord\app-1.0\Discord.exe");
        SpotifyExe = Folder.File(@"C\Users\tester\AppData\Roaming\Spotify\Spotify.exe");
        WindowsDir = Path.Combine(Folder.Root, "C", "Windows");
        Game = new BoostGame("steam:4000", "Space Game", "Steam", gameDir, "4000", Array.Empty<string>());
        Store = new BoostStateStore(Path.Combine(Folder.Root, "Data"));

        Processes.Add(OneDrivePid, "OneDrive", Path.Combine(Folder.Root, @"C\Users\tester\AppData\Local\Microsoft\OneDrive\OneDrive.exe"))
            .Add(DiscordPid, "Discord", DiscordExe, window: true)
            .Add(DiscordPid + 1, "Discord", DiscordExe)
            .Add(SpotifyPid, "Spotify", SpotifyExe, window: true)
            .Add(SteamPid, "steam", Path.Combine(Folder.Root, @"C\Program Files (x86)\Steam\steam.exe"));
        CommandLines.Lines[DiscordPid] = $"\"{DiscordExe}\" --start-minimized";
        CommandLines.Lines[SpotifyPid] = $"\"{SpotifyExe}\" --login-token=abc123";

        Settings.Enabled = true;
        Settings.PauseOneDrive = true;
        Settings.SetGame(Game.Key, Game.Name, true);
        Settings.AppsToClose.Add(new BoostAppChoice { ProcessName = "Discord", DisplayName = "Discord" });
        Settings.AppsToClose.Add(new BoostAppChoice { ProcessName = "Spotify", DisplayName = "Spotify" });
    }

    public BoostService CreateService(int exitGracePolls = 2) => new(new BoostDependencies
    {
        Flow = Flow, Processes = Processes, CommandLines = CommandLines, Steam = Steam, Games = new FakeGames(Game),
        Store = Store, Settings = () => Settings, WindowsDir = WindowsDir, ExitGracePolls = exitGracePolls,
    });

    public void StartGame() => Processes.Add(GamePid, "SpaceGame", GameExe, window: true);
    public void StopGame() => Processes.Remove("SpaceGame");

    public void Dispose() => Folder.Dispose();
}
