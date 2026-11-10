using System.Globalization;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;

namespace PcTidy.Features.Duplicates.Hashing;

/// <summary>
/// A quick look at a huge file: XXH3-64 over three 1 MB blocks (start, middle, end). Two huge files of the same size
/// with the same sampled hash are only PROBABLY the same: they're shown as "Probably the same (not fully checked)"
/// and nothing is ever removed on a sampled match. Read-only; never called for online-only files.
/// </summary>
public static class SampledHash
{
    public const int BlockBytes = 1024 * 1024;

    /// <summary>Hex hash of the three blocks (as <see cref="DuplicateFinder.Hex"/> writes it).</summary>
    public static string Compute(string path, long size)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        var hash = new XxHash3();
        var buffer = new byte[BlockBytes];
        foreach (var offset in Offsets(size))
        {
            int want = (int)Math.Min(BlockBytes, size - offset), got = 0;
            while (got < want)
            {
                int n = RandomAccess.Read(handle, buffer.AsSpan(got, want - got), offset + got);
                if (n == 0) throw new IOException("The file got shorter while it was being read.");
                got += n;
            }
            hash.Append(buffer.AsSpan(0, got));
        }
        // The size is part of the sample, so a file that only matches in its blocks doesn't count.
        hash.Append(BitConverter.GetBytes(size));
        return hash.GetCurrentHashAsUInt64().ToString("X16", CultureInfo.InvariantCulture);
    }

    /// <summary>Start, middle and end block. A file of three blocks or less is simply read whole.</summary>
    public static IReadOnlyList<long> Offsets(long size)
    {
        if (size <= 3L * BlockBytes)
            return Enumerable.Range(0, (int)((size + BlockBytes - 1) / BlockBytes)).Select(i => (long)i * BlockBytes).ToArray();
        return new[] { 0L, size / 2 - BlockBytes / 2, size - BlockBytes };
    }
}
