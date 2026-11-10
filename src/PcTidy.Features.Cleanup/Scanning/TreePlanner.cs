using PcTidy.Core;

namespace PcTidy.Features.Cleanup.Scanning;

/// <summary>What to do with a folder that is only partly eligible (some files too new, or a link inside).</summary>
internal enum MixedFolders
{
    /// <summary>Leave it alone (Quick scan of age-based targets).</summary>
    Skip,
    /// <summary>Look inside and take the eligible parts.</summary>
    Descend,
    /// <summary>Look inside, and count what is found as "found by Deep" (Deep scan of age-based targets).</summary>
    DescendDeepOnly,
}

/// <summary>One file or folder chosen for quarantine. <see cref="Bytes"/>/<see cref="Files"/> cover everything inside it.</summary>
internal readonly record struct Pick(string Root, FileEntry Entry, long Bytes, int Files, bool DeepOnly);

/// <summary>
/// Turns a <see cref="FolderTree"/> into the smallest set of quarantine picks: a whole folder when everything
/// inside it is eligible (one action instead of thousands), otherwise its eligible parts.
/// </summary>
internal static class TreePlanner
{
    public static List<Pick> Plan(FolderTree tree, Func<FileEntry, bool> eligible, MixedFolders mixed)
    {
        var whole = WholeEligibility(tree, eligible);
        var picks = new List<Pick>();
        var stack = new Stack<(TreeNode Node, bool DeepOnly)>();
        for (var i = tree.TopLevel.Count - 1; i >= 0; i--) stack.Push((tree.TopLevel[i], false));

        while (stack.Count > 0)
        {
            var (node, deepOnly) = stack.Pop();
            if (whole[node])
            {
                picks.Add(new Pick(tree.Root, node.Entry, node.Bytes, node.Files, deepOnly));
                continue;
            }
            if (!node.Entry.IsDirectory || node.IsBlocked || mixed == MixedFolders.Skip) continue;
            var childDeepOnly = deepOnly || mixed == MixedFolders.DescendDeepOnly;
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push((node.Children[i], childDeepOnly));
        }
        return picks;
    }

    /// <summary>A node can be taken whole when it and everything inside it is eligible and nothing inside is blocked.</summary>
    private static Dictionary<TreeNode, bool> WholeEligibility(FolderTree tree, Func<FileEntry, bool> eligible)
    {
        var order = FolderTree.PreOrder(tree.TopLevel);
        var whole = new Dictionary<TreeNode, bool>(order.Count, ReferenceEqualityComparer.Instance);
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            var ok = !n.ContainsBlocked && eligible(n.Entry);
            if (ok)
                foreach (var c in n.Children)
                    if (!whole[c]) { ok = false; break; }
            whole[n] = ok;
        }
        return whole;
    }
}
