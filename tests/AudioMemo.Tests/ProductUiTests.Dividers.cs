using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifySlimDividers(Application application, string language)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { UiLanguage = language, SaveFolder = folder.Root, HistoryVisible = true };
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings);
        try
        {
            Invoke(window, "ApplySettingsToControls");
            var root = Assert.IsType<Grid>(window.Content);
            foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
            {
                AppAppearance.Apply(application, theme);
                foreach (var size in new[] { new Size(1440, 900), new Size(768, 900), new Size(640, 420) })
                {
                    root.Measure(size);
                    root.Arrange(new Rect(size));
                    Pump(root);
                    foreach (var name in new[] { "ReadingDivider", "NotesDivider" })
                    {
                        var divider = Assert.IsType<GridSplitter>(window.FindName(name));
                        if (divider.Visibility != Visibility.Visible)
                            continue;
                        divider.ApplyTemplate();
                        var line = Assert.IsType<Border>(divider.Template.FindName("DividerLine", divider));
                        var horizontal = divider.ResizeDirection == GridResizeDirection.Rows;
                        Assert.Equal(2, horizontal ? line.ActualHeight : line.ActualWidth, 1);
                        Assert.Equal(6, horizontal ? divider.ActualHeight : divider.ActualWidth, 1);
                        Assert.Equal(BrushColor(application.FindResource("AppDivider")), BrushColor(line.Background));
                        Assert.True(divider.Focusable);
                        Assert.NotNull(VisualTreeHelper.HitTest(divider,
                            horizontal ? new Point(divider.ActualWidth / 2, 0.5) : new Point(0.5, divider.ActualHeight / 2)));

                        var grid = Assert.IsType<Grid>(divider.Parent);
                        var before = horizontal ? grid.RowDefinitions[0].ActualHeight : grid.ColumnDefinitions[0].ActualWidth;
                        divider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                        divider.RaiseEvent(new DragDeltaEventArgs(horizontal ? 0 : 16, horizontal ? 16 : 0)
                            { RoutedEvent = Thumb.DragDeltaEvent });
                        divider.RaiseEvent(new DragCompletedEventArgs(horizontal ? 0 : 16, horizontal ? 16 : 0, false)
                            { RoutedEvent = Thumb.DragCompletedEvent });
                        Pump(root);
                        var after = horizontal ? grid.RowDefinitions[0].ActualHeight : grid.ColumnDefinitions[0].ActualWidth;
                        Assert.InRange(after - before, 15, 17);
                    }
                    if (size.Width == 1440)
                    {
                        var grip = Assert.IsType<Thumb>(window.FindName("HistoryResizeGrip"));
                        grip.ApplyTemplate();
                        var line = Assert.IsType<Border>(grip.Template.FindName("DividerLine", grip));
                        Assert.Equal(2, line.ActualWidth, 1);
                        Assert.Equal(7, grip.Width);
                        var scale = VisualTreeHelper.GetDpi(grip).DpiScaleX;
                        Assert.Equal(Math.Round(7 * scale) / scale, grip.ActualWidth, 6);
                        Assert.Equal(BrushColor(application.FindResource("AppDivider")), BrushColor(line.Background));
                        Assert.Equal(0, Assert.IsType<Border>(window.FindName("HistoryPanel")).BorderThickness.Right);
                        Assert.NotNull(VisualTreeHelper.HitTest(grip, new Point(0.5, grip.ActualHeight / 2)));
                    }
                }
            }
        }
        finally { window.Close(); AppAppearance.Apply(application, AppThemes.Light); }
    }
}
