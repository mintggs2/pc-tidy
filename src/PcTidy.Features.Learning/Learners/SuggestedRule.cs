using System.Globalization;
using PcTidy.Core.Model;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Learning.Learners;

/// <summary>
/// A rule suggested from the user's own moves, as plain data any section can read (the "OrganizeRule-compatible
/// description"). <see cref="CreateRule"/> builds the exact <see cref="OrganizeRule"/> Organize imports.
/// A suggested rule is never automatic: it always goes through Organize's preview and confirm.
/// </summary>
public sealed record SuggestedRule
{
    /// <summary>Learned item id ("rule:extension:blend&gt;d:\3d").</summary>
    public required string Id { get; init; }
    public required MoveFeature Feature { get; init; }
    public required string Value { get; init; }
    /// <summary>Only files in this folder (null = any folder Organize sorts).</summary>
    public string? SourceFolder { get; init; }
    /// <summary>Absolute destination folder.</summary>
    public required string Destination { get; init; }
    /// <summary>Rule name (the user may have edited it).</summary>
    public required string Name { get; init; }
    public required double Moves { get; init; }
    public required double TotalMoves { get; init; }
    public required int Days { get; init; }
    public required DateTime FirstUtc { get; init; }
    public required IReadOnlyList<string> Examples { get; init; }

    /// <summary>"Move .blend files from Downloads to D:\3D".</summary>
    public string Title => $"Move {MoveFeatures.Describe(Feature, Value)}{(SourceFolder is null || Feature == MoveFeature.SourceFolder ? "" : " from " + MoveFeatures.FolderName(SourceFolder))} to {Destination}";

    /// <summary>"You moved 7 of 8 of these there, on 3 different days."</summary>
    public string Reason
    {
        get
        {
            var moved = Count(Moves);
            var total = Count(TotalMoves);
            var share = moved == total ? $"all {moved}" : $"{moved} of {total}";
            return $"You moved {share} of these there yourself, on {Days} different days.";
        }
    }

    private static string Count(double weight) => Math.Round(weight).ToString("0", CultureInfo.InvariantCulture);

    public static SuggestedRule From(RuleCandidate c, LearnedNames? edits = null) => new()
    {
        Id = c.Key,
        Feature = c.Feature,
        Value = c.Value,
        SourceFolder = c.Feature == MoveFeature.SourceFolder ? c.Value : c.CommonSource,
        Destination = string.IsNullOrWhiteSpace(edits?.Destination) ? c.Destination : edits!.Destination!,
        Name = string.IsNullOrWhiteSpace(edits?.Name) ? DefaultName(c) : edits!.Name!,
        Moves = c.Moves,
        TotalMoves = c.TotalMoves,
        Days = c.Days,
        FirstUtc = c.FirstUtc,
        Examples = c.Examples,
    };

    private static string DefaultName(RuleCandidate c)
    {
        var what = MoveFeatures.Describe(c.Feature, c.Value);
        return char.ToUpperInvariant(what[0]) + what[1..] + " → " + MoveFeatures.FolderName(c.Destination);
    }

    /// <summary>The Organize rule: same conditions, absolute destination, never overwrite, never automatic.</summary>
    public OrganizeRule CreateRule()
    {
        var conditions = new List<RuleCondition> { Condition() };
        if (SourceFolder is not null && Feature != MoveFeature.SourceFolder)
            conditions.Add(new RuleCondition { Kind = ConditionKind.SourceFolder, Values = { SourceFolder } });
        return new OrganizeRule
        {
            Name = Name,
            Icon = "Lightbulb24",
            Enabled = true,
            IsBuiltIn = false,
            Conditions = ConditionGroup.AllOf(conditions.ToArray()),
            Action = new RuleAction { Kind = RuleActionKind.MoveToFolder, Destination = Destination },
            ClashPolicy = NameClashPolicy.AutoNumber,
            RunAutomatically = false,
            AllowAutomaticQuarantine = false,
        };
    }

    private RuleCondition Condition() => Feature switch
    {
        MoveFeature.Extension => RuleCondition.Ext(Value),
        MoveFeature.NamePattern => RuleCondition.Glob(Value),
        MoveFeature.DownloadSite => new RuleCondition { Kind = ConditionKind.DownloadSite, Values = { Value } },
        MoveFeature.SourceFolder => new RuleCondition { Kind = ConditionKind.SourceFolder, Values = { Value } },
        _ => SizeCondition(Value),
    };

    private static RuleCondition SizeCondition(string bucketId)
    {
        var bucket = MoveFeatures.SizeBuckets.First(b => b.Id == bucketId);
        // The rule's maximum is inclusive; the bucket's is exclusive.
        return new RuleCondition { Kind = ConditionKind.Size, MinBytes = bucket.Min > 0 ? bucket.Min : null, MaxBytes = bucket.Max - 1 };
    }
}

/// <summary>The user's edits to a suggested rule.</summary>
public sealed record LearnedNames(string? Name, string? Destination);
