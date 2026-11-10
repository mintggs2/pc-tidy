using PcTidy.Core;
using PcTidy.Features.Organize.Rules;

namespace PcTidy.Features.Organize;

/// <summary>Everything the Organize section remembers. Stored with <c>AppSettings.SetFeature("organize", …)</c>; the shell saves.</summary>
public sealed class OrganizeSettings
{
    public const string FeatureKey = "organize";
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>Extra folders the user picked (Downloads and Desktop are always offered).</summary>
    public List<string> ExtraFolders { get; set; } = new();
    public List<OrganizeRule> Rules { get; set; } = DefaultRules.Create();
    /// <summary>Year/Month option: Pictures\2026\2026-11. Off by default.</summary>
    public bool UseDateFolders { get; set; }
    /// <summary>Move whole loose folders as one item (never split). Off by default.</summary>
    public bool MoveWholeFolders { get; set; }
    public WatchSettings Watch { get; set; } = new();
    /// <summary>Files PC Tidy already sorted (see <see cref="OrganizeLedger"/>).</summary>
    public List<LedgerEntry> Ledger { get; set; } = new();

    public static OrganizeSettings Load(AppSettings settings)
    {
        OrganizeSettings? stored = null;
        try { stored = settings.GetFeature<OrganizeSettings>(FeatureKey); }
        catch (System.Text.Json.JsonException) { /* corrupt blob: start from defaults, never crash */ }
        if (stored is null) return new OrganizeSettings();
        stored.Rules?.RemoveAll(r => r is null);
        if (stored.Rules is null || stored.Rules.Count == 0) stored.Rules = DefaultRules.Create();
        // Every rule needs its own id (the editor, the ledger and an import's rule-by-rule compare all go by it):
        // a missing or repeated id gets a fresh one; the first rule with an id keeps it.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in stored.Rules)
            if (string.IsNullOrEmpty(rule.Id) || !seen.Add(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");
        stored.ExtraFolders ??= new();
        stored.Watch ??= new();
        stored.Ledger ??= new();
        stored.Version = CurrentVersion;
        return stored;
    }

    /// <summary>Puts the settings into <paramref name="settings"/>. Saving to disk is the shell's job.</summary>
    public void Store(AppSettings settings) => settings.SetFeature(FeatureKey, this);

    public OrganizeSettings Clone() => new()
    {
        Version = Version,
        ExtraFolders = ExtraFolders.ToList(),
        Rules = Rules.Select(r => r.Clone()).ToList(),
        UseDateFolders = UseDateFolders,
        MoveWholeFolders = MoveWholeFolders,
        Watch = new WatchSettings { Enabled = Watch.Enabled, Folders = Watch.Folders.ToList(), RescanMinutes = Watch.RescanMinutes },
        Ledger = Ledger.Select(e => new LedgerEntry { Path = e.Path, RuleId = e.RuleId, Size = e.Size, Utc = e.Utc }).ToList(),
    };
}

public sealed class WatchSettings
{
    /// <summary>Off by default (Plan §9).</summary>
    public bool Enabled { get; set; }
    /// <summary>Folders to watch. Empty = Downloads.</summary>
    public List<string> Folders { get; set; } = new();
    /// <summary>Full re-check interval; clamped to 15–30 minutes.</summary>
    public int RescanMinutes { get; set; } = 20;
}
