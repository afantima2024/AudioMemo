using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifySharedDialogAppearance(Application application, string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        try
        {
            foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
            {
                var settings = new AppSettings { SaveFolder = folder.Root, UiLanguage = language, Theme = theme, HistoryVisible = false };
                AppAppearance.Apply(application, settings);
                var editor = new TranscriptEditWindow(session, TranscriptCorrectionTests.Plan(session, 0, 1, 2),
                    settings, false, (_, _) => { });
                try
                {
                    var root = MountDialog(editor, new Size(1060, 740));
                    var grid = Assert.IsType<DataGrid>(editor.FindName("CorrectionRows"));
                    var expected = BrushColor(application.FindResource("SolidBackgroundFillColorQuarternaryBrush"));
                    foreach (var enabled in new[] { true, false })
                    {
                        grid.IsEnabled = enabled;
                        Pump(root);
                        AssertThemedTableSurface(application, root, grid);
                        foreach (var item in grid.Items.Cast<TranscriptEditWindow.CorrectionRow>())
                        {
                            Assert.Equal(TranscriptComparisonState.NotCompared, item.ComparisonState);
                            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(item));
                            Assert.Equal(expected, BrushColor(row.Background));
                            var cell = FindChild<DataGridCell>(row)!;
                            var painted = Pixel(cell, (int)cell.ActualWidth - 8, 3);
                            Assert.Equal(expected, painted);
                            if (enabled)
                                AssertReadable(BrushColor(row.Foreground), painted);
                        }
                    }
                    foreach (var switchedTheme in new[] { AppThemes.System,
                        theme == AppThemes.Dark ? AppThemes.Light : AppThemes.Dark, theme })
                    {
                        settings.Theme = switchedTheme;
                        AppAppearance.Apply(application, settings);
                        Pump(root);
                        Assert.False(grid.IsEnabled);
                        AssertThemedTableSurface(application, root, grid);
                    }
                    grid.IsEnabled = true;
                    grid.CurrentCell = new DataGridCellInfo(grid.Items[0], grid.Columns[3]);
                    Assert.True(grid.BeginEdit());
                    Pump(root);
                    var text = Assert.IsType<TextBox>(grid.Columns[3].GetCellContent(grid.Items[0]));
                    var background = BrushColor(text.Background);
                    Assert.Equal(theme == AppThemes.Dark, AppAppearance.Rgb(background).Luminance < 0.4);
                    AssertReadable(BrushColor(text.Foreground), background);
                    Assert.Equal(BrushColor(application.FindResource("TranscriptTextSelectionForeground")), BrushColor(text.SelectionTextBrush));
                    Assert.True(grid.CommitEdit(DataGridEditingUnit.Cell, true));
                    Assert.True(grid.CommitEdit(DataGridEditingUnit.Row, true));
                    AssertStableButtonProfiles(application, settings, root);
                }
                finally { editor.Close(); }

                var main = new MainWindow(new AppStorage(folder.File("preferences")), settings);
                try
                {
                    Invoke(main, "ApplySettingsToControls");
                    Invoke(main, "UpdateControls");
                    var root = Assert.IsType<Grid>(main.Content);
                    root.Measure(new Size(1440, 900));
                    root.Arrange(new Rect(0, 0, 1440, 900));
                    Pump(root);
                    foreach (var name in new[] { "MicEnabled", "PlaybackEnabled", "LiveTextCheck" })
                        AssertCenteredCheckBox(Assert.IsType<CheckBox>(main.FindName(name)));
                    AssertStableButtonProfiles(application, settings, root);
                }
                finally { main.Close(); }

                var preferences = new SettingsWindow(settings);
                try
                {
                    var root = MountDialog(preferences, new Size(980, 840));
                    var mainPoints = Assert.IsType<CheckBox>(preferences.FindName("ShowMainPointsCheck"));
                    var notes = Assert.IsType<CheckBox>(preferences.FindName("ShowMyNotesCheck"));
                    Assert.True(mainPoints.IsChecked);
                    Assert.True(notes.IsChecked);
                    AssertCenteredCheckBox(mainPoints);
                    AssertCenteredCheckBox(notes);
                    mainPoints.IsChecked = false;
                    notes.IsChecked = false;
                    Invoke(preferences, "ReadAppearance");
                    Assert.False(preferences.Settings.MainPointsVisible);
                    Assert.False(preferences.Settings.MyNotesVisible);
                    Assert.True(settings.MainPointsVisible);
                    Assert.True(settings.MyNotesVisible);
                }
                finally { preferences.Close(); }

                var help = new HelpWindow(settings.Shortcuts);
                try
                {
                    MountDialog(help, new Size(700, 540));
                    var logo = Assert.IsType<Image>(help.FindName("AboutLogo"));
                    Assert.Same(AppIcons.Artwork, logo.Source);
                    Assert.Equal(64, logo.ActualWidth);
                    Assert.Equal(64, logo.ActualHeight);
                }
                finally { help.Close(); }

                foreach (var buttons in new[] { MessageBoxButton.OK, MessageBoxButton.OKCancel, MessageBoxButton.YesNo, MessageBoxButton.YesNoCancel })
                {
                    var defaultResult = buttons is MessageBoxButton.OK or MessageBoxButton.OKCancel ? MessageBoxResult.OK : MessageBoxResult.No;
                    var dialog = new MessageDialog(null, "Synthetic confirmation without opening a window.", "AudioMemo",
                        buttons, MessageBoxImage.Question, defaultResult);
                    try
                    {
                        var root = MountDialog(dialog, new Size(540, 260));
                        Assert.Equal(BrushColor(application.FindResource("SolidBackgroundFillColorBaseBrush")), BrushColor(dialog.Background));
                        Assert.Same(AppIcons.Logo, dialog.Icon);
                        var choices = Descendants<Button>(root).Where(button => button.Tag is MessageBoxResult).ToArray();
                        Assert.Equal(defaultResult, Assert.Single(choices, button => button.IsDefault).Tag);
                        var cancelled = buttons is MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel ? MessageBoxResult.Cancel :
                            buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : MessageBoxResult.OK;
                        Assert.Equal(cancelled, dialog.Result);
                        Assert.Equal(cancelled, Assert.Single(choices, button => button.IsCancel).Tag);
                        AssertStableButtonProfiles(application, settings, root);
                    }
                    finally { dialog.Close(); }
                }
            }
            var artwork = Assert.IsAssignableFrom<BitmapSource>(AppIcons.Artwork);
            Assert.Equal(1024, artwork.PixelWidth);
            Assert.Equal(1024, artwork.PixelHeight);
            Assert.Equal(256, Assert.IsAssignableFrom<BitmapSource>(AppIcons.Logo).PixelWidth);
            IconAssetTests.AssertLargeArtwork(Assert.IsAssignableFrom<BitmapSource>(AppIcons.Logo));
            var pixels = new byte[4];
            var converted = new FormatConvertedBitmap(artwork, PixelFormats.Bgra32, null, 0);
            converted.CopyPixels(new Int32Rect(0, 0, 1, 1), pixels, 4, 0);
            Assert.Equal(0, pixels[3]);
        }
        finally { AppAppearance.Apply(application, AppThemes.Light); }
    }

    private static Grid MountDialog(Window window, Size size)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        var root = new Grid();
        window.Content = root;
        root.Children.Add(content);
        root.Measure(size);
        root.Arrange(new Rect(size));
        Pump(root);
        return root;
    }

    private static Color BrushColor(object brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static void AssertThemedTableSurface(Application application, FrameworkElement root, DataGrid grid)
    {
        var expected = BrushColor(application.FindResource("SolidBackgroundFillColorQuarternaryBrush"));
        var pixels = RenderPixels(root);
        var bottom = (int)grid.ActualHeight - 8;
        Assert.All(grid.Items.Cast<object>(), item =>
        {
            var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(item));
            Assert.True(row.TranslatePoint(new Point(0, row.ActualHeight), grid).Y < bottom);
        });
        Assert.Equal(expected, Pixel(grid, (int)grid.ActualWidth / 2, bottom, pixels));
        var headers = Descendants<DataGridColumnHeader>(grid)
            .Where(header => header.Column is not null && header.ActualWidth > 12).ToArray();
        Assert.NotEmpty(headers);
        foreach (var header in headers)
        {
            var background = Pixel(header, 8, 3, pixels);
            Assert.Equal(AppAppearance.Rgb(expected).Luminance < 0.4, AppAppearance.Rgb(background).Luminance < 0.4);
            AssertReadable(BrushColor(header.Foreground), background);
        }
    }

    private static void AssertCenteredCheckBox(CheckBox check)
    {
        var square = Assert.IsType<Border>(check.Template.FindName("CheckSquare", check));
        var caption = Assert.IsType<ContentPresenter>(check.Template.FindName("CheckCaption", check));
        var squareCenter = square.TranslatePoint(new Point(0, square.ActualHeight / 2), check).Y;
        var captionCenter = caption.TranslatePoint(new Point(0, caption.ActualHeight / 2), check).Y;
        Assert.InRange(Math.Abs(squareCenter - captionCenter), 0, 0.5);
        Assert.True(square.ActualHeight > 0 && caption.ActualHeight > 0);
    }

    private static void AssertStableButtonProfiles(Application application, AppSettings settings, FrameworkElement root)
    {
        settings.Appearance.ButtonStyle = ButtonStyles.Windows;
        AppAppearance.Apply(application, settings);
        Pump(root);
        var buttons = Descendants<Button>(root).Where(button => button.ActualWidth > 0 && button.Visibility == Visibility.Visible).ToArray();
        Assert.NotEmpty(buttons);
        var sizes = buttons.ToDictionary(button => button, button => button.RenderSize);
        foreach (var style in ButtonStyles.Options(UiText.Current).Select(option => option.Code))
        {
            settings.Appearance.ButtonStyle = style;
            AppAppearance.Apply(application, settings);
            Pump(root);
            foreach (var button in buttons)
            {
                if (button.Name is "AppMenuButton" or "MinimizeWindowButton" or "MaximizeWindowButton" or "CloseWindowButton")
                {
                    Assert.Equal(ButtonStyles.Windows, ButtonVisuals.GetProfile(button));
                    Assert.IsType<Border>(button.Template.FindName("CaptionSurface", button));
                }
                else
                {
                    Assert.Equal(style, ButtonVisuals.GetProfile(button));
                    Assert.IsType<Border>(button.Template.FindName("Chrome", button));
                }
                Assert.Equal(sizes[button].Width, button.ActualWidth, 3);
                Assert.Equal(sizes[button].Height, button.ActualHeight, 3);
            }
        }
        settings.Appearance.ButtonStyle = ButtonStyles.Windows;
        AppAppearance.Apply(application, settings);
        Pump(root);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T item)
                yield return item;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
}
