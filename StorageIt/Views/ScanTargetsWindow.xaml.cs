using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StorageIt.Services;

namespace StorageIt.Views;

public sealed class DriveRow : INotifyPropertyChanged
{
    private bool _checked;

    public DriveRow(DriveInfo drive)
    {
        Path = drive.RootDirectory.FullName;
        string label = "";
        try { label = drive.VolumeLabel; } catch (IOException) { }
        Title = string.IsNullOrWhiteSpace(label) ? Path : $"{label} ({Path.TrimEnd('\\')})";
        Subtitle = $"{drive.DriveType} · {drive.DriveFormat}";
        Total = drive.TotalSize;
        Free = drive.AvailableFreeSpace;
    }

    public string Path { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public long Total { get; }
    public long Free { get; }
    public double UsedFraction => Total > 0 ? (double)(Total - Free) / Total : 0;
    public string UsageText => $"{SizeFormat.Format(Total - Free)} used of {SizeFormat.Format(Total)}";

    public bool IsChecked
    {
        get => _checked;
        set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class ScanTargetsWindow : Window
{
    private readonly List<DriveRow> _drives = new();

    public ScanTargetsWindow(IEnumerable<string>? previous = null)
    {
        InitializeComponent();
        var prev = (previous ?? Array.Empty<string>()).ToList();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var row = new DriveRow(d);
                row.IsChecked = prev.Any(p => string.Equals(p, row.Path, StringComparison.OrdinalIgnoreCase));
                _drives.Add(row);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        DriveList.ItemsSource = _drives;

        foreach (var p in prev.Where(p => !_drives.Any(d => string.Equals(d.Path, p, StringComparison.OrdinalIgnoreCase))))
            FolderList.Items.Add(p);
        UpdateHint();
    }

    public IReadOnlyList<string> Targets { get; private set; } = Array.Empty<string>();

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose folders to scan", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FolderNames)
            if (!FolderList.Items.Cast<string>().Contains(f, StringComparer.OrdinalIgnoreCase))
                FolderList.Items.Add(f);
        UpdateHint();
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FolderList.SelectedItems.Cast<string>().ToList()) FolderList.Items.Remove(item);
        UpdateHint();
    }

    private void UpdateHint() => FolderHint.Visibility = FolderList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        var targets = _drives.Where(d => d.IsChecked).Select(d => d.Path)
            .Concat(FolderList.Items.Cast<string>()).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one drive or add a folder.", "StorageIt", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Targets = targets;
        DialogResult = true;
    }
}
