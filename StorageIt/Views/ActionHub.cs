using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using StorageIt.Models;
using StorageIt.Services;

namespace StorageIt.Views;

/// <summary>
/// The one place that knows how to open, delete, inspect and run custom commands on nodes,
/// so the map, the tree and the lists all behave identically.
/// </summary>
public sealed class ActionHub
{
    private readonly Window _owner;

    public ActionHub(Window owner)
    {
        _owner = owner;
        Commands = CommandStore.Load();
    }

    public FsNode? Root { get; set; }
    public List<CustomCommand> Commands { get; set; }

    /// <summary>Raised after nodes were removed; carries the folders they used to live in.</summary>
    public event Action<IReadOnlyList<FsNode>>? NodesDeleted;
    public event Action? RescanRequested;
    public event Action<FsNode>? ShowInMapRequested;
    public event Action? ManageCommandsRequested;
    public event Action<string>? Status;

    public bool IsAttached(FsNode node)
    {
        var n = node;
        while (n.Parent is not null) n = n.Parent;
        return ReferenceEquals(n, Root);
    }

    // ------------------------------------------------------------------ menu

    public ContextMenu BuildMenu(IReadOnlyList<FsNode> nodes)
    {
        nodes = nodes.Where(n => !n.IsVirtualRoot).ToList();
        var menu = new ContextMenu();
        if (nodes.Count == 0) return menu;
        bool single = nodes.Count == 1;
        var first = nodes[0];

        menu.Items.Add(Item(single && first.IsDirectory ? "Open in Explorer" : "Open", "Enter", () => OpenNodes(nodes)));
        if (single)
        {
            menu.Items.Add(Item("Show in Explorer", null, () => Safe(() => ShellActions.Reveal(first.FullPath))));
            menu.Items.Add(Item("Properties", "Alt+Enter", () => Properties(first)));
        }
        menu.Items.Add(Item(single ? "Copy path" : "Copy paths", "Ctrl+C", () => CopyPaths(nodes)));
        if (single && first.IsDirectory)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Show in space map", null, () => ShowInMapRequested?.Invoke(first)));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete (Recycle Bin)", "Del", () => _ = DeleteAsync(nodes, permanent: false)));
        menu.Items.Add(Item("Delete permanently…", "Shift+Del", () => _ = DeleteAsync(nodes, permanent: true)));

        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Cleanup commands" };
        foreach (var cmd in Commands.Where(c => nodes.All(n => c.AppliesToItem(n.IsDirectory))))
        {
            var c = cmd;
            custom.Items.Add(Item(c.Name, null, () => _ = RunCommandAsync(c, nodes)));
        }
        if (custom.Items.Count > 0) custom.Items.Add(new Separator());
        custom.Items.Add(Item("Manage commands…", null, () => ManageCommandsRequested?.Invoke()));
        menu.Items.Add(custom);
        return menu;
    }

    private static MenuItem Item(string header, string? gesture, Action click)
    {
        var mi = new MenuItem { Header = header, InputGestureText = gesture };
        mi.Click += (_, _) => click();
        return mi;
    }

    // ------------------------------------------------------------------ keys

    /// <summary>Shared keyboard shortcuts for any list or the map. Returns true when the key was used.</summary>
    public bool HandleKey(KeyEventArgs e, IReadOnlyList<FsNode> nodes)
    {
        nodes = nodes.Where(n => !n.IsVirtualRoot).ToList();
        if (nodes.Count == 0) return false;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Delete && (mods & ~ModifierKeys.Shift) == 0)
        {
            _ = DeleteAsync(nodes, permanent: (mods & ModifierKeys.Shift) != 0);
            return true;
        }
        if (key == Key.Enter && mods == ModifierKeys.Alt && nodes.Count == 1) { Properties(nodes[0]); return true; }
        if (key == Key.Enter && mods == ModifierKeys.None) { OpenNodes(nodes); return true; }
        if (key == Key.C && mods == ModifierKeys.Control) { CopyPaths(nodes); return true; }
        return false;
    }

    // --------------------------------------------------------------- actions

    public void OpenNodes(IReadOnlyList<FsNode> nodes)
    {
        if (nodes.Count > 8 &&
            MessageBox.Show(_owner, $"Open {nodes.Count} items at once?", "StorageIt", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        foreach (var n in nodes.Where(n => !n.IsVirtualRoot))
            Safe(() => ShellActions.Open(n.FullPath));
    }

    public void Properties(FsNode node)
    {
        var hwnd = new WindowInteropHelper(_owner).Handle;
        Safe(() =>
        {
            if (!ShellActions.ShowProperties(node.FullPath, hwnd))
                throw new IOException("Windows could not show the properties for this item.");
        });
    }

    public void CopyPaths(IReadOnlyList<FsNode> nodes)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, nodes.Select(n => n.FullPath)));
            Status?.Invoke(nodes.Count == 1 ? "Path copied to the clipboard." : $"{nodes.Count} paths copied to the clipboard.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Status?.Invoke("The clipboard is busy; try again.");
        }
    }

    public async Task DeleteAsync(IReadOnlyList<FsNode> requested, bool permanent)
    {
        // Selecting a folder and a file inside it must not delete the file twice.
        var nodes = requested.Where(n => !n.IsVirtualRoot && IsAttached(n)).ToList();
        nodes = nodes.Where(n => !nodes.Any(o => !ReferenceEquals(o, n) && n.IsDescendantOf(o))).ToList();
        if (nodes.Count == 0) return;

        foreach (var n in nodes)
        {
            var refusal = ShellActions.RefuseReason(n.FullPath);
            if (refusal is not null)
            {
                MessageBox.Show(_owner, $"{n.FullPath}\n\n{refusal}", "StorageIt", MessageBoxButton.OK, MessageBoxImage.Stop);
                return;
            }
        }

        bool recycle = !permanent && nodes.All(n => ShellActions.RecycleBinAvailable(n.FullPath));
        long bytes = nodes.Sum(n => n.Size);
        var sb = new System.Text.StringBuilder();
        sb.Append(recycle ? "Move to the Recycle Bin" : "Permanently delete").Append(' ')
          .Append(nodes.Count == 1 ? $"“{nodes[0].Name}”" : $"{nodes.Count} items")
          .Append(" (").Append(SizeFormat.Format(bytes)).Append(")?\n");
        if (!recycle)
        {
            sb.Append(permanent
                ? "\nThis cannot be undone."
                : "\nThese items are not on a local fixed drive, so Windows has no Recycle Bin for them: they will be deleted for good.");
        }
        if (nodes.Count > 1)
        {
            sb.Append("\n\n");
            // "parent\name" keeps each entry on one line while still telling similar names apart.
            foreach (var n in nodes.Take(6))
                sb.Append("•  ").Append(n.Parent is { IsVirtualRoot: false } p ? Path.Combine(p.Name, n.Name) : n.Name).Append('\n');
            if (nodes.Count > 6) sb.Append($"…and {nodes.Count - 6} more");
        }
        if (nodes.Any(n => ShellActions.IsSensitive(n.FullPath)))
            sb.Append("\n\nWarning: this is inside a Windows or program folder. Deleting it can break Windows or installed programs.");

        var answer = MessageBox.Show(_owner, sb.ToString().TrimEnd(), recycle ? "Delete" : "Delete permanently",
            MessageBoxButton.YesNo, recycle ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var items = nodes.Select(n => (n.FullPath, n.IsDirectory)).ToList();
        var previous = _owner.Cursor;
        _owner.Cursor = Cursors.Wait;
        DeleteResult result;
        try { result = await Task.Run(() => ShellActions.Delete(items, permanent)); }
        finally { _owner.Cursor = previous; }

        var byPath = nodes.ToDictionary(n => n.FullPath, StringComparer.OrdinalIgnoreCase);
        var parents = new List<FsNode>();
        long freed = 0;
        foreach (var path in result.Deleted)
        {
            var node = byPath[path];
            if (node.Parent is not null) parents.Add(node.Parent);
            freed += node.Size;
            node.RemoveFromTree();
        }

        if (result.Deleted.Count > 0)
        {
            Status?.Invoke($"Deleted {result.Deleted.Count} item(s), freed {SizeFormat.Format(freed)}.");
            NodesDeleted?.Invoke(parents);
        }
        if (result.Failed.Count > 0)
        {
            var msg = string.Join("\n", result.Failed.Take(5).Select(f => $"{f.Path}\n    {f.Error}"));
            MessageBox.Show(_owner, $"{result.Failed.Count} item(s) could not be deleted:\n\n{msg}\n\nThey may be in use or protected. Rescan to refresh the view.",
                "StorageIt", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public async Task RunCommandAsync(CustomCommand cmd, IReadOnlyList<FsNode> nodes)
    {
        var items = nodes.Where(n => !n.IsVirtualRoot).Select(n => (n.FullPath, n.IsDirectory)).ToList();
        if (items.Count == 0) return;
        if (cmd.Confirm)
        {
            string what = items.Count == 1 ? $"“{nodes[0].Name}”" : $"{items.Count} items";
            string how = cmd.PerItem || items.Count == 1 ? "" : " (one run for all of them)";
            var preview = CommandRunner.Expand($"{cmd.Program} {cmd.Arguments}", items[0].FullPath, items[0].IsDirectory,
                cmd.PerItem ? null : items.Select(i => i.FullPath).ToList());
            if (MessageBox.Show(_owner, $"Run “{cmd.Name}” on {what}{how}?\n\n{preview}", "Cleanup command",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }

        Status?.Invoke($"Running “{cmd.Name}”…");
        var results = await CommandRunner.RunAsync(cmd, items);
        Status?.Invoke($"“{cmd.Name}” finished.");

        if (cmd.Mode == CommandRunMode.HiddenWithOutput)
            new OutputWindow(results) { Owner = _owner }.Show();
        if (cmd.RescanAfter) RescanRequested?.Invoke();
    }

    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            MessageBox.Show(_owner, ex.Message, "StorageIt", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

internal static class UiHelpers
{
    public static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }
}
