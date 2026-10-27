using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.FileIO;

namespace StorageIt.Services;

public sealed record DeleteResult(List<string> Deleted, List<(string Path, string Error)> Failed);

/// <summary>Open / reveal / properties / delete, done the way Explorer does them.</summary>
public static class ShellActions
{
    public static void Open(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void Reveal(string path)
    {
        // /select needs the argument as one quoted token; ArgumentList would escape the comma wrongly.
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"", UseShellExecute = true });
    }

    public static bool ShowProperties(string path, IntPtr owner)
    {
        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SeeMaskInvokeIdList,
            hwnd = owner,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
        };
        return ShellExecuteEx(ref info);
    }

    /// <summary>
    /// Returns a reason when the path must never be deleted from here (drive roots, the Windows folder, ...),
    /// otherwise null.
    /// </summary>
    public static string? RefuseReason(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetPathRoot(Path.GetFullPath(path))?.TrimEnd(Path.DirectorySeparatorChar) == full || full.Length <= 2)
            return "A whole drive cannot be deleted from here.";

        foreach (var (folder, what) in ProtectedFolders())
        {
            if (string.Equals(full, folder, StringComparison.OrdinalIgnoreCase))
                return $"The {what} folder cannot be deleted from here.";
        }
        return null;
    }

    /// <summary>True for locations where deleting things can break Windows or installed programs.</summary>
    public static bool IsSensitive(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var (folder, _) in SensitiveFolders())
            if (Scanner.IsSameOrInside(full, folder)) return true;
        return false;
    }

    /// <summary>The Recycle Bin only exists for fixed local drives.</summary>
    public static bool RecycleBinAvailable(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root is not null && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch { return false; }
    }

    public static DeleteResult Delete(IEnumerable<(string path, bool isDirectory)> items, bool permanent)
    {
        var result = new DeleteResult(new(), new());
        foreach (var (path, isDir) in items)
        {
            try
            {
                var opt = permanent || !RecycleBinAvailable(path) ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
                if (isDir) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, opt, UICancelOption.DoNothing);
                else FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, opt, UICancelOption.DoNothing);
                result.Deleted.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
                                          or System.Security.SecurityException or InvalidOperationException)
            {
                result.Failed.Add((path, ex.Message));
            }
        }
        return result;
    }

    private static IEnumerable<(string, string)> ProtectedFolders()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\'), "Windows");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'), "user profile");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\'), "Program Files");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86).TrimEnd('\\'), "Program Files (x86)");
    }

    private static IEnumerable<(string, string)> SensitiveFolders()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Windows");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Program Files (x86)");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData");
    }

    private const uint SeeMaskInvokeIdList = 0x0000000C;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIconOrMonitor;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ShellExecuteExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
}
