using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.AiChats.Archive;
using PcTidy.Features.AiChats.Local;
using PcTidy.Features.AiChats.Model;

namespace PcTidy.Features.AiChats;

/// <summary>Extra scan options (tests, the page): <see cref="ScanContext.Options"/> keys.</summary>
public static class AiChatsOptions
{
    /// <summary>string: archive folder override (else the saved one).</summary>
    public const string ArchiveFolder = "aichats.archiveFolder";
    /// <summary><see cref="IProcessProbe"/> override.</summary>
    public const string ProcessProbe = "aichats.processProbe";
    /// <summary><see cref="TimeProvider"/> override.</summary>
    public const string Clock = "aichats.clock";
}

/// <summary>
/// Local agent chats on this PC (Claude Code and Codex), per project: how much space they take and, once an archive
/// folder is chosen, "archive a copy" (CopyPath: originals stay). Live sessions are shown but can't be ticked.
/// Never part of "Optimize &amp; Organize everything". Read-only.
/// </summary>
public sealed class AiChatsScanner : IFeatureScanner
{
    public const string CleanupAdvice =
        "Claude Code deletes chats older than 30 days by itself (you can change that in Claude Code's settings). Archive a copy to keep them. PC Tidy never changes Claude Code's settings.";

    public string FeatureId => AiChatsIds.FeatureId;
    public string Title => "AI chats";
    public Release Release => Release.V3;
    public bool IncludedInOptimizeAll => false;

    public async Task<ScanResult> ScanAsync(ScanContext ctx, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        progress?.Report(new ScanProgress("Looking for Claude Code and Codex chats…", 0));
        var sessions = await AgentChatInventory.ListAsync(ctx.Locations, ct).ConfigureAwait(false);
        var clock = ctx.Options.TryGetValue(AiChatsOptions.Clock, out var c) && c is TimeProvider tp ? tp : TimeProvider.System;
        var probe = ctx.Options.TryGetValue(AiChatsOptions.ProcessProbe, out var p) && p is IProcessProbe pp ? pp : null;
        var live = new LiveSessionDetector(probe, clock).Detect(ctx.Locations);
        var archive = ctx.Options.TryGetValue(AiChatsOptions.ArchiveFolder, out var a) && a is string s && s.Length > 0
            ? s : AiChatsSettings.From(ctx.Settings).ArchiveFolder;

        var groups = new List<ScanGroup>();
        foreach (var source in new[] { ChatSource.ClaudeCode, ChatSource.Codex })
        {
            var mine = sessions.Where(x => x.Source == source).ToList();
            if (mine.Count == 0) continue;
            var items = mine.GroupBy(x => x.ProjectName, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Sum(x => x.TotalBytes))
                .Select(g => ProjectItem(source, g.Key, g.ToList(), live, archive, ctx, ct))
                .ToList();
            groups.Add(new ScanGroup
            {
                Id = "aichats." + ChatSources.Key(source),
                Title = ChatSources.Display(source) + " chats",
                Description = source == ChatSource.ClaudeCode
                    ? "Chat transcripts in your .claude folder, by project. " + CleanupAdvice
                    : "Codex session files in your .codex folder, by project. Older ones are compressed (.jsonl.zst).",
                Badge = Badge.Safe,
                Items = items,
            });
        }

        var notes = new List<string>();
        if (sessions.Count == 0) notes.Add("No Claude Code or Codex chats were found on this PC.");
        else if (archive is null) notes.Add("Choose an archive folder on the AI chats page to keep copies of these chats.");
        var liveCount = sessions.Count(live.IsLive);
        if (liveCount > 0) notes.Add($"{liveCount} chat{(liveCount == 1 ? " is" : "s are")} in use right now and won't be touched.");
        progress?.Report(new ScanProgress("Done", 1, sessions.Count, sessions.Sum(x => x.TotalBytes)));
        return new ScanResult { FeatureId = FeatureId, Title = Title, Depth = ctx.Depth, Duration = sw.Elapsed, Groups = groups, Notes = notes };
    }

    private static ScanItem ProjectItem(ChatSource source, string project, IReadOnlyList<AgentSession> sessions, LiveSessions live,
        string? archive, ScanContext ctx, CancellationToken ct)
    {
        var idle = sessions.Where(s => !live.IsLive(s)).ToList();
        var bytes = sessions.Sum(s => s.TotalBytes);
        var newest = sessions.Max(s => s.LastWriteUtc);
        var oldest = sessions.Min(s => s.LastWriteUtc);
        var byMonth = sessions.GroupBy(s => s.LastWriteUtc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))
            .OrderByDescending(g => g.Key)
            .ToDictionary(g => g.Key, g => $"{g.Count()} chat{(g.Count() == 1 ? "" : "s")}, {Sizes.Format(g.Sum(x => x.TotalBytes))}");

        string? disabled = null;
        IReadOnlyList<PlanItem> actions = Array.Empty<PlanItem>();
        if (idle.Count == 0) disabled = "All of these chats are in use right now. Try again when the agent is closed.";
        else if (archive is null) disabled = "Choose an archive folder on the AI chats page first.";
        else
        {
            var plan = AgentArchivePlanner.PlanCopy(idle, archive, live, null, ct);
            actions = plan.Items;
            if (actions.Count == 0) disabled = "Already archived: every chat here has an identical copy in your archive folder.";
            else if (actions.Any(i => i.SourcePath is { } src && ctx.Safety.ClassifyPath(src).Protection == PathProtection.Never))
            {
                disabled = "PC Tidy's safety rules don't allow copying these files.";
                actions = Array.Empty<PlanItem>();
            }
        }

        // The row's size is what ticking it copies (when it can be ticked), so the totals match the plan.
        var copyBytes = actions.Where(i => i.Kind == ActionKind.CopyPath).Sum(i => i.ExpectedSize);
        var copied = archive is null ? 0 : sessions.Count(s => AgentArchivePlanner.VerifyCopy(archive, s, ct).Verified);
        return new ScanItem
        {
            Title = project,
            Detail = $"{sessions.Count} chat{(sessions.Count == 1 ? "" : "s")} · {oldest:yyyy-MM-dd} to {newest:yyyy-MM-dd} · {Sizes.Format(bytes)} on disk",
            Reason = "Archiving copies these chats into your archive folder and checks each copy. The originals stay where they are, so resuming them still works.",
            Badge = Badge.Safe,
            DefaultSelected = false,
            SizeBytes = actions.Count > 0 ? copyBytes : bytes,
            SizeOnDiskBytes = actions.Count > 0 ? copyBytes : bytes,
            FileCount = sessions.Count + sessions.Sum(s => s.SidecarTranscripts.Count),
            OwnerApp = ChatSources.Display(source),
            LastModifiedUtc = newest,
            CurrentState = copied > 0 ? $"{copied} of {sessions.Count} archived" : null,
            Details = byMonth,
            Actions = actions,
            DisabledReason = disabled,
        };
    }
}
