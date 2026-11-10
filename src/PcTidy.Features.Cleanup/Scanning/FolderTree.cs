using PcTidy.Core;

namespace PcTidy.Features.Cleanup.Scanning;

/// <summary>One file or folder seen under a cleanup root, with totals for everything beneath it.</summary>
internal sealed class TreeNode
{
    public TreeNode(FileEntry entry, bool isBlocked)
    {
        Entry = entry;
        IsBlocked = isBlocked;
    }

    public FileEntry Entry { get; }

    /// <summary>
    /// Link (junction/symlink), cloud placeholder, System-attributed or user-excluded.
    /// Never acted on, and a folder containing one is never taken as a whole.
    /// </summary>
    public bool IsBlocked { get; }

    public List<TreeNode> Children { get; } = new();
    public long Bytes { get; set; }
    public int Files { get; set; }
    public bool ContainsBlocked { get; set; }
}

/// <summary>
/// In-memory listing of a cleanup root, built with the read-only <see cref="IFileWalker"/>.
/// Links, placeholders, System items and excluded folders are recorded but never descended into.
/// </summary>
internal sealed class FolderTree
{
    private FolderTree(string root, List<TreeNode> topLevel, WalkStats stats)
    {
        Root = root;
        TopLevel = topLevel;
        Stats = stats;
    }

    public string Root { get; }
    public IReadOnlyList<TreeNode> TopLevel { get; }
    public WalkStats Stats { get; }

    /// <summary>The root exists but its contents couldn't be listed (usually: needs admin).</summary>
    public bool RootUnreadable => TopLevel.Count == 0 && Stats.Inaccessible > 0;

    public static FolderTree Build(IFileWalker walker, string root, int maxDepth, Func<string, bool> isExcluded, CancellationToken ct)
    {
        var topLevel = new List<TreeNode>();
        var folders = new Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase);
        var rootKey = root.TrimEnd('\\');
        // System items are listed on purpose so they can be marked blocked (the walker would otherwise hide them,
        // and a folder holding one would wrongly look safe to take as a whole).
        var options = new WalkOptions { Recurse = true, IncludeHidden = true, IncludeSystem = true, MaxDepth = maxDepth };

        var stats = walker.Walk(root, options, entry =>
        {
            var blocked = entry.IsReparsePoint || entry.IsCloudPlaceholder || entry.IsSystem
                          || (entry.IsDirectory && isExcluded(entry.FullPath));
            var node = new TreeNode(entry, blocked);
            var parent = Path.GetDirectoryName(entry.FullPath);
            if (parent is null || string.Equals(parent.TrimEnd('\\'), rootKey, StringComparison.OrdinalIgnoreCase))
                topLevel.Add(node);
            else if (folders.TryGetValue(parent, out var parentNode))
                parentNode.Children.Add(node);
            else
                return false; // parent not seen (vanished mid-scan): ignore safely

            if (entry.IsDirectory) folders[entry.FullPath] = node;
            return entry.IsDirectory && !blocked;
        }, ct);

        Aggregate(topLevel);
        return new FolderTree(root, topLevel, stats);
    }

    /// <summary>Every node, parents before children (iterative: deep trees can't overflow the stack).</summary>
    public static List<TreeNode> PreOrder(IEnumerable<TreeNode> nodes)
    {
        var order = new List<TreeNode>();
        var stack = new Stack<TreeNode>(nodes);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            order.Add(n);
            foreach (var c in n.Children) stack.Push(c);
        }
        return order;
    }

    private static void Aggregate(List<TreeNode> topLevel)
    {
        var order = PreOrder(topLevel);
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            if (!n.Entry.IsDirectory)
            {
                n.Bytes = n.Entry.Size;
                n.Files = 1;
                n.ContainsBlocked = n.IsBlocked;
                continue;
            }
            long bytes = 0;
            var files = 0;
            var blocked = n.IsBlocked;
            foreach (var c in n.Children)
            {
                bytes += c.Bytes;
                files += c.Files;
                blocked |= c.ContainsBlocked;
            }
            n.Bytes = bytes;
            n.Files = files;
            n.ContainsBlocked = blocked;
        }
    }
}
