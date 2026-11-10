using PcTidy.Core;

namespace PcTidy.Features.Learning.Sources;

/// <summary>
/// Which paths learning may remember. Path history is private: OneDrive (and other cloud) folders are left out unless
/// the user allowed that folder in Settings, and PC Tidy's own data folder (quarantine, runs) is never learned from.
/// </summary>
public sealed class PrivacyFilter
{
    private readonly IReadOnlyList<string> _cloudRoots;
    private readonly IReadOnlyList<string> _allowed;
    private readonly string _dataRoot;

    public PrivacyFilter(IKnownLocations locations, AppSettings settings)
    {
        _cloudRoots = locations.CloudRoots;
        _allowed = settings.AllowedAskFirstFolders.ToList();
        _dataRoot = locations.DataRoot;
    }

    public bool MayRemember(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            if (PathUtil.IsUnder(path, _dataRoot)) return false;
            if (_cloudRoots.Any(root => PathUtil.IsUnder(path, root)))
                return _allowed.Any(a => PathUtil.IsUnder(path, a));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
