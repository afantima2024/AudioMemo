using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyCompactHelpAndGuide(Application application, string language)
    {
        var settings = new ShortcutSettings { Overrides = new()
        {
            ["help"] = "F8", ["search"] = "F7", ["edit"] = "", ["reviewUnclear"] = "Ctrl+Alt+U"
        } };
        settings.Validate();
        var saved = JsonSerializer.Serialize(settings);
        Assert.Equal(new UiText("en")["AboutCopyright"],
            typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
        foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
        {
            AppAppearance.Apply(application, theme);
            var opened = new List<Window>();
            var help = new HelpWindow(settings, opened.Add);
            try
            {
                Assert.Empty(opened);
                Assert.False(help.IsVisible);
                Assert.Null(help.Owner);
                Assert.NotSame(settings.Overrides, help.Shortcuts.Overrides);
                Assert.InRange(help.Width, 420, 640);
                Assert.InRange(help.Height, 320, 460);
                var root = MountDialog(help, new Size(help.Width, help.Height - 40));
                var copyright = Assert.IsType<TextBlock>(help.FindName("AboutCopyright"));
                var credits = Assert.IsType<TextBlock>(help.FindName("AboutCredits"));
                Assert.Equal(UiText.Get("AboutCopyright"), copyright.Text);
                Assert.Contains("Afanti Ma", credits.Text);
                Assert.Contains("AI", credits.Text);
                Assert.True(copyright.FontSize > credits.FontSize);
                Assert.Equal(BrushColor(application.FindResource("TextFillColorPrimaryBrush")), BrushColor(copyright.Foreground));
                var thirdParty = Assert.IsType<TextBlock>(help.FindName("AboutThirdParty"));
                Assert.Equal(UiText.Get("AboutThirdParty"), thirdParty.Text);
                Assert.Contains(@"support\Licenses", thirdParty.Text);
                Assert.Contains(@"support\Docs\THIRD-PARTY-NOTICES.txt", thirdParty.Text);
                Assert.Null(help.FindName("ShortcutHelpGrid"));
                Assert.Null(help.FindName("GuideBody"));
                var details = Assert.IsType<ScrollViewer>(help.FindName("HelpDetails"));
                Assert.InRange(details.ScrollableHeight, 0, 1);
                Assert.Equal(Visibility.Collapsed, details.ComputedHorizontalScrollBarVisibility);
                foreach (var (name, label) in new[]
                {
                    ("UserGuideButton", "UserGuide"), ("ShortcutsButton", "HelpAndShortcuts"),
                    ("MessageHistoryButton", "MessageHistory")
                })
                {
                    var button = Assert.IsType<Button>(help.FindName(name));
                    Assert.Equal(UiText.Get(label), button.Content);
                    Assert.True(button.IsEnabled);
                    AssertHelpControlFits(button, root);
                }
                Assert.IsType<Button>(help.FindName("UserGuideButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var guide = Assert.IsType<UserGuideWindow>(Assert.Single(opened));
                VerifyUserGuideReference(guide, language);
                Assert.IsType<Button>(help.FindName("ShortcutsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, opened.Count);
                VerifySavedShortcutReference(Assert.IsType<ShortcutHelpWindow>(opened[1]), settings, language);
                Assert.All(opened, window => { Assert.Null(window.Owner); Assert.False(window.IsVisible); });
                Assert.Equal(saved, JsonSerializer.Serialize(settings));
                Assert.Equal(saved, JsonSerializer.Serialize(help.Shortcuts));

                root.Measure(new Size(380, 280));
                root.Arrange(new Rect(0, 0, 380, 280));
                Pump(root);
                Assert.True(details.ScrollableHeight > 0);
                details.ScrollToBottom();
                Pump(root);
                AssertHelpControlFits(Assert.IsType<Button>(help.FindName("MessageHistoryButton")), root);
                AssertHelpControlFits(Assert.IsType<Button>(help.FindName("CloseHelpButton")), root);
                Assert.Equal(Visibility.Collapsed, details.ComputedHorizontalScrollBarVisibility);
            }
            finally
            {
                foreach (var window in opened) window.Close();
                help.Close();
                AppAppearance.Apply(application, AppThemes.Light);
            }
        }
    }

    private static void VerifyUserGuideReference(UserGuideWindow guide, string language)
    {
        Assert.Equal(UiText.Get("UserGuide"), guide.Title);
        Assert.Equal(language, guide.Language.IetfLanguageTag, ignoreCase: true);
        Assert.Null(guide.FindName("SaveButton"));
        var root = MountDialog(guide, new Size(780, 600));
        var topics = Assert.IsType<ComboBox>(guide.FindName("GuideTopics"));
        var body = Assert.IsType<TextBox>(guide.FindName("GuideBody"));
        var heading = Assert.IsType<TextBlock>(guide.FindName("GuideHeading"));
        var close = Assert.IsType<Button>(guide.FindName("CloseGuideButton"));
        Assert.True(close.IsCancel);
        Assert.True(body.IsReadOnly);
        Assert.False(body.IsUndoEnabled);
        Assert.Equal(TextWrapping.Wrap, body.TextWrapping);
        Assert.Equal(ScrollBarVisibility.Disabled, body.HorizontalScrollBarVisibility);
        Assert.Equal(14, topics.Items.Count);
        Assert.Equal("QuickStart", Assert.IsType<UserGuideTopic>(topics.SelectedItem).Id);
        Assert.Equal(guide.Topics.Count, guide.Topics.Select(topic => topic.Id).Distinct().Count());
        foreach (var topic in guide.Topics)
        {
            topics.SelectedItem = topic;
            Pump(root);
            Assert.Equal(UiText.Get("Guide" + topic.Id + "Title"), heading.Text);
            Assert.Equal(UiText.Get("Guide" + topic.Id + "Body"), body.Text);
            Assert.True(body.Text.Length > 200);
            Assert.InRange(body.VerticalOffset, 0, 1);
            body.Select(0, 20);
            Assert.Equal(body.Text[..20], body.SelectedText);
        }
        root.Measure(new Size(420, 380));
        root.Arrange(new Rect(0, 0, 420, 380));
        Pump(root);
        Assert.True(body.ActualHeight >= 60);
        body.ScrollToEnd();
        Pump(root);
        Assert.True(body.VerticalOffset > 0);
        topics.SelectedIndex = 0;
        Pump(root);
        Assert.InRange(body.VerticalOffset, 0, 1);
        AssertHelpControlFits(topics, root);
        AssertHelpControlFits(body, root);
        AssertHelpControlFits(close, root);
        Assert.InRange(body.HorizontalOffset, 0, 1);
    }

    private static void VerifySavedShortcutReference(ShortcutHelpWindow reference, ShortcutSettings settings, string language)
    {
        Assert.Equal(UiText.Get("HelpAndShortcuts"), reference.Title);
        Assert.Equal(language, reference.Language.IetfLanguageTag, ignoreCase: true);
        Assert.Null(reference.FindName("SaveButton"));
        var root = MountDialog(reference, new Size(700, 540));
        var grid = Assert.IsType<ItemsControl>(reference.FindName("ShortcutHelpGrid"));
        Assert.Equal(ShortcutSettings.Commands.Count, grid.Items.Count);
        foreach (var command in ShortcutSettings.Commands)
        {
            var row = grid.Items.Cast<object>().Single(value =>
                Equals(value.GetType().GetProperty("Id")!.GetValue(value), command.Id));
            Assert.Equal(UiText.Get(command.LabelKey), row.GetType().GetProperty("Label")!.GetValue(row));
            Assert.Equal(UiText.Get(command.TranscriptOnly ? "ShortcutTranscriptScope" : "ShortcutWindowScope"),
                row.GetType().GetProperty("Scope")!.GetValue(row));
            var gesture = settings.Gesture(command.Id);
            Assert.Equal(gesture.Length == 0 ? UiText.Get("ShortcutUnassigned") : ShortcutInput.Display(gesture),
                row.GetType().GetProperty("DisplayGesture")!.GetValue(row));
        }
        root.Measure(new Size(380, 280));
        root.Arrange(new Rect(0, 0, 380, 280));
        Pump(root);
        var details = Assert.IsType<ScrollViewer>(reference.FindName("ShortcutDetails"));
        Assert.True(details.ScrollableHeight > 0);
        Assert.Equal(Visibility.Collapsed, details.ComputedHorizontalScrollBarVisibility);
        var close = Assert.IsType<Button>(reference.FindName("CloseShortcutsButton"));
        Assert.True(close.IsCancel && close.IsDefault);
        AssertHelpControlFits(close, root);
    }

    private static void AssertHelpControlFits(FrameworkElement control, FrameworkElement root)
    {
        Assert.True(control.ActualWidth > 0 && control.ActualHeight > 0, control.Name);
        var topLeft = control.TranslatePoint(new Point(0, 0), root);
        var bottomRight = control.TranslatePoint(new Point(control.ActualWidth, control.ActualHeight), root);
        Assert.True(topLeft.X >= -1 && topLeft.Y >= -1, control.Name);
        Assert.True(bottomRight.X <= root.ActualWidth + 1 && bottomRight.Y <= root.ActualHeight + 1, control.Name);
    }
}
