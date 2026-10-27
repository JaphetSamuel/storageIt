namespace StorageIt.Models;

/// <summary>A file or folder in the scanned tree. Children are kept sorted by size, largest first.</summary>
public sealed class FsNode
{
    public const string NoExtension = "(none)";

    public FsNode(string name, bool isDirectory, FsNode? parent)
    {
        Name = name;
        IsDirectory = isDirectory;
        Parent = parent;
    }

    /// <summary>File/folder name. For a scan root this is the full path (e.g. <c>C:\</c>).</summary>
    public string Name { get; }
    public bool IsDirectory { get; }
    public FsNode? Parent { get; private set; }
    public long Size { get; set; }
    public long FileCount { get; set; }
    public DateTime Modified { get; set; }
    public string Extension { get; set; } = NoExtension;
    public FsNode[] Children { get; set; } = Array.Empty<FsNode>();

    /// <summary>Junction / symlink: listed but never followed, so nothing is counted twice.</summary>
    public bool IsReparsePoint { get; set; }

    /// <summary>Synthetic parent used when several drives/folders are scanned together.</summary>
    public bool IsVirtualRoot { get; init; }

    public string FullPath
    {
        get
        {
            if (IsVirtualRoot) return string.Empty;
            if (Parent is null || Parent.IsVirtualRoot) return Name;
            return Path.Combine(Parent.FullPath, Name);
        }
    }

    public IEnumerable<FsNode> Ancestors()
    {
        for (var p = Parent; p is not null; p = p.Parent) yield return p;
    }

    public bool IsDescendantOf(FsNode other)
    {
        for (var p = Parent; p is not null; p = p.Parent)
            if (ReferenceEquals(p, other)) return true;
        return false;
    }

    /// <summary>Enumerates this node and everything below it (depth first).</summary>
    public IEnumerable<FsNode> Descendants(bool includeSelf = true)
    {
        var stack = new Stack<FsNode>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (includeSelf || !ReferenceEquals(n, this)) yield return n;
            for (int i = n.Children.Length - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    public IEnumerable<FsNode> Files() => Descendants().Where(n => !n.IsDirectory);

    /// <summary>Detaches this node from the tree and subtracts its size from every ancestor.</summary>
    public void RemoveFromTree()
    {
        var parent = Parent;
        if (parent is null) return;
        long size = Size, count = IsDirectory ? FileCount : 1;
        parent.Children = parent.Children.Where(c => !ReferenceEquals(c, this)).ToArray();
        for (var p = parent; p is not null; p = p.Parent)
        {
            p.Size -= size;
            p.FileCount -= count;
        }
        Parent = null;
    }

    /// <summary>Recomputes size/count from the children and sorts them largest first.</summary>
    public void Seal()
    {
        long size = 0, count = 0;
        foreach (var c in Children) { size += c.Size; count += c.IsDirectory ? c.FileCount : 1; }
        Size = size;
        FileCount = count;
        Array.Sort(Children, static (a, b) => b.Size.CompareTo(a.Size));
    }

    public override string ToString() => IsVirtualRoot ? "(selection)" : FullPath;
}
