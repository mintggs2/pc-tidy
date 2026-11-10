namespace PcTidy.Features.Search.Structure;

/// <summary>The four folder layouts PC Tidy can suggest (Community research T8 §H).</summary>
public enum StructurePreset { Simple, ByDate, ProjectsAndAreas, Numbered }

/// <summary>One folder of a proposed tree, relative to the drive (or Documents on the Windows drive).</summary>
public sealed record PresetFolder(string RelativePath, string Purpose);

/// <summary>A layout: its tree and the rule that says where an existing top-level folder would go.</summary>
public sealed record PresetDefinition(StructurePreset Preset, string Title, string Summary, IReadOnlyList<PresetFolder> Tree);

/// <summary>Where a folder would go under a preset, or null with the reason it stays.</summary>
public sealed record Placement(string? RelativeFolder, string Reason);

/// <summary>
/// The presets. Shallow on purpose (depth ≤ 3, research: "depth ≤ 3, ≤ 10-ish items per level"). They only decide
/// WHERE whole folders go; nothing inside a folder is renamed, sorted or opened. Johnny.Decimal gets a generic empty
/// starter (areas and categories); IDs inside categories are left to you, as the system intends.
/// </summary>
public static class StructurePresets
{
    private const int StaleDays = 365;
    private const int ParaStaleDays = 90;

    public static readonly IReadOnlyList<PresetDefinition> All = new[]
    {
        new PresetDefinition(StructurePreset.Simple, "Simple",
            "A few big category folders: Media, Documents, Projects, Games, Software and Archive.",
            new PresetFolder[]
            {
                new(@"Media\Photos", "Photo folders"), new(@"Media\Videos", "Video folders"), new(@"Media\Music", "Music folders"),
                new("Documents", "Papers, scans, office files"), new("Projects", "Things you're working on"), new("Games", "Game files you keep (not installed games)"),
                new("Software", "Installers you want to keep"), new("Archive", "Old things you rarely open"),
            }),
        new PresetDefinition(StructurePreset.ByDate, "By date",
            "Photos by year and an Archive by year (ISO 2026, never \"November\"), for people who think in dates.",
            new PresetFolder[] { new("Photos", "Photo and video folders, by year"), new("Archive", "Anything untouched for a year, by year") }),
        new PresetDefinition(StructurePreset.ProjectsAndAreas, "Projects & Areas",
            "PARA-lite: Inbox, Projects (with an end), Areas (ongoing), Resources (reference) and Archive.",
            new PresetFolder[]
            {
                new("0 Inbox", "New things, sorted later"), new("1 Projects", "Work with a finish line"), new("2 Areas", "Ongoing parts of life: money, home, health"),
                new("3 Resources", "Reference: media, manuals, installers"), new("4 Archive", "Finished or inactive, by year"),
            }),
        new PresetDefinition(StructurePreset.Numbered, "Numbered",
            "Johnny.Decimal-style areas (10-19 …) and categories (11 …). You give the IDs inside each category.",
            new PresetFolder[]
            {
                new(@"10-19 Life admin\11 Money", "Bank, bills, taxes, insurance"), new(@"10-19 Life admin\12 Home", "House, car, family"),
                new(@"10-19 Life admin\13 Health", "Health and medical"), new(@"10-19 Life admin\14 Papers", "Other documents"),
                new(@"20-29 Work\21 Projects", "Current work"), new(@"20-29 Work\22 Reference", "Work reference"),
                new(@"30-39 Media\31 Photos", "Photos"), new(@"30-39 Media\32 Videos", "Videos"), new(@"30-39 Media\33 Music", "Music"),
                new(@"40-49 Software & games\41 Installers", "Installers you keep"), new(@"40-49 Software & games\42 Games", "Game files you keep"),
                new(@"90-99 Archive\91 Old files", "Old things you rarely open"),
            }),
    };

    public static PresetDefinition Get(StructurePreset preset) => All.First(p => p.Preset == preset);

    /// <summary>The top-level folder names of a preset (folders already named like this are part of the structure).</summary>
    public static IReadOnlySet<string> TopLevelNames(StructurePreset preset)
        => Get(preset).Tree.Select(f => f.RelativePath.Split('\\')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] MoneyWords = { "money", "finance", "tax", "bank", "bill", "invoice", "receipt", "insurance", "salary", "budget" };
    private static readonly string[] HomeWords = { "home", "house", "car", "family", "kids", "school", "travel" };
    private static readonly string[] HealthWords = { "health", "medical", "doctor", "fitness" };

    /// <param name="rootIsDocuments">True on the Windows drive, where the tree lives inside Documents.</param>
    public static Placement Place(StructurePreset preset, FolderProfile f, DateTime nowUtc, bool rootIsDocuments)
    {
        int? year = f.NewestUtc?.ToLocalTime().Year;
        double ageDays = f.NewestUtc is { } n ? (nowUtc - n).TotalDays : 0;
        if (f.Kind == FolderKind.Empty) return new(null, "It's empty, so there's nothing to place.");

        switch (preset)
        {
            case StructurePreset.Simple:
                return f.Kind switch
                {
                    FolderKind.Photos => new(@"Media\Photos", $"{f.KindReason}, so it goes with your photos."),
                    FolderKind.Videos => new(@"Media\Videos", $"{f.KindReason}, so it goes with your videos."),
                    FolderKind.Music => new(@"Media\Music", $"{f.KindReason}, so it goes with your music."),
                    FolderKind.Documents when rootIsDocuments => new(null, "Already documents, already in Documents."),
                    FolderKind.Documents => new("Documents", $"{f.KindReason}, so it goes with your documents."),
                    FolderKind.Projects => new("Projects", $"{f.KindReason}, so it goes with your projects."),
                    FolderKind.Games => new("Games", $"{f.KindReason}, so it goes with your games."),
                    FolderKind.Installers => new("Software", $"{f.KindReason}, so it goes with your installers."),
                    FolderKind.Archives or FolderKind.Backups => new("Archive", $"{f.KindReason}, so it goes in the Archive."),
                    _ when ageDays >= StaleDays => new("Archive", $"Nothing in it changed for over a year, so it goes in the Archive."),
                    _ => new(null, $"{f.KindReason} that you still use, so there's no single right place: it stays."),
                };

            case StructurePreset.ByDate:
                if (year is null) return new(null, "No dated files inside, so it stays.");
                if (f.Kind is FolderKind.Photos or FolderKind.Videos)
                    return new($@"Photos\{year}", $"{f.KindReason}, newest from {year}, so it goes under Photos\\{year}.");
                if (ageDays >= StaleDays)
                    return new($@"Archive\{year}", $"Nothing in it changed since {year}, so it goes under Archive\\{year}.");
                return new(null, "You used it in the last year, so it stays where it is.");

            case StructurePreset.ProjectsAndAreas:
                if (ageDays >= ParaStaleDays && year is not null)
                    return new($@"4 Archive\{year}", $"Nothing in it changed for {Math.Floor(ageDays):N0} days, so it looks finished: Archive\\{year}.");
                if (HasWord(f.Name, MoneyWords) || HasWord(f.Name, HomeWords) || HasWord(f.Name, HealthWords))
                    return new("2 Areas", "Its name sounds like an ongoing part of life, so it goes under Areas.");
                if (f.Kind is FolderKind.Photos or FolderKind.Videos or FolderKind.Music or FolderKind.Installers or FolderKind.Archives or FolderKind.Backups or FolderKind.Games)
                    return new("3 Resources", $"{f.KindReason}: reference material, so it goes under Resources.");
                return new("1 Projects", "You've used it recently, so it's suggested as a project. Only tick it if that fits.");

            default: // Numbered
                if (HasWord(f.Name, MoneyWords)) return new(@"10-19 Life admin\11 Money", "Its name sounds like money matters.");
                if (HasWord(f.Name, HomeWords)) return new(@"10-19 Life admin\12 Home", "Its name sounds like home and family.");
                if (HasWord(f.Name, HealthWords)) return new(@"10-19 Life admin\13 Health", "Its name sounds like health.");
                return f.Kind switch
                {
                    FolderKind.Photos => new(@"30-39 Media\31 Photos", $"{f.KindReason}."),
                    FolderKind.Videos => new(@"30-39 Media\32 Videos", $"{f.KindReason}."),
                    FolderKind.Music => new(@"30-39 Media\33 Music", $"{f.KindReason}."),
                    FolderKind.Installers => new(@"40-49 Software & games\41 Installers", $"{f.KindReason}."),
                    FolderKind.Games => new(@"40-49 Software & games\42 Games", $"{f.KindReason}."),
                    FolderKind.Archives or FolderKind.Backups => new(@"90-99 Archive\91 Old files", $"{f.KindReason}."),
                    _ when ageDays >= StaleDays => new(@"90-99 Archive\91 Old files", "Nothing in it changed for over a year."),
                    FolderKind.Documents => new(@"10-19 Life admin\14 Papers", $"{f.KindReason}."),
                    FolderKind.Projects => new(@"20-29 Work\21 Projects", $"{f.KindReason}."),
                    _ => new(null, $"{f.KindReason}: no clear category, so it stays. Give it a number yourself if you like."),
                };
        }
    }

    /// <summary>Whole words only, singular or plural ("Taxes 2025" yes, "Cards" is not "car").</summary>
    private static bool HasWord(string name, string[] words)
    {
        var parts = name.ToLowerInvariant().Split(NonLetters(name), StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => words.Any(w => p == w || p == w + "s" || p == w + "es"));
    }

    private static char[] NonLetters(string name) => name.Where(c => !char.IsLetter(c)).Distinct().ToArray() is { Length: > 0 } seps ? seps : new[] { ' ' };
}
