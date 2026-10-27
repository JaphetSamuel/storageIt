using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StorageIt.Models;
using StorageIt.Services;

namespace StorageIt.Views;

/// <summary>One visible line of the directory tree (or the "N more items" placeholder).</summary>
public sealed class TreeRow : INotifyPropertyChanged
{
    private bool _expanded;

    public TreeRow(FsNode node, int depth, long parentSize)
    {
        Node = node;
        Depth = depth;
        ParentSize = parentSize;
    }

    /// <summary>Placeholder row standing in for children that are not listed yet.</summary>
    public static TreeRow More(FsNode parent, int depth, int hidden, long hiddenSize) =>
        new(parent, depth, 0) { IsMore = true, HiddenCount = hidden, HiddenSize = hiddenSize };

    public FsNode Node { get; }
    public int Depth { get; }
    public long ParentSize { get; }
    public bool IsMore { get; private init; }
    public int HiddenCount { get; private init; }
    public long HiddenSize { get; private init; }
    /// <summary>How many of Node's children are currently listed under this row.</summary>
    public int Shown { get; set; }

    public bool CanExpand => !IsMore && Node.IsDirectory && Node.Children.Length > 0;
    public bool IsExpanded
    {
        get => _expanded;
        set { _expanded = value; Raise(nameof(ExpanderGlyph)); }
    }

    public Thickness Indent => new(Depth * 16, 0, 0, 0);
    public string ExpanderGlyph => !CanExpand ? "" : _expanded ? "" : "";
    public string Name => IsMore
        ? $"… {SizeFormat.Count(HiddenCount)} more items ({SizeFormat.Format(HiddenSize)}) — double-click to show more"
        : Node.Name;
    public string SizeText => IsMore ? "" : SizeFormat.Format(Node.Size);
    public string FilesText => IsMore || !Node.IsDirectory ? "" : SizeFormat.Count(Node.FileCount);
    public string ModifiedText => IsMore || Node.IsVirtualRoot || Node.Modified == default ? "" : Node.Modified.ToString("g", CultureInfo.CurrentCulture);
    public double Fraction => IsMore || ParentSize <= 0 ? 1 : Math.Clamp((double)Node.Size / ParentSize, 0, 1);
    public double BarWidth => Math.Max(1, Fraction * 120);
    public string PercentText => ParentSize <= 0 ? "" : SizeFormat.Percent(Fraction);
    public Visibility BarVisibility => IsMore || ParentSize <= 0 ? Visibility.Hidden : Visibility.Visible;
    public Brush Swatch => ExtensionColors.GetBrush(Node.Extension);
    public Visibility SwatchVisibility => !IsMore && !Node.IsDirectory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FolderVisibility => !IsMore && Node.IsDirectory ? Visibility.Visible : Visibility.Collapsed;
    public FontStyle NameStyle => IsMore ? FontStyles.Italic : FontStyles.Normal;
    public double NameOpacity => IsMore || Node.IsReparsePoint ? 0.65 : 1;

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => Raise(string.Empty);
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// A virtualized, flattened tree: a plain list whose rows carry an indent and an expander,
/// so a folder with 100,000 files stays responsive.
/// </summary>
public partial class TreePane : UserControl
{
    private const int Batch = 500;

    private ObservableCollection<TreeRow> _rows = new();
    private FsNode? _root;
    private bool _suppress;

    public TreePane()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
    }

    public ActionHub? Hub { get; set; }

    /// <summary>Raised when the user changes the selection (not when the program does).</summary>
    public event Action<IReadOnlyList<FsNode>>? NodesSelected;

    public IReadOnlyList<FsNode> SelectedNodes =>
        List.SelectedItems.OfType<TreeRow>().Where(r => !r.IsMore).Select(r => r.Node).ToList();

    public void Load(FsNode? root)
    {
        _root = root;
        _rows = new ObservableCollection<TreeRow>();
        if (root is not null)
        {
            var list = new List<TreeRow>();
            Flatten(root, 0, 0, list, new HashSet<FsNode> { root }, new Dictionary<FsNode, int>());
            foreach (var r in list) _rows.Add(r);
        }
        List.ItemsSource = _rows;
    }

    /// <summary>Rebuilds the visible rows after nodes were deleted, keeping expanded folders open.</summary>
    public void Rebuild()
    {
        if (_root is null) return;
        var expanded = _rows.Where(r => r.IsExpanded && !r.IsMore).Select(r => r.Node).ToHashSet();
        var shown = _rows.Where(r => r.IsExpanded && !r.IsMore).ToDictionary(r => r.Node, r => r.Shown);
        var selected = SelectedNodes;
        var list = new List<TreeRow>();
        Flatten(_root, 0, 0, list, expanded, shown);

        _suppress = true;
        _rows = new ObservableCollection<TreeRow>(list);
        List.ItemsSource = _rows;
        var keep = _rows.Where(r => !r.IsMore && selected.Contains(r.Node)).ToList();
        foreach (var r in keep) List.SelectedItems.Add(r);
        _suppress = false;
    }

    private static void Flatten(FsNode node, int depth, long parentSize, List<TreeRow> into,
        HashSet<FsNode> expanded, Dictionary<FsNode, int> shown)
    {
        var row = new TreeRow(node, depth, parentSize);
        into.Add(row);
        if (!node.IsDirectory || node.Children.Length == 0 || !expanded.Contains(node)) return;

        row.IsExpanded = true;
        int count = Math.Min(node.Children.Length, Math.Max(Batch, shown.GetValueOrDefault(node)));
        for (int i = 0; i < count; i++)
            Flatten(node.Children[i], depth + 1, node.Size, into, expanded, shown);
        row.Shown = count;
        if (count < node.Children.Length)
            into.Add(MoreRowFor(node, depth + 1, count));
    }

    private static TreeRow MoreRowFor(FsNode parent, int depth, int shown)
    {
        long hiddenSize = 0;
        for (int i = shown; i < parent.Children.Length; i++) hiddenSize += parent.Children[i].Size;
        return TreeRow.More(parent, depth, parent.Children.Length - shown, hiddenSize);
    }

    // ------------------------------------------------------ expand / collapse

    private void Expand(int index)
    {
        var row = _rows[index];
        if (!row.CanExpand || row.IsExpanded) return;
        row.IsExpanded = true;
        row.Shown = 0;
        InsertBatch(row, index + 1);
    }

    /// <summary>Lists the next batch of the row's children starting at <paramref name="insertAt"/>.</summary>
    private void InsertBatch(TreeRow parentRow, int insertAt)
    {
        var kids = parentRow.Node.Children;
        int from = parentRow.Shown, to = Math.Min(kids.Length, from + Batch);
        for (int i = from; i < to; i++)
            _rows.Insert(insertAt++, new TreeRow(kids[i], parentRow.Depth + 1, parentRow.Node.Size));
        parentRow.Shown = to;
        if (to < kids.Length) _rows.Insert(insertAt, MoreRowFor(parentRow.Node, parentRow.Depth + 1, to));
    }

    private void Collapse(int index)
    {
        var row = _rows[index];
        if (!row.IsExpanded) return;
        row.IsExpanded = false;
        row.Shown = 0;
        while (index + 1 < _rows.Count && _rows[index + 1].Depth > row.Depth) _rows.RemoveAt(index + 1);
    }

    private void LoadMore(int moreIndex)
    {
        var more = _rows[moreIndex];
        int parentIndex = moreIndex - 1;
        while (parentIndex >= 0 && !(ReferenceEquals(_rows[parentIndex].Node, more.Node) && !_rows[parentIndex].IsMore && _rows[parentIndex].Depth == more.Depth - 1))
            parentIndex--;
        if (parentIndex < 0) return;
        _rows.RemoveAt(moreIndex);
        InsertBatch(_rows[parentIndex], moreIndex);
    }

    private int IndexOf(FsNode node)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (!_rows[i].IsMore && ReferenceEquals(_rows[i].Node, node)) return i;
        return -1;
    }

    // --------------------------------------------------------------- reveal

    /// <summary>Expands the folders leading to <paramref name="node"/>, selects it and scrolls it into view.</summary>
    public void Reveal(FsNode node)
    {
        if (_root is null) return;
        var chain = node.Ancestors().Reverse().Append(node).ToList();
        if (chain.Count == 0 || !ReferenceEquals(chain[0], _root)) return;

        for (int k = 0; k < chain.Count - 1; k++)
        {
            int idx = IndexOf(chain[k]);
            if (idx < 0) return;
            if (!_rows[idx].IsExpanded) Expand(idx);

            // The next folder may be hidden behind a "more items" row.
            var parentRow = _rows[idx];
            int position = Array.IndexOf(parentRow.Node.Children, chain[k + 1]);
            while (position >= parentRow.Shown && parentRow.Shown < parentRow.Node.Children.Length)
            {
                idx = IndexOf(chain[k]);
                int moreIdx = idx + 1;
                while (moreIdx < _rows.Count && !(_rows[moreIdx].IsMore && ReferenceEquals(_rows[moreIdx].Node, chain[k]))) moreIdx++;
                if (moreIdx >= _rows.Count) break;
                LoadMore(moreIdx);
            }
        }

        int target = IndexOf(node);
        if (target < 0) return;
        _suppress = true;
        List.SelectedItems.Clear();
        List.SelectedItem = _rows[target];
        _suppress = false;
        List.ScrollIntoView(_rows[target]);
    }

    public void ClearSelection()
    {
        _suppress = true;
        List.SelectedItems.Clear();
        _suppress = false;
    }

    // ---------------------------------------------------------------- events

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        NodesSelected?.Invoke(SelectedNodes);
    }

    private void Expander_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TreeRow row) return;
        int idx = _rows.IndexOf(row);
        if (idx < 0) return;
        if (row.IsExpanded) Collapse(idx); else Expand(idx);
        e.Handled = true;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UiHelpers.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext is not TreeRow row) return;
        int idx = _rows.IndexOf(row);
        if (idx < 0) return;

        if (row.IsMore) LoadMore(idx);
        else if (row.Node.IsDirectory) { if (row.IsExpanded) Collapse(idx); else Expand(idx); }
        else Hub?.OpenNodes(new[] { row.Node });
        e.Handled = true;
    }

    private void List_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (List.SelectedItem is TreeRow row && List.SelectedItems.Count == 1)
        {
            int idx = _rows.IndexOf(row);
            if (e.Key == Key.Right && row.CanExpand && !row.IsExpanded) { Expand(idx); e.Handled = true; return; }
            if (e.Key == Key.Left)
            {
                if (row.IsExpanded) Collapse(idx);
                else
                {
                    int p = idx - 1;
                    while (p >= 0 && _rows[p].Depth >= row.Depth) p--;
                    if (p >= 0) List.SelectedItem = _rows[p];
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && row.IsMore) { LoadMore(idx); e.Handled = true; return; }
            if (e.Key == Key.Enter && row.Node.IsDirectory && Keyboard.Modifiers == ModifierKeys.None)
            {
                if (row.IsExpanded) Collapse(idx); else Expand(idx);
                e.Handled = true;
                return;
            }
        }
        if (Hub is not null && Hub.HandleKey(e, SelectedNodes)) e.Handled = true;
    }

    private void List_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (UiHelpers.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { } item && !item.IsSelected)
        {
            List.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var nodes = SelectedNodes;
        if (Hub is null || nodes.Count == 0) { e.Handled = true; return; }
        List.ContextMenu = Hub.BuildMenu(nodes);
    }
}
