using System.Collections.Concurrent;
using StorageIt.Models;

namespace StorageIt.Services;

/// <summary>Live counters the UI can poll while a scan runs.</summary>
public sealed class ScanState
{
    private long _files, _dirs, _bytes, _errors, _cloud;
    public long Files => Interlocked.Read(ref _files);
    public long Directories => Interlocked.Read(ref _dirs);
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Errors => Interlocked.Read(ref _errors);
    /// <summary>Cloud-only placeholders (OneDrive "online only" etc.) that take no local space.</summary>
    public long CloudOnly => Interlocked.Read(ref _cloud);
    public volatile string CurrentPath = "";

    internal void AddFile(long bytes) { Interlocked.Increment(ref _files); Interlocked.Add(ref _bytes, bytes); }
    internal void AddDir() => Interlocked.Increment(ref _dirs);
    internal void AddError() => Interlocked.Increment(ref _errors);
    internal void AddCloud() => Interlocked.Increment(ref _cloud);
}

public static class Scanner
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN (0x40000) and FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (0x400000):
    // set on OneDrive "online only" placeholders, which occupy no local space.
    private const FileAttributes CloudMask = FileAttributes.Offline | (FileAttributes)0x40000 | (FileAttributes)0x400000;

    private static readonly EnumerationOptions Options = new()
    {
        // false: a folder we are not allowed to open throws, which is caught per folder and counted in
        // ScanState.Errors. (With true, .NET skips it silently and the user is never told.)
        IgnoreInaccessible = false,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,            // report hidden/system files and reparse points too
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Win32,
    };

    private static readonly ConcurrentDictionary<string, string> ExtCache = new(StringComparer.OrdinalIgnoreCase);

    public static Task<FsNode> ScanAsync(IEnumerable<string> targets, ScanState state, CancellationToken ct) =>
        Task.Run(() => Scan(targets, state, ct), ct);

    /// <summary>Scans the given drives/folders. Targets nested inside another target are merged into it.</summary>
    public static FsNode Scan(IEnumerable<string> targets, ScanState state, CancellationToken ct)
    {
        var roots = NormalizeTargets(targets);
        if (roots.Count == 0) throw new ArgumentException("Nothing to scan.", nameof(targets));

        FsNode? virtualRoot = roots.Count > 1 ? new FsNode("This selection", true, null) { IsVirtualRoot = true } : null;
        var nodes = new List<FsNode>();
        foreach (var r in roots)
        {
            var node = new FsNode(r, true, virtualRoot) { Modified = SafeLastWrite(r) };
            ScanDirectory(node, r, state, 0, ct);
            nodes.Add(node);
        }

        if (virtualRoot is null) return nodes[0];
        virtualRoot.Children = nodes.ToArray();
        virtualRoot.Seal();
        return virtualRoot;
    }

    internal static List<string> NormalizeTargets(IEnumerable<string> targets)
    {
        var full = new List<string>();
        foreach (var t in targets)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            var p = Path.GetFullPath(t.Trim());
            if (Directory.Exists(p)) full.Add(p);
        }
        var result = new List<string>();
        foreach (var p in full.OrderBy(p => p.Length))
            if (!result.Any(r => IsSameOrInside(p, r))) result.Add(p);
        return result;
    }

    internal static bool IsSameOrInside(string path, string root)
    {
        var r = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var p = path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime SafeLastWrite(string path)
    {
        try { return Directory.GetLastWriteTime(path); } catch { return default; }
    }

    private static void ScanDirectory(FsNode dir, string path, ScanState state, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        state.AddDir();
        state.CurrentPath = path;

        var children = new List<FsNode>();
        var subDirs = new List<(FsNode node, string path)>();
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", Options))
            {
                ct.ThrowIfCancellationRequested();
                var attrs = entry.Attributes;
                bool reparse = (attrs & FileAttributes.ReparsePoint) != 0;

                if ((attrs & FileAttributes.Directory) != 0)
                {
                    var d = new FsNode(entry.Name, true, dir) { Modified = entry.LastWriteTime, IsReparsePoint = reparse };
                    children.Add(d);
                    if (!reparse) subDirs.Add((d, entry.FullName));
                    continue;
                }

                bool cloud = (attrs & CloudMask) != 0;
                long len = 0;
                if (cloud) state.AddCloud();
                else if (entry is FileInfo fi) len = fi.Length;

                children.Add(new FsNode(entry.Name, false, dir)
                {
                    Size = len,
                    FileCount = 1,
                    Modified = entry.LastWriteTime,
                    Extension = ExtensionOf(entry.Name),
                    IsReparsePoint = reparse,
                });
                state.AddFile(len);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            state.AddError();
        }

        if (subDirs.Count > 1 && depth < 3)
        {
            Parallel.ForEach(subDirs,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
                s => ScanDirectory(s.node, s.path, state, depth + 1, ct));
        }
        else
        {
            foreach (var s in subDirs) ScanDirectory(s.node, s.path, state, depth + 1, ct);
        }

        dir.Children = children.ToArray();
        dir.Seal();
    }

    internal static string ExtensionOf(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(ext) || ext.Length == 1) return FsNode.NoExtension;
        return ExtCache.GetOrAdd(ext, static e => e.ToLowerInvariant());
    }
}
