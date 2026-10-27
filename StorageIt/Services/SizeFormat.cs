using System.Globalization;

namespace StorageIt.Services;

public static class SizeFormat
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < Units.Length - 1) { v /= 1024; u++; }
        string fmt = u == 0 ? "0" : v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00";
        return v.ToString(fmt, CultureInfo.CurrentCulture) + " " + Units[u];
    }

    public static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    public static string Percent(double fraction) =>
        fraction >= 0.1 ? (fraction * 100).ToString("0", CultureInfo.CurrentCulture) + " %"
        : (fraction * 100).ToString("0.0", CultureInfo.CurrentCulture) + " %";
}
