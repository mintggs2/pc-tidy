using System.Globalization;
using PcTidy.Core;
using PcTidy.Features.Drivers.Model;

namespace PcTidy.Features.Drivers.Analysis;

/// <summary>Where PC Tidy keeps driver backups: &lt;DataRoot&gt;\backups\drivers\&lt;yyyy-MM-dd&gt;. Listing only reads.</summary>
public static class BackupFolders
{
    public const string DateFormat = "yyyy-MM-dd";

    public static string Root(string dataRoot) => Path.Combine(DataPaths.Backups(dataRoot), "drivers");

    /// <summary>The folder a backup made on <paramref name="localDate"/> goes to.</summary>
    public static string ForDate(string dataRoot, DateTime localDate)
        => Path.Combine(Root(dataRoot), localDate.ToString(DateFormat, CultureInfo.InvariantCulture));

    /// <summary>Earlier backups that contain at least one driver package (.inf), newest first.</summary>
    public static IReadOnlyList<DriverBackup> List(string backupsRoot)
    {
        var list = new List<DriverBackup>();
        try
        {
            var root = new DirectoryInfo(backupsRoot);
            if (!root.Exists) return list;
            foreach (var dir in root.EnumerateDirectories())
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                // "2026-11-10", or "2026-11-10-2" for a second backup made the same day.
                if (dir.Name.Length < DateFormat.Length
                    || !DateTime.TryParseExact(dir.Name[..DateFormat.Length], DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
                int infs = CountInfs(dir);
                if (infs > 0) list.Add(new DriverBackup(dir.FullName, date, infs));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Unreadable backups folder: nothing to restore from.
        }
        return list.OrderByDescending(b => b.CreatedLocal).ThenByDescending(b => b.Folder, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>pnputil exports one sub-folder per package; count the .inf files two levels deep (no reparse points).</summary>
    private static int CountInfs(DirectoryInfo backup)
    {
        int n = 0;
        try
        {
            n += backup.EnumerateFiles("*.inf").Count();
            foreach (var sub in backup.EnumerateDirectories())
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                try { n += sub.EnumerateFiles("*.inf").Count(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return n;
    }
}
