using PcTidy.Core.Model;

namespace PcTidy.Platform;

/// <summary>
/// .reg backups of whole keys before they are deleted, using Windows' own reg.exe (exact for every value type).
/// Undo imports the file only after checking it lives in PC Tidy's backup folder and touches nothing but the deleted key,
/// so a tampered file can't be used to write elsewhere (the import may run elevated).
/// </summary>
internal static class RegFileBackup
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private static string RegExe => Path.Combine(Environment.SystemDirectory, "reg.exe");

    public static string BackupFolder(string dataRoot) => Path.Combine(DataPaths.Backups(dataRoot), "reg");

    public static string PathFor(string dataRoot, string runId, int index)
    {
        var safeRun = string.Concat(runId.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
        return Path.Combine(BackupFolder(dataRoot), $"{safeRun}-{index}.reg");
    }

    public static string RegKeyName(RegistryHive hive, string keyPath) => $"{RegistryAccess.HiveShort(hive)}\\{keyPath}";

    public static async Task<string?> ExportAsync(RegistryHive hive, bool view64, string keyPath, string file, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var result = await ProcessRunner.RunAsync(RegExe,
            new[] { "export", RegKeyName(hive, keyPath), file, "/y", view64 ? "/reg:64" : "/reg:32" }, Timeout, null, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) return "Windows couldn't back up this registry key, so it wasn't deleted.";
        return IsValidBackupFor(file, hive, keyPath, out var why) ? null : why;
    }

    /// <summary>
    /// Undo: a private copy of the backup is made in <paramref name="privateFolder"/> (in the elevated helper: its
    /// admin-only folder), and only that copy is checked and imported, so nobody can swap the file between the
    /// check and the import. The copy is removed afterwards.
    /// </summary>
    public static async Task<string?> ImportVerifiedCopyAsync(string file, string privateFolder, RegistryHive hive, string keyPath, bool view64, CancellationToken ct)
    {
        string copy;
        try
        {
            Directory.CreateDirectory(privateFolder);
            copy = Path.Combine(privateFolder, $"import-{Guid.NewGuid():N}.reg");
            File.Copy(file, copy, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "The registry backup couldn't be read.";
        }
        try
        {
            if (!IsValidBackupFor(copy, hive, keyPath, out var why)) return why;
            return await ImportAsync(copy, view64, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task<string?> ImportAsync(string file, bool view64, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(RegExe, new[] { "import", file, view64 ? "/reg:64" : "/reg:32" }, Timeout, null, ct).ConfigureAwait(false);
        return result.ExitCode == 0 ? null : "Windows couldn't put the registry key back from its backup.";
    }

    /// <summary>Checks a .reg file only adds keys at or under the given key (no deletions, no other keys).</summary>
    public static bool IsValidBackupFor(string file, RegistryHive hive, string keyPath, out string why)
    {
        why = "";
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length == 0) { why = "The registry backup is missing."; return false; }
            var root = $"{RegistryAccess.HiveLong(hive)}\\{keyPath}";
            var sawHeader = false;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (!line.StartsWith('[')) continue;
                if (line.StartsWith("[-", StringComparison.Ordinal)) { why = "The registry backup contains deletions, so PC Tidy won't use it."; return false; }
                var name = line.TrimStart('[').TrimEnd(']');
                if (!(name.Equals(root, StringComparison.OrdinalIgnoreCase) || name.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)))
                {
                    why = "The registry backup touches other keys, so PC Tidy won't use it.";
                    return false;
                }
                sawHeader = true;
            }
            if (!sawHeader) why = "The registry backup is empty.";
            return sawHeader;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = "The registry backup couldn't be read.";
            return false;
        }
    }
}
