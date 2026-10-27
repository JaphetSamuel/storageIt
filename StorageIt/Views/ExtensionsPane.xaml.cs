using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StorageIt.Models;
using StorageIt.Services;

namespace StorageIt.Views;

public sealed class ExtensionRow
{
    public ExtensionRow(ExtensionStat stat, long total)
    {
        Stat = stat;
        Fraction = total > 0 ? (double)stat.Size / total : 0;
    }

    public ExtensionStat Stat { get; }
    public double Fraction { get; }
    public Brush Swatch => ExtensionColors.GetBrush(Stat.Extension);
    public string SizeText => SizeFormat.Format(Stat.Size);
    public string CountText => SizeFormat.Count(Stat.Count);
    public string PercentText => SizeFormat.Percent(Fraction);
    public double BarWidth => Math.Max(1, Fraction * 120);
}

public sealed class FileRow
{
    public FileRow(FsNode node) => Node = node;
    public FsNode Node { get; }
    public string Name => Node.Name;
    public string SizeText => SizeFormat.Format(Node.Size);
    public string Folder => Node.Parent?.FullPath ?? "";
    public string ModifiedText => Node.Modified.ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>Groups everything under the current folder by file extension.</summary>
public partial class ExtensionsPane : UserControl
{
    private FsNode? _scope;
    private bool _dirty;
    private CancellationTokenSource? _cts;
    private bool _suppress;

    public ExtensionsPane()
    {
        InitializeComponent();
        IsVisibleChanged += (_, _) => { if (IsVisible && _dirty) _ = RecomputeAsync(); };
    }

    public ActionHub? Hub { get; set; }

    /// <summary>The extension currently picked in the list, or null.</summary>
    public string? SelectedExtension => (ExtList.SelectedItem as ExtensionRow)?.Stat.Extension;

    public event Action<string?>? ExtensionSelected;
    public event Action<IReadOnlyList<FsNode>>? FilesSelected;

    /// <summary>Points the pane at a folder; the work happens when the pane is on screen.</summary>
    public void SetScope(FsNode? scope)
    {
        _scope = scope;
        _dirty = true;
        if (IsVisible) _ = RecomputeAsync();
    }

    public void MarkDirty()
    {
        _dirty = true;
        if (IsVisible) _ = RecomputeAsync();
    }

    private async Task RecomputeAsync()
    {
        _dirty = false;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var scope = _scope;
        var previous = SelectedExtension;

        if (scope is null)
        {
            ExtList.ItemsSource = null;
            FileList.ItemsSource = null;
            Placeholder.Visibility = Visibility.Visible;
            return;
        }

        List<ExtensionStat> stats;
        try { stats = await Task.Run(() => ExtensionStats.Compute(scope, cts.Token), cts.Token); }
        catch (OperationCanceledException) { return; }

        long total = stats.Sum(s => s.Size);
        Placeholder.Visibility = stats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _suppress = true;
        ExtList.ItemsSource = stats.Select(s => new ExtensionRow(s, total)).ToList();
        var again = ExtList.Items.OfType<ExtensionRow>().FirstOrDefault(r => r.Stat.Extension == previous);
        if (again is not null) ExtList.SelectedItem = again;
        _suppress = false;

        if (again is null)
        {
            FileList.ItemsSource = null;
            FilesHeader.Text = "Largest files of the selected type";
            ExtensionSelected?.Invoke(null);
        }
        else await ShowFilesAsync(again.Stat.Extension);
    }

    private async void ExtList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        var ext = SelectedExtension;
        ExtensionSelected?.Invoke(ext);
        if (ext is null || _scope is null) { FileList.ItemsSource = null; return; }
        await ShowFilesAsync(ext);
    }

    private async Task ShowFilesAsync(string ext)
    {
        var scope = _scope;
        if (scope is null) return;
        List<FsNode> files;
        try { files = await Task.Run(() => ExtensionStats.LargestFiles(scope, ext, 500)); }
        catch (OperationCanceledException) { return; }
        if (SelectedExtension != ext) return;   // the user moved on while we were collecting
        FileList.ItemsSource = files.Select(f => new FileRow(f)).ToList();
        FilesHeader.Text = $"Largest “{(ext == FsNode.NoExtension ? "no extension" : ext)}” files" + (files.Count == 500 ? " (top 500)" : "");
    }

    // --------------------------------------------------------- file list

    private IReadOnlyList<FsNode> SelectedFiles => FileList.SelectedItems.OfType<FileRow>().Select(r => r.Node).ToList();

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => FilesSelected?.Invoke(SelectedFiles);

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UiHelpers.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext is FileRow row)
            Hub?.OpenNodes(new[] { row.Node });
    }

    private void FileList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Hub is not null && Hub.HandleKey(e, SelectedFiles)) e.Handled = true;
    }

    private void FileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (UiHelpers.FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { } item && !item.IsSelected)
        {
            FileList.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void FileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var nodes = SelectedFiles;
        if (Hub is null || nodes.Count == 0) { e.Handled = true; return; }
        FileList.ContextMenu = Hub.BuildMenu(nodes);
    }

    /// <summary>Drops rows whose files were deleted.</summary>
    public void PruneDeleted()
    {
        if (Hub is null) return;
        if (FileList.ItemsSource is IEnumerable<FileRow> rows)
            FileList.ItemsSource = rows.Where(r => Hub.IsAttached(r.Node)).ToList();
        MarkDirty();
    }
}
