namespace PcTidy.Features.Duplicates.Hashing;

/// <summary>
/// The only code in Duplicates that writes a file: the hash cache, PC Tidy's own derived data (paths, sizes, times and
/// hashes, never file contents), into <c>&lt;DataRoot&gt;\duplicates\</c> (<see cref="HashCache.PathIn"/>).
/// Written to a temporary file first and swapped in, so a crash never leaves a half-written cache.
/// </summary>
public static class HashCacheStore
{
    /// <summary>Writes the cache when it changed. False (and nothing written) when it didn't, or the folder can't be written.</summary>
    public static bool Save(HashCache cache, string dataRoot)
    {
        if (!cache.IsDirty || string.IsNullOrWhiteSpace(dataRoot)) return false;
        var file = HashCache.PathIn(dataRoot);
        var temp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!); // boundary-ok: own data in DataRoot
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) // boundary-ok: own data in DataRoot
                cache.Save(stream);
            File.Move(temp, file, overwrite: true); // boundary-ok: own data in DataRoot
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Not saved: tried again after the next scan (until then, the next session simply hashes again).
            cache.MarkDirty();
            try { File.Delete(temp); } // boundary-ok: own data in DataRoot (our own temporary file)
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
