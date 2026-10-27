using System.Windows;
using System.Windows.Controls;
using StorageIt.Models;
using StorageIt.Services;
using StorageIt.Views;

namespace StorageIt.Tests;

/// <summary>Context menus are built by one shared routine; check what they offer for files and folders.</summary>
[Collection("command store")]
public class MenuTests
{
    /// <summary>What a menu offers, as plain strings (WPF objects must stay on the thread that made them).</summary>
    private sealed record MenuShape(string[] Top, string[] Custom);

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = work(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("STA work failed", error);
        return result;
    }

    private static MenuShape Shape(ContextMenu menu)
    {
        var top = menu.Items.OfType<MenuItem>().ToList();
        var custom = top.SingleOrDefault(m => (string)m.Header == "Cleanup commands");
        return new MenuShape(
            top.Select(m => (string)m.Header).ToArray(),
            custom?.Items.OfType<MenuItem>().Select(m => (string)m.Header).ToArray() ?? Array.Empty<string>());
    }

    [Fact]
    public void Menu_offers_the_core_actions_and_only_the_custom_commands_that_fit()
    {
        var old = CommandStore.FilePath;
        CommandStore.FilePath = Path.Combine(Path.GetTempPath(), "storageit-menu-" + Guid.NewGuid().ToString("N"), "commands.json");
        try
        {
            CommandStore.Save(new[]
            {
                new CustomCommand { Name = "Files only", Program = "x.exe", AppliesTo = CommandTarget.Files },
                new CustomCommand { Name = "Folders only", Program = "x.exe", AppliesTo = CommandTarget.Folders },
                new CustomCommand { Name = "Anything", Program = "x.exe" },
            });

            var (file, folder, mixed, virt, virtualItemCount) = OnSta(() =>
            {
                var hub = new ActionHub(new Window());
                var root = new FsNode("C:\\", true, null);
                var dir = new FsNode("Docs", true, root);
                var f = new FsNode("a.txt", false, root);
                var v = new FsNode("This selection", true, null) { IsVirtualRoot = true };
                var virtualMenu = hub.BuildMenu(new[] { v });
                return (Shape(hub.BuildMenu(new[] { f })), Shape(hub.BuildMenu(new[] { dir })),
                        Shape(hub.BuildMenu(new[] { f, dir })), virtualMenu, virtualMenu.Items.Count);
            });

            Assert.Contains("Open", file.Top);
            Assert.Contains("Show in Explorer", file.Top);
            Assert.Contains("Properties", file.Top);
            Assert.Contains("Delete (Recycle Bin)", file.Top);
            Assert.Contains("Delete permanently…", file.Top);

            Assert.Equal(new[] { "Files only", "Anything", "Manage commands…" }, file.Custom);
            Assert.Equal(new[] { "Folders only", "Anything", "Manage commands…" }, folder.Custom);
            Assert.Equal(new[] { "Anything", "Manage commands…" }, mixed.Custom);

            Assert.Contains("Show in space map", folder.Top);
            Assert.DoesNotContain("Show in Explorer", mixed.Top);   // single-item actions only
            Assert.Equal(0, virtualItemCount);                      // the synthetic root has no file actions
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(CommandStore.FilePath)!, true); } catch (IOException) { }
            CommandStore.FilePath = old;
        }
    }
}

public class CommandLineTests
{
    [Theory]
    [InlineData(@"C:""", @"C:\")]               // what Windows hands over for "C:\"
    [InlineData("C:", @"C:\")]
    [InlineData(@"""D:\Data\My Files""", @"D:\Data\My Files")]
    [InlineData(@"  E:\x  ", @"E:\x")]
    public void Stray_quotes_and_bare_drive_letters_are_cleaned_up(string raw, string expected) =>
        Assert.Equal(expected, MainWindow.CleanArgument(raw));
}
