using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using StorageIt.Models;
using StorageIt.Services;

namespace StorageIt.Controls;

/// <summary>
/// Squarified treemap of one folder: every rectangle's area is proportional to its size,
/// folders are framed groups, files are colored by extension.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    private enum TileKind : byte { File, Folder, CollapsedFolder }

    private readonly record struct Tile(FsNode Node, Rect Rect, TileKind Kind, int Depth, string Extension);

    private const double Pad = 2;
    private const double HeaderHeight = 16;
    private const double MinTileArea = 6;
    private const double MinContainerSide = 18;
    private const int MaxLabels = 700;

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(TreemapControl),
        new FrameworkPropertyMetadata(Brushes.Black, (d, _) => ((TreemapControl)d).Redraw()));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(TreemapControl),
        new FrameworkPropertyMetadata(Brushes.White, (d, _) => ((TreemapControl)d).Redraw()));

    private readonly DrawingVisual _base = new();
    private readonly DrawingVisual _overlay = new();
    private readonly List<Tile> _tiles = new();
    private readonly Dictionary<FsNode, int> _tileOf = new();
    private readonly ToolTip _tip = new() { Placement = PlacementMode.Relative, IsHitTestVisible = false };

    private FsNode? _viewRoot;
    private FsNode? _selected;
    private Func<FsNode, bool>? _highlight;
    private string _emptyText = "";
    private int _hover = -1;

    public TreemapControl()
    {
        AddVisualChild(_base);
        AddVisualChild(_overlay);
        Focusable = true;
        ClipToBounds = true;
        _tip.PlacementTarget = this;
    }

    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>The folder (or file) whose contents fill the map.</summary>
    public FsNode? ViewRoot
    {
        get => _viewRoot;
        set { _viewRoot = value; Relayout(); }
    }

    public FsNode? SelectedNode
    {
        get => _selected;
        set { _selected = value; DrawOverlay(); }
    }

    /// <summary>When set, files for which it returns false are dimmed (used for extension / duplicate highlighting).</summary>
    public Func<FsNode, bool>? Highlight
    {
        get => _highlight;
        set { _highlight = value; Redraw(); }
    }

    public string EmptyText
    {
        get => _emptyText;
        set { _emptyText = value; Redraw(); }
    }

    public event Action<FsNode?>? NodeClicked;
    public event Action<FsNode>? NodeActivated;
    public event Action<FsNode, Point>? NodeContextRequested;

    /// <summary>Recomputes the layout, e.g. after files were deleted or colors were reassigned.</summary>
    public void Refresh() => Relayout();

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _base : _overlay;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Relayout();
    }

    // ---------------------------------------------------------------- layout

    private void Relayout()
    {
        _tiles.Clear();
        _tileOf.Clear();
        _hover = -1;
        var size = RenderSize;
        if (_viewRoot is not null && size.Width > 4 && size.Height > 4 && _viewRoot.Size > 0)
        {
            var bounds = new Rect(1, 1, size.Width - 2, size.Height - 2);
            if (!_viewRoot.IsDirectory)
                _tiles.Add(new Tile(_viewRoot, bounds, TileKind.File, 0, _viewRoot.Extension));
            else
                LayoutChildren(_viewRoot, bounds, 0);
            for (int i = 0; i < _tiles.Count; i++) _tileOf[_tiles[i].Node] = i;
        }
        Redraw();
    }

    private void LayoutChildren(FsNode dir, Rect rect, int depth)
    {
        var kids = dir.Children;
        long total = dir.Size;
        double area = rect.Width * rect.Height;
        if (total <= 0 || kids.Length == 0 || area < MinTileArea) return;

        // Items too small to get a visible tile are skipped without looking at them one by one.
        double minSize = MinTileArea * total / area;
        int count = 0;
        while (count < kids.Length && kids[count].Size > 0 && kids[count].Size >= minSize) count++;
        if (count == 0) return;

        var areas = new double[count];
        for (int i = 0; i < count; i++) areas[i] = kids[i].Size * area / total;

        Squarify.Layout(areas, count, rect, (i, r) =>
        {
            var child = kids[i];
            if (!child.IsDirectory)
            {
                _tiles.Add(new Tile(child, r, TileKind.File, depth, child.Extension));
                return;
            }

            if (r.Width < MinContainerSide || r.Height < MinContainerSide || child.Children.Length == 0)
            {
                _tiles.Add(new Tile(child, r, TileKind.CollapsedFolder, depth, DominantExtension(child)));
                return;
            }

            double header = r.Width >= 56 && r.Height >= 34 ? HeaderHeight : Pad;
            var inner = new Rect(r.X + Pad, r.Y + header, r.Width - 2 * Pad, r.Height - header - Pad);
            if (inner.Width < 4 || inner.Height < 4)
            {
                _tiles.Add(new Tile(child, r, TileKind.CollapsedFolder, depth, DominantExtension(child)));
                return;
            }
            _tiles.Add(new Tile(child, r, TileKind.Folder, depth, ""));
            LayoutChildren(child, inner, depth + 1);
        });
    }

    private static string DominantExtension(FsNode folder)
    {
        var n = folder;
        while (n.IsDirectory)
        {
            if (n.Children.Length == 0) return FsNode.NoExtension;
            n = n.Children[0];
        }
        return n.Extension;
    }

    // ------------------------------------------------------------- rendering

    // The Fluent theme gives windows a transparent background (the backdrop shows through), so an opaque
    // map surface is derived from the text color instead: light text means a dark theme.
    private Color BackgroundColor
    {
        get
        {
            if (Background is SolidColorBrush { Color.A: 255 } solid) return solid.Color;
            bool darkTheme = Foreground is SolidColorBrush fg && ExtensionColors.Luminance(fg.Color) > 0.5;
            return darkTheme ? Color.FromRgb(0x1F, 0x22, 0x28) : Color.FromRgb(0xF5, 0xF6, 0xF8);
        }
    }

    private bool IsDark => ExtensionColors.Luminance(BackgroundColor) < 0.5;

    private void Redraw()
    {
        var size = RenderSize;
        var bg = BackgroundColor;
        bool dark = IsDark;
        var bgBrush = Freeze(new SolidColorBrush(bg));
        var gap = Freeze(new Pen(bgBrush, 1));
        var containerBrushes = new Brush[3];
        for (int i = 0; i < 3; i++)
            containerBrushes[i] = Freeze(new SolidColorBrush(dark
                ? ExtensionColors.Blend(bg, Colors.White, 0.07 + 0.045 * i)
                : ExtensionColors.Blend(bg, Colors.Black, 0.05 + 0.04 * i)));

        var fills = new Dictionary<(string, bool), Brush>();
        Brush FillFor(string ext, bool dim)
        {
            if (fills.TryGetValue((ext, dim), out var b)) return b;
            var c = ExtensionColors.Get(ext);
            if (dim) c = ExtensionColors.Blend(c, bg, dark ? 0.82 : 0.78);
            var g = new LinearGradientBrush(ExtensionColors.Lighten(c, 0.20), ExtensionColors.Darken(c, 0.22), new Point(0, 0), new Point(1, 1));
            return fills[(ext, dim)] = Freeze(g);
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var muted = Foreground.Clone();
        muted.Opacity = 0.85;
        muted.Freeze();
        var darkText = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)));
        var lightText = Freeze(new SolidColorBrush(Colors.White));

        int labels = 0;
        using var dc = _base.RenderOpen();
        dc.DrawRectangle(bgBrush, null, new Rect(0, 0, size.Width, size.Height));

        foreach (var t in _tiles)
        {
            switch (t.Kind)
            {
                case TileKind.Folder:
                    dc.DrawRectangle(containerBrushes[t.Depth % 3], gap, t.Rect);
                    if (t.Rect.Width >= 56 && t.Rect.Height >= 34 && labels < MaxLabels)
                    {
                        labels++;
                        var ft = MakeText($"{t.Node.Name}  ·  {SizeFormat.Format(t.Node.Size)}", typeface, 11, muted, dpi, t.Rect.Width - 8, HeaderHeight);
                        dc.DrawText(ft, new Point(t.Rect.X + 4, t.Rect.Y + 1));
                    }
                    break;

                default:
                    bool lit = _highlight is null || (t.Kind == TileKind.File ? _highlight(t.Node) : AnyLit(t.Node));
                    dc.DrawRectangle(FillFor(t.Extension, !lit), gap, t.Rect);
                    if (t.Rect.Width >= 54 && t.Rect.Height >= 16 && labels < MaxLabels && lit)
                    {
                        labels++;
                        var c = ExtensionColors.Get(t.Extension);
                        var textBrush = ExtensionColors.Luminance(c) > 0.62 ? darkText : lightText;
                        var name = MakeText(t.Node.Name, typeface, 11, textBrush, dpi, t.Rect.Width - 8, 15);
                        dc.DrawText(name, new Point(t.Rect.X + 4, t.Rect.Y + 2));
                        if (t.Rect.Height >= 34)
                        {
                            var sz = MakeText(SizeFormat.Format(t.Node.Size), typeface, 10, textBrush, dpi, t.Rect.Width - 8, 14);
                            dc.DrawText(sz, new Point(t.Rect.X + 4, t.Rect.Y + 17));
                        }
                    }
                    break;
            }
        }

        if (_tiles.Count == 0 && !string.IsNullOrEmpty(_emptyText))
        {
            var ft = MakeText(_emptyText, typeface, 14, muted, dpi, Math.Max(10, size.Width - 40), size.Height);
            ft.TextAlignment = TextAlignment.Center;
            dc.DrawText(ft, new Point(20, Math.Max(0, size.Height / 2 - ft.Height / 2)));
        }

        DrawOverlay();
    }

    // A collapsed folder stays lit if anything inside it is highlighted.
    private bool AnyLit(FsNode folder)
    {
        if (_highlight is null) return true;
        foreach (var f in folder.Files()) if (_highlight(f)) return true;
        return false;
    }

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        var white = Freeze(new Pen(Brushes.White, 2));
        var black = Freeze(new Pen(Brushes.Black, 1));

        if (_hover >= 0 && _hover < _tiles.Count)
        {
            var edge = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1));
            dc.DrawRectangle(null, edge, Deflate(_tiles[_hover].Rect, 0.5));
        }

        var sel = FindTile(_selected);
        if (sel >= 0)
        {
            var r = _tiles[sel].Rect;
            dc.DrawRectangle(null, black, Deflate(r, 0.5));
            dc.DrawRectangle(null, white, Deflate(r, 1.5));
            dc.DrawRectangle(null, black, Deflate(r, 2.5));
        }
    }

    private int FindTile(FsNode? node)
    {
        // Items too small for their own tile are shown by the nearest ancestor that has one.
        for (var n = node; n is not null; n = n.Parent)
            if (_tileOf.TryGetValue(n, out var i)) return i;
        return -1;
    }

    private static Rect Deflate(Rect r, double by) =>
        new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    private static FormattedText MakeText(string text, Typeface face, double size, Brush brush, double dpi, double maxWidth, double maxHeight) =>
        new(text, CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, face, size, brush, dpi)
        {
            MaxTextWidth = Math.Max(1, maxWidth),
            MaxTextHeight = Math.Max(1, maxHeight),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    // ----------------------------------------------------------- interaction

    private int HitTest(Point p)
    {
        // Containers are stored before their children and siblings never overlap,
        // so the last tile containing the point is the innermost one.
        for (int i = _tiles.Count - 1; i >= 0; i--)
            if (_tiles[i].Rect.Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        int hit = HitTest(p);
        if (hit != _hover)
        {
            _hover = hit;
            DrawOverlay();
            if (hit < 0) _tip.IsOpen = false;
            else { _tip.Content = BuildTip(_tiles[hit].Node); _tip.IsOpen = true; }
        }
        if (_tip.IsOpen)
        {
            _tip.HorizontalOffset = p.X + 16;
            _tip.VerticalOffset = p.Y + 18;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _tip.IsOpen = false;
        if (_hover != -1) { _hover = -1; DrawOverlay(); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        int hit = HitTest(e.GetPosition(this));
        var node = hit >= 0 ? _tiles[hit].Node : null;
        _selected = node;
        DrawOverlay();
        NodeClicked?.Invoke(node);
        if (e.ClickCount == 2 && node is not null) NodeActivated?.Invoke(node);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        var pos = e.GetPosition(this);
        int hit = HitTest(pos);
        if (hit < 0) return;
        var node = _tiles[hit].Node;
        _selected = node;
        DrawOverlay();
        NodeClicked?.Invoke(node);
        NodeContextRequested?.Invoke(node, pos);
        e.Handled = true;
    }

    private object BuildTip(FsNode node)
    {
        double share = _viewRoot is { Size: > 0 } ? (double)node.Size / _viewRoot.Size : 0;
        var panel = new StackPanel { MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = node.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = node.FullPath, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        string detail = node.IsDirectory
            ? $"{SizeFormat.Format(node.Size)}  ·  {SizeFormat.Count(node.FileCount)} files  ·  {SizeFormat.Percent(share)} of view"
            : $"{SizeFormat.Format(node.Size)}  ·  {SizeFormat.Percent(share)} of view  ·  {node.Modified:g}";
        panel.Children.Add(new TextBlock { Text = detail, Margin = new Thickness(0, 4, 0, 0) });
        return panel;
    }
}
