namespace PcTidy.Setup.Install;

/// <summary>
/// The user's own part of an install, done with the user's normal rights after the admin part succeeded:
/// a spare copy of PC Tidy Rescue in the data folder (it survives an uninstall), and removal of an older per-user copy
/// of PC Tidy (version 1 installed itself in %LOCALAPPDATA%\Programs\PC Tidy). That old copy lives in a folder any
/// program can change, so it is removed: its files (only those its install.json lists), its shortcuts and its HKCU
/// "Installed apps" entry. The data folder is shared and stays.
/// </summary>
public sealed class UserSetup
{
    private readonly InstallEnvironment _env;
    private readonly Registration _registration;

    public UserSetup(InstallEnvironment env)
    {
        _env = env;
        _registration = new Registration(env.Registry);
    }

    public IReadOnlyList<string> AfterInstall()
    {
        var notes = new List<string>();
        CopyRescueToDataFolder(notes);
        RemoveLegacyInstall(notes);
        return notes;
    }

    /// <summary>A copy of Rescue in the data folder survives an uninstall (the data folder is kept by default).</summary>
    private void CopyRescueToDataFolder(List<string> notes)
    {
        if (!File.Exists(_env.RescuePath)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_env.DataRescuePath)!);
            File.Copy(_env.RescuePath, _env.DataRescuePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add("A spare copy of PC Tidy Rescue couldn't be put in the data folder: " + ex.Message);
        }
    }

    private void RemoveLegacyInstall(List<string> notes)
    {
        ExistingInstall? legacy;
        try { legacy = _registration.FindLegacy(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return; }
        if (legacy is null) return;

        var dir = legacy.InstallDir;
        if (!Path.IsPathFullyQualified(dir) || FolderSafety.Same(dir, _env.InstallDir)) { _registration.RemoveLegacyEntry(); return; }

        var leftBehind = new List<string>();
        var manifest = InstallManifest.Load(Path.Combine(dir, Product.ManifestFile));
        Uninstaller.RemoveShortcuts(manifest,
            new[]
            {
                Path.Combine(_env.UserStartMenuFolder, Product.Name + ".lnk"), Path.Combine(_env.UserStartMenuFolder, Product.RescueName + ".lnk"),
                Path.Combine(_env.UserDesktop, Product.Name + ".lnk"),
            },
            _env.UserStartMenuFolder, dir, leftBehind);

        if (Directory.Exists(dir) && (File.Exists(Path.Combine(dir, Product.ManifestFile)) || File.Exists(Path.Combine(dir, Product.AppExe))))
        {
            var files = (manifest?.Files ?? Array.Empty<string>())
                .Concat(new[] { Product.AppExe, Product.RescueExe, Product.UninstallerExe, Product.ManifestFile })
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in files)
            {
                var path = Path.GetFullPath(Path.Combine(dir, relative));
                if (!FolderSafety.IsSameOrUnder(path, dir) || !File.Exists(path) || FolderSafety.GoesThroughLink(path, dir)) continue;
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { leftBehind.Add(path); }
            }
            Folders.RemoveEmptyTree(dir);
        }

        try { _registration.RemoveLegacyEntry(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { leftBehind.Add("its entry in Installed apps"); }
        notes.Add(leftBehind.Count == 0
            ? $"Removed the older copy of PC Tidy from {dir}. Your data folder wasn't touched."
            : $"Removed most of the older copy of PC Tidy from {dir}. Still there (probably in use): {string.Join(", ", leftBehind.Take(5))}");
    }
}
