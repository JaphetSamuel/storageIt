using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StorageIt.Services;

namespace StorageIt.Views;

public partial class CommandsWindow : Window
{
    private readonly ObservableCollection<CustomCommand> _commands;
    private bool _loading;

    public CommandsWindow(IEnumerable<CustomCommand> commands)
    {
        InitializeComponent();
        _commands = new ObservableCollection<CustomCommand>(commands.Select(c => c.Clone()));
        CommandList.ItemsSource = _commands;
        if (_commands.Count > 0) CommandList.SelectedIndex = 0;
    }

    public List<CustomCommand> Result => _commands.ToList();

    private CustomCommand? Current => CommandList.SelectedItem as CustomCommand;

    private void CommandList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var c = Current;
        Editor.IsEnabled = c is not null;
        if (c is null) return;

        _loading = true;
        NameBox.Text = c.Name;
        ProgramBox.Text = c.Program;
        ArgsBox.Text = c.Arguments;
        TargetBox.SelectedIndex = (int)c.AppliesTo;
        ModeBox.SelectedIndex = (int)c.Mode;
        PerItemBox.IsChecked = c.PerItem;
        ConfirmBox.IsChecked = c.Confirm;
        RescanBox.IsChecked = c.RescanAfter;
        _loading = false;
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || Current is not { } c) return;
        c.Name = NameBox.Text;
        c.Program = ProgramBox.Text;
        c.Arguments = ArgsBox.Text;
        c.AppliesTo = (CommandTarget)Math.Max(0, TargetBox.SelectedIndex);
        c.Mode = (CommandRunMode)Math.Max(0, ModeBox.SelectedIndex);
        c.PerItem = PerItemBox.IsChecked == true;
        c.Confirm = ConfirmBox.IsChecked == true;
        c.RescanAfter = RescanBox.IsChecked == true;
        if (ReferenceEquals(sender, NameBox)) CommandList.Items.Refresh();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var c = new CustomCommand { Name = "New command", Program = "cmd.exe", Arguments = "/c echo \"{path}\" & pause" };
        _commands.Add(c);
        CommandList.SelectedItem = c;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } c) return;
        var copy = c.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name += " (copy)";
        _commands.Add(copy);
        CommandList.SelectedItem = copy;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } c) return;
        int i = CommandList.SelectedIndex;
        _commands.Remove(c);
        if (_commands.Count > 0) CommandList.SelectedIndex = Math.Min(i, _commands.Count - 1);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose a program", Filter = "Programs and scripts|*.exe;*.bat;*.cmd;*.ps1;*.com|All files|*.*" };
        if (dlg.ShowDialog(this) == true) ProgramBox.Text = dlg.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var bad = _commands.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Name) || string.IsNullOrWhiteSpace(c.Program));
        if (bad is not null)
        {
            CommandList.SelectedItem = bad;
            MessageBox.Show(this, "Every command needs a name and a program.", "StorageIt", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
