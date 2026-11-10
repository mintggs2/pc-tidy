namespace PcTidy.Features.Boost;

/// <summary>
/// What the user chose for Game boost. Stored with <c>AppSettings.SetFeature("boost", …)</c>; the shell saves.
/// Everything is off until the user switches it on: turning a game on IS the approval for boosting it.
/// </summary>
public sealed class BoostSettings
{
    public const string FeatureKey = "boost";
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>Master switch. Off by default.</summary>
    public bool Enabled { get; set; }
    /// <summary>Games the user turned boost on for (by "launcher:id" key). Nothing else is ever boosted.</summary>
    public List<BoostGameChoice> Games { get; set; } = new();
    /// <summary>Background apps to close politely while a game runs. Only these, never anything else.</summary>
    public List<BoostAppChoice> AppsToClose { get; set; } = new();
    /// <summary>Pause OneDrive (OneDrive /shutdown, then /background afterwards). Off until the user ticks it.</summary>
    public bool PauseOneDrive { get; set; }
    /// <summary>Open the closed apps again when the game exits.</summary>
    public bool ReopenAppsAfter { get; set; } = true;
    /// <summary>How often the process list is read, in seconds (clamped to 3–5).</summary>
    public int PollSeconds { get; set; } = 4;

    public IReadOnlySet<string> EnabledGameKeys()
        => Games.Where(g => g.Enabled).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool IsGameEnabled(string key) => Games.Any(g => g.Enabled && g.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    public void SetGame(string key, string name, bool enabled)
    {
        var existing = Games.FirstOrDefault(g => g.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is null) Games.Add(new BoostGameChoice { Key = key, Name = name, Enabled = enabled });
        else { existing.Enabled = enabled; existing.Name = name; }
    }

    public static TimeSpan ClampPoll(int seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 3, 5));

    public static BoostSettings Load(AppSettings settings)
    {
        BoostSettings? stored = null;
        try { stored = settings.GetFeature<BoostSettings>(FeatureKey); }
        catch (System.Text.Json.JsonException) { /* corrupt blob: start from defaults, never crash */ }
        if (stored is null) return new BoostSettings();
        stored.Games ??= new();
        stored.AppsToClose ??= new();
        stored.Games.RemoveAll(g => string.IsNullOrWhiteSpace(g?.Key));
        stored.AppsToClose.RemoveAll(a => string.IsNullOrWhiteSpace(a?.ProcessName));
        stored.Version = CurrentVersion;
        return stored;
    }

    /// <summary>Puts the settings into <paramref name="settings"/>. Saving to disk is the shell's job.</summary>
    public void Store(AppSettings settings) => settings.SetFeature(FeatureKey, this);
}

public sealed class BoostGameChoice
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class BoostAppChoice
{
    /// <summary>Process name without ".exe", e.g. "Discord".</summary>
    public string ProcessName { get; set; } = "";
    /// <summary>Friendly name shown in the list.</summary>
    public string DisplayName { get; set; } = "";
}
