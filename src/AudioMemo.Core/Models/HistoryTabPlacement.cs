namespace AudioMemo.Core.Models;

public static class HistoryTabPlacement
{
    public static double Offset(double position, double height, double tabHeight)
    {
        if (!double.IsFinite(position) || position is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(position));
        var (margin, travel) = Bounds(height, tabHeight);
        return margin + travel * position;
    }

    public static double Position(double offset, double height, double tabHeight)
    {
        if (!double.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        var (margin, travel) = Bounds(height, tabHeight);
        return travel == 0 ? 0 : Math.Clamp((offset - margin) / travel, 0, 1);
    }

    private static (double Margin, double Travel) Bounds(double height, double tabHeight)
    {
        if (!double.IsFinite(height) || !double.IsFinite(tabHeight) || height < 0 || tabHeight < 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        var available = Math.Max(0, height - tabHeight);
        var margin = Math.Min(8, available / 2);
        return (margin, available - margin * 2);
    }
}
