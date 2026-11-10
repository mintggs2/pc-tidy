namespace PcTidy.Rescue;

/// <summary>What the command line asked for.</summary>
public enum RescueCommand
{
    /// <summary>No command: open the window.</summary>
    Window,
    List,
    Undo,
    UndoLast,
    Resume,
    Rollback,
    RestoreQuarantine,
    Help,
}

/// <summary>
/// Parsed command line of <c>PC Tidy Rescue.exe</c>:
/// <code>
///   --data &lt;folder&gt;        use this data folder (default: the one PC Tidy uses)
///   --sandbox &lt;root&gt;       work on a practice folder made with PcTidy.exe --sandbox
///   --list                  list runs, newest first
///   --undo &lt;runId&gt;         undo one run
///   --undo-last             undo the newest run that can be undone
///   --resume &lt;runId&gt;       finish an interrupted run
///   --rollback &lt;runId&gt;     undo the finished part of an interrupted run
///   --restore-quarantine &lt;runId&gt;  put back everything still in quarantine for a run
///   --yes                   don't ask before changing anything
/// </code>
/// </summary>
public sealed record RescueOptions
{
    public RescueCommand Command { get; init; } = RescueCommand.Window;
    public string? RunId { get; init; }
    public string? DataFolder { get; init; }
    public string? SandboxRoot { get; init; }
    public bool Yes { get; init; }
    /// <summary>Set when the command line couldn't be understood (shown with the help text).</summary>
    public string? Error { get; init; }

    public bool IsCommandLine => Command != RescueCommand.Window;

    public const string HelpText =
        """
        PC Tidy Rescue: undo what PC Tidy changed, even if PC Tidy won't start.

          "PC Tidy Rescue.exe"                      open the window
          "PC Tidy Rescue.exe" --list               list runs, newest first
          "PC Tidy Rescue.exe" --undo <runId>       undo one run
          "PC Tidy Rescue.exe" --undo-last          undo the newest run that can be undone
          "PC Tidy Rescue.exe" --resume <runId>     finish an interrupted run
          "PC Tidy Rescue.exe" --rollback <runId>   undo the finished part of an interrupted run
          "PC Tidy Rescue.exe" --restore-quarantine <runId>
                                                    put back everything still in quarantine for a run

        Options:
          --yes               don't ask before changing anything
          --data <folder>     use this PC Tidy data folder
          --sandbox <root>    work on a practice folder (PcTidy.exe --sandbox <root>)
        """;

    public static RescueOptions Parse(IReadOnlyList<string> args)
    {
        var o = new RescueOptions();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? Next() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            RescueOptions WithCommand(RescueCommand c, string? id = null)
                => o.Command != RescueCommand.Window && o.Command != c
                    ? o with { Error = "Use one command at a time." }
                    : o with { Command = c, RunId = id ?? o.RunId };

            switch (a.ToLowerInvariant())
            {
                case "--list": o = WithCommand(RescueCommand.List); break;
                case "--undo-last": o = WithCommand(RescueCommand.UndoLast); break;
                case "--undo": o = Need(WithCommand(RescueCommand.Undo, Next()), a); break;
                case "--resume": o = Need(WithCommand(RescueCommand.Resume, Next()), a); break;
                case "--rollback": o = Need(WithCommand(RescueCommand.Rollback, Next()), a); break;
                case "--restore-quarantine": o = Need(WithCommand(RescueCommand.RestoreQuarantine, Next()), a); break;
                case "--yes" or "-y": o = o with { Yes = true }; break;
                case "--data": o = Next() is { } d ? o with { DataFolder = d } : o with { Error = "--data needs a folder." }; break;
                case "--sandbox": o = Next() is { } s ? o with { SandboxRoot = s } : o with { Error = "--sandbox needs a folder." }; break;
                case "--help" or "-h" or "/?": o = o with { Command = RescueCommand.Help }; break;
                // The elevated helper switch is handled before parsing (Program.Main).
                default: o = o with { Error = $"Unknown option: {a}" }; break;
            }
            if (o.Error is not null) return o with { Command = RescueCommand.Help };
        }
        if (o.DataFolder is not null && o.SandboxRoot is not null)
            return o with { Command = RescueCommand.Help, Error = "Use --data or --sandbox, not both." };
        return o;
    }

    private static RescueOptions Need(RescueOptions o, string option)
        => o.Error is null && string.IsNullOrWhiteSpace(o.RunId) ? o with { Error = $"{option} needs a run id (see --list)." } : o;
}
