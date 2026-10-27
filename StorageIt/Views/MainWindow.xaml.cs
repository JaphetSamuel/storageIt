using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StorageIt.Models;
using StorageIt.Services;

namespace StorageIt.Views;

public sealed record LegendItem(string Label, Brush Brush, string SizeText);

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand ScanCommand = new("Scan", nameof(ScanCommand), typeof(MainWindow));
    public static readonly RoutedUICommand RescanCommand = new("Rescan", nameof(RescanCommand), typeof(MainWindow));

    private readonly ActionHub _hub;
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CancellationTokenSource? _cts;
    private ScanState? _state;
    private List<string> _targets = new();
    private FsNode? _root;
    private FsNode? _viewRoot;

    public MainWindow()
    {
        InitializeComponent();

        _hub = new ActionHub(this);
        Tree.Hub = Exts.Hub = Dups.Hub = _hub;
        Dups.RootProvider = () => _root;
        Dups.ViewRootProvider = () => _viewRoot;

        _hub.NodesDeleted += OnNodesDeleted;
        _hub.RescanRequested += () => _ = StartScanAsync(_targets);
        _hub.ShowInMapRequested += n => SetViewRoot(n, reveal: true);
        _hub.ManageCommandsRequested += ShowCommandsDialog;
        _hub.Status += SetStatus;

        Tree.NodesSelected += OnTreeSelected;
        Exts.ExtensionSelected += _ => UpdateHighlight();
        Exts.FilesSelected += OnListFilesSelected;
        Dups.HighlightChanged += UpdateHighlight;
        Dups.FilesSelected += OnListFilesSelected;

        Map.NodeClicked += OnMapClicked;
        Map.NodeActivated += OnMapActivated;
        Map.NodeContextRequested += OnMapContext;
        Map.EmptyText = "Choose drives or folders to scan.\nYou can also drop a folder on this window.";

        _scanTimer.Tick += (_, _) => ShowScanProgress();
        SetStatus("Ready. Press Ctrl+O to choose what to scan.");
    }

    // ------------------------------------------------------------- scanning

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs().Skip(1).Select(CleanArgument).Where(Directory.Exists).ToList();
        if (args.Count > 0) _ = StartScanAsync(args);
        else ShowScanDialog();
    }

    /// <summary>
    /// Windows reads <c>"C:\"</c> on a command line as <c>C:"</c> (the backslash escapes the quote), so strip
    /// stray quotes, and turn a bare drive letter into the drive root rather than "the current folder on C:".
    /// </summary>
    internal static string CleanArgument(string arg)
    {
        var a = arg.Trim().Trim('"').Trim();
        return a.Length == 2 && a[1] == ':' ? a + "\\" : a;
    }

    private void Scan_Executed(object sender, ExecutedRoutedEventArgs e) => ShowScanDialog();
    private void Rescan_Executed(object sender, ExecutedRoutedEventArgs e) => _ = StartScanAsync(_targets);
    private void Rescan_CanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _targets.Count > 0;
    private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void ShowScanDialog()
    {
        var dlg = new ScanTargetsWindow(_targets) { Owner = this };
        if (dlg.ShowDialog() == true) _ = StartScanAsync(dlg.Targets);
    }

    private async Task StartScanAsync(IReadOnlyList<string> targets)
    {
        if (targets.Count == 0) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var state = _state = new ScanState();
        _targets = targets.ToList();
        var sw = Stopwatch.StartNew();

        StopButton.IsEnabled = true;
        ScanProgress.Visibility = Visibility.Visible;
        _scanTimer.Start();
        CommandManager.InvalidateRequerySuggested();

        try
        {
            var root = await Scanner.ScanAsync(targets, state, cts.Token);
            var stats = await Task.Run(() => ExtensionStats.Compute(root), cts.Token);
            ApplyRoot(root, stats, state, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            SetStatus(_root is null ? "Scan cancelled." : "Scan cancelled; the previous results are still shown.");
        }
        catch (ArgumentException)
        {
            SetStatus("None of the chosen places exist.");
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                _scanTimer.Stop();
                StopButton.IsEnabled = false;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowScanProgress()
    {
        if (_state is not { } s) return;
        SetStatus($"Scanning…  {SizeFormat.Count(s.Files)} files  ·  {SizeFormat.Format(s.Bytes)}  ·  {s.CurrentPath}");
    }

    private void ApplyRoot(FsNode root, List<ExtensionStat> stats, ScanState state, TimeSpan elapsed)
    {
        _root = root;
        _hub.Root = root;
        ExtensionColors.Assign(stats);
        Legend.ItemsSource = stats.Take(12)
            .Select(s => new LegendItem(s.Label, ExtensionColors.GetBrush(s.Extension), SizeFormat.Format(s.Size))).ToList();

        Tree.Load(root);
        Dups.Reset();
        Exts.SetScope(root);
        SetViewRoot(root, reveal: false);
        Title = root.IsVirtualRoot ? "StorageIt — several locations" : $"StorageIt — {root.Name}";

        var notes = new List<string>();
        if (state.Errors > 0) notes.Add($"{SizeFormat.Count(state.Errors)} folders could not be read");
        if (state.CloudOnly > 0) notes.Add($"{SizeFormat.Count(state.CloudOnly)} online-only cloud files not counted");
        SetStatus($"{SizeFormat.Count(root.FileCount)} files in {SizeFormat.Count(state.Directories)} folders  ·  {SizeFormat.Format(root.Size)}  ·  scanned in {elapsed.TotalSeconds:0.0} s"
                  + (notes.Count > 0 ? "  ·  " + string.Join("  ·  ", notes) : ""));
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => _cts?.Cancel();

    // ------------------------------------------------------- drag and drop

    private static string[] DroppedFolders(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();
        return ((string[])e.Data.GetData(DataFormats.FileDrop)!)
            .Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p) ?? "")
            .Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFolders(e).Length > 0 ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var folders = DroppedFolders(e);
        if (folders.Length > 0) _ = StartScanAsync(folders);
    }

    // -------------------------------------------------------- view handling

    /// <summary>Points the map (and the file-type list) at a folder.</summary>
    private void SetViewRoot(FsNode? node, bool reveal)
    {
        if (node is null) return;
        if (!node.IsDirectory) node = node.Parent ?? node;
        _viewRoot = node;

        Map.EmptyText = node.Size == 0 ? "This folder is empty." : "";
        Map.ViewRoot = node;
        Map.SelectedNode = null;
        CrumbPath.Text = node.IsVirtualRoot ? "All scanned locations" : node.FullPath;
        CrumbInfo.Text = $"{SizeFormat.Format(node.Size)}  ·  {SizeFormat.Count(node.FileCount)} files";
        UpButton.IsEnabled = node.Parent is not null;
        Exts.SetScope(node);
        if (reveal) Tree.Reveal(node);
        UpdateHighlight();
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_viewRoot?.Parent is { } parent) SetViewRoot(parent, reveal: true);
    }

    private void OnTreeSelected(IReadOnlyList<FsNode> nodes)
    {
        if (nodes.Count != 1) return;
        var n = nodes[0];
        if (n.IsDirectory) { SetViewRoot(n, reveal: false); return; }

        if (_viewRoot is null || !n.IsDescendantOf(_viewRoot)) SetViewRoot(n.Parent, reveal: false);
        Map.SelectedNode = n;
    }

    private void OnListFilesSelected(IReadOnlyList<FsNode> nodes)
    {
        var n = nodes.FirstOrDefault();
        if (n is null) return;
        if (_viewRoot is null || !n.IsDescendantOf(_viewRoot)) SetViewRoot(n.Parent, reveal: false);
        Map.SelectedNode = n;
    }

    private void OnMapClicked(FsNode? node)
    {
        if (node is not null) Tree.Reveal(node);
    }

    private void OnMapActivated(FsNode node)
    {
        if (node.IsDirectory) SetViewRoot(node, reveal: true);
        else _hub.OpenNodes(new[] { node });
    }

    private void OnMapContext(FsNode node, Point where)
    {
        var menu = _hub.BuildMenu(new[] { node });
        menu.PlacementTarget = Map;
        menu.Placement = PlacementMode.Relative;
        menu.HorizontalOffset = where.X;
        menu.VerticalOffset = where.Y;
        menu.IsOpen = true;
    }

    private void Map_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Back) { Up_Click(sender, e); e.Handled = true; return; }
        var nodes = Map.SelectedNode is { } n ? new[] { n } : Array.Empty<FsNode>();
        if (_hub.HandleKey(e, nodes)) e.Handled = true;
    }

    private void LeftTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, LeftTabs)) return;   // ignore lists inside the tabs
        UpdateHighlight();
    }

    /// <summary>Dims everything in the map except the file type or duplicates the visible tab is about.</summary>
    private void UpdateHighlight()
    {
        Func<FsNode, bool>? lit = null;
        if (ReferenceEquals(LeftTabs.SelectedItem, ExtTab) && Exts.SelectedExtension is { } ext)
            lit = n => string.Equals(n.Extension, ext, StringComparison.OrdinalIgnoreCase);
        else if (ReferenceEquals(LeftTabs.SelectedItem, DupTab) && Dups.HighlightEnabled)
            lit = Dups.IsDuplicate;
        Map.Highlight = lit;
    }

    // ------------------------------------------------------------- deletion

    private void OnNodesDeleted(IReadOnlyList<FsNode> formerParents)
    {
        if (_root is null) return;
        var view = _viewRoot is not null && _hub.IsAttached(_viewRoot)
            ? _viewRoot
            : formerParents.FirstOrDefault(_hub.IsAttached) ?? _root;

        Tree.Rebuild();
        Exts.PruneDeleted();
        Dups.PruneDeleted();
        SetViewRoot(view, reveal: false);
        SetStatus($"{SizeFormat.Count(_root.FileCount)} files  ·  {SizeFormat.Format(_root.Size)} in view after deleting.");
    }

    // ------------------------------------------------------------- commands

    private void Commands_Click(object sender, RoutedEventArgs e) => ShowCommandsDialog();

    private void ShowCommandsDialog()
    {
        var dlg = new CommandsWindow(_hub.Commands) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _hub.Commands = dlg.Result;
        CommandStore.Save(_hub.Commands);
        SetStatus("Cleanup commands saved.");
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
