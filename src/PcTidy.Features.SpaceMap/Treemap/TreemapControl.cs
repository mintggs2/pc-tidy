using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PcTidy.Features.SpaceMap.Index;

namespace PcTidy.Features.SpaceMap.Treemap;

/// <summary>What the pointer is over: a tile of the current scene.</summary>
public sealed record TreemapHit(int Tile, TreemapTile Info, Point Position)
{
    /// <summary>The node to select: the tile's node, or for merged tiles the folder holding the small items.</summary>
    public int Node => Info.Node >= 0 ? Info.Node : Info.Folder;
}

public sealed class TreemapHitEventArgs : EventArgs
{
    public TreemapHitEventArgs(TreemapHit? hit, int clickCount = 0) { Hit = hit; ClickCount = clickCount; }
    public TreemapHit? Hit { get; }
    public int ClickCount { get; }
}

/// <summary>
/// Squarified treemap in flat type colours (protected areas hatched). Layout and painting run on a background thread into a pixel buffer
/// (one WriteableBitmap for the whole map); the UI thread only copies pixels and draws a few hundred labels.
/// Hover and click hit-testing is one lookup in the tile map painted alongside, so it stays instant with 1M+ files.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private static readonly FontFamily TextFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private const string LockGlyph = "";

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _base = new();
    private readonly DrawingVisual _overlay = new();
    private readonly DispatcherTimer _resizeTimer;

    private SpaceIndex? _index;
    private int _viewRoot;
    private FileCategory? _highlight;
    private int _selectedNode = -1;
    private int _hoverTile = -1;

    private CancellationTokenSource? _renderCts;
    private int _version;
    private TreemapScene? _scene;
    private int[]? _tileMap;
    private WriteableBitmap? _bitmap;
    private double _scale = 1;

    public TreemapControl()
    {
        _visuals = new VisualCollection(this) { _base, _overlay };
        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(90) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); RequestRender(); };
        Focusable = true;
        ClipToBounds = true;
        // Theme brushes as resource references: a theme switch re-renders with the new colours.
        SetResourceReference(CardBrushProperty, "CardBackgroundFillColorDefaultBrush");
        SetResourceReference(ProtectedBrushProperty, "TidyProtectedFillBrush");
        SetResourceReference(HatchBrushProperty, "TidyHatchLineBrush");
        SetResourceReference(PrimaryTextBrushProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(SecondaryTextBrushProperty, "TextFillColorSecondaryBrush");
        SetResourceReference(DimBrushProperty, "TidyTreemapGapBrush");
        Unloaded += (_, _) => _renderCts?.Cancel();
        Loaded += (_, _) => RequestRender();
    }

    public static readonly DependencyProperty CardBrushProperty = ThemeBrush(nameof(CardBrush));
    public static readonly DependencyProperty ProtectedBrushProperty = ThemeBrush(nameof(ProtectedBrush));
    public static readonly DependencyProperty HatchBrushProperty = ThemeBrush(nameof(HatchBrush));
    public static readonly DependencyProperty PrimaryTextBrushProperty = ThemeBrush(nameof(PrimaryTextBrush));
    public static readonly DependencyProperty SecondaryTextBrushProperty = ThemeBrush(nameof(SecondaryTextBrush));
    public static readonly DependencyProperty DimBrushProperty = ThemeBrush(nameof(DimBrush));
    public Brush? CardBrush { get => (Brush?)GetValue(CardBrushProperty); set => SetValue(CardBrushProperty, value); }
    public Brush? ProtectedBrush { get => (Brush?)GetValue(ProtectedBrushProperty); set => SetValue(ProtectedBrushProperty, value); }
    public Brush? HatchBrush { get => (Brush?)GetValue(HatchBrushProperty); set => SetValue(HatchBrushProperty, value); }
    public Brush? PrimaryTextBrush { get => (Brush?)GetValue(PrimaryTextBrushProperty); set => SetValue(PrimaryTextBrushProperty, value); }
    public Brush? SecondaryTextBrush { get => (Brush?)GetValue(SecondaryTextBrushProperty); set => SetValue(SecondaryTextBrushProperty, value); }
    public Brush? DimBrush { get => (Brush?)GetValue(DimBrushProperty); set => SetValue(DimBrushProperty, value); }

    private static DependencyProperty ThemeBrush(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(TreemapControl),
        new PropertyMetadata(null, (d, _) => ((TreemapControl)d).RequestRender()));

    public event EventHandler<TreemapHitEventArgs>? HoverChanged;
    public event EventHandler<TreemapHitEventArgs>? TileClicked;
    public event EventHandler<TreemapHitEventArgs>? TileContextRequested;

    public TreemapScene? Scene => _scene;
    public int ViewRoot => _viewRoot;

    /// <summary>Shows <paramref name="viewRoot"/> of <paramref name="index"/> (null clears the map).</summary>
    public void Show(SpaceIndex? index, int viewRoot)
    {
        _index = index;
        _viewRoot = viewRoot;
        _hoverTile = -1;
        RequestRender();
    }

    /// <summary>Dims every tile that isn't in this colour group (null = none dimmed).</summary>
    public FileCategory? Highlight
    {
        get => _highlight;
        set { if (_highlight != value) { _highlight = value; RequestRender(); } }
    }

    /// <summary>Node outlined as selected (or its nearest visible ancestor). -1 = none.</summary>
    public int SelectedNode
    {
        get => _selectedNode;
        set { _selectedNode = value; DrawOverlay(); }
    }

    public TreemapHit? HitTest(Point p)
    {
        if (_scene is null || _tileMap is null) return null;
        int x = (int)(p.X * _scale), y = (int)(p.Y * _scale);
        if (x < 0 || y < 0 || x >= _scene.Width || y >= _scene.Height) return null;
        int t = _tileMap[y * _scene.Width + x];
        return t < 0 ? null : new TreemapHit(t, _scene.Tiles[t], p);
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // Stretch the old bitmap while resizing; re-layout once the size settles.
        DrawBase();
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => RequestRender();

    private void RequestRender()
    {
        if (!IsLoaded) return;
        _renderCts?.Cancel();
        var index = _index;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Round(ActualWidth * scale), h = (int)Math.Round(ActualHeight * scale);
        if (index is null || w < 8 || h < 8)
        {
            _scene = null; _tileMap = null; _bitmap = null;
            DrawBase(); DrawOverlay();
            return;
        }

        var cts = new CancellationTokenSource();
        _renderCts = cts;
        int version = ++_version;
        var options = new TreemapLayoutOptions { Scale = scale, Highlight = _highlight };
        var palette = BuildPalette();
        int viewRoot = _viewRoot;
        var reuse = _bitmap is not null && _bitmap.PixelWidth == w && _bitmap.PixelHeight == h;

        _ = Task.Run(() =>
        {
            var scene = TreemapLayoutEngine.Build(index, viewRoot, w, h, options, cts.Token);
            var pixels = new int[w * h];
            var map = new int[w * h];
            TreemapRenderer.Render(scene, palette, pixels, map, scale, cts.Token);
            return (scene, pixels, map);
        }, cts.Token).ContinueWith(t =>
        {
            if (t.IsFaulted) { _ = t.Exception; return; } // observed; the previous map stays up
            if (t.IsCanceled || version != _version) return;
            var (scene, pixels, map) = t.Result;
            if (!reuse || _bitmap is null) _bitmap = new WriteableBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null);
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
            _scene = scene; _tileMap = map; _scale = scale;
            _hoverTile = -1;
            DrawBase();
            DrawOverlay();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private TreemapPalette BuildPalette() => new()
    {
        Card = ArgbOf(CardBrush, 0x0DFFFFFF),
        Protected = ArgbOf(ProtectedBrush, 0xFF3A3F45),
        HatchLine = ArgbOf(HatchBrush, 0x38FFFFFF),
        Dim = ArgbOf(DimBrush, 0xFF202020),
    };

    private static uint ArgbOf(Brush? b, uint fallback)
        => b is SolidColorBrush s ? (uint)(s.Color.A << 24 | s.Color.R << 16 | s.Color.G << 8 | s.Color.B) : fallback;

    // ---------------- drawing ----------------

    private void DrawBase()
    {
        using var dc = _base.RenderOpen();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // whole area hit-testable
        if (_bitmap is null || _scene is null) return;
        dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        // Labels only once the layout matches the current size (stale labels would float off their tiles).
        if (Math.Abs(_scene.Width - ActualWidth * _scale) > 1 || Math.Abs(_scene.Height - ActualHeight * _scale) > 1) return;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var primary = PrimaryTextBrush ?? Brushes.White;
        var secondary = SecondaryTextBrush ?? Brushes.Gray;
        foreach (var label in _scene.Labels) DrawLabel(dc, label, ppd, primary, secondary);
    }

    private static readonly Brush TileText = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
    private static readonly Brush TileSubText = Frozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

    private void DrawLabel(DrawingContext dc, TreemapLabel label, double ppd, Brush primary, Brush secondary)
    {
        var r = new Rect(label.Rect.X / _scale, label.Rect.Y / _scale, label.Rect.W / _scale, label.Rect.H / _scale);
        switch (label.Kind)
        {
            case LabelKind.CardHeader:
            {
                var size = Text(label.Subtitle ?? "", 12, secondary, ppd, false);
                bool showSize = r.Width > size.Width + 90;
                var title = Text(label.Title, 13, primary, ppd, true, r.Width - 20 - (showSize ? size.Width + 10 : 0));
                dc.DrawText(title, new Point(r.X + 10, r.Y + 6));
                if (showSize) dc.DrawText(size, new Point(r.Right - size.Width - 10, r.Y + 7));
                break;
            }
            case LabelKind.Protected:
            {
                dc.DrawText(Text(LockGlyph, 14, primary, ppd, false, 0, IconFont), new Point(r.X + 12, r.Y + 10));
                dc.DrawText(Text(label.Title, 13, primary, ppd, true, r.Width - 44), new Point(r.X + 34, r.Y + 8));
                if (label.Subtitle is not null)
                    dc.DrawText(Text(label.Subtitle, 12, secondary, ppd, false, r.Width - 24), new Point(r.X + 12, r.Y + 30));
                break;
            }
            default:
                if (label.Subtitle is null)
                    dc.DrawText(Text(label.Title, 11, TileText, ppd, false, r.Width - 8), new Point(r.X + 4, r.Y + 3));
                else
                {
                    dc.DrawText(Text(label.Title, 12, TileText, ppd, true, r.Width - 14), new Point(r.X + 7, r.Y + 5));
                    dc.DrawText(Text(label.Subtitle, 11.5, TileSubText, ppd, false, r.Width - 14), new Point(r.X + 7, r.Y + 21));
                }
                break;
        }
    }

    private static readonly Pen HoverPen = FrozenPen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 1.5);
    private static readonly Pen SelectPen = FrozenPen(Colors.White, 2.5);
    private static readonly Pen SelectShadowPen = FrozenPen(Color.FromArgb(0x80, 0, 0, 0), 4.5);

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (_scene is null || _index is null) return;
        if (_hoverTile >= 0 && _hoverTile < _scene.TileCount)
            dc.DrawRectangle(null, HoverPen, Dip(_scene.Tiles[_hoverTile].Rect, 0.75));
        if (_selectedNode >= 0 && _selectedNode < _index.Count)
        {
            int t = _scene.TileFor(_index, _selectedNode);
            if (t >= 0)
            {
                var r = Dip(_scene.Tiles[t].Rect, 1.25);
                dc.DrawRoundedRectangle(null, SelectShadowPen, r, 3, 3);
                dc.DrawRoundedRectangle(null, SelectPen, r, 3, 3);
            }
        }
    }

    private Rect Dip(RectD r, double inset)
    {
        var rect = new Rect(r.X / _scale, r.Y / _scale, r.W / _scale, r.H / _scale);
        if (rect.Width > inset * 2 && rect.Height > inset * 2) rect.Inflate(-inset, -inset);
        return rect;
    }

    private static FormattedText Text(string s, double size, Brush brush, double ppd, bool bold, double maxWidth = 0, FontFamily? family = null)
    {
        var tf = new Typeface(family ?? TextFont, FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, tf, size, brush, ppd);
        if (maxWidth > 0)
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double w) { var p = new Pen(Frozen(c), w); p.Freeze(); return p; }

    // ---------------- input ----------------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.GetPosition(this));
        int tile = hit?.Tile ?? -1;
        if (tile != _hoverTile)
        {
            _hoverTile = tile;
            DrawOverlay();
        }
        HoverChanged?.Invoke(this, new TreemapHitEventArgs(hit));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverTile = -1;
        DrawOverlay();
        HoverChanged?.Invoke(this, new TreemapHitEventArgs(null));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (HitTest(e.GetPosition(this)) is { } hit)
        {
            TileClicked?.Invoke(this, new TreemapHitEventArgs(hit, e.ClickCount));
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (HitTest(e.GetPosition(this)) is { } hit)
        {
            TileContextRequested?.Invoke(this, new TreemapHitEventArgs(hit));
            e.Handled = true;
        }
    }
}
