using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static TranscriptSearchPanel SearchPanel(TranscriptView view) =>
        Assert.IsType<TranscriptSearchPanel>(view.GetType().GetField("searchPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view));

    private static Color Pixel(FrameworkElement element, int x, int y, byte[]? pixels = null)
    {
        var root = element;
        while (VisualTreeHelper.GetParent(root) is FrameworkElement parent)
            root = parent;
        var point = element.TranslatePoint(new Point(x, y), root);
        Assert.True(point.X >= 0 && point.Y >= 0 && point.X < root.ActualWidth && point.Y < root.ActualHeight,
            $"The sampled element is outside its root: {point}, root={root.ActualWidth}x{root.ActualHeight}.");
        pixels ??= RenderPixels(root);
        var width = (int)Math.Ceiling(root.ActualWidth);
        var position = ((int)point.Y * width + (int)point.X) * 4;
        return Color.FromArgb(pixels[position + 3], pixels[position + 2], pixels[position + 1], pixels[position]);
    }

    private static bool Navigate(TranscriptView view, Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        Assert.IsType<bool>(view.GetType().GetMethod("HandleTranscriptNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [key, modifiers]));

    private static void VerifyTranscriptPaging(TranscriptView view, FrameworkElement root, ScrollViewer scroll)
    {
        Assert.True(Navigate(view, Key.Home, ModifierKeys.Control));
        Pump(root);
        Assert.False(view.IsFollowing);
        Assert.InRange(scroll.VerticalOffset, 0, 1);
        Assert.True(Navigate(view, Key.PageDown));
        Pump(root);
        Assert.InRange(scroll.VerticalOffset, scroll.ViewportHeight * 0.8, scroll.ViewportHeight * 1.2);
        Assert.False(view.IsFollowing);
        Assert.True(Navigate(view, Key.PageUp));
        Pump(root);
        Assert.InRange(scroll.VerticalOffset, 0, 1);
        Assert.False(Navigate(view, Key.PageDown, ModifierKeys.Shift));
        Assert.True(Navigate(view, Key.End, ModifierKeys.Control));
        Pump(root);
        Assert.True(view.IsFollowing);
        Assert.InRange(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight), 0, 1);
    }

    private static void VerifyHistoryTabPosition(MainWindow window, AppSettings settings, FrameworkElement root)
    {
        var tab = Assert.IsType<Button>(window.FindName("HistoryPeekTab"));
        var body = Assert.IsType<Grid>(window.FindName("WorkspaceBody"));
        var original = settings.HistoryTabPosition;
        window.GetType().GetMethod("SetHistoryTabPosition", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [0.72, false]);
        Pump(root);
        Assert.Equal(0.72, settings.HistoryTabPosition);
        Assert.Equal(0, Grid.GetColumn(tab));
        Assert.Equal(0, tab.Margin.Left);
        Assert.Equal(HistoryTabPlacement.Offset(0.72, body.ActualHeight, tab.Height), tab.Margin.Top, 6);
        var before = tab.Margin.Top;
        window.GetType().GetMethod("BeginHistoryTabDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [new Point(5, before + 8)]);
        var moved = window.GetType().GetMethod("MoveHistoryTabDrag", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False(Assert.IsType<bool>(moved.Invoke(window, [new Point(6, before + 9)])));
        Assert.True(Assert.IsType<bool>(moved.Invoke(window, [new Point(800, 5000)])));
        Assert.Equal(1, settings.HistoryTabPosition);
        Assert.Equal(0, tab.Margin.Left);
        Assert.True(tab.Margin.Top + tab.Height <= body.ActualHeight);
        window.GetType().GetMethod("FinishHistoryTabDrag", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [false]);
        Assert.Equal(0.72, settings.HistoryTabPosition);
        window.GetType().GetMethod("SetHistoryTabPosition", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [original, false]);
        Assert.NotNull(window.FindName("PinHistoryButton"));
    }

    private static void VerifySearchPopup(TranscriptView view, FrameworkElement root, TranscriptSearchPanel panel)
    {
        Assert.Null(view.FindName("SearchBox"));
        var toolbar = Assert.IsType<ToolBar>(Assert.IsType<Button>(view.FindName("SearchButton")).Parent);
        Assert.Equal(toolbar.Items.IndexOf(Assert.IsType<Button>(view.FindName("CorrectRowsButton"))) + 1,
            toolbar.Items.IndexOf(Assert.IsType<Button>(view.FindName("SearchButton"))));
        var requestCount = 0;
        Action requested = () => requestCount++;
        view.SearchRequested += requested;
        Assert.IsType<Button>(view.FindName("SearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(HandleSearchKey(view, Key.F, ModifierKeys.Control, false));
        Assert.Equal(2, requestCount);
        view.SearchRequested -= requested;
        var create = view.GetType().GetMethod("CreateSearchWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var window = Assert.IsType<Window>(create.Invoke(view, [null]));
        try
        {
            Assert.Same(window, create.Invoke(view, [null]));
            Assert.Same(panel, window.Content);
            Assert.False(window.ShowInTaskbar);
            Assert.Equal(WindowStyle.ToolWindow, window.WindowStyle);
            panel.Measure(new Size(500, 120));
            panel.Arrange(new Rect(0, 0, 500, 120));
            Pump(panel);
            Assert.True(Assert.IsType<TextBox>(panel.FindName("SearchBox")).ActualWidth > 250);
            Assert.IsType<TextBox>(panel.FindName("SearchBox")).Text = "retained query";
            CompleteUiTask(root, BeginTranscriptSearch(view));
        }
        finally { window.Close(); }
        var reopened = Assert.IsType<Window>(create.Invoke(view, [null]));
        try
        {
            Assert.NotSame(window, reopened);
            Assert.Equal("retained query", Assert.IsType<TextBox>(panel.FindName("SearchBox")).Text);
            Assert.IsType<TextBox>(panel.FindName("SearchBox")).Clear();
        }
        finally { reopened.Close(); }
    }

    private static void VerifyAppearanceControls(Application app, SettingsWindow window, AppSettings original, FrameworkElement root)
    {
        var categories = Assert.IsType<ListBox>(window.FindName("SettingsCategories"));
        Assert.Equal(["appearance", "recording", "speech", "summary", "shortcuts"], categories.Items.Cast<ListBoxItem>().Select(item => item.Tag));
        Assert.Null(window.FindName("HelpPage"));
        var pages = new[] { "AppearancePage", "RecordingPage", "SpeechPage", "SummaryPage", "ShortcutsPage" }
            .Select(name => Assert.IsType<Border>(window.FindName(name))).ToArray();
        for (var index = 0; index < categories.Items.Count; index++)
        {
            categories.SelectedIndex = index;
            Pump(root);
            Assert.Single(pages, page => page.Visibility == Visibility.Visible);
            Assert.Equal(Visibility.Visible, pages[index].Visibility);
            Assert.True(categories.TranslatePoint(new Point(categories.ActualWidth, 0), root).X <
                pages[index].TranslatePoint(new Point(), root).X);
        }
        categories.SelectedIndex = 0;
        Assert.True(pages[0].IsAncestorOf(Assert.IsType<Button>(window.FindName("RunSetupButton"))));
        var rainbow = Assert.IsType<CheckBox>(window.FindName("RainbowRecordingIndicatorCheck"));
        Assert.True(pages[0].IsAncestorOf(rainbow));
        Assert.Equal(UiText.Get("RainbowRecordingIndicator"), rainbow.Content);
        Assert.Equal(original.Appearance.RainbowRecordingIndicator, rainbow.IsChecked);
        rainbow.IsChecked = true;
        var profiles = Assert.IsType<ComboBox>(window.FindName("ButtonStyleBox"));
        Assert.Equal([ButtonStyles.Windows, ButtonStyles.Office, ButtonStyles.Shadow, ButtonStyles.Outline],
            profiles.Items.Cast<LanguageOption>().Select(option => option.Code));
        var preview = Assert.IsType<Border>(window.FindName("ButtonPreview"));
        var buttons = Assert.IsType<WrapPanel>(preview.Child).Children.OfType<Button>().ToArray();
        foreach (var profile in profiles.Items.Cast<LanguageOption>().Select(option => option.Code).ToArray())
        {
            profiles.SelectedValue = profile;
            Pump(root);
            var chrome = Assert.IsType<Border>(buttons[1].Template.FindName("Chrome", buttons[1]));
            Assert.Equal(profile, ButtonVisuals.GetProfile(buttons[1]));
            Assert.Equal(profile == ButtonStyles.Office ? 2 : profile == ButtonStyles.Outline ? 3 : 7, chrome.CornerRadius.TopLeft);
            Assert.Equal(profile == ButtonStyles.Shadow, chrome.Effect is DropShadowEffect);
            if (profile == ButtonStyles.Outline)
            {
                Assert.Equal(2, chrome.BorderThickness.Left);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(chrome.Background).Color);
            }
            Assert.Equal(0.42, Assert.IsType<Grid>(buttons[2].Template.FindName("ButtonSurface", buttons[2])).Opacity, 2);
            Assert.Equal(ButtonStyles.Windows, original.Appearance.ButtonStyle);
        }
        Assert.IsType<ColorPickerButton>(window.FindName("LightSelectionColor")).Color = "#182A3B";
        Assert.IsType<ColorPickerButton>(window.FindName("DarkSelectionColor")).Color = "#F3E0D0";
        Assert.IsType<ColorPickerButton>(window.FindName("LightComparisonDifferentColor")).Color = "#123456";
        Assert.IsType<ColorPickerButton>(window.FindName("DarkComparisonDifferentColor")).Color = "#654321";
        Assert.IsType<ColorPickerButton>(window.FindName("LightComparisonMatchingColor")).Color = "#234567";
        Assert.IsType<ColorPickerButton>(window.FindName("DarkComparisonMatchingColor")).Color = "#765432";
        Assert.IsType<ColorPickerButton>(window.FindName("LightComparisonUnclearColor")).Color = "#345678";
        Assert.IsType<ColorPickerButton>(window.FindName("DarkComparisonUnclearColor")).Color = "#876543";
        Invoke(window, "ReadAppearance");
        Assert.True(window.Settings.Appearance.RainbowRecordingIndicator);
        Assert.False(original.Appearance.RainbowRecordingIndicator);
        Assert.Equal(ButtonStyles.Outline, window.Settings.Appearance.ButtonStyle);
        Assert.Equal("#182A3B", window.Settings.Appearance.Light.Selection);
        Assert.Equal("#F3E0D0", window.Settings.Appearance.Dark.Selection);
        Assert.Equal("#123456", window.Settings.Appearance.LightComparison.Different);
        Assert.Equal("#654321", window.Settings.Appearance.DarkComparison.Different);
        Assert.Equal("#234567", window.Settings.Appearance.LightComparison.Matching);
        Assert.Equal("#765432", window.Settings.Appearance.DarkComparison.Matching);
        Assert.Equal("#345678", window.Settings.Appearance.LightComparison.Unclear);
        Assert.Equal("#876543", window.Settings.Appearance.DarkComparison.Unclear);
        Assert.Equal("#FCE4EC", original.Appearance.LightComparison.Different);
        Assert.Equal("#522D3F", original.Appearance.DarkComparison.Different);
        Assert.Equal("#C8DDF5", original.Appearance.Light.Selection);
        Assert.Equal("#294B63", original.Appearance.Dark.Selection);
        AppAppearance.Apply(app, window.Settings);
        Pump(root);
        Assert.Equal(ButtonStyles.Outline, ButtonVisuals.GetProfile(Assert.IsType<Button>(window.FindName("SaveButton"))));
        AppAppearance.Apply(app, original.Theme);
        Call(window, "ResetColors_Click", window, new RoutedEventArgs());
        Invoke(window, "ReadAppearance");
        Assert.Equal("#FCE4EC", window.Settings.Appearance.LightComparison.Different);
        Assert.Equal("#244838", window.Settings.Appearance.DarkComparison.Matching);
        Assert.Equal("#E4E7EB", window.Settings.Appearance.LightComparison.Unclear);
        Assert.True(window.Settings.Appearance.RainbowRecordingIndicator);
        var reopened = new SettingsWindow(window.Settings);
        try
        {
            var savedRainbow = Assert.IsType<CheckBox>(reopened.FindName("RainbowRecordingIndicatorCheck"));
            Assert.True(savedRainbow.IsChecked);
            savedRainbow.IsChecked = false;
            Invoke(reopened, "ReadAppearance");
            Assert.False(reopened.Settings.Appearance.RainbowRecordingIndicator);
            Assert.True(window.Settings.Appearance.RainbowRecordingIndicator);
        }
        finally { reopened.Close(); }

        var picker = new ColorPickerWindow(null, "Synthetic color", "#123456", allowNeutral: true);
        try
        {
            var panel = Assert.IsType<StackPanel>(picker.Content);
            panel.Measure(new Size(410, 400));
            panel.Arrange(new Rect(0, 0, 410, 400));
            Pump(panel);
            Assert.Equal(16, Assert.IsType<WrapPanel>(picker.FindName("PresetsPanel")).Children.Count);
            var hex = Assert.IsType<TextBox>(picker.FindName("HexBox"));
            hex.Text = "#zzzzzz";
            Assert.False(Assert.IsType<Button>(picker.FindName("UseColorButton")).IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<TextBlock>(picker.FindName("ColorStatus")).Text));
            hex.Text = "#FFFFFF";
            Assert.True(Assert.IsType<Button>(picker.FindName("UseColorButton")).IsEnabled);
            Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(Assert.IsType<TextBlock>(picker.FindName("PreviewText")).Foreground).Color);
            Assert.Null(picker.SelectedColor);
        }
        finally { picker.Close(); }
    }

    private static void VerifyActualRowColors(TranscriptView view, FrameworkElement root, DataGrid grid)
    {
        var app = Application.Current;
        var dark = AppAppearance.Rgb(Assert.IsType<SolidColorBrush>(app.FindResource("SolidBackgroundFillColorBaseBrush")).Color).Luminance < 0.4;
        var theme = dark ? AppThemes.Dark : AppThemes.Light;
        var settings = new AppSettings { Theme = theme };
        settings.Appearance.Light.Selection = settings.Appearance.Dark.Selection = "#E2EBF7";
        settings.Appearance.Light.ActiveMatch = settings.Appearance.Dark.ActiveMatch = "#000000";
        var window = new Window { Content = root };
        try
        {
            AppAppearance.Apply(app, settings);
            var document = new TranscriptDocument
            {
                SpeakersIdentified = true,
                Segments =
                [
                    new(0, 1, "Speaker one sample.") { SpeakerId = "spkr1" },
                    new(1, 2, "Other voice.") { SpeakerId = "spkr2" },
                    new(2, 3, "Speaker one repeats.") { SpeakerId = "spkr1" }
                ]
            };
            document.SetSpeakerColor("spkr1", "#294B63");
            document.SetSpeakerColor("spkr2", "#F2D2D2");
            view.SetDocument(document, reset: true);
            Pump(root);
            for (var index = 0; index < grid.Items.Count; index++)
            {
                var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(index));
                var expected = AppAppearance.Brush(RgbColor.Parse(document.SpeakerColors[((TranscriptRow)row.Item).SpeakerId!])).Color;
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(row.Background).Color);
                var cell = FindChild<DataGridCell>(row)!;
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(cell.Background).Color);
                Assert.Equal(expected, Assert.IsType<SolidColorBrush>(Assert.IsType<Border>(cell.Template.FindName("CellChrome", cell)).Background).Color);
                Assert.Equal(expected, Pixel(row, 8, 4));
                AssertReadable(Assert.IsType<SolidColorBrush>(row.Foreground).Color, expected);
            }
            string? requested = null;
            view.CanSetSpeakerColors = true;
            Action<string?> request = id => requested = id;
            view.SpeakerColorRequested += request;
            var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Control.PreviewMouseDoubleClickEvent,
                Source = grid.Columns[1].GetCellContent(grid.Items[0])
            };
            view.GetType().GetMethod("Grid_PreviewMouseDoubleClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [grid, mouse]);
            view.SpeakerColorRequested -= request;
            Assert.Equal("spkr1", requested);
            Assert.True(mouse.Handled);
            grid.SelectedItem = grid.Items[0];
            Pump(root);
            var selected = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
            var selection = AppAppearance.Brush(RgbColor.Parse("#E2EBF7")).Color;
            Assert.Equal(selection, Assert.IsType<SolidColorBrush>(app.FindResource("TranscriptSelectionBackground")).Color);
            Assert.Equal(selection, Assert.IsType<SolidColorBrush>(view.SelectionBackground).Color);
            Assert.Equal(selection, Pixel(selected, 8, 4));
            var text = FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(grid.Items[0]))!;
            Assert.Equal(Colors.Black, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            AssertReadable(Assert.IsType<SolidColorBrush>(text.Foreground).Color, selection);
            Assert.IsType<TextBox>(SearchPanel(view).FindName("SearchBox")).Text = "Speaker";
            CompleteUiTask(root, BeginTranscriptSearch(view));
            var adjusted = RgbColor.Parse("#000000").ForText(new(0, 0, 0));
            var pixels = RenderPixels(text);
            Assert.True(Enumerable.Range(0, pixels.Length / 4).Any(index =>
                pixels[index * 4] == adjusted.Blue && pixels[index * 4 + 1] == adjusted.Green &&
                pixels[index * 4 + 2] == adjusted.Red && pixels[index * 4 + 3] == 255),
                "The readable search-highlight shade was not actually painted.");
            FocusManager.SetFocusedElement(window, text);
            text.Select(0, 7);
            Assert.Equal("Speaker", text.SelectedText);
            Assert.True(AppContext.TryGetSwitch("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", out var adorner) && !adorner);
            Assert.Equal(Colors.White, Assert.IsType<SolidColorBrush>(text.SelectionTextBrush).Color);
            // Hidden windows receive no native focus notification; refresh WPF's selection layer explicitly.
            var container = typeof(TextBox).GetProperty("TextContainer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(text)!;
            var containerContract = container.GetType().GetInterfaces().Single(type => type.Name == "ITextContainer");
            var selectionState = containerContract.GetProperty("TextSelection")!.GetValue(container)!;
            var selectionContract = selectionState.GetType().GetInterfaces().Single(type => type.Name == "ITextSelection");
            selectionContract.GetMethod("UpdateCaretAndHighlight")!.Invoke(selectionState, null);
            Pump(root);
            var selectedPixels = RenderPixels(text);
            var first = text.GetRectFromCharacterIndex(0);
            var last = text.GetRectFromCharacterIndex(6, true);
            var width = (int)Math.Ceiling(text.ActualWidth);
            var backgroundPixels = 0;
            var glyphPixels = 0;
            var brightest = 0;
            for (var y = (int)Math.Ceiling(first.Top); y < Math.Floor(first.Bottom); y++)
                for (var x = (int)Math.Ceiling(first.Left); x < Math.Floor(last.Right); x++)
                {
                    var offset = (y * width + x) * 4;
                    brightest = Math.Max(brightest, selectedPixels[offset]);
                    if (selectedPixels[offset] < 8 && selectedPixels[offset + 1] < 8 && selectedPixels[offset + 2] < 8)
                        backgroundPixels++;
                    if (selectedPixels[offset] > 220 && selectedPixels[offset + 1] > 220 && selectedPixels[offset + 2] > 220)
                        glyphPixels++;
                }
            Assert.True(backgroundPixels > 30, "The actual text-selection background was not painted.");
            Assert.True(glyphPixels > 10, $"An opaque selection rectangle hid the selected glyphs. Background={backgroundPixels}, glyphs={glyphPixels}, brightest={brightest}, active={text.IsSelectionActive}.");
            text.Select(0, 0);
            FocusManager.SetFocusedElement(window, null);
        }
        finally
        {
            Assert.IsType<TextBox>(SearchPanel(view).FindName("SearchBox")).Clear();
            AppAppearance.Apply(app, theme);
            window.Content = null;
            window.Close();
        }
    }
}
