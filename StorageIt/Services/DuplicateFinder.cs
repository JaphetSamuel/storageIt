using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using StorageIt.Models;

namespace StorageIt.Services;

public sealed record DuplicateGroup(long FileSize, IReadOnlyList<FsNode> Files)
{
    public long Wasted => FileSize * (Files.Count - 1);
}

public sealed class DuplicateProgress
{
    public volatile string Stage = "";
    private long _done, _total;
    public long Done => Interlocked.Read(ref _done);
    public long Total => Interlocked.Read(ref _total);
    internal void SetTotal(long total) { Interlocked.Exchange(ref _total, total); Interlocked.Exchange(ref _done, 0); }
    internal void Add(long n) => Interlocked.Add(ref _done, n);
}

public sealed class DuplicateOptions
{
    /// <summary>Files smaller than this are ignored.</summary>
    public long MinSize { get; init; } = 1024 * 1024;
    /// <summary>Folders (full paths) that are never searched, with everything below them.</summary>
    public IReadOnlyList<string> ExcludedFolders { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Finds identical files in three passes: same size, same first 64 KB, same full SHA-256.
/// Several paths to the same physical file (hard links, as found all over WinSxS) count as one copy.
/// </summary>
public static class DuplicateFinder
{
    private const int PartialBytes = 64 * 1024;

    public static Task<List<DuplicateGroup>> FindAsync(FsNode root, DuplicateOptions options,
        DuplicateProgress progress, CancellationToken ct) =>
        Task.Run(() => Find(root, options, progress, ct), ct);

    public static List<DuplicateGroup> Find(FsNode root, DuplicateOptions options, DuplicateProgress progress, CancellationToken ct)
    {
        progress.Stage = "Grouping files by size";
        var bySize = new Dictionary<long, List<FsNode>>();
        Collect(root, root.FullPath, options, bySize, ct);

        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();

        // Pass 2: first 64 KB.
        progress.Stage = "Comparing file starts";
        progress.SetTotal(candidates.Sum(l => l.Count));
        var partialGroups = new List<List<FsNode>>();
        foreach (var sizeGroup in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var buckets = new Dictionary<string, List<FsNode>>();
            foreach (var f in sizeGroup)
            {
                var h = TryHash(f.FullPath, PartialBytes, out _, ct);
                progress.Add(1);
                if (h is null) continue;
                if (!buckets.TryGetValue(h, out var l)) buckets[h] = l = new List<FsNode>();
                l.Add(f);
            }
            partialGroups.AddRange(buckets.Values.Where(l => l.Count > 1));
        }

        // Pass 3: full content hash.
        progress.Stage = "Hashing file contents";
        progress.SetTotal(partialGroups.Sum(g => g.Sum(f => f.Size)));
        var result = new List<DuplicateGroup>();
        var gate = new object();
        var dop = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), CancellationToken = ct };

        Parallel.ForEach(partialGroups, dop, group =>
        {
            var buckets = new Dictionary<string, Dictionary<FileId, FsNode>>();
            foreach (var f in group)
            {
                ct.ThrowIfCancellationRequested();
                var h = TryHash(f.FullPath, long.MaxValue, out var id, ct, progress);
                if (h is null) continue;
                if (!buckets.TryGetValue(h, out var files)) buckets[h] = files = new Dictionary<FileId, FsNode>();
                files.TryAdd(id, f);   // same physical file reached through several paths: keep one
            }
            foreach (var files in buckets.Values.Where(b => b.Count > 1))
            {
                var list = files.Values.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
                lock (gate) result.Add(new DuplicateGroup(list[0].Size, list));
            }
        });

        result.Sort(static (a, b) => b.Wasted.CompareTo(a.Wasted));
        return result;
    }

    private static void Collect(FsNode node, string path, DuplicateOptions options,
        Dictionary<long, List<FsNode>> bySize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (options.ExcludedFolders.Any(x => Scanner.IsSameOrInside(path, x))) return;
        foreach (var c in node.Children)
        {
            if (c.IsDirectory)
            {
                if (!c.IsReparsePoint) Collect(c, Path.Combine(path, c.Name), options, bySize, ct);
            }
            else if (c.Size >= options.MinSize && c.Size > 0 && !c.IsReparsePoint)
            {
                if (!bySize.TryGetValue(c.Size, out var l)) bySize[c.Size] = l = new List<FsNode>();
                l.Add(c);
            }
        }
    }

    /// <summary>SHA-256 of up to <paramref name="limit"/> bytes, or null if the file cannot be read.</summary>
    private static string? TryHash(string path, long limit, out FileId id, CancellationToken ct, DuplicateProgress? progress = null)
    {
        id = default;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            id = FileId.Of(handle, path);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[Math.Min(limit, 1 << 20)];
            long offset = 0;
            while (offset < limit)
            {
                ct.ThrowIfCancellationRequested();
                int want = (int)Math.Min(buffer.Length, limit - offset);
                int read = RandomAccess.Read(handle, buffer.AsSpan(0, want), offset);
                if (read <= 0) break;
                sha.AppendData(buffer, 0, read);
                offset += read;
                progress?.Add(read);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Volume + file index: identifies one physical file regardless of how many paths lead to it.</summary>
    internal readonly record struct FileId(uint Volume, ulong Index, string? PathFallback)
    {
        public static FileId Of(SafeFileHandle handle, string path)
        {
            if (GetFileInformationByHandle(handle, out var info))
                return new FileId(info.dwVolumeSerialNumber, ((ulong)info.nFileIndexHigh << 32) | info.nFileIndexLow, null);
            return new FileId(0, 0, path.ToUpperInvariant());
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
            public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
    }
}
