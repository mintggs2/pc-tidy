using System.Collections.Concurrent;
using PcTidy.Features.Duplicates.Hashing;
using PcTidy.Features.Duplicates.Model;
using PcTidy.Features.Duplicates.Planning;
using PcTidy.Features.Duplicates.Scanning;

namespace PcTidy.Features.Duplicates.Signatures;

/// <summary>
/// Wrong-extension finder (Plan census v3): reads the first <see cref="FileSignatures.HeadBytes"/> bytes of files whose
/// extension PC Tidy can judge and lists those whose content is something else. A program dressed up as a document
/// or picture is a security warning and is never renamed; a harmless mismatch (a .jpg that is really a PNG) gets a
/// rename suggestion the user may tick. Read-only; online-only files never get here (the walk leaves them out).
/// </summary>
public static class WrongExtensionFinder
{
    public static IReadOnlyList<WrongExtensionItem> Find(IEnumerable<FileRec> files, Func<string, string> friendly, ISafetyGuard guard, CancellationToken ct)
    {
        var candidates = files.Where(f => f.Size >= 4 && !f.IsSystem && FileSignatures.IsJudged(Extension(f.Name))).ToList();
        var found = new ConcurrentBag<WrongExtensionItem>();
        Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) }, f =>
        {
            if (Check(f, friendly, guard) is { } item) found.Add(item);
        });
        // Security warnings first, then by folder and name.
        return found.OrderByDescending(i => i.IsSecurityWarning).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static WrongExtensionItem? Check(FileRec f, Func<string, string> friendly, ISafetyGuard guard)
    {
        Span<byte> head = stackalloc byte[FileSignatures.HeadBytes];
        int read;
        try
        {
            using var fs = new FileStream(Win32Path.Raw(f.Path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            read = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        var ext = Extension(f.Name);
        if (FileSignatures.Mismatch(ext, head[..read]) is not { } m) return null;
        return Describe(f, ext, m.Real, m.FixExtension, friendly, guard);
    }

    /// <summary>The row for one mismatch (also used by tests with hand-made headers).</summary>
    public static WrongExtensionItem Describe(FileRec f, string ext, ContentType real, string? fixExtension, Func<string, string> friendly, ISafetyGuard guard)
    {
        if (real.IsProgram)
            return new WrongExtensionItem
            {
                Path = f.Path, Size = f.Size, LastWriteUtc = f.WriteUtc, Extension = ext, RealType = real.Words, IsSecurityWarning = true,
                Reason = $"Careful: this .{ext} is really {real.Words}. Programs named like documents or pictures are a common trick. Don't open it unless you know where it came from, and scan it with Windows Security first.",
            };

        string? suggested = null, blocked = null;
        if (fixExtension is not null)
        {
            suggested = FreeName(Path.Combine(Path.GetDirectoryName(f.Path)!, Path.GetFileNameWithoutExtension(f.Path) + "." + fixExtension));
            var verdict = SafeCheck(guard, DuplicatePlanner.ForRename(f.Path, suggested, f.Size, f.WriteUtc));
            if (!verdict.Allowed) blocked = verdict.Reason ?? "PC Tidy's safety rules keep this file as it is.";
        }
        var reason = $"This .{ext} is really {real.Words}."
                     + (suggested is null
                         ? " PC Tidy can't tell which name is right, so it only shows you."
                         : $" Renaming it to \"{Path.GetFileName(suggested)}\" lets the right app open it. Nothing else changes.");
        return new WrongExtensionItem
        {
            Path = f.Path, Size = f.Size, LastWriteUtc = f.WriteUtc, Extension = ext, RealType = real.Words,
            SuggestedPath = suggested, Reason = reason, RenameBlockedReason = blocked,
        };
    }

    private static string Extension(string name)
    {
        var e = Path.GetExtension(name);
        return e.Length > 1 ? e[1..].ToLowerInvariant() : "";
    }

    /// <summary>"report.zip", or "report (2).zip" when that name is taken. Never overwrites.</summary>
    private static string FreeName(string desired)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired)) return desired;
        var dir = Path.GetDirectoryName(desired)!;
        var stem = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        for (int i = 2; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return desired;
    }

    private static SafetyVerdict SafeCheck(ISafetyGuard guard, PlanItem item)
    {
        try { return guard.Check(item); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SafetyVerdict.Block("PC Tidy couldn't check this file safely, so it's left alone.");
        }
    }
}
