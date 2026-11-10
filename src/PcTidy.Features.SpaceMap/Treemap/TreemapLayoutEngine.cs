using System.Buffers;
using PcTidy.Core;
using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap.Treemap;

public sealed record TreemapLayoutOptions
{
    /// <summary>Device pixels per DIP (1.0 at 100 % scaling). Sizes below are in DIPs unless they say otherwise.</summary>
    public double Scale { get; init; } = 1.0;
    /// <summary>Items smaller than this many square device pixels (about 10 x 10) are merged into one "N small items" tile.</summary>
    public double MinTileArea { get; init; } = 100;
    /// <summary>
    /// Tiles thinner than this (DIPs, either side) are merged too: a strip of 1-5 px slivers is unreadable and can't be clicked.
    /// </summary>
    public double MinTileSide { get; init; } = 6;
    /// <summary>Folders smaller than this (device px, either side) aren't opened up.</summary>
    public double MinFolderSide { get; init; } = 6;
    public int MaxLabels { get; init; } = 400;
    /// <summary>Hard cap on tiles per folder so one folder with 1M files can't dominate the work.</summary>
    public int MaxChildrenPerFolder { get; init; } = 20_000;
    /// <summary>Only this colour group stays bright (File types tab); null = all.</summary>
    public FileCategory? Highlight { get; init; }
}

/// <summary>
/// Lays out an index into a <see cref="TreemapScene"/>. Work is bounded by the pixel area, not by the number of
/// files: children are sorted biggest first, and the first child that would be smaller than
/// <see cref="TreemapLayoutOptions.MinTileArea"/> or thinner than <see cref="TreemapLayoutOptions.MinTileSide"/>
/// ends the folder (the rest become one merged tile). Pure computation: run it on a background thread.
/// </summary>
public static class TreemapLayoutEngine
{
    private const int MaxDepth = 120;
    /// <summary>Re-layouts allowed per folder while trimming slivers (each one only ever shrinks the shown set).</summary>
    private const int MaxSliverPasses = 4;

    public static TreemapScene Build(SpaceIndex index, int viewRoot, int width, int height, TreemapLayoutOptions? options = null, CancellationToken ct = default)
    {
        options ??= new TreemapLayoutOptions();
        var b = new Builder(index, options, ct, viewRoot);
        if (width > 0 && height > 0 && index.Size(viewRoot) > 0)
            b.LayoutChildren(viewRoot, new RectD(0, 0, width, height), depth: 1, parentTile: -1);
        return new TreemapScene(width, height, viewRoot, b.Tiles, b.Count, b.Labels, b.TileOfNode);
    }

    private sealed class Builder
    {
        private readonly SpaceIndex _ix;
        private readonly TreemapLayoutOptions _o;
        private readonly CancellationToken _ct;
        private readonly bool _viewIsProtected;
        private readonly double _s;
        private readonly double _minSide;

        public TreemapTile[] Tiles = new TreemapTile[1024];
        public int Count;
        public readonly List<TreemapLabel> Labels = new();
        public readonly Dictionary<int, int> TileOfNode = new();

        public Builder(SpaceIndex ix, TreemapLayoutOptions o, CancellationToken ct, int viewRoot)
        {
            _ix = ix; _o = o; _ct = ct; _s = o.Scale;
            _minSide = o.MinTileSide * o.Scale;
            // Inside a protected folder everything is protected: show the contents normally (the lock is on the bar).
            _viewIsProtected = ix.Has(viewRoot, SpaceNodeFlags.Protected);
        }

        public void LayoutChildren(int folder, RectD area, int depth, int parentTile)
        {
            if (area.W < 1 || area.H < 1 || depth > MaxDepth) return;
            if ((Count & 1023) == 0) _ct.ThrowIfCancellationRequested();
            long total = _ix.Size(folder);
            if (total <= 0) return;

            var kids = _ix.Children(folder);
            double scale = area.Area / total;
            int limit = Math.Min(kids.Length, _o.MaxChildrenPerFolder);
            int take = 0;
            while (take < limit)
            {
                long sz = _ix.Size(kids[take]);
                if (sz <= 0 || sz * scale < _o.MinTileArea) break;
                take++;
            }

            var weights = ArrayPool<double>.Shared.Rent(take + 2);
            var rects = ArrayPool<RectD>.Shared.Rent(take + 2);
            try
            {
                int shown = 0, restCount = 0;
                long rest = 0;
                bool allowSingle = true;
                for (int pass = 0; ; pass++)
                {
                    long taken = 0;
                    for (int k = 0; k < take; k++) taken += _ix.Size(kids[k]);
                    rest = total - taken;
                    restCount = 0;
                    for (int k = take; k < kids.Length && _ix.Size(kids[k]) > 0; k++) restCount++;
                    // One small item left over: show it as itself rather than as "1 small item".
                    bool single = allowSingle && restCount == 1;
                    shown = single ? take + 1 : take;
                    if (single) { rest = 0; restCount = 0; }

                    int n = shown + (rest > 0 ? 1 : 0);
                    if (n == 0) return;
                    for (int k = 0; k < shown; k++) weights[k] = _ix.Size(kids[k]);
                    if (rest > 0) weights[shown] = rest;
                    SquarifiedLayout.Layout(weights.AsSpan(0, n), area, rects.AsSpan(0, n));
                    if (pass == MaxSliverPasses) break;

                    // Slivers go into the merged tile; smaller siblings come later, so cut at the first thin one.
                    int thin = FirstThin(rects, shown);
                    if (thin < 0) break;
                    if (thin >= take) allowSingle = false;
                    else take = thin;
                }
                for (int k = 0; k < shown; k++) PlaceChild(kids[k], folder, rects[k], depth, parentTile);
                if (rest > 0) PlaceMerged(folder, rects[shown], rest, restCount, depth, parentTile);
            }
            finally
            {
                ArrayPool<double>.Shared.Return(weights);
                ArrayPool<RectD>.Shared.Return(rects);
            }
        }

        private int FirstThin(RectD[] rects, int count)
        {
            for (int k = 0; k < count; k++)
                if (Math.Min(rects[k].W, rects[k].H) < _minSide) return k;
            return -1;
        }

        private void PlaceChild(int node, int folder, RectD r, int depth, int parentTile)
        {
            bool top = depth == 1;
            if (top) r = r.Inset(2 * _s);
            else if (depth == 2 && r.W >= 6 * _s && r.H >= 6 * _s) r = r.Inset(0.5 * _s);
            if (r.W < 1 || r.H < 1) return;

            var flags = _ix.Flags(node);
            bool isProtected = !_viewIsProtected && (flags & (SpaceNodeFlags.Protected | SpaceNodeFlags.Pseudo)) != 0;
            bool isDir = (flags & SpaceNodeFlags.Directory) != 0 && (flags & SpaceNodeFlags.Link) == 0;
            var category = _ix.Category(node);

            if (isProtected)
            {
                int t = Add(node, folder, parentTile, r, TileKind.Protected, category, depth, top);
                if (r.W >= 70 * _s && r.H >= 26 * _s)
                    Label(new TreemapLabel(r, _ix.Name(node), r.H >= 48 * _s ? $"{Sizes.Format(_ix.Size(node))} · {ProtectedNote(flags)}" : null, LabelKind.Protected, t));
                return;
            }

            if (isDir && top && r.W >= 90 * _s && r.H >= 64 * _s && _ix.ChildCount(node) > 0)
            {
                int t = Add(node, folder, parentTile, r, TileKind.Card, category, depth, rounded: true);
                Label(new TreemapLabel(r, _ix.Name(node), Sizes.Format(_ix.Size(node)), LabelKind.CardHeader, t));
                var inner = r.Inset(4 * _s, 28 * _s, 4 * _s, 4 * _s);
                LayoutChildren(node, inner, depth + 1, t);
                return;
            }

            if (isDir && _ix.ChildCount(node) > 0 && r.W >= _o.MinFolderSide && r.H >= _o.MinFolderSide)
            {
                int t = Add(node, folder, parentTile, r, TileKind.Folder, category, depth, top);
                LayoutChildren(node, r, depth + 1, t);
                return;
            }

            int leaf = Add(node, folder, parentTile, r, TileKind.Leaf, category, depth, top);
            LabelTile(r, _ix.Name(node), Sizes.Format(_ix.Size(node)), LabelKind.Tile, leaf);
        }

        private void PlaceMerged(int folder, RectD r, long bytes, int count, int depth, int parentTile)
        {
            if (depth == 1) r = r.Inset(2 * _s);
            if (r.W < 1 || r.H < 1) return;
            int t = Add(-1, folder, parentTile, r, TileKind.Merged, FileCategory.Other, depth, depth == 1);
            Tiles[t].MergedCount = count;
            Tiles[t].Bytes = bytes;
            LabelTile(r, count == 1 ? "1 small item" : $"{count:N0} small items", Sizes.Format(bytes), LabelKind.Merged, t);
        }

        private void LabelTile(RectD r, string title, string size, LabelKind kind, int tile)
        {
            if (r.W > 54 * _s && r.H > 36 * _s) Label(new TreemapLabel(r, title, size, kind, tile));
            else if (r.W > 40 * _s && r.H > 18 * _s) Label(new TreemapLabel(r, size, null, kind, tile));
        }

        private void Label(TreemapLabel label)
        {
            if (Labels.Count < _o.MaxLabels) Labels.Add(label);
        }

        private static string ProtectedNote(SpaceNodeFlags flags)
            => (flags & SpaceNodeFlags.Pseudo) != 0 ? "page file, hibernation, restore points" : "protected, never cleaned";

        private int Add(int node, int folder, int parentTile, RectD r, TileKind kind, FileCategory category, int depth, bool rounded)
        {
            if (Count == Tiles.Length) Array.Resize(ref Tiles, Tiles.Length * 2);
            int i = Count++;
            Tiles[i] = new TreemapTile
            {
                Rect = r,
                Node = node,
                Folder = folder,
                ParentTile = parentTile,
                Bytes = node >= 0 ? _ix.Size(node) : 0,
                Kind = kind,
                Category = category,
                Depth = (byte)Math.Min(depth, 255),
                Rounded = rounded,
                Dimmed = _o.Highlight is { } hl && kind is TileKind.Leaf or TileKind.Merged && (kind == TileKind.Merged || category != hl),
            };
            if (node >= 0) TileOfNode[node] = i;
            return i;
        }
    }
}
