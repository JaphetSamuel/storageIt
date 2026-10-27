using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StorageIt.Models;
using StorageIt.Services;

namespace StorageIt.Views;

/// <summary>Either a group heading or one copy of a duplicated file.</summary>
public sealed class DupRow : INotifyPropertyChanged
{
    private bool _checked;

    private readonly string _basePath;

    public DupRow(DuplicateGroup group, FsNode? node, string basePath)
    {
        Group = group;
        Node = node;
        _basePath = basePath;
    }

    public DuplicateGroup Group { get; }
    public FsNode? Node { get; }
    public bool IsHeader => Node is null;

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            CheckedChanged?.Invoke();
        }
    }

    public static event Action? CheckedChanged;

    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FileVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
    public Brush Swatch => ExtensionColors.GetBrush(Group.Files[0].Extension);
    public string HeaderTitle => $"{Group.Files[0].Name}";
    public string HeaderDetail => $"{Group.Files.Count} copies × {SizeFormat.Format(Group.FileSize)}  =  {SizeFormat.Format(Group.Wasted)} wasted";
    /// <summary>Path relative to the scan root, so the part that tells copies apart is not cut off.</summary>
    public string PathText
    {
        get
        {
            var full = Node?.FullPath ?? "";
            return _basePath.Length > 0 && full.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase)
                ? full[_basePath.Length..].TrimStart('\\')
                : full;
        }
    }

    public string FullPathText => Node?.FullPath ?? "";
    public string ModifiedText => Node is null ? "" : Node.Modified.ToString("g", CultureInfo.CurrentCulture);

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class DuplicatesPane : UserControl
{
    private readonly List<DupRow> _rows = new();
    private List<DuplicateGroup> _groups = new();
    private CancellationTokenSource? _cts;
    private DuplicateProgress? _progress;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public DuplicatesPane()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateProgress();
        DupRow.CheckedChanged += UpdateDeleteButton;
        Unloaded += (_, _) => DupRow.CheckedChanged -= UpdateDeleteButton;
    }

    public ActionHub? Hub { get; set; }

    /// <summary>The scan root, and the folder currently shown in the map.</summary>
    public Func<FsNode?>? RootProvider { get; set; }
    public Func<FsNode?>? ViewRootProvider { get; set; }

    /// <summary>Raised when the set of duplicate files to highlight in the map changes.</summary>
    public event Action? HighlightChanged;
    public event Action<IReadOnlyList<FsNode>>? FilesSelected;

    public bool HighlightEnabled => HighlightBox.IsChecked == true && _groups.Count > 0;

    public bool IsDuplicate(FsNode node) => _duplicateSet.Contains(node);
    private HashSet<FsNode> _duplicateSet = new();

    /// <summary>Forget old results, e.g. after a new scan.</summary>
    public void Reset()
    {
        _cts?.Cancel();
        _groups = new();
        Populate();
        StatusText.Text = "Finds files with identical content, biggest waste first.";
    }

    private async void FindButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null) { _cts.Cancel(); return; }   // button doubles as "Stop"

        var root = ScopeBox.SelectedIndex == 1 ? ViewRootProvider?.Invoke() : RootProvider?.Invoke();
        if (root is null) { StatusText.Text = "Scan something first."; return; }

        long minSize = long.Parse((string)((ComboBoxItem)MinSizeBox.SelectedItem).Tag, CultureInfo.InvariantCulture);
        var excluded = SkipSystemBox.IsChecked == true
            ? new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            }.Where(p => !string.IsNullOrEmpty(p)).ToArray()
            : Array.Empty<string>();

        var cts = _cts = new CancellationTokenSource();
        _progress = new DuplicateProgress();
        FindButton.Content = "Stop";
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        _timer.Start();

        try
        {
            _groups = await DuplicateFinder.FindAsync(root, new DuplicateOptions { MinSize = minSize, ExcludedFolders = excluded }, _progress, cts.Token);
            long wasted = _groups.Sum(g => g.Wasted);
            StatusText.Text = _groups.Count == 0
                ? "No duplicates found."
                : $"{SizeFormat.Count(_groups.Count)} groups of identical files — {SizeFormat.Format(wasted)} could be freed.";
        }
        catch (OperationCanceledException)
        {
            _groups = new();
            StatusText.Text = "Search stopped.";
        }
        finally
        {
            _timer.Stop();
            _cts = null;
            _progress = null;
            FindButton.Content = "Find duplicates";
            Progress.Visibility = Visibility.Collapsed;
        }
        Populate();
    }

    private void UpdateProgress()
    {
        if (_progress is null) return;
        long total = _progress.Total;
        if (total > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Maximum = total;
            Progress.Value = Math.Min(total, _progress.Done);
        }
        else Progress.IsIndeterminate = true;
        StatusText.Text = _progress.Stage + "…";
    }

    private void Populate()
    {
        _rows.Clear();
        var basePath = RootProvider?.Invoke() is { IsVirtualRoot: false } root ? root.FullPath : "";
        foreach (var g in _groups)
        {
            _rows.Add(new DupRow(g, null, basePath));
            foreach (var f in g.Files) _rows.Add(new DupRow(g, f, basePath));
        }
        _duplicateSet = _groups.SelectMany(g => g.Files).ToHashSet();
        ResultList.ItemsSource = null;
        ResultList.ItemsSource = _rows;
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _cts is null && StatusText.Text.StartsWith("No duplicates", StringComparison.Ordinal)
            ? "Every file in this scope is unique (at the chosen minimum size)." : "";
        UpdateDeleteButton();
        HighlightChanged?.Invoke();
    }

    // ------------------------------------------------------------- checking

    private void KeepOldest_Click(object sender, RoutedEventArgs e) => CheckAllBut(g => g.Files.OrderBy(f => f.Modified).First());
    private void KeepNewest_Click(object sender, RoutedEventArgs e) => CheckAllBut(g => g.Files.OrderByDescending(f => f.Modified).First());
    private void ClearChecks_Click(object sender, RoutedEventArgs e) => CheckAllBut(null);

    private void CheckAllBut(Func<DuplicateGroup, FsNode>? keeper)
    {
        var keep = keeper is null ? new HashSet<FsNode>() : _groups.Select(keeper).ToHashSet();
        foreach (var row in _rows.Where(r => !r.IsHeader))
            row.IsChecked = keeper is not null && !keep.Contains(row.Node!);
    }

    private List<DupRow> CheckedRows => _rows.Where(r => r.IsChecked && !r.IsHeader).ToList();

    private void UpdateDeleteButton()
    {
        var rows = CheckedRows;
        DeleteButton.IsEnabled = rows.Count > 0;
        DeleteButton.Content = rows.Count == 0 ? "Delete checked…"
            : $"Delete {rows.Count} checked ({SizeFormat.Format(rows.Sum(r => r.Node!.Size))})…";
    }

    private async void DeleteChecked_Click(object sender, RoutedEventArgs e)
    {
        if (Hub is null) return;
        var rows = CheckedRows;
        var allChecked = _groups.FirstOrDefault(g => g.Files.All(f => rows.Any(r => ReferenceEquals(r.Node, f))));
        if (allChecked is not null)
        {
            MessageBox.Show(Window.GetWindow(this),
                $"Every copy of “{allChecked.Files[0].Name}” is checked. Leave at least one copy of each file unchecked.",
                "StorageIt", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await Hub.DeleteAsync(rows.Select(r => r.Node!).ToList(), permanent: false);
    }

    /// <summary>Removes copies that were deleted (from here or elsewhere) and groups that are no longer duplicates.</summary>
    public void PruneDeleted()
    {
        if (Hub is null || _groups.Count == 0) return;
        _groups = _groups
            .Select(g => new DuplicateGroup(g.FileSize, g.Files.Where(Hub.IsAttached).ToList()))
            .Where(g => g.Files.Count > 1)
            .ToList();
        long wasted = _groups.Sum(g => g.Wasted);
        StatusText.Text = _groups.Count == 0 ? "No duplicates left." : $"{SizeFormat.Count(_groups.Count)} groups — {SizeFormat.Format(wasted)} could be freed.";
        Populate();
    }

    private void HighlightBox_Changed(object sender, RoutedEventArgs e) => HighlightChanged?.Invoke();

    // ----------------------------------------------------------- list events

    private IReadOnlyList<FsNode> SelectedFiles =>
        ResultList.SelectedItems.OfType<DupRow>().Where(r => !r.IsHeader).Select(r => r.Node!).ToList();

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e) => FilesSelected?.Invoke(SelectedFiles);

    private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UiHelpers.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext is DupRow { IsHeader: false } row)
            Hub?.OpenNodes(new[] { row.Node! });
    }

    private void ResultList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && ResultList.SelectedItem is DupRow { IsHeader: false } row && e.OriginalSource is not CheckBox)
        {
            row.IsChecked = !row.IsChecked;
            e.Handled = true;
            return;
        }
        if (Hub is not null && Hub.HandleKey(e, SelectedFiles)) e.Handled = true;
    }

    private void ResultList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (UiHelpers.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { } item && !item.IsSelected)
        {
            ResultList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void ResultList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var nodes = SelectedFiles;
        if (Hub is null || nodes.Count == 0) { e.Handled = true; return; }
        ResultList.ContextMenu = Hub.BuildMenu(nodes);
    }
}
