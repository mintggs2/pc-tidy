using PcTidy.Core;
using PcTidy.Core.Model;

namespace PcTidy.Features.Search.Favorites;

/// <summary>
/// PC Tidy's own Favorites: folders you go to often, in your order. Kept in PC Tidy's settings (never in Explorer's
/// Quick access file). Pinning to Quick access is a separate, explicit change (<see cref="PinPlan"/>) that goes
/// through the normal confirm → run → undo flow.
/// </summary>
public sealed class FavoritesList
{
    public const int MaxFavorites = 100;

    private readonly AppSettings _settings;
    private readonly SearchSettings _state;

    public FavoritesList(AppSettings settings)
    {
        _settings = settings;
        _state = SearchSettings.Load(settings);
    }

    public IReadOnlyList<FavoriteFolder> Items => _state.Favorites;

    public event Action? Changed;

    /// <summary>Adds a folder (a file's folder if given a file). Returns false if it's already there or the list is full.</summary>
    public bool Add(string path, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = PathUtil.Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (_state.Favorites.Count >= MaxFavorites || IndexOf(full) >= 0) return false;
        _state.Favorites.Add(new FavoriteFolder(full, string.IsNullOrWhiteSpace(label) ? DefaultLabel(full) : label.Trim()));
        Save();
        return true;
    }

    public bool Remove(string path)
    {
        int i = IndexOf(path);
        if (i < 0) return false;
        _state.Favorites.RemoveAt(i);
        Save();
        return true;
    }

    /// <summary>Moves a favorite to a new position (clamped to the list).</summary>
    public bool Move(int from, int to)
    {
        var list = _state.Favorites;
        if (from < 0 || from >= list.Count) return false;
        to = Math.Clamp(to, 0, list.Count - 1);
        if (from == to) return false;
        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
        Save();
        return true;
    }

    public bool Rename(string path, string label)
    {
        int i = IndexOf(path);
        if (i < 0 || string.IsNullOrWhiteSpace(label)) return false;
        _state.Favorites[i] = _state.Favorites[i] with { Label = label.Trim() };
        Save();
        return true;
    }

    public bool Contains(string path) => IndexOf(path) >= 0;

    /// <summary>
    /// "Pin to Quick access": one plan item using the shell's own pin verb, only built when the user clicks it.
    /// It goes through the flow host (confirm, journal, undo = unpin) like any other change.
    /// </summary>
    public static ActionPlan PinPlan(string folder) => new()
    {
        FeatureId = SearchIds.FeatureId,
        Title = "Pin to Quick access",
        Items = new[]
        {
            new PlanItem
            {
                Kind = ActionKind.QuickAccessPin,
                FeatureId = SearchIds.FeatureId,
                SourcePath = folder,
                IsDirectory = true,
                Description = $"Pin '{DefaultLabel(folder)}' to Quick access in File Explorer. Undo unpins it.",
            },
        },
    };

    /// <summary>"Photos" for D:\Photos, "D:\" for a drive.</summary>
    public static string DefaultLabel(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    private int IndexOf(string path)
    {
        string full;
        try { full = PathUtil.Normalize(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return -1; }
        return _state.Favorites.FindIndex(f => string.Equals(f.Path, full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Re-reads the rest of the section's settings first, so other pages' edits aren't overwritten.</summary>
    private void Save()
    {
        var latest = SearchSettings.Load(_settings);
        latest.Favorites = _state.Favorites.ToList();
        latest.Store(_settings);
        Changed?.Invoke();
    }
}
