using PcTidy.Core.Model;

namespace PcTidy.Rescue;

/// <summary>
/// Command-line mode (<c>--list</c>, <c>--undo</c>, <c>--undo-last</c>, ...). Output goes to a TextWriter and the
/// "are you sure?" question to a callback, so this is fully testable. Exit codes: see <see cref="ExitCodes"/>.
/// </summary>
public sealed class RescueCli
{
    public static class ExitCodes
    {
        public const int Ok = 0;
        public const int Failed = 1;
        public const int Usage = 2;
        public const int NothingToDo = 3;
        public const int Declined = 4;
    }

    private readonly TextWriter _out;
    private readonly Func<string, bool> _confirm;
    private readonly Func<RescueTarget, RescueService> _open;

    /// <param name="confirm">Asked before any change unless --yes; return true for "yes".</param>
    /// <param name="open">Builds the service for a target (tests pass their own).</param>
    public RescueCli(TextWriter output, Func<string, bool> confirm, Func<RescueTarget, RescueService>? open = null)
    {
        _out = output;
        _confirm = confirm;
        _open = open ?? RescueService.Open;
    }

    public async Task<int> RunAsync(RescueOptions o, CancellationToken ct = default)
    {
        if (o.Command == RescueCommand.Help || o.Error is not null)
        {
            if (o.Error is not null) _out.WriteLine(o.Error);
            _out.WriteLine(RescueOptions.HelpText);
            return o.Error is null ? ExitCodes.Ok : ExitCodes.Usage;
        }

        RescueService service;
        try
        {
            service = _open(RescueTarget.Resolve(o));
        }
        catch (Exception ex)
        {
            RescueLog.Error("Opening the data folder", ex);
            _out.WriteLine("Couldn't open the PC Tidy data folder: " + ex.Message);
            return ExitCodes.Failed;
        }

        using (service)
        {
            RescueLog.Use(service.DataRoot);
            _out.WriteLine($"Data folder: {service.DataRoot}{(service.IsSandbox ? " (practice folder)" : "")}");
            try
            {
                return o.Command switch
                {
                    RescueCommand.List => await ListAsync(service, ct),
                    RescueCommand.UndoLast => await UndoLastAsync(service, o.Yes, ct),
                    _ => await RunOneAsync(service, o, ct),
                };
            }
            catch (Exception ex)
            {
                RescueLog.Error("Command line " + o.Command, ex);
                _out.WriteLine("Something went wrong: " + ex.Message);
                return ExitCodes.Failed;
            }
        }
    }

    private async Task<int> ListAsync(RescueService service, CancellationToken ct)
    {
        var runs = await service.ListRunsAsync(ct);
        if (runs.Count == 0)
        {
            _out.WriteLine("No runs found in this data folder.");
            return ExitCodes.Ok;
        }
        _out.WriteLine($"{"Run id",-24} {"Date",-18} {"Status",-26} {"Items",5}  Title");
        foreach (var r in runs)
            _out.WriteLine($"{r.RunId,-24} {r.When,-18} {Cut(r.StatusText, 26),-26} {r.ItemCount,5}  {r.Title}{Hints(r)}");
        return ExitCodes.Ok;
    }

    private static string Hints(RunEntry r)
    {
        var hints = new List<string>();
        if (r.CanUndo) hints.Add("can undo");
        if (r.IsInterrupted) hints.Add("can resume or roll back");
        if (r.QuarantineLeft > 0) hints.Add($"{r.QuarantineLeft} in quarantine");
        return hints.Count == 0 ? "" : "  [" + string.Join(", ", hints) + "]";
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Undoes the newest undoable run. If it was one part of an "Optimize everything" run, the other parts are undone
    /// too (newest first), because to the user that was one run.
    /// </summary>
    private async Task<int> UndoLastAsync(RescueService service, bool yes, CancellationToken ct)
    {
        var runs = await service.ListRunsAsync(ct);
        var last = runs.FirstOrDefault(r => r.CanUndo);
        if (last is null)
        {
            _out.WriteLine("There's no run left to undo.");
            return ExitCodes.NothingToDo;
        }
        var group = last.Run.ParentRunId is { } parent
            ? runs.Where(r => r.CanUndo && r.Run.ParentRunId == parent).ToList()
            : new List<RunEntry> { last };

        var what = group.Count == 1
            ? $"Undo \"{last.Title}\" from {last.When} ({last.UndoableSteps} change(s))?"
            : $"Undo the combined run from {last.When} ({group.Count} parts: {string.Join(", ", group.Select(g => g.Title))})?";
        if (!Confirm(what, yes)) return ExitCodes.Declined;

        var code = ExitCodes.Ok;
        foreach (var r in group)
        {
            var result = await service.UndoAsync(r.RunId, Progress(), ct);
            Print(result);
            if (!result.Ok) code = ExitCodes.Failed;
        }
        return code;
    }

    private async Task<int> RunOneAsync(RescueService service, RescueOptions o, CancellationToken ct)
    {
        var entry = await service.FindAsync(o.RunId!, ct);
        if (entry is null)
        {
            _out.WriteLine($"No run with id {o.RunId}. Use --list to see the runs.");
            return ExitCodes.NothingToDo;
        }
        _out.WriteLine($"{entry.Title} ({entry.When}): {entry.StatusText}");

        string question;
        Func<Task<RescueResult>> action;
        switch (o.Command)
        {
            case RescueCommand.Undo:
                if (!entry.CanUndo) return Explain(entry.IsInterrupted
                    ? "This run was interrupted: use --resume or --rollback."
                    : "There's nothing left to undo in this run.");
                question = $"Undo \"{entry.Title}\" ({entry.UndoableSteps} change(s))?";
                action = () => service.UndoAsync(entry.RunId, Progress(), ct);
                break;
            case RescueCommand.Resume:
                if (!entry.CanResume) return Explain("Only an interrupted run can be resumed.");
                question = $"Finish the interrupted run \"{entry.Title}\"? The remaining changes will be made.";
                action = () => service.ResumeAsync(entry.RunId, Progress(), ct);
                break;
            case RescueCommand.Rollback:
                if (!entry.CanRollback) return Explain("Only an interrupted run can be rolled back. Use --undo for a finished run.");
                question = $"Roll back the interrupted run \"{entry.Title}\"? What it changed will be put back.";
                action = () => service.RollbackAsync(entry.RunId, Progress(), ct);
                break;
            case RescueCommand.RestoreQuarantine:
                if (!entry.CanRestoreQuarantine) return Explain(entry.QuarantineText);
                question = $"Put back the {entry.QuarantineLeft} item(s) of \"{entry.Title}\" that are still in quarantine?";
                action = () => service.RestoreQuarantineAsync(entry.RunId, Progress(), ct);
                break;
            default:
                return Explain("Unknown command.", ExitCodes.Usage);
        }

        if (!Confirm(question, o.Yes)) return ExitCodes.Declined;
        var result = await action();
        Print(result);
        return result.Ok ? ExitCodes.Ok : ExitCodes.Failed;
    }

    private int Explain(string message, int code = ExitCodes.NothingToDo)
    {
        _out.WriteLine(message);
        return code;
    }

    private bool Confirm(string question, bool yes)
    {
        _out.WriteLine(question + (yes ? " yes (--yes)" : " [y/N]"));
        if (yes) return true;
        var ok = _confirm(question);
        if (!ok) _out.WriteLine("Nothing was changed.");
        return ok;
    }

    private void Print(RescueResult r)
    {
        _out.WriteLine(r.Message);
        foreach (var d in r.Details.Take(50)) _out.WriteLine("  - " + d);
        if (r.Details.Count > 50) _out.WriteLine($"  ...and {r.Details.Count - 50} more (see the log).");
        if (RescueLog.FilePath is { } log) _out.WriteLine("Log: " + log);
    }

    /// <summary>Prints every 25th step so long runs show life without flooding the console.</summary>
    private IProgress<RunProgress> Progress() => new SyncProgress(p =>
    {
        if (p.StepCount > 0 && (p.StepIndex % 25 == 0)) _out.WriteLine($"  {p.Message}");
    });

    /// <summary>Progress&lt;T&gt; posts to the thread pool; console output should stay in order, so report synchronously.</summary>
    private sealed class SyncProgress(Action<RunProgress> report) : IProgress<RunProgress>
    {
        public void Report(RunProgress value) => report(value);
    }
}
