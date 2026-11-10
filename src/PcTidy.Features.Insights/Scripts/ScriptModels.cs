using PcTidy.Core.Model;

namespace PcTidy.Features.Insights.Scripts;

public enum ScriptLanguage { PowerShell, Batch, RegFile, VbScript, JScript }

/// <summary>Research verdict for one change (same words as the Tweaks page). Ordered from best to worst.</summary>
public enum ScriptVerdict { Proven, Situational, Placebo, Unknown, Harmful }

/// <summary>What kind of change a line makes.</summary>
public enum ScriptArea
{
    Registry, Service, BootSettings, Network, Firewall, StoreApp, ScheduledTask, PowerPlan,
    Download, Defender, HostsFile, HiddenCode, ExecutionPolicy, Files, Other,
    /// <summary>Not a change but how the script behaves: an opaque score, an "apply all" step, a late restore point, SmartScreen coaching.</summary>
    WarningSign,
}

/// <summary>The script's overall verdict.</summary>
public enum ScriptOverall
{
    /// <summary>Nothing PC Tidy recognises (which is not the same as safe).</summary>
    NothingRecognised,
    /// <summary>Only known, documented settings.</summary>
    KnownSettings,
    /// <summary>Mostly changes that don't help (placebo), nothing harmful.</summary>
    MostlyPlacebo,
    /// <summary>Changes PC Tidy can't vouch for, nothing known to be harmful.</summary>
    Unclear,
    /// <summary>Harmful changes or a red flag (download-and-run, Defender exclusions, hidden code).</summary>
    Dangerous,
}

/// <summary>One change the script would make, read from its text.</summary>
public sealed record ScriptFinding
{
    /// <summary>1-based line in the file (the first line of a joined continuation).</summary>
    public required int Line { get; init; }
    public required ScriptArea Area { get; init; }
    /// <summary>Plain English: what it changes ("Sets HKLM\…\SystemResponsiveness to 0").</summary>
    public required string What { get; init; }
    public required ScriptVerdict Verdict { get; init; }
    /// <summary>Why the verdict (from the research), plain English.</summary>
    public required string Why { get; init; }
    /// <summary>The source line, trimmed and shortened (shown as code, never run).</summary>
    public required string Evidence { get; init; }
    /// <summary>Download-and-run, Defender exclusion or switch-off, hidden code: shown in red at the top.</summary>
    public bool RedFlag { get; init; }
    /// <summary>Tweak or bad-tweak check id this matched, when any.</summary>
    public string? MatchedId { get; init; }
    /// <summary>Registry details for registry changes (hive/key/value/data as the script writes them).</summary>
    public RegistryValueSpec? Registry { get; init; }
    /// <summary>True when the line was inside an -EncodedCommand payload PC Tidy decoded (as text).</summary>
    public bool FromDecodedPayload { get; init; }
}

/// <summary>Everything PC Tidy read from a script. The file is only ever read as text.</summary>
public sealed record ScriptReport
{
    public required string FileName { get; init; }
    public required ScriptLanguage Language { get; init; }
    public required IReadOnlyList<ScriptFinding> Findings { get; init; }
    public required ScriptOverall Overall { get; init; }
    /// <summary>One calm sentence for the top of the report.</summary>
    public required string Headline { get; init; }
    /// <summary>What to do next, plain English.</summary>
    public required string Advice { get; init; }
    public int LinesRead { get; init; }
    /// <summary>The file was longer than PC Tidy reads; only the start was checked.</summary>
    public bool Truncated { get; init; }

    public IEnumerable<ScriptFinding> RedFlags => Findings.Where(f => f.RedFlag);
    public int Count(ScriptVerdict v) => Findings.Count(f => f.Verdict == v);
}
