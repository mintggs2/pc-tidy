using Microsoft.Data.Sqlite;
using PcTidy.App.Shell;
using PcTidy.Core;

namespace PcTidy.App.Portability;

/// <summary>What an import did.</summary>
public sealed record ImportResult(int Applied, bool LearningReplaced, string? BackupPath, string? Problem);

/// <summary>
/// The only code that writes for export/import, and only PC Tidy's own data: the export goes to
/// &lt;DataRoot&gt;\exports\, the backup of the current settings to &lt;DataRoot&gt;\backups\settings-&lt;date&gt;.json, the
/// imported learned data to the learning file (its old copy to backups\ first). Settings themselves are saved
/// through <see cref="ISettingsStore"/> like every other settings change.
/// </summary>
public sealed class SettingsPackageStore
{
    private readonly string _dataRoot;
    private readonly TimeProvider _clock;

    public SettingsPackageStore(string dataRoot, TimeProvider? clock = null)
    {
        _dataRoot = dataRoot;
        _clock = clock ?? TimeProvider.System;
    }

    public string ExportsFolder => Path.Combine(_dataRoot, "exports");
    public string LearningFile => DataPaths.LearnedDb(_dataRoot);

    /// <summary>
    /// Writes "PC Tidy settings yyyy-MM-dd HHmm.pctidy" (never overwriting) and returns its path. The learned data names
    /// folders on this PC, so it goes in only when <paramref name="includeLearning"/> (the user ticked it).
    /// </summary>
    public string Export(AppSettings settings, IReadOnlyList<string> drives, string appVersion, bool includeLearning)
    {
        var now = _clock.GetLocalNow();
        Directory.CreateDirectory(ExportsFolder); // boundary-ok: own data in DataRoot
        var path = PathUtil.NextFreeName(Path.Combine(ExportsFolder, $"PC Tidy settings {now:yyyy-MM-dd HHmm}{SettingsPackage.Extension}"));
        var snapshot = includeLearning && File.Exists(LearningFile) ? SnapshotLearning() : null;
        try
        {
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write)) // boundary-ok: own data in DataRoot
            using (var learning = snapshot is null ? null : new FileStream(snapshot, FileMode.Open, FileAccess.Read))
                SettingsPackage.Write(fs, settings, drives, appVersion, learning, now.UtcDateTime);
            File.Move(tmp, path); // boundary-ok: own data in DataRoot
        }
        finally
        {
            if (snapshot is not null) TryDelete(snapshot);
        }
        return path;
    }

    /// <summary>
    /// A consistent copy of the learning database (SQLite's own backup, so a half-written page can't sneak in), in
    /// the exports folder, deleted again after packing. Null when it can't be read.
    /// </summary>
    private string? SnapshotLearning()
    {
        var tmp = Path.Combine(ExportsFolder, $"learned-{Guid.NewGuid():N}.db");
        try
        {
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = LearningFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = tmp, Pooling = false }.ToString()); // boundary-ok: own data in DataRoot
            source.Open();
            target.Open();
            source.BackupDatabase(target);
            return tmp;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            CrashHandler.Write(ex, "Exporting learned data", terminating: false);
            TryDelete(tmp);
            return null;
        }
    }

    /// <summary>
    /// Applies the ticked changes: first a backup of the current settings, then the changes, then one save. The
    /// learned data (if ticked) replaces the learning file, after keeping a copy of it.
    /// </summary>
    public ImportResult Import(AppSettings settings, ISettingsStore store, IReadOnlyList<ImportChange> changes, LoadedPackage package)
    {
        string? backup;
        try { backup = BackupSettings(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ImportResult(0, false, null, "PC Tidy couldn't keep a copy of your current settings first, so nothing was imported: " + ex.Message);
        }

        int applied = ImportPreview.Apply(settings, changes);
        try { store.Save(settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new ImportResult(applied, false, backup, "The imported settings apply now but couldn't be saved: " + ex.Message);
        }

        bool learning = false;
        string? problem = null;
        if (package.LearningDb is { } db && changes.Any(c => c.IsLearning && c.Include))
        {
            try { ReplaceLearning(db); learning = true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                problem = "Your settings were imported, but what PC Tidy learned couldn't be replaced right now (" + ex.Message + "). Try again after restarting PC Tidy.";
            }
        }
        return new ImportResult(applied, learning, backup, problem);
    }

    /// <summary>backups\settings-yyyyMMdd-HHmmss.json: the settings file as it is now (or the live settings if none yet).</summary>
    public string BackupSettings(AppSettings settings)
    {
        var folder = DataPaths.Backups(_dataRoot);
        Directory.CreateDirectory(folder); // boundary-ok: own data in DataRoot
        var path = PathUtil.NextFreeName(Path.Combine(folder, $"settings-{_clock.GetLocalNow():yyyyMMdd-HHmmss}.json"));
        var current = AppSettings.SettingsPath(_dataRoot);
        if (File.Exists(current)) File.Copy(current, path); // boundary-ok: own data in DataRoot
        else File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(settings, AppSettings.Json)); // boundary-ok: own data in DataRoot
        return path;
    }

    private void ReplaceLearning(byte[] db)
    {
        var folder = Path.GetDirectoryName(LearningFile)!;
        Directory.CreateDirectory(folder); // boundary-ok: own data in DataRoot
        // Pooled connections keep the file open; let them go so the file can be replaced.
        SqliteConnection.ClearAllPools();
        var incoming = LearningFile + ".import";
        File.WriteAllBytes(incoming, db); // boundary-ok: own data in DataRoot
        if (File.Exists(LearningFile))
        {
            var keep = PathUtil.NextFreeName(Path.Combine(DataPaths.Backups(_dataRoot), $"learned-{_clock.GetLocalNow():yyyyMMdd-HHmmss}.db"));
            Directory.CreateDirectory(Path.GetDirectoryName(keep)!); // boundary-ok: own data in DataRoot
            File.Replace(incoming, LearningFile, keep, ignoreMetadataErrors: true); // boundary-ok: own data in DataRoot
        }
        else File.Move(incoming, LearningFile); // boundary-ok: own data in DataRoot
        foreach (var side in new[] { LearningFile + "-wal", LearningFile + "-shm" }) TryDelete(side);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } // boundary-ok: own data in DataRoot
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
