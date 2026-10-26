using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyTranscriptSelectionPersistence(string language)
    {
        var view = new TranscriptView();
        var root = new Grid { Width = 760, Height = 360 };
        root.Children.Add(view);
        var host = new Window { Content = root };
        try
        {
            var document = new TranscriptDocument
            {
                Segments = Enumerable.Range(0, 240).Select(index => new TranscriptSegment(index, index + 1,
                    language == "en" ? $"Row {index:D3}: selected words stay with this row." :
                        $"\u7b2c {index:D3} \u884c\uff1a\u6eda\u52a8\u540e\u4fdd\u7559\u9009\u4e2d\u6587\u5b57\u3002")
                { SpeakerId = index % 2 == 0 ? "spkr1" : "spkr2" }).ToList()
            };
            view.SetDocument(document, reset: true);
            root.Measure(new Size(760, 360));
            root.Arrange(new Rect(0, 0, 760, 360));
            view.NavigateTranscript("first");
            Pump(root);
            var grid = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
            var first = FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(grid.Items[0]))!;
            var selectedRow = grid.Items[1];
            first.Select(1, 6);
            grid.SelectedItem = selectedRow;
            var second = FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(selectedRow))!;
            second.Select(4, 8);
            var selectedText = second.SelectedText;
            Assert.Equal(0, first.SelectionLength);
            Assert.Equal(8, second.SelectionLength);

            var scroll = FindChild<ScrollViewer>(grid)!;
            var scrollbar = FindChild<System.Windows.Controls.Primitives.ScrollBar>(scroll)!;
            Call(view, "TextSelection_PreviewMouseDown", grid, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = scrollbar
            });
            Assert.Equal(selectedText, Call(view, "GetSelectedText"));
            Call(view, "TextSelection_PreviewMouseDown", grid, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Middle)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = first
            });
            Assert.Equal(selectedText, Call(view, "GetSelectedText"));
            for (var iteration = 0; iteration < 3; iteration++)
            {
                scroll.ScrollToEnd();
                Pump(root);
                Assert.Equal(selectedText, Call(view, "GetSelectedText"));
                Assert.True(ApplicationCommands.Copy.CanExecute(null, grid));
                Assert.Same(selectedRow, Assert.Single(grid.SelectedItems.Cast<object>()));
                Assert.True(Enumerable.Range(0, grid.Items.Count)
                    .Count(index => grid.ItemContainerGenerator.ContainerFromIndex(index) is not null) < 60);
                foreach (var box in Enumerable.Range(0, grid.Items.Count)
                    .Select(index => grid.ItemContainerGenerator.ContainerFromIndex(index))
                    .OfType<DataGridRow>().Select(row => FindChild<TranscriptTextBox>(row)).OfType<TranscriptTextBox>())
                {
                    if (!ReferenceEquals(box.DataContext, selectedRow))
                        Assert.Equal(0, box.SelectionLength);
                }
                scroll.ScrollToTop();
                Pump(root);
                second = FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(selectedRow))!;
                Assert.Equal(4, second.SelectionStart);
                Assert.Equal(8, second.SelectionLength);
                Assert.Equal(selectedText, second.SelectedText);
            }

            var rowContainer = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(1));
            Assert.InRange(rowContainer.ActualHeight, 24, 32);
            Click(Assert.IsType<Button>(view.FindName("TranscriptPageDownButton")));
            Pump(root);
            Assert.Equal(selectedText, Call(view, "GetSelectedText"));
            document.Segments.Add(new(240, 241, "More recorded text.") { SpeakerId = "spkr3" });
            view.SetDocument(document);
            Pump(root);
            Assert.Equal(selectedText, Call(view, "GetSelectedText"));
            Click(Assert.IsType<Button>(view.FindName("TranscriptPageUpButton")));
            Pump(root);
            second = FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(grid.Items[1]))!;
            Assert.Equal(selectedText, second.SelectedText);
            var changed = document.Copy();
            changed.Segments[1] = changed.Segments[1] with { Text = "Replacement text invalidates the previous selection." };
            view.SetDocument(changed);
            Pump(root);
            Assert.Null(Call(view, "GetSelectedText"));
            Assert.Equal(0, FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(grid.Items[1]))!.SelectionLength);

            second = FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(grid.Items[1]))!;
            second.Select(1, 7);
            view.SetDocument(document.Copy(), reset: true);
            view.NavigateTranscript("first");
            Pump(root);
            Assert.Equal(0, FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(grid.Items[1]))!.SelectionLength);
            Assert.Null(Call(view, "GetSelectedText"));
            grid.SelectedItems.Clear();
            var selectedRows = grid.Items.Cast<object>().Skip(1).Take(3).ToArray();
            foreach (var selected in selectedRows)
                grid.SelectedItems.Add(selected);
            scroll.ScrollToEnd();
            Pump(root);
            Assert.Equal(selectedRows, grid.SelectedItems.Cast<object>());
            scroll.ScrollToTop();
            Pump(root);
            Assert.Equal(selectedRows, grid.SelectedItems.Cast<object>());

            var longText = string.Concat(Enumerable.Repeat("Wrapped text \u591a\u884c\u6587\u5b57 \U0001f4dd. ", 30));
            view.SetDocument(new() { Segments = [new(0, 2, longText)] }, reset: true);
            view.NavigateTranscript("first");
            Pump(root);
            var wrapped = FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(grid.Items[0]))!;
            var last = wrapped.GetRectFromCharacterIndex(wrapped.Text.Length - 1, true);
            Assert.True(wrapped.LineCount > 1);
            Assert.False(last.IsEmpty);
            Assert.True(last.Bottom <= wrapped.ActualHeight, $"Compact rows clipped their final line: {last}, height {wrapped.ActualHeight}.");
        }
        finally
        {
            host.Content = null;
            host.Close();
        }
    }

    private static void VerifyFloatingTranscriptNavigation(Application application, string language)
    {
        var view = new TranscriptView
        {
            CanReplay = false,
            CanEdit = false,
            CanCorrect = false,
            CanChangeSpeakers = false,
            CanExportAudio = false,
            CanSummarizeAboutMe = false
        };
        view.ConfigureShortcuts(new());
        var root = new Grid { Width = 760, Height = 360 };
        root.Children.Add(view);
        var host = new Window { Content = root };
        var document = new TranscriptDocument
        {
            Segments = Enumerable.Range(0, 240).Select(index =>
                new TranscriptSegment(index, index + 1, $"Synthetic floating navigation row {index:D3}.")
                { SpeakerId = "spkr1" }).ToList()
        };
        var grid = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
        var viewport = Assert.IsType<Grid>(grid.Parent);
        var canvas = Assert.IsType<Canvas>(view.FindName("NavigationCanvas"));
        var navigation = Assert.IsType<Border>(view.FindName("FloatingNavigation"));
        var grip = Assert.IsType<Thumb>(view.FindName("NavigationDragHandle"));
        var savedPositions = new List<Point>();
        view.NavigationPositionChanged += savedPositions.Add;
        var top = Assert.IsType<Button>(view.FindName("TranscriptTopButton"));
        var pageUp = Assert.IsType<Button>(view.FindName("TranscriptPageUpButton"));
        var pageDown = Assert.IsType<Button>(view.FindName("TranscriptPageDownButton"));
        var bottom = Assert.IsType<Button>(view.FindName("TranscriptBottomButton"));
        try
        {
            view.SetDocument(document, reset: true);
            Resize(760, 360);
            view.NavigateTranscript("first");
            Pump(root);
            var scroll = FindChild<ScrollViewer>(grid)!;
            Assert.Equal(Visibility.Visible, navigation.Visibility);
            Assert.Same(viewport, canvas.Parent);
            Assert.Null(canvas.Background);
            Assert.Equal(new Size(0, 0), canvas.DesiredSize);
            Assert.Equal(new Point(1, 1), view.NavigationPosition);
            Assert.Equal(UiText.Get("MoveTranscriptNavigation"), AutomationProperties.GetName(grip));
            Assert.Equal(UiText.Get("MoveTranscriptNavigationHint"), grip.ToolTip);
            Assert.Equal(Cursors.SizeAll, grip.Cursor);
            Assert.True(grip.Focusable && grip.IsTabStop && grip.IsEnabled);
            Assert.Equal(["first", "pageUp", "pageDown", "latest"],
                Assert.IsType<StackPanel>(view.FindName("NavigationButtonStrip")).Children.OfType<Button>().Select(button => button.Tag));
            Assert.False(top.IsEnabled || pageUp.IsEnabled);
            Assert.True(pageDown.IsEnabled && bottom.IsEnabled);
            foreach (var (button, label, command) in new[]
            {
                    (top, "TranscriptBeginning", "first"), (pageUp, "TranscriptPageUp", "pageUp"),
                    (pageDown, "TranscriptPageDown", "pageDown"), (bottom, "TranscriptBottom", "latest")
                })
            {
                Assert.Equal(UiText.Get(label), AutomationProperties.GetName(button));
                Assert.True(button.Focusable && button.IsTabStop);
                Assert.True(ToolTipService.GetShowOnDisabled(button));
                Assert.Contains(ShortcutInput.Display(view.Shortcuts.Gesture(command)), Assert.IsType<string>(button.ToolTip));
            }
            Click(pageDown);
            Pump(root);
            Assert.InRange(scroll.VerticalOffset, scroll.ViewportHeight * 0.8, scroll.ViewportHeight * 1.2);
            Assert.False(view.IsFollowing);
            Assert.True(top.IsEnabled && pageUp.IsEnabled);
            Click(pageUp);
            Pump(root);
            Assert.InRange(scroll.VerticalOffset, 0, 1);
            Assert.False(view.IsFollowing);
            Click(bottom);
            Pump(root);
            Assert.True(view.IsFollowing);
            Assert.InRange(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight), 0, 1);
            Assert.False(bottom.IsEnabled || pageDown.IsEnabled);
            Assert.True(top.IsEnabled && pageUp.IsEnabled);

            var next = document.Copy();
            next.Segments.Add(new(240, 241, "Incoming live text while following."));
            view.SetDocument(next);
            Pump(root);
            Assert.True(view.IsFollowing);
            Assert.InRange(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight), 0, 1);
            Click(top);
            Pump(root);
            Assert.False(view.IsFollowing);
            next.Segments.Add(new(241, 242, "Incoming AI text while reading the beginning."));
            view.SetDocument(next);
            Pump(root);
            Assert.InRange(scroll.VerticalOffset, 0, 1);
            Assert.True(pageDown.IsEnabled && bottom.IsEnabled);
            Click(pageDown);
            Pump(root);
            var offset = scroll.VerticalOffset;
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent
            };
            bottom.RaiseEvent(wheel);
            Pump(root);
            Assert.True(wheel.Handled);
            if (SystemParameters.WheelScrollLines != 0)
                Assert.True(scroll.VerticalOffset < offset);
            Assert.False(view.IsFollowing);

            var shortcuts = new ShortcutSettings
            {
                Overrides = new()
                {
                    ["first"] = "Ctrl+Alt+Home",
                    ["latest"] = "Ctrl+Alt+End",
                    ["pageUp"] = "Alt+PageUp",
                    ["pageDown"] = "Alt+PageDown"
                }
            };
            view.ConfigureShortcuts(shortcuts);
            foreach (var button in new[] { top, pageUp, pageDown, bottom })
                Assert.Contains(ShortcutInput.Display(shortcuts.Gesture((string)button.Tag)), Assert.IsType<string>(button.ToolTip));
            shortcuts.Overrides["pageDown"] = "";
            view.ConfigureShortcuts(shortcuts);
            Assert.Equal(UiText.Get("TranscriptPageDown"), pageDown.ToolTip);
            Click(pageDown);
            Pump(root);
            Assert.True(scroll.VerticalOffset > 1);

            Click(top);
            Pump(root);
            var selectedRow = grid.Items[1];
            grid.SelectedItem = selectedRow;
            var selectedCell = FindChild<TranscriptTextBox>(grid.Columns[^1].GetCellContent(selectedRow))!;
            selectedCell.Select(2, 8);
            var selectedText = selectedCell.SelectedText;
            var originalBounds = Bounds();
            var originalOffset = scroll.VerticalOffset;
            StartDrag();
            Assert.True(view.HandlesNavigationPositionKey(Key.PageDown, ModifierKeys.None));
            Drag(-128, -64);
            Drag(-24, -16);
            Pump(root);
            Assert.InRange(Math.Abs(Bounds().Left - (originalBounds.Left - 152)), 0, 1);
            Assert.InRange(Math.Abs(Bounds().Top - (originalBounds.Top - 80)), 0, 1);
            Assert.Empty(savedPositions);
            var dragWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            grip.RaiseEvent(dragWheel);
            Pump(root);
            Assert.True(dragWheel.Handled);
            Assert.Equal(originalOffset, scroll.VerticalOffset);
            Assert.False(view.IsFollowing);
            Assert.Same(selectedRow, grid.SelectedItem);
            Assert.Equal(selectedText, Call(view, "GetSelectedText"));
            EndDrag();
            var movedPosition = Assert.Single(savedPositions);
            Assert.Equal(movedPosition, view.NavigationPosition);
            Assert.False(view.HandlesNavigationPositionKey(Key.PageDown, ModifierKeys.None));

            StartDrag();
            EndDrag();
            Assert.Single(savedPositions);
            StartDrag();
            Drag(30, 25);
            EndDrag(canceled: true);
            Pump(root);
            Assert.Equal(movedPosition, view.NavigationPosition);
            Assert.Single(savedPositions);
            Assert.Equal(selectedText, Call(view, "GetSelectedText"));
            StartDrag();
            Drag(30, 25);
            grip.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            EndDrag();
            Assert.Equal(movedPosition, view.NavigationPosition);
            Assert.Single(savedPositions);

            view.SetNavigationPosition(new Point(0.5, 0.5));
            StartDrag();
            Drag(-10000, -10000);
            EndDrag();
            Pump(root);
            Assert.Equal(new Point(0, 0), view.NavigationPosition);
            Assert.InRange(Bounds().Left, 7, 9);
            Assert.True(Bounds().Top >= grid.ActualHeight - scroll.ViewportHeight + 7);
            StartDrag();
            Drag(10000, 10000);
            EndDrag();
            Pump(root);
            Assert.Equal(new Point(1, 1), view.NavigationPosition);
            Assert.True(Bounds().Bottom <= grid.ActualHeight - 7);
            var afterDrag = savedPositions.Count;
            Assert.True(Assert.IsType<bool>(Call(view, "MoveNavigationWithKey", Key.Left)));
            Assert.True(Assert.IsType<bool>(Call(view, "MoveNavigationWithKey", Key.Up)));
            Assert.True(view.NavigationPosition.X < 1 && view.NavigationPosition.Y < 1);
            Assert.Equal(afterDrag + 2, savedPositions.Count);
            Assert.True(Assert.IsType<bool>(Call(view, "MoveNavigationWithKey", Key.Home)));
            Assert.Equal(new Point(1, 1), view.NavigationPosition);
            Assert.Equal(selectedText, Call(view, "GetSelectedText"));
            Assert.False(Assert.IsType<bool>(Call(view, "MoveNavigationWithKey", Key.PageDown)));
            Assert.Throws<ArgumentOutOfRangeException>(() => view.SetNavigationPosition(new Point(double.NaN, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => view.SetNavigationPosition(new Point(0, 1.1)));
            Call(view, "ClearTextSelection");
            view.SetNavigationPosition(new Point(0.3, 0.4));
            savedPositions.Clear();

            foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
            {
                AppAppearance.Apply(application, theme);
                foreach (var size in new[] { new Size(760, 360), new Size(320, 240), new Size(200, 180) })
                {
                    Resize(size.Width, size.Height);
                    Assert.Equal(Visibility.Visible, navigation.Visibility);
                    Assert.Equal(new Point(0.3, 0.4), view.NavigationPosition);
                    var bounds = navigation.TransformToAncestor(viewport).TransformBounds(new Rect(navigation.RenderSize));
                    Assert.True(bounds.Left >= 0);
                    Assert.True(bounds.Top >= grid.ActualHeight - scroll.ViewportHeight);
                    Assert.True(bounds.Bottom <= grid.ActualHeight - 7);
                    var scrollbar = scroll.Template.FindName("PART_VerticalScrollBar", scroll) as FrameworkElement;
                    var rightEdge = scrollbar is { ActualWidth: > 0 }
                        ? scrollbar.TranslatePoint(new Point(), grid).X : grid.ActualWidth - SystemParameters.VerticalScrollBarWidth;
                    Assert.True(bounds.Right <= rightEdge - 7);
                    var height = grid.ActualHeight;
                    navigation.Visibility = Visibility.Collapsed;
                    Pump(root);
                    Assert.Equal(height, grid.ActualHeight);
                    Call(view, "UpdateNavigationControls");
                    Pump(root);
                    Assert.Equal(Visibility.Visible, navigation.Visibility);
                    Assert.Equal(height, grid.ActualHeight);
                    Assert.Equal(new Size(0, 0), canvas.DesiredSize);
                    var glyph = Assert.IsType<System.Windows.Shapes.Path>(pageDown.Content);
                    Assert.Equal(Assert.IsType<SolidColorBrush>(pageDown.Foreground).Color,
                        Assert.IsType<SolidColorBrush>(glyph.Stroke).Color);
                    var gripGlyph = FindChild<System.Windows.Shapes.Path>(grip)!;
                    Assert.Equal(Assert.IsType<SolidColorBrush>(application.Resources["TextFillColorSecondaryBrush"]).Color,
                        Assert.IsType<SolidColorBrush>(gripGlyph.Stroke).Color);
                }
            }
            Resize(760, 360);
            StartDrag();
            Drag(30, 20);
            Resize(120, 180);
            EndDrag();
            Assert.Equal(Visibility.Collapsed, navigation.Visibility);
            Assert.Equal(new Point(0.3, 0.4), view.NavigationPosition);
            Assert.Empty(savedPositions);
            Resize(760, 70);
            Assert.Equal(Visibility.Collapsed, navigation.Visibility);
            Resize(760, 360);
            Assert.Equal(Visibility.Visible, navigation.Visibility);
            StartDrag();
            Drag(30, 20);
            view.SetSpeakerFilter([]);
            Pump(root);
            EndDrag();
            Assert.Equal(Visibility.Visible, navigation.Visibility);
            Assert.Equal(new Point(0.3, 0.4), view.NavigationPosition);
            Assert.Empty(savedPositions);
            view.SetSpeakerFilter(null);
            Pump(root);
            Assert.Equal(Visibility.Visible, navigation.Visibility);
            view.SetDocument(new() { Segments = [new(0, 1, "Short transcript.")] }, reset: true);
            Pump(root);
            Assert.Equal(0, scroll.ScrollableHeight);
            Assert.Equal(Visibility.Visible, navigation.Visibility);
            view.SetDocument(new(), reset: true);
            Pump(root);
            Assert.Equal(Visibility.Visible, navigation.Visibility);
            Assert.False(top.IsEnabled || pageUp.IsEnabled || pageDown.IsEnabled || bottom.IsEnabled);
            view.SetDocument(document, reset: true);
            Pump(root);
            Assert.Equal(Visibility.Visible, navigation.Visibility);
            Assert.Equal(new Point(0.3, 0.4), view.NavigationPosition);
            Assert.Empty(savedPositions);
            Assert.True(view.IsFollowing);
            StartDrag();
            Drag(30, 20);
            EndDrag();
            Pump(root);
            Assert.True(view.IsFollowing);
            Assert.Single(savedPositions);
        }
        finally
        {
            host.Content = null;
            host.Close();
            AppAppearance.Apply(application, AppThemes.Light);
        }

        Rect Bounds() => navigation.TransformToAncestor(viewport).TransformBounds(new Rect(navigation.RenderSize));
        void StartDrag() => grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        void Drag(double x, double y) => grip.RaiseEvent(new DragDeltaEventArgs(x, y) { RoutedEvent = Thumb.DragDeltaEvent });
        void EndDrag(bool canceled = false) =>
            grip.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });

        void Resize(double width, double height)
        {
            root.Width = width;
            root.Height = height;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            Pump(root);
        }
    }
}
