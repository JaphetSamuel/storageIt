using System.Windows.Media;
using StorageIt.Models;

namespace StorageIt.Services;

public sealed record ExtensionStat(string Extension, long Count, long Size)
{
    public string Label => Extension == FsNode.NoExtension ? "(no extension)" : Extension;
}

/// <summary>Per-extension totals plus a stable color for each extension, shared by every view.</summary>
public static class ExtensionColors
{
    private static readonly string[] Palette =
    {
        "#4C8BF5", "#E5484D", "#3FB950", "#F5A524", "#A371F7", "#22C7D6", "#F265B5", "#E3D23A",
        "#1FA187", "#5B5BD6", "#8BC34A", "#C27B4D", "#C03AC0", "#61AFEF", "#FF7A59", "#9AA33B",
        "#7A8CFF", "#4ADE80", "#FB7185", "#FBBF24",
    };

    private static Dictionary<string, Color> _assigned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Brush> _brushes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Color Fallback = Color.FromRgb(0x8A, 0x94, 0xA6);

    /// <summary>Assigns palette colors to the biggest extensions so the map's main colors are all distinct.</summary>
    public static void Assign(IEnumerable<ExtensionStat> stats)
    {
        var map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        foreach (var s in stats.OrderByDescending(s => s.Size))
        {
            if (i >= Palette.Length) break;
            map[s.Extension] = (Color)ColorConverter.ConvertFromString(Palette[i++]);
        }
        _assigned = map;
        _brushes.Clear();
    }

    public static Color Get(string extension)
    {
        if (_assigned.TryGetValue(extension, out var c)) return c;
        if (extension == FsNode.NoExtension) return Fallback;
        uint h = 2166136261;
        foreach (char ch in extension) h = (h ^ ch) * 16777619;
        return FromHsl((h % 360) / 360.0, 0.50, 0.56);
    }

    /// <summary>Flat brush for swatches in lists.</summary>
    public static Brush GetBrush(string extension)
    {
        if (!_brushes.TryGetValue(extension, out var b))
        {
            var sb = new SolidColorBrush(Get(extension));
            sb.Freeze();
            _brushes[extension] = b = sb;
        }
        return b;
    }

    public static Color Lighten(Color c, double amount) => Blend(c, Colors.White, amount);
    public static Color Darken(Color c, double amount) => Blend(c, Colors.Black, amount);

    public static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return Color.FromRgb((byte)(Hue(p, q, h + 1.0 / 3) * 255), (byte)(Hue(p, q, h) * 255), (byte)(Hue(p, q, h - 1.0 / 3) * 255));
    }
}

public static class ExtensionStats
{
    /// <summary>Totals per extension for every file at or below <paramref name="root"/>, largest first.</summary>
    public static List<ExtensionStat> Compute(FsNode root, CancellationToken ct = default)
    {
        var map = new Dictionary<string, (long count, long size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in root.Descendants())
        {
            if (n.IsDirectory) continue;
            ct.ThrowIfCancellationRequested();
            map.TryGetValue(n.Extension, out var v);
            map[n.Extension] = (v.count + 1, v.size + n.Size);
        }
        return map.Select(kv => new ExtensionStat(kv.Key, kv.Value.count, kv.Value.size))
                  .OrderByDescending(s => s.Size).ThenBy(s => s.Extension).ToList();
    }

    /// <summary>The largest files with the given extension, biggest first.</summary>
    public static List<FsNode> LargestFiles(FsNode root, string extension, int max, CancellationToken ct = default)
    {
        var all = new List<FsNode>();
        foreach (var n in root.Descendants())
        {
            if (n.IsDirectory || !string.Equals(n.Extension, extension, StringComparison.OrdinalIgnoreCase)) continue;
            ct.ThrowIfCancellationRequested();
            all.Add(n);
        }
        all.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        if (all.Count > max) all.RemoveRange(max, all.Count - max);
        return all;
    }
}
