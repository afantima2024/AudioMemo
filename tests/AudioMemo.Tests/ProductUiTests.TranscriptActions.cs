using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AudioMemo.Audio;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyFloatingTranscriptActions(Application application, string language)
    {
        var text = new UiText(language);
        foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
        {
            AppAppearance.Apply(application, theme);
            var view = new TranscriptView { CanReplay = true, CanEdit = true, CanCorrect = true };
            var root = new Grid();
            root.Children.Add(view);
            var host = new Window { Content = root };
            try
            {
                var shortcuts = new ShortcutSettings { Overrides = new() { ["search"] = "F7", ["edit"] = "" } };
                view.ConfigureShortcuts(shortcuts);
                view.SetDocument(new()
                {
                    Segments = [new(0, 1, "First voice.") { SpeakerId = "one" },
                        new(1, 2, "Second voice.") { SpeakerId = "two" }]
                }, reset: true);
                Resize(760, 320);
                var grid = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
                var floating = Assert.IsType<Border>(view.FindName("FloatingNavigation"));
                var actionsButton = Assert.IsType<Button>(view.FindName("TranscriptActionsButton"));
                var actions = Assert.IsType<ContextMenu>(view.FindName("TranscriptActionsMenu"));
                var navigation = Assert.IsType<StackPanel>(view.FindName("NavigationButtonStrip"));
                Assert.Null(FindChild<ToolBar>(view));
                Assert.Equal(Visibility.Visible, floating.Visibility);
                Assert.Equal(0, FindChild<ScrollViewer>(grid)!.ScrollableHeight);
                Assert.Equal(0, grid.TranslatePoint(new Point(), view).Y);
                Assert.Same(actions, actionsButton.ContextMenu);
                Assert.Same(actionsButton, actions.PlacementTarget);
                Assert.Equal(text["TranscriptActionsLabel"], AutomationProperties.GetName(actionsButton));
                Assert.Equal(text["TranscriptActionsHint"], actionsButton.ToolTip);
                var strip = Assert.IsType<StackPanel>(floating.Child);
                Assert.Equal(strip.Children.IndexOf(actionsButton) + 1, strip.Children.IndexOf(navigation));
                Assert.Equal(new[] { "SpeakerFilterAction", "CopyVisibleAction", "CopyAnnotatedAction", "FollowAction",
                    "PlayRangeAction", "MuteRowsAction", "CorrectRowsAction", "SearchAction", "OnlineRowsAction" },
                    actions.Items.OfType<MenuItem>().Select(item => item.Name));
                foreach (var (id, name) in new[]
                {
                    ("speakerFilter", "SpeakerFilterAction"), ("copy", "CopyVisibleAction"),
                    ("copyAnnotated", "CopyAnnotatedAction"), ("latest", "FollowAction"), ("play", "PlayRangeAction"),
                    ("silence", "MuteRowsAction"), ("edit", "CorrectRowsAction"),
                    ("search", "SearchAction"), ("online", "OnlineRowsAction")
                })
                {
                    var item = Action(name);
                    Assert.Same(item, view.ShortcutTarget(id));
                    Assert.Equal(ShortcutInput.Display(shortcuts.Gesture(id)), item.InputGestureText);
                }
                Assert.Empty(Action("CorrectRowsAction").InputGestureText);
                Assert.Contains("F7", Assert.IsType<string>(Action("SearchAction").ToolTip));
                Assert.False(Action("PlayRangeAction").IsEnabled);
                grid.SelectedIndex = 1;
                PlaybackRange? replay = null;
                IReadOnlyList<TranscriptRow>? edited = null, corrected = null, online = null;
                var searches = 0;
                view.ReplayRequested += value => replay = value;
                view.EditRequested += value => edited = value;
                view.CorrectionRequested += (value, _) => corrected = value;
                view.OnlineTranscriptionRequested += value => online = value;
                view.SearchRequested += () => searches++;
                foreach (var name in new[] { "PlayRangeAction", "MuteRowsAction", "CorrectRowsAction", "OnlineRowsAction", "SearchAction" })
                    Assert.True(ShortcutActions.Invoke(Action(name)));
                Assert.Equal(new PlaybackRange(1, 2), replay);
                Assert.Equal(1, Assert.Single(edited!).StartSeconds);
                Assert.Equal(1, Assert.Single(corrected!).StartSeconds);
                Assert.Equal(1, Assert.Single(online!).StartSeconds);
                Assert.Equal(1, searches);
                Assert.Equal(1, grid.SelectedIndex);
                view.CanEdit = view.CanCorrect = false;
                Assert.False(Action("MuteRowsAction").IsEnabled || Action("CorrectRowsAction").IsEnabled || Action("OnlineRowsAction").IsEnabled);
                Assert.True(Action("SearchAction").IsEnabled && Action("CopyVisibleAction").IsEnabled);

                var speaker = Action("SpeakerFilterAction");
                Assert.All(speaker.Items.OfType<MenuItem>(), item => Assert.True(item.StaysOpenOnClick));
                var all = Assert.IsType<MenuItem>(speaker.Items[0]);
                all.IsChecked = false;
                all.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Pump(root);
                Assert.Empty(grid.Items);
                Assert.Equal(Visibility.Visible, floating.Visibility);
                Assert.True(actionsButton.IsEnabled && speaker.IsEnabled && Action("SearchAction").IsEnabled);
                Assert.False(Action("CopyVisibleAction").IsEnabled);
                all.IsChecked = true;
                all.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Pump(root);
                Assert.Equal(2, grid.Items.Count);
                view.SetDocument(new(), reset: true);
                Pump(root);
                Assert.Equal(Visibility.Visible, floating.Visibility);
                foreach (var size in new[] { new Size(760, 320), new Size(180, 180) })
                {
                    Resize(size.Width, size.Height);
                    Assert.Equal(Visibility.Visible, floating.Visibility);
                    Assert.Equal(size.Width < 250 ? Visibility.Collapsed : Visibility.Visible, navigation.Visibility);
                    Assert.True(floating.ActualWidth + Canvas.GetLeft(floating) <= size.Width);
                    Assert.True(floating.ActualHeight + Canvas.GetTop(floating) <= size.Height);
                    Assert.Equal(BrushColor(application.FindResource("TextFillColorPrimaryBrush")), BrushColor(actionsButton.Foreground));
                }
            }
            finally
            {
                host.Content = null;
                host.Close();
                AppAppearance.Apply(application, AppThemes.Light);
            }

            MenuItem Action(string name) => Assert.IsType<MenuItem>(view.FindName(name));
            void Resize(double width, double height)
            {
                root.Measure(new Size(width, height));
                root.Arrange(new Rect(0, 0, width, height));
                Pump(root);
            }
        }
    }
}
