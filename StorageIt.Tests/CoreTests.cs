using System.Runtime.InteropServices;
using System.Windows;
using StorageIt.Models;
using StorageIt.Services;

namespace StorageIt.Tests;

/// <summary>Creates a throw-away folder tree and deletes it afterwards.</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "storageit-tests-" + Guid.NewGuid().ToString("N"));

    public TempTree() => Directory.CreateDirectory(Root);

    public string Write(string relative, int bytes, byte fill = 1)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, bytes).ToArray());
        return path;
    }

    public string WriteBytes(string relative, byte[] data)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

public class SquarifyTests
{
    [Fact]
    public void Rectangles_fill_the_bounds_without_overlap()
    {
        var bounds = new Rect(0, 0, 800, 500);
        double[] sizes = { 60, 30, 20, 20, 10, 5, 5, 2, 1, 1 };
        double scale = bounds.Width * bounds.Height / sizes.Sum();
        var areas = sizes.Select(s => s * scale).ToArray();

        var rects = new Rect[areas.Length];
        Squarify.Layout(areas, areas.Length, bounds, (i, r) => rects[i] = r);

        Assert.Equal(bounds.Width * bounds.Height, rects.Sum(r => r.Width * r.Height), 3);
        for (int i = 0; i < rects.Length; i++)
        {
            Assert.True(bounds.Contains(Rect.Inflate(rects[i], -0.001, -0.001)), $"rect {i} leaves the bounds");
            Assert.Equal(areas[i], rects[i].Width * rects[i].Height, 3);
            for (int j = i + 1; j < rects.Length; j++)
            {
                var overlap = Rect.Intersect(Rect.Inflate(rects[i], -0.01, -0.01), Rect.Inflate(rects[j], -0.01, -0.01));
                Assert.True(overlap.IsEmpty || overlap.Width * overlap.Height < 0.01, $"rects {i} and {j} overlap");
            }
        }
    }

    [Fact]
    public void Biggest_item_gets_the_biggest_rectangle()
    {
        var bounds = new Rect(0, 0, 400, 300);
        double[] sizes = { 50, 25, 15, 10 };
        var areas = sizes.Select(s => s * bounds.Width * bounds.Height / 100).ToArray();
        var rects = new Rect[4];
        Squarify.Layout(areas, 4, bounds, (i, r) => rects[i] = r);
        Assert.True(rects[0].Width * rects[0].Height > rects[1].Width * rects[1].Height);
    }

    [Fact]
    public void Items_are_reasonably_square()
    {
        var bounds = new Rect(0, 0, 1000, 600);
        var areas = Enumerable.Repeat(1.0, 24).Select(_ => bounds.Width * bounds.Height / 24).ToArray();
        Squarify.Layout(areas, 24, bounds, (_, r) =>
        {
            double ratio = Math.Max(r.Width / r.Height, r.Height / r.Width);
            Assert.True(ratio < 3, $"aspect ratio {ratio:0.0} is too stretched");
        });
    }
}

public class ScannerTests
{
    [Fact]
    public void Sizes_are_summed_and_children_sorted_largest_first()
    {
        using var t = new TempTree();
        t.Write(@"a\big.bin", 5000);
        t.Write(@"a\small.txt", 100);
        t.Write(@"b\mid.dat", 1200);
        t.Write("top.log", 40);

        var root = Scanner.Scan(new[] { t.Root }, new ScanState(), default);

        Assert.Equal(6340, root.Size);
        Assert.Equal(4, root.FileCount);
        Assert.Equal(new[] { "a", "b", "top.log" }, root.Children.Select(c => c.Name));
        Assert.Equal(new[] { "big.bin", "small.txt" }, root.Children[0].Children.Select(c => c.Name));
        Assert.Equal(".bin", root.Children[0].Children[0].Extension);
    }

    [Fact]
    public void Extensions_are_lowercase_and_missing_ones_are_marked()
    {
        using var t = new TempTree();
        t.Write("Photo.JPG", 10);
        t.Write("README", 10);

        var root = Scanner.Scan(new[] { t.Root }, new ScanState(), default);

        Assert.Contains(root.Children, c => c.Name == "Photo.JPG" && c.Extension == ".jpg");
        Assert.Contains(root.Children, c => c.Name == "README" && c.Extension == FsNode.NoExtension);
    }

    [Fact]
    public void Several_targets_share_a_virtual_root_and_nested_ones_are_merged()
    {
        using var t = new TempTree();
        t.Write(@"one\f.bin", 300);
        t.Write(@"two\g.bin", 700);

        var targets = new[] { Path.Combine(t.Root, "one"), Path.Combine(t.Root, "two"), Path.Combine(t.Root, "one", "sub") };
        Directory.CreateDirectory(targets[2]);
        var root = Scanner.Scan(targets, new ScanState(), default);

        Assert.True(root.IsVirtualRoot);
        Assert.Equal(2, root.Children.Length);
        Assert.Equal(1000, root.Size);
        Assert.Equal(Path.Combine(t.Root, "two"), root.Children[0].FullPath);   // larger first
    }

    [Fact]
    public void Full_path_is_rebuilt_from_the_tree()
    {
        using var t = new TempTree();
        var file = t.Write(@"x\y\z.txt", 5);
        var root = Scanner.Scan(new[] { t.Root }, new ScanState(), default);
        var node = root.Files().Single();
        Assert.Equal(file, node.FullPath);
    }

    [Fact]
    public void Cancelling_stops_the_scan()
    {
        using var t = new TempTree();
        t.Write("a.txt", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Scanner.Scan(new[] { t.Root }, new ScanState(), cts.Token));
    }

    [Fact]
    public void Folders_that_cannot_be_opened_are_counted_and_the_rest_still_scans()
    {
        using var t = new TempTree();
        t.Write(@"open\a.bin", 700);
        t.Write(@"locked\secret.bin", 9000);
        var locked = Path.Combine(t.Root, "locked");
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

        static void Icacls(string path, string args)
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe", $"\"{path}\" {args}") { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit();
        }

        Icacls(locked, $"/deny *{sid}:(RD)");   // deny "list folder" to ourselves
        try
        {
            var state = new ScanState();
            var root = Scanner.Scan(new[] { t.Root }, state, default);

            Assert.Equal(1, state.Errors);
            Assert.Equal(700, root.Size);   // the open folder is counted, the locked one contributes nothing
        }
        finally
        {
            Icacls(locked, $"/remove:d *{sid}");   // restore access so the temp folder can be deleted
        }
    }

    [Fact]
    public void Junctions_are_listed_but_not_followed()
    {
        using var t = new TempTree();
        t.Write(@"real\data.bin", 2000);
        var link = Path.Combine(t.Root, "link");
        // A junction (unlike a symlink) can be created without special privileges.
        using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(t.Root, "real")}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create the junction");

        try
        {
            var root = Scanner.Scan(new[] { t.Root }, new ScanState(), default);

            Assert.Equal(2000, root.Size);   // not 4000
            Assert.True(root.Children.Single(c => c.Name == "link").IsReparsePoint);
        }
        finally
        {
            Directory.Delete(link);   // non-recursive: removes the junction, not its target
        }
    }
}

public class FsNodeTests
{
    [Fact]
    public void Removing_a_node_updates_every_ancestor()
    {
        using var t = new TempTree();
        t.Write(@"a\b\x.bin", 1000);
        t.Write(@"a\b\y.bin", 500);
        t.Write(@"a\z.bin", 100);
        var root = Scanner.Scan(new[] { t.Root }, new ScanState(), default);
        var b = root.Descendants().Single(n => n.Name == "b");

        b.RemoveFromTree();

        Assert.Equal(100, root.Size);
        Assert.Equal(1, root.FileCount);
        Assert.DoesNotContain(root.Descendants(), n => n.Name == "x.bin");
        Assert.Null(b.Parent);
    }
}

public class DuplicateTests
{
    private static List<DuplicateGroup> Find(FsNode root, long min = 1, params string[] exclude) =>
        DuplicateFinder.Find(root, new DuplicateOptions { MinSize = min, ExcludedFolders = exclude }, new DuplicateProgress(), default);

    [Fact]
    public void Identical_files_are_grouped_and_same_size_different_content_is_not()
    {
        using var t = new TempTree();
        t.Write(@"a\one.dat", 4000, 7);
        t.Write(@"b\copy-of-one.dat", 4000, 7);
        t.Write(@"c\third.dat", 4000, 7);
        t.Write(@"d\same-size-other.dat", 4000, 9);
        t.Write(@"e\unique.dat", 1234, 3);

        var groups = Find(Scanner.Scan(new[] { t.Root }, new ScanState(), default));

        var g = Assert.Single(groups);
        Assert.Equal(3, g.Files.Count);
        Assert.Equal(8000, g.Wasted);
        Assert.DoesNotContain(g.Files, f => f.Name == "same-size-other.dat");
    }

    [Fact]
    public void Files_that_differ_only_after_the_first_64KB_are_told_apart()
    {
        using var t = new TempTree();
        var a = new byte[200_000]; var b = new byte[200_000];
        b[150_000] = 1;
        t.WriteBytes("a.bin", a);
        t.WriteBytes("b.bin", b);
        t.WriteBytes("c.bin", (byte[])a.Clone());

        var groups = Find(Scanner.Scan(new[] { t.Root }, new ScanState(), default));

        var g = Assert.Single(groups);
        Assert.Equal(new[] { "a.bin", "c.bin" }, g.Files.Select(f => f.Name).Order());
    }

    [Fact]
    public void Minimum_size_and_excluded_folders_are_respected()
    {
        using var t = new TempTree();
        t.Write(@"keep\a.dat", 5000);
        t.Write(@"keep\b.dat", 5000);
        t.Write(@"skip\c.dat", 5000);
        t.Write(@"tiny\x.dat", 10);
        t.Write(@"tiny\y.dat", 10);
        var root = Scanner.Scan(new[] { t.Root }, new ScanState(), default);

        var excluded = Find(root, 100, Path.Combine(t.Root, "skip"));
        Assert.Equal(new[] { "a.dat", "b.dat" }, Assert.Single(excluded).Files.Select(f => f.Name).Order());

        var all = Find(root, 100);
        Assert.Equal(3, Assert.Single(all).Files.Count);
    }

    [Fact]
    public void Hard_links_to_one_file_are_not_reported_as_duplicates()
    {
        using var t = new TempTree();
        var original = t.Write(@"a\orig.bin", 3000, 5);
        var link = Path.Combine(t.Root, "b", "link.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Assert.True(CreateHardLink(link, original, IntPtr.Zero), "could not create a hard link");

        var groups = Find(Scanner.Scan(new[] { t.Root }, new ScanState(), default));

        Assert.Empty(groups);   // one physical file: deleting either path frees nothing
    }

    [Fact]
    public void Real_copy_next_to_a_hard_link_still_counts_once()
    {
        using var t = new TempTree();
        var original = t.Write(@"a\orig.bin", 3000, 5);
        t.Write(@"c\copy.bin", 3000, 5);
        var link = Path.Combine(t.Root, "b", "link.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        CreateHardLink(link, original, IntPtr.Zero);

        var groups = Find(Scanner.Scan(new[] { t.Root }, new ScanState(), default));

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Files.Count);
        Assert.Equal(3000, g.Wasted);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);
}

public class ExtensionStatsTests
{
    [Fact]
    public void Totals_are_grouped_by_extension_and_largest_files_are_listed()
    {
        using var t = new TempTree();
        t.Write("a.mp4", 9000);
        t.Write(@"x\b.mp4", 3000);
        t.Write("c.txt", 50);
        var root = Scanner.Scan(new[] { t.Root }, new ScanState(), default);

        var stats = ExtensionStats.Compute(root);

        Assert.Equal(".mp4", stats[0].Extension);
        Assert.Equal(12000, stats[0].Size);
        Assert.Equal(2, stats[0].Count);
        Assert.Equal(new[] { "a.mp4", "b.mp4" }, ExtensionStats.LargestFiles(root, ".mp4", 10).Select(f => f.Name));
        Assert.Single(ExtensionStats.LargestFiles(root, ".mp4", 1));
    }
}

[Collection("command store")]
public class CommandTests
{
    [Fact]
    public void Placeholders_are_expanded_for_files_and_folders()
    {
        var file = @"C:\Data\Photos\trip.JPG";
        Assert.Equal(@"C:\Data\Photos\trip.JPG|trip.JPG|trip|.JPG|C:\Data\Photos|C:\Data\Photos|C:\",
            CommandRunner.Expand("{path}|{name}|{nameNoExt}|{ext}|{dir}|{folder}|{drive}", file, false));

        var folder = @"C:\Data\Photos";
        Assert.Equal(@"C:\Data\Photos|Photos||C:\Data|C:\Data\Photos",
            CommandRunner.Expand("{path}|{name}|{ext}|{dir}|{folder}", folder, true));
    }

    [Fact]
    public void Paths_placeholder_lists_every_item_quoted()
    {
        var all = new[] { @"C:\a b\1.txt", @"C:\c\2.txt" };
        Assert.Equal("\"C:\\a b\\1.txt\" \"C:\\c\\2.txt\"", CommandRunner.Expand("{paths}", all[0], false, all));
    }

    [Fact]
    public async Task Hidden_command_returns_its_output_and_exit_code()
    {
        using var t = new TempTree();
        var file = t.Write("hello world.txt", 3);
        var cmd = new CustomCommand { Name = "echo", Program = "cmd.exe", Arguments = "/c echo name=\"{name}\"", Mode = CommandRunMode.HiddenWithOutput };

        var results = await CommandRunner.RunAsync(cmd, new[] { (file, false) });

        var r = Assert.Single(results);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("name=\"hello world.txt\"", r.Output);
    }

    [Fact]
    public async Task One_run_per_item_unless_the_command_takes_all_paths()
    {
        using var t = new TempTree();
        var a = t.Write("a.txt", 1);
        var b = t.Write("b.txt", 1);
        var items = new[] { (a, false), (b, false) };

        var per = await CommandRunner.RunAsync(new CustomCommand { Program = "cmd.exe", Arguments = "/c echo {name}", Mode = CommandRunMode.HiddenWithOutput, PerItem = true }, items);
        var once = await CommandRunner.RunAsync(new CustomCommand { Program = "cmd.exe", Arguments = "/c echo {paths}", Mode = CommandRunMode.HiddenWithOutput, PerItem = false }, items);

        Assert.Equal(2, per.Count);
        var single = Assert.Single(once);
        Assert.Contains("a.txt", single.Output);
        Assert.Contains("b.txt", single.Output);
    }

    [Fact]
    public async Task A_missing_program_is_reported_instead_of_throwing()
    {
        using var t = new TempTree();
        var file = t.Write("a.txt", 1);
        var cmd = new CustomCommand { Program = "definitely-not-a-real-program.exe", Mode = CommandRunMode.HiddenWithOutput };
        var r = Assert.Single(await CommandRunner.RunAsync(cmd, new[] { (file, false) }));
        Assert.Equal(-1, r.ExitCode);
        Assert.StartsWith("Could not start", r.Output);
    }

    [Fact]
    public void Commands_survive_a_save_and_load()
    {
        var old = CommandStore.FilePath;
        CommandStore.FilePath = Path.Combine(Path.GetTempPath(), "storageit-cmds-" + Guid.NewGuid().ToString("N"), "commands.json");
        try
        {
            CommandStore.Save(new[] { new CustomCommand { Name = "Zip", Program = "7z.exe", Arguments = "a \"{path}.7z\" \"{path}\"", AppliesTo = CommandTarget.Folders, RescanAfter = true } });
            var loaded = Assert.Single(CommandStore.Load());
            Assert.Equal("Zip", loaded.Name);
            Assert.Equal(CommandTarget.Folders, loaded.AppliesTo);
            Assert.True(loaded.RescanAfter);
            Assert.True(loaded.AppliesToItem(true));
            Assert.False(loaded.AppliesToItem(false));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(CommandStore.FilePath)!, true); } catch (IOException) { }
            CommandStore.FilePath = old;
        }
    }

    [Fact]
    public void First_run_creates_the_example_commands()
    {
        var old = CommandStore.FilePath;
        CommandStore.FilePath = Path.Combine(Path.GetTempPath(), "storageit-cmds-" + Guid.NewGuid().ToString("N"), "commands.json");
        try
        {
            var defaults = CommandStore.Load();
            Assert.NotEmpty(defaults);
            Assert.True(File.Exists(CommandStore.FilePath));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(CommandStore.FilePath)!, true); } catch (IOException) { }
            CommandStore.FilePath = old;
        }
    }
}

public class ShellSafetyTests
{
    [Fact]
    public void Drive_roots_and_the_windows_folder_cannot_be_deleted()
    {
        Assert.NotNull(ShellActions.RefuseReason(@"C:\"));
        Assert.NotNull(ShellActions.RefuseReason(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.NotNull(ShellActions.RefuseReason(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    [Fact]
    public void Ordinary_files_are_allowed_and_windows_files_are_flagged_sensitive()
    {
        using var t = new TempTree();
        var f = t.Write("x.txt", 1);
        Assert.Null(ShellActions.RefuseReason(f));
        Assert.False(ShellActions.IsSensitive(f));
        Assert.True(ShellActions.IsSensitive(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "kernel32.dll")));
    }

    [Fact]
    public void Permanent_delete_removes_files_and_folders_and_reports_failures()
    {
        using var t = new TempTree();
        var file = t.Write("gone.txt", 1);
        t.Write(@"dir\inner.txt", 1);
        var dir = Path.Combine(t.Root, "dir");

        var result = ShellActions.Delete(new[] { (file, false), (dir, true), (Path.Combine(t.Root, "missing.txt"), false) }, permanent: true);

        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(dir));
        Assert.Equal(2, result.Deleted.Count);
        Assert.Single(result.Failed);
    }
}

public class SizeFormatTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.00 KB")]
    [InlineData(1536 * 1024, "1.50 MB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    public void Sizes_are_human_readable(long bytes, string expected)
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, SizeFormat.Format(bytes)); }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }
}
