namespace PcTidy.Features.SpaceMap.Index;

/// <summary>
/// Merges the per-worker <see cref="NodeSegment"/>s into one exact-size <see cref="SpaceIndex"/>:
/// remaps parents and extension ids, aggregates folder totals bottom-up, picks each folder's
/// dominant file type, adds the "Unscanned / system" block and sorts every folder's children by size.
/// </summary>
internal static class SpaceIndexAssembler
{
    public const string UnscannedName = "Unscanned / system";
    private const ushort ProtectedFlag = (ushort)SpaceNodeFlags.Protected;
    private const ushort ContainsProtectedFlag = (ushort)SpaceNodeFlags.ContainsProtected;

    /// <param name="skeleton">Segment 0: the root (local index 0) and the top levels walked before splitting.</param>
    /// <param name="branches">Worker segments, each anchored to the skeleton node it was walked from.</param>
    /// <param name="driveUsedBytes">Used bytes on the drive when the root is a whole drive; adds the Unscanned block.</param>
    public static SpaceIndex Assemble(
        NodeSegment skeleton, IReadOnlyList<(NodeSegment Segment, int Anchor)> branches,
        long? driveUsedBytes, Func<SpaceIndexTotals, SpaceScanSummary> summarize)
    {
        int total = skeleton.Count + branches.Sum(b => b.Segment.Count);
        int capacity = total + (driveUsedBytes.HasValue ? 1 : 0);
        int namesTotal = skeleton.NamesLength + branches.Sum(b => b.Segment.NamesLength) + UnscannedName.Length;

        var parent = new int[capacity];
        var nameOffset = new int[capacity];
        var nameLength = new ushort[capacity];
        var names = new char[namesTotal];
        var flags = new ushort[capacity];
        var ext = new ushort[capacity];
        var size = new long[capacity];
        var onDisk = new long[capacity];
        var touched = new int[capacity];
        var accessed = new int[capacity];

        var extensions = new ExtensionTable();
        int nodeBase = 0, nameBase = 0;
        void Copy(NodeSegment s, int anchor)
        {
            var extMap = new ushort[s.Extensions.Count];
            for (int e = 0; e < extMap.Length; e++) extMap[e] = extensions.GetOrAdd(s.Extensions[e]);
            for (int i = 0; i < s.Count; i++)
            {
                int g = nodeBase + i;
                int p = s.Parent[i];
                parent[g] = p == NodeSegment.ExternalParent ? anchor : nodeBase + p;
                nameOffset[g] = nameBase + s.NameOffset[i];
                ext[g] = extMap[s.Ext[i]];
            }
            Array.Copy(s.NameLength, 0, nameLength, nodeBase, s.Count);
            Array.Copy(s.Flags, 0, flags, nodeBase, s.Count);
            Array.Copy(s.Size, 0, size, nodeBase, s.Count);
            Array.Copy(s.OnDisk, 0, onDisk, nodeBase, s.Count);
            Array.Copy(s.Touched, 0, touched, nodeBase, s.Count);
            Array.Copy(s.Accessed, 0, accessed, nodeBase, s.Count);
            Array.Copy(s.Names, 0, names, nameBase, s.NamesLength);
            nodeBase += s.Count;
            nameBase += s.NamesLength;
        }
        Copy(skeleton, -1);
        foreach (var (segment, anchor) in branches) Copy(segment, anchor);
        parent[0] = -1;

        var extNames = new string[extensions.Count];
        var extCategory = new FileCategory[extensions.Count];
        for (int e = 0; e < extNames.Length; e++) { extNames[e] = extensions[e]; extCategory[e] = FileCategories.FromExtension(extNames[e]); }

        var fileCount = new int[capacity];
        var category = new byte[capacity];
        var totals = Aggregate(total, parent, flags, ext, extCategory, size, onDisk, touched, fileCount, category);

        int count = total, unscanned = -1;
        if (driveUsedBytes is long used)
        {
            // Pagefile, hibernation file, restore points, folders we may not list... so the map adds up to the drive.
            long missing = Math.Max(0, used - onDisk[0]);
            unscanned = count++;
            parent[unscanned] = 0;
            nameOffset[unscanned] = nameBase;
            nameLength[unscanned] = (ushort)UnscannedName.Length;
            UnscannedName.CopyTo(names.AsSpan(nameBase));
            flags[unscanned] = (ushort)(SpaceNodeFlags.Pseudo | SpaceNodeFlags.Protected);
            size[unscanned] = missing;
            onDisk[unscanned] = missing;
            category[unscanned] = (byte)FileCategory.System;
            size[0] += missing;
            onDisk[0] += missing;
        }

        var (childStart, children) = BuildSortedChildren(count, parent, size);
        var order = BreadthFirstOrder(count, childStart, children, out var newOf, out var firstChild);
        children = null!; childStart = null!; // release before the permuted copies are made

        // Names in id order, so a name's length is the gap to the next offset.
        int nameChars = 0;
        for (int i = 0; i < count; i++) nameChars += nameLength[i];
        var orderedNames = new char[nameChars];
        var orderedOffset = new int[count + 1];
        for (int k = 0, pos = 0; k < count; k++)
        {
            int o = order[k];
            orderedOffset[k] = pos;
            names.AsSpan(nameOffset[o], nameLength[o]).CopyTo(orderedNames.AsSpan(pos));
            pos += nameLength[o];
            orderedOffset[k + 1] = pos;
        }
        names = null!; nameOffset = null!; nameLength = null!;

        var orderedParent = new int[count];
        for (int k = 0; k < count; k++) orderedParent[k] = k == 0 ? -1 : newOf[parent[order[k]]];

        return new SpaceIndex(count, orderedParent, firstChild, orderedOffset, orderedNames,
            Permute(flags, order), Permute(ext, order), Permute(category, order), Permute(size, order), Permute(onDisk, order),
            Permute(touched, order), Permute(accessed, order), Permute(fileCount, order), extNames, summarize(totals),
            unscanned >= 0 ? newOf[unscanned] : -1);
    }

    /// <summary>
    /// Renumbers nodes breadth-first, children in size order. Then every folder's children are one contiguous id
    /// range (no child list needed) and parents still come before children.
    /// </summary>
    private static int[] BreadthFirstOrder(int count, int[] childStart, int[] children, out int[] newOf, out int[] firstChild)
    {
        var order = new int[count];   // new id → old id
        newOf = new int[count];       // old id → new id
        firstChild = new int[count + 1];
        int tail = 1;
        for (int k = 0; k < count; k++)
        {
            int old = order[k];
            newOf[old] = k;
            firstChild[k] = tail;
            for (int j = childStart[old]; j < childStart[old + 1]; j++) order[tail++] = children[j];
        }
        firstChild[count] = tail;
        return order;
    }

    private static T[] Permute<T>(T[] source, int[] order)
    {
        var result = new T[order.Length];
        for (int k = 0; k < order.Length; k++) result[k] = source[order[k]];
        return result;
    }

    /// <summary>Bottom-up pass. Valid because every node is stored after its parent (walk order + segment order).</summary>
    private static SpaceIndexTotals Aggregate(int count, int[] parent, ushort[] flags, ushort[] ext, FileCategory[] extCategory,
        long[] size, long[] onDisk, int[] touched, int[] fileCount, byte[] category)
    {
        const ushort dirFlag = (ushort)SpaceNodeFlags.Directory;
        // Dense slot per folder for the per-type byte tallies (files don't need one).
        var slot = new int[count];
        int folders = 0;
        for (int i = 0; i < count; i++) slot[i] = (flags[i] & dirFlag) != 0 ? folders++ : -1;
        var typeBytes = new long[(long)folders * FileCategories.Count];

        // Folders with an unprotected folder somewhere inside (only possible below a CheckInside folder like Users).
        var openInside = new bool[count];
        long files = 0, links = 0, longPaths = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            bool isDir = (flags[i] & dirFlag) != 0;
            if ((flags[i] & (ushort)SpaceNodeFlags.LongPath) != 0) longPaths++;
            if (isDir)
            {
                // "Users" blocks other accounts, but your profile inside is yours: the folder only stays put as a whole.
                if ((flags[i] & ProtectedFlag) != 0 && openInside[i])
                    flags[i] = (ushort)((flags[i] & ~(ProtectedFlag | (ushort)SpaceNodeFlags.NotWalked)) | (ushort)SpaceNodeFlags.SelfLocked);
                flags[i] &= unchecked((ushort)~SpaceNodeFlags.CheckInside);
                long best = -1; int bestCat = 0, b = slot[i] * FileCategories.Count;
                for (int c = 0; c < FileCategories.Count; c++)
                    if (typeBytes[b + c] > best) { best = typeBytes[b + c]; bestCat = c; }
                category[i] = (byte)bestCat;
                if ((flags[i] & (ushort)SpaceNodeFlags.Link) != 0) links++;
            }
            else
            {
                files++;
                fileCount[i] = 1;
                var cat = (flags[i] & (ushort)SpaceNodeFlags.InGameLibrary) != 0 ? FileCategory.Games : extCategory[ext[i]];
                category[i] = (byte)cat;
                if ((flags[i] & (ushort)SpaceNodeFlags.Link) != 0) links++;
            }

            int p = parent[i];
            if (p < 0) continue;
            // Only protected folders count: a stray system file (desktop.ini) mustn't make Downloads look half-locked.
            if ((flags[i] & ContainsProtectedFlag) != 0 || (isDir && (flags[i] & ProtectedFlag) != 0))
                flags[p] |= ContainsProtectedFlag;
            if (isDir && (flags[i] & ProtectedFlag) == 0) openInside[p] = true;
            size[p] += size[i];
            onDisk[p] += onDisk[i];
            fileCount[p] += fileCount[i];
            if (touched[i] > touched[p]) touched[p] = touched[i];
            int pb = slot[p] * FileCategories.Count;
            if (isDir)
            {
                int cb = slot[i] * FileCategories.Count;
                for (int c = 0; c < FileCategories.Count; c++) typeBytes[pb + c] += typeBytes[cb + c];
            }
            else typeBytes[pb + category[i]] += size[i];
        }
        return new SpaceIndexTotals(files, folders, links, longPaths, size[0], onDisk[0]);
    }

    private static (int[] ChildStart, int[] Children) BuildSortedChildren(int count, int[] parent, long[] size)
    {
        var childStart = new int[count + 1];
        for (int i = 1; i < count; i++) childStart[parent[i] + 1]++;
        for (int i = 0; i < count; i++) childStart[i + 1] += childStart[i];
        var children = new int[Math.Max(0, count - 1)];
        var fill = new int[count];
        for (int i = 1; i < count; i++) { int p = parent[i]; children[childStart[p] + fill[p]++] = i; }

        // Sort each folder's slice, biggest first (negated keys → ascending sort).
        var keys = new long[children.Length];
        for (int j = 0; j < children.Length; j++) keys[j] = -size[children[j]];
        for (int i = 0; i < count; i++)
        {
            int len = childStart[i + 1] - childStart[i];
            if (len > 1) Array.Sort(keys, children, childStart[i], len);
        }
        return (childStart, children);
    }
}

internal readonly record struct SpaceIndexTotals(long Files, long Folders, long Links, long LongPaths, long Bytes, long BytesOnDisk);
