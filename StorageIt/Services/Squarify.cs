using System.Windows;

namespace StorageIt.Services;

/// <summary>Squarified treemap layout (Bruls, Huizing, van Wijk).</summary>
public static class Squarify
{
    /// <summary>
    /// Lays out <paramref name="count"/> items whose <paramref name="areas"/> (sorted descending,
    /// already scaled so they sum to at most the bounds' area) fill <paramref name="bounds"/>.
    /// </summary>
    public static void Layout(IReadOnlyList<double> areas, int count, Rect bounds, Action<int, Rect> emit)
    {
        var rect = bounds;
        int i = 0;
        while (i < count && rect.Width > 0 && rect.Height > 0)
        {
            double side = Math.Min(rect.Width, rect.Height);
            int start = i, j = i;
            double rowSum = 0, worst = double.MaxValue;

            while (j < count)
            {
                double newSum = rowSum + areas[j];
                double newWorst = Worst(areas[start], areas[j], newSum, side);
                if (j > start && newWorst > worst) break;
                rowSum = newSum;
                worst = newWorst;
                j++;
            }

            double thickness = rowSum / side;
            if (rect.Width >= rect.Height)
            {
                // Vertical strip on the left edge.
                double y = rect.Y;
                for (int k = start; k < j; k++)
                {
                    double h = thickness > 0 ? areas[k] / thickness : 0;
                    emit(k, new Rect(rect.X, y, thickness, h));
                    y += h;
                }
                rect = new Rect(rect.X + thickness, rect.Y, Math.Max(0, rect.Width - thickness), rect.Height);
            }
            else
            {
                // Horizontal strip along the top edge.
                double x = rect.X;
                for (int k = start; k < j; k++)
                {
                    double w = thickness > 0 ? areas[k] / thickness : 0;
                    emit(k, new Rect(x, rect.Y, w, thickness));
                    x += w;
                }
                rect = new Rect(rect.X, rect.Y + thickness, rect.Width, Math.Max(0, rect.Height - thickness));
            }
            i = j;
        }
    }

    // Worst aspect ratio of a row whose largest item is `max` and smallest is `min`.
    private static double Worst(double max, double min, double sum, double side)
    {
        if (sum <= 0 || min <= 0) return double.MaxValue;
        double s2 = side * side, sum2 = sum * sum;
        return Math.Max(s2 * max / sum2, sum2 / (s2 * min));
    }
}
