using System.Windows;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class WindowSizingTests
{
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(240)]
    public void MaximizationCoversTheExactWorkAreaWithoutAResizeFrameGap(uint dpi)
    {
        foreach (var origin in new[] { (-3840, 0), (0, 0), (3840, -1200) })
        foreach (var taskbar in new[] { (0, 0, 0, 80), (80, 0, 0, 0), (0, 80, 0, 0), (0, 0, 80, 0) })
        {
            var monitor = new WindowSizing.NativeRect
            {
                Left = origin.Item1, Top = origin.Item2, Right = origin.Item1 + 3840, Bottom = origin.Item2 + 2560
            };
            var work = new WindowSizing.NativeRect
            {
                Left = monitor.Left + taskbar.Item1, Top = monitor.Top + taskbar.Item2,
                Right = monitor.Right - taskbar.Item3, Bottom = monitor.Bottom - taskbar.Item4
            };
            var bounds = new WindowSizing.MinMaxInfo();
            WindowSizing.SetMaximizedBounds(ref bounds, monitor, work, dpi, new Size(640, 420),
                new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.Equal(work.Left, monitor.Left + bounds.MaxPosition.X);
            Assert.Equal(work.Top, monitor.Top + bounds.MaxPosition.Y);
            Assert.Equal(work.Right, monitor.Left + bounds.MaxPosition.X + bounds.MaxSize.X);
            Assert.Equal(work.Bottom, monitor.Top + bounds.MaxPosition.Y + bounds.MaxSize.Y);
            Assert.Equal(bounds.MaxSize.X, bounds.MaxTrackSize.X);
            Assert.Equal(bounds.MaxSize.Y, bounds.MaxTrackSize.Y);
        }
    }

    [Fact]
    public void ExplicitMaximumAndTinyDisplaysClampTrackSizesAtTheCurrentDpi()
    {
        var monitor = new WindowSizing.NativeRect { Right = 1920, Bottom = 1080 };
        var work = new WindowSizing.NativeRect { Right = 1900, Bottom = 1040 };
        var bounds = new WindowSizing.MinMaxInfo();
        WindowSizing.SetMaximizedBounds(ref bounds, monitor, work, 192, new Size(1000, 700), new Size(600, 450));
        Assert.Equal(1200, bounds.MaxSize.X);
        Assert.Equal(900, bounds.MaxSize.Y);
        Assert.Equal(bounds.MaxSize.X, bounds.MinTrackSize.X);
        Assert.Equal(bounds.MaxSize.Y, bounds.MinTrackSize.Y);
        Assert.Throws<InvalidOperationException>(() => WindowSizing.AvailableSize(0, 100, 96));
        Assert.Throws<InvalidOperationException>(() => WindowSizing.AvailableSize(100, 100, 0));
    }
}
