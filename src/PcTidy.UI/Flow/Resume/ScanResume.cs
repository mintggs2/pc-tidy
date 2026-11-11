using System.Globalization;
using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.UI.Flow.Resume;

/// <summary>
/// Resumable scans: the flow pages (Scan everything and the standard section pages) save a checkpoint after each
/// finished section, so a scan cut short by a restart, a crash or closing PC Tidy can carry on where it stopped.
/// Registered by the shell (real PC: data folder + registry reader; practice PC: its own data folder + simulated
/// Windows facts; demo: memory only). Not registered = no checkpoints and no restart note.
/// Limits: it works per section (a section that was halfway through starts again), and only for scans that go
/// through <see cref="ScanRunner.RunAsync(IReadOnlyList{IFeatureScanner}, ScanContext, Action{int, IFeatureScanner, ScanProgress}, CancellationToken, ScanCheckpointSession?)"/>.
/// </summary>
public sealed class ScanResume
{
    /// <summary>Flow id of "Scan everything".</summary>
    public const string CombinedFlow = "combined";

    public ScanResume(IScanCheckpointStore store, IWindowsStateReader windows, string appVersion, TimeProvider? clock = null)
    {
        Store = store;
        Windows = windows;
        AppVersion = appVersion;
        Clock = clock ?? TimeProvider.System;
    }

    public IScanCheckpointStore Store { get; }
    public IWindowsStateReader Windows { get; }
    public string AppVersion { get; }
    public TimeProvider Clock { get; }
    /// <summary>Looks at a sampled file again (tests can swap it; default reads the file's size and time).</summary>
    public Func<FileSample, bool> StillSame { get; init; } = CheckpointRules.StillSame;

    internal DateTime NowUtc => Clock.GetUtcNow().UtcDateTime;

    public static ScanResume? From(IServiceProvider? services) => services?.GetService(typeof(ScanResume)) as ScanResume;

    public ScanCheckpointKey KeyFor(string flow, IReadOnlyList<IFeatureScanner> scanners, ScanDepth depth, string? preset, IReadOnlyList<string>? roots, IKnownLocations locations)
        => ScanCheckpointKey.Create(flow, scanners, depth, preset, roots, locations, AppVersion);

    /// <summary>
    /// The flow's saved scan if it might still be usable (not too old, same PC Tidy). A stale one is deleted.
    /// Only the small header is read (no section results): <see cref="Evaluate"/> reads those when it needs them.
    /// It reads a file, so the pages call it off the UI thread.
    /// </summary>
    public ScanCheckpoint? Pending(string flow)
    {
        var cp = Store.LoadHeader(flow);
        if (cp is null) return null;
        if (ObviouslyStale(cp)) { Store.Delete(flow); return null; }
        return cp;
    }

    /// <summary>The checkpoint with its sections' results read in (unchanged when they already are), or null when they can't be read.</summary>
    public ScanCheckpoint? WithResults(ScanCheckpoint cp) => cp.NeedsResults ? Store.Load(cp.Key.Flow) : cp;

    /// <summary>Every saved scan that might still be usable, newest first (Home's card).</summary>
    public IReadOnlyList<ScanCheckpoint> PendingAll()
    {
        var list = new List<ScanCheckpoint>();
        foreach (var cp in Store.LoadAll())
        {
            if (ObviouslyStale(cp)) Store.Delete(cp.Key.Flow);
            else list.Add(cp);
        }
        return list;
    }

    private bool ObviouslyStale(ScanCheckpoint cp)
    {
        var age = NowUtc - cp.StartedUtc;
        return cp.Sections.Count == 0 || age > CheckpointRules.MaxAge || age < -CheckpointRules.ClockSkew
               || !string.Equals(cp.Key.AppVersion, AppVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// All the staleness rules (<see cref="CheckpointRules"/>) against the scan as it would run now. Reads the saved
    /// results and looks at sampled files, so it runs off the UI thread (the pages call <see cref="ResumeFlow.Start"/> in Task.Run).
    /// The decision's reused sections carry their results.
    /// </summary>
    public ResumeDecision Evaluate(ScanCheckpoint cp, ScanCheckpointKey current)
    {
        // Too old, another version or other options: no need to read any results to know.
        if (CheckpointRules.PreCheck(cp, current, NowUtc) is { } discard) return discard;
        var full = WithResults(cp);
        if (full is null) return ResumeDecision.Discard("its saved results couldn't be read");
        return CheckpointRules.Evaluate(full, current, NowUtc, WindowsBuild(), StillSame);
    }

    public void Discard(string flow) => Store.Delete(flow);

    /// <summary>Windows' CurrentBuild.UBR (read-only), or null.</summary>
    public string? WindowsBuild()
    {
        try { return Windows.WindowsBuild(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>Whether Windows Update is waiting to restart the PC (read-only; false when unknown).</summary>
    public bool RestartPending()
    {
        try { return Windows.RestartPending(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>A fresh scan: the flow's old checkpoint goes, and a new one is written once the first section finishes.</summary>
    public ScanCheckpointSession StartFresh(ScanCheckpointKey key, string title, string navigateTo)
    {
        Store.Delete(key.Flow);
        var seed = new ScanCheckpoint
        {
            Key = key, Title = title, NavigateTo = navigateTo, StartedUtc = NowUtc, UpdatedUtc = NowUtc,
            WindowsBuild = WindowsBuild(), TotalSections = key.ScannerIds.Count,
        };
        return new ScanCheckpointSession(this, seed, new Dictionary<int, CheckpointSection>());
    }

    /// <summary>Carries on a saved scan: the sections <paramref name="decision"/> keeps are reused, the rest run.</summary>
    public ScanCheckpointSession Resume(ScanCheckpoint cp, ResumeDecision decision)
    {
        if (!decision.CanResume) throw new InvalidOperationException("This scan can't be resumed: " + decision.DiscardReason);
        // The original start time stays, so resuming again and again never stretches the 24-hour limit.
        var seed = cp with
        {
            WindowsBuild = WindowsBuild(),
            StoppedByUser = false,
            Sections = decision.Reuse.Values.OrderBy(s => s.Index).ToList(),
            UpdatedUtc = NowUtc,
        };
        return new ScanCheckpointSession(this, seed, decision.Reuse) { ResumedFrom = cp };
    }
}

/// <summary>One scan's checkpoint while it runs: reused sections in, each finished section saved, deleted at the end.</summary>
public sealed class ScanCheckpointSession
{
    private readonly ScanResume _owner;
    private ScanCheckpoint _current;
    private readonly List<string> _notes = new();

    internal ScanCheckpointSession(ScanResume owner, ScanCheckpoint seed, IReadOnlyDictionary<int, CheckpointSection> reuse)
    {
        _owner = owner;
        _current = seed;
        Reused = reuse;
    }

    /// <summary>Finished sections taken from the saved scan, by scanner position.</summary>
    public IReadOnlyDictionary<int, CheckpointSection> Reused { get; }
    /// <summary>The saved scan this one carries on (null for a fresh scan).</summary>
    public ScanCheckpoint? ResumedFrom { get; init; }
    public ScanCheckpoint Current => _current;
    /// <summary>Plain notes about progress that couldn't be kept ("Clean up's results were too big to keep for resuming").</summary>
    public IReadOnlyList<string> Notes => _notes;
    public bool Finished { get; private set; }

    private const string CouldNotSave = "PC Tidy couldn't save its progress on this scan, so it can't resume it if it is stopped.";

    /// <summary>
    /// Saves a finished section (off the UI thread): its results go to their own file, measured while they are
    /// written and dropped as soon as they pass the cap, then the small header is replaced. Never throws: a
    /// checkpoint that can't be written only means no resume.
    /// </summary>
    public async Task SectionFinishedAsync(int index, IFeatureScanner scanner, ScanResult result)
    {
        if (Finished) return;
        try
        {
            var (next, saved) = await Task.Run(() =>
            {
                var cp = WithSection(index, scanner, result);
                return (cp, _owner.Store.Save(cp));
            }).ConfigureAwait(true);
            _current = next;
            if (!saved && !_notes.Contains(CouldNotSave)) _notes.Add(CouldNotSave);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!_notes.Contains(CouldNotSave)) _notes.Add(CouldNotSave);
        }
    }

    private ScanCheckpoint WithSection(int index, IFeatureScanner scanner, ScanResult result)
    {
        // The whole checkpoint has a budget too: what the other sections already use is known from their files.
        long others = _current.Sections.Where(s => s.Index != index).Sum(s => s.ResultBytes);
        long budget = Math.Min(CheckpointRules.MaxSectionBytes, FileScanCheckpointStore.MaxFileBytes - others);
        var write = _owner.Store.SaveSection(_current.Key.Flow, index, result, budget);
        string? notSaved = write.Saved ? null : write.TooBig ? "its results were too big to keep for resuming" : "its results couldn't be saved";
        if (write.TooBig) _notes.Add($"{scanner.Title} found a lot, so its results weren't kept for resuming. If this scan stops, that section is scanned again.");
        else if (!write.Saved && !_notes.Contains(CouldNotSave)) _notes.Add(CouldNotSave);
        var section = new CheckpointSection
        {
            Index = index,
            FeatureId = scanner.FeatureId,
            Title = scanner.Title,
            FinishedUtc = _owner.NowUtc,
            Result = write.Saved ? result : null,
            ResultFile = write.FileName,
            ResultBytes = write.Bytes,
            Samples = write.Saved ? CheckpointRules.Sample(result) : Array.Empty<FileSample>(),
            NotSavedReason = notSaved,
        };
        return _current with
        {
            Sections = _current.Sections.Where(s => s.Index != index).Append(section).OrderBy(s => s.Index).ToList(),
            UpdatedUtc = _owner.NowUtc,
        };
    }

    /// <summary>The user pressed Stop: the finished sections stay saved (and say so). Only the small header is written.</summary>
    public void Stopped()
    {
        if (Finished || _current.Sections.Count == 0) return;
        _current = _current with { StoppedByUser = true, UpdatedUtc = _owner.NowUtc };
        try { _owner.Store.Save(_current); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    /// <summary>The whole scan finished: the checkpoint is deleted.</summary>
    public void Completed()
    {
        Finished = true;
        try { _owner.Store.Delete(_current.Key.Flow); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}

/// <summary>The words resumable scans use (plain English, short sentences).</summary>
public static class ResumeText
{
    public const string RestartTitle = "Windows Update is waiting to restart this PC.";

    public const string RestartMessage = "Windows Update is waiting to restart this PC, and it may restart during a long scan. " +
                                         "PC Tidy saves its progress after each section, so you can resume. " +
                                         "To avoid it, restart first or pause updates in Windows Settings.";

    /// <summary>The Settings page the note links to (opened through the shell's OpenExternal path; nothing is changed).</summary>
    public const string WindowsUpdateSettings = "ms-settings:windowsupdate";

    public const string Limits = "Finished sections are reused and only the rest are scanned. A section that was halfway through starts again. " +
                                 "Everything is checked again before anything changes.";

    /// <summary>"12:41", "yesterday at 18:02"</summary>
    public static string Time(DateTime utc, DateTime? nowLocal = null)
    {
        var local = utc.ToLocalTime();
        var today = (nowLocal ?? DateTime.Now).Date;
        var t = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today) return t;
        if (local.Date == today.AddDays(-1)) return "yesterday at " + t;
        return local.ToString("d MMM", CultureInfo.CurrentCulture) + " at " + t;
    }

    /// <summary>
    /// The preview's "Scanned at 12:41": the earliest time any shown section was found (a resumed section keeps its
    /// original scan time, 3.1.1 review L4), else now. "Scanned yesterday at 18:02" for an older resumed section.
    /// </summary>
    public static string ScannedAt(IEnumerable<DateTime?> foundAtUtc, DateTime? nowLocal = null)
    {
        var earliest = foundAtUtc.Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty().Min();
        if (earliest == default) return "Scanned at " + (nowLocal ?? DateTime.Now).ToString("t", CultureInfo.CurrentCulture);
        var when = Time(earliest, nowLocal);
        return "Scanned " + (!when.Contains(" at ", StringComparison.Ordinal) ? "at " + when : when.StartsWith("yesterday", StringComparison.Ordinal) ? when : "on " + when);
    }

    /// <summary>"Found at 12:41, before PC Tidy closed"</summary>
    public static string FoundAt(DateTime utc, bool stoppedByUser, DateTime? nowLocal = null)
    {
        var when = Time(utc, nowLocal);
        var lead = !when.Contains(" at ", StringComparison.Ordinal) ? "Found at " : when.StartsWith("yesterday", StringComparison.Ordinal) ? "Found " : "Found on ";
        return lead + when + (stoppedByUser ? ", before you stopped the scan" : ", before PC Tidy closed");
    }

    /// <summary>"Your last scan stopped after 7 of 12 sections (12:41)."</summary>
    public static string Stopped(ScanCheckpoint cp, DateTime? nowLocal = null)
    {
        var what = cp.Key.Flow == ScanResume.CombinedFlow ? "Your last scan" : $"Your last {cp.Title} scan";
        return $"{what} stopped after {cp.ProgressText} ({Time(cp.LastProgressUtc, nowLocal)}).";
    }

    /// <summary>"Scanned again: Tweaks (Windows was updated since)."</summary>
    public static string? Rescanned(IReadOnlyList<RescanReason> rescan)
    {
        if (rescan.Count == 0) return null;
        var parts = rescan.GroupBy(r => r.Reason).Select(g => $"{string.Join(", ", g.Select(r => r.Title).Distinct())} ({g.Key})");
        return "Scanned again: " + string.Join("; ", parts) + ".";
    }
}
