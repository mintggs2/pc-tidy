using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap.Treemap;

public enum TileKind : byte
{
    /// <summary>A file, or a folder too small to open up: a flat block in its type colour.</summary>
    Leaf,
    /// <summary>A top-level folder drawn as a card with a header; its contents sit inside.</summary>
    Card,
    /// <summary>A folder whose contents are drawn inside it (not painted itself).</summary>
    Folder,
    /// <summary>Many tiny (or too thin to see) items merged into one "N small items" tile.</summary>
    Merged,
    /// <summary>Protected area or the Unscanned block: hatched, with a lock, not opened up.</summary>
    Protected,
}

/// <summary>One laid-out rectangle. Tiles are stored parents-first, so later tiles paint over earlier ones.</summary>
public struct TreemapTile
{
    public RectD Rect;
    /// <summary>Index node, or -1 for a merged tile.</summary>
    public int Node;
    /// <summary>Folder the tile sits in (for merged tiles: the folder whose small items it holds).</summary>
    public int Folder;
    /// <summary>Tile index of the enclosing tile, or -1 at the top.</summary>
    public int ParentTile;
    public int MergedCount;
    public long Bytes;
    public TileKind Kind;
    public FileCategory Category;
    public byte Depth;
    public bool Rounded;
    public bool Dimmed;
}

public enum LabelKind : byte { Tile, CardHeader, Protected, Merged }

/// <summary>Text drawn over a tile by the UI (only for tiles big enough to read).</summary>
public readonly record struct TreemapLabel(RectD Rect, string Title, string? Subtitle, LabelKind Kind, int Tile);

/// <summary>A finished layout for one view: tiles (for painting and hit-testing) and labels.</summary>
public sealed class TreemapScene
{
    public TreemapScene(int width, int height, int viewRoot, TreemapTile[] tiles, int tileCount, IReadOnlyList<TreemapLabel> labels, Dictionary<int, int> tileOfNode)
    {
        Width = width; Height = height; ViewRoot = viewRoot;
        Tiles = tiles; TileCount = tileCount; Labels = labels; _tileOfNode = tileOfNode;
    }

    private readonly Dictionary<int, int> _tileOfNode;

    public int Width { get; }
    public int Height { get; }
    public int ViewRoot { get; }
    public TreemapTile[] Tiles { get; }
    public int TileCount { get; }
    public IReadOnlyList<TreemapLabel> Labels { get; }

    /// <summary>The tile showing <paramref name="node"/>, or the tile of its nearest shown ancestor; -1 if outside the view.</summary>
    public int TileFor(SpaceIndex index, int node)
    {
        for (int n = node; n >= 0; n = n == SpaceIndex.RootNode ? -1 : index.Parent(n))
        {
            if (_tileOfNode.TryGetValue(n, out var t)) return t;
            if (n == ViewRoot) return -1;
        }
        return -1;
    }
}
