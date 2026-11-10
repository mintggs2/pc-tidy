using System.Diagnostics;
using PcTidy.Core;
using PcTidy.Core.Model;
using PcTidy.Features.Cleanup.Scanning;

namespace PcTidy.Features.Cleanup.Targets;

/// <summary>Recognises installers by extension, name or version info. Never runs anything.</summary>
internal static class InstallerDetector
{
    private static readonly HashSet<string> PackageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle",
    };

    /// <summary>"install" also covers installer / installation.</summary>
    private static readonly string[] Words = { "setup", "install" };

    public static bool IsInstaller(FileEntry file, out string why)
    {
        why = "";
        var ext = Path.GetExtension(file.Name);
        if (PackageExtensions.Contains(ext))
        {
            why = "Windows installer package";
            return true;
        }
        if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (MentionsInstall(Path.GetFileNameWithoutExtension(file.Name)))
        {
            why = "The file name says it's an installer";
            return true;
        }
        // Online-only cloud files are never opened (it would download them).
        if (file.IsCloudPlaceholder) return false;
        try
        {
            // Reads the version resource only; the file is not executed.
            var info = FileVersionInfo.GetVersionInfo(file.FullPath);
            var fields = new[] { info.FileDescription, info.ProductName, info.InternalName, info.OriginalFilename };
            if (fields.Any(MentionsInstall))
            {
                why = "Its version info says it's an installer";
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            // Locked or vanished: just don't list it.
        }
        return false;
    }

    private static bool MentionsInstall(string? text)
        => !string.IsNullOrEmpty(text) && Words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// §7 #19: installers in Downloads untouched for 30+ days, each its own unticked row.
/// Quick looks at the Downloads folder itself; Deep also looks in its sub-folders (skipping code projects and app folders).
/// </summary>
internal sealed class InstallersTarget : ICleanupTarget
{
    private const int MinAgeDays = 30;

    public CleanupTargetInfo Info => CleanupTargets.OldInstallers;

    public IReadOnlyList<ScanItem>? Collect(ScanSession s, CancellationToken ct)
    {
        var downloads = s.Locations.Downloads;
        var tree = s.Tree(downloads, ct, maxDepth: s.IsDeep ? int.MaxValue : 1);
        if (tree is null) return null;

        var items = new List<ScanItem?>();
        var stack = new Stack<TreeNode>(tree.TopLevel);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = stack.Pop();
            var e = node.Entry;
            if (node.IsBlocked) continue;
            if (e.IsDirectory)
            {
                if (!LooksLikeProjectOrApp(s, e.FullPath))
                    foreach (var c in node.Children) stack.Push(c);
                continue;
            }
            if (!s.IsOlderThan(e, MinAgeDays) || !InstallerDetector.IsInstaller(e, out var why)) continue;

            var deepOnly = e.Depth > 1;
            items.Add(ItemFactory.Quarantine(s, Info, new ItemSpec
            {
                Title = e.Name,
                Detail = e.FullPath,
                DeepLabel = "installers in Downloads sub-folders",
                ExtraDetails = new Dictionary<string, string>
                {
                    ["Why it's listed"] = why,
                    ["Last changed"] = e.LastTouchedUtc.ToLocalTime().ToString("d"),
                },
            }, new[] { new Pick(downloads, e, e.Size, 1, deepOnly) }));
        }
        return items.Present().OrderBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool LooksLikeProjectOrApp(ScanSession s, string folder)
    {
        try { return s.Context.Safety.IsInsideProjectOrAppFolder(folder, out _); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }
}
