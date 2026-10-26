using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AudioMemo.Controls;

public partial class TranscriptView
{
    private Rect navigationTravel;
    private Point? navigationDragStart;
    public Point NavigationPosition { get; private set; } = new(1, 1);
    public event Action<Point>? NavigationPositionChanged;

    public void SetNavigationPosition(Point position)
    {
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) ||
            position.X is < 0 or > 1 || position.Y is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(position));
        CancelNavigationDrag();
        NavigationPosition = position;
        UpdateNavigationControls();
    }

    private void UpdateNavigationControls()
    {
        if (FloatingNavigation is null || NavigationCanvas is null || RowsGrid is null)
            return;
        scroll ??= FindChild<ScrollViewer>(RowsGrid);
        var scrollbarWidth = scroll?.Template?.FindName("PART_VerticalScrollBar", scroll) is FrameworkElement { ActualWidth: > 0 } scrollbar
            ? scrollbar.ActualWidth : SystemParameters.VerticalScrollBarWidth;
        var viewportHeight = scroll?.ViewportHeight ?? 0;
        navigationTravel = new Rect(8, Math.Max(0, NavigationCanvas.ActualHeight - viewportHeight) + 8,
            Math.Max(0, NavigationCanvas.ActualWidth - scrollbarWidth - FloatingNavigation.Width - 16),
            Math.Max(0, viewportHeight - FloatingNavigation.Height - 16));
        var visible = rows.Count > 0 && scroll is { ScrollableHeight: > 1 } &&
            viewportHeight >= FloatingNavigation.Height + 16 &&
            NavigationCanvas.ActualWidth >= FloatingNavigation.Width + scrollbarWidth + 16;
        if (!visible)
            CancelNavigationDrag();
        FloatingNavigation.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlaceNavigation();
        var canGoUp = visible && scroll is { VerticalOffset: > 1 };
        var canGoDown = visible && scroll is not null && scroll.ScrollableHeight - scroll.VerticalOffset > 1;
        TranscriptTopButton.IsEnabled = visible && (canGoUp || IsFollowing);
        TranscriptPageUpButton.IsEnabled = canGoUp;
        TranscriptPageDownButton.IsEnabled = canGoDown;
        TranscriptBottomButton.IsEnabled = visible && (canGoDown || !IsFollowing);
    }

    private void PlaceNavigation()
    {
        Canvas.SetLeft(FloatingNavigation, navigationTravel.X + navigationTravel.Width * NavigationPosition.X);
        Canvas.SetTop(FloatingNavigation, navigationTravel.Y + navigationTravel.Height * NavigationPosition.Y);
    }

    private void MoveNavigation(Vector delta)
    {
        NavigationPosition = new Point(
            navigationTravel.Width > 0 ? Math.Clamp(NavigationPosition.X + delta.X / navigationTravel.Width, 0, 1) : NavigationPosition.X,
            navigationTravel.Height > 0 ? Math.Clamp(NavigationPosition.Y + delta.Y / navigationTravel.Height, 0, 1) : NavigationPosition.Y);
        PlaceNavigation();
    }

    private void CommitNavigationPosition(Point previous)
    {
        if (NavigationPosition == previous)
            return;
        try { NavigationPositionChanged?.Invoke(NavigationPosition); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            NavigationPosition = previous;
            UpdateNavigationControls();
            OperationFailed?.Invoke(ex);
        }
    }

    private void NavigationDrag_Started(object sender, DragStartedEventArgs e)
    {
        if (FloatingNavigation.Visibility == Visibility.Visible)
            navigationDragStart = NavigationPosition;
        e.Handled = true;
    }

    private void NavigationDrag_Delta(object sender, DragDeltaEventArgs e)
    {
        if (navigationDragStart is not null)
            MoveNavigation(new Vector(e.HorizontalChange, e.VerticalChange));
        e.Handled = true;
    }

    private void NavigationDrag_Completed(object sender, DragCompletedEventArgs e)
    {
        if (navigationDragStart is { } previous)
        {
            navigationDragStart = null;
            if (e.Canceled)
            {
                NavigationPosition = previous;
                UpdateNavigationControls();
            }
            else
                CommitNavigationPosition(previous);
        }
        e.Handled = true;
    }

    private void CancelNavigationDrag()
    {
        if (navigationDragStart is not { } previous)
            return;
        navigationDragStart = null;
        NavigationPosition = previous;
        NavigationDragHandle.CancelDrag();
        UpdateNavigationControls();
    }

    private void NavigationDrag_Unloaded(object sender, RoutedEventArgs e) => CancelNavigationDrag();

    private void NavigationCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CancelNavigationDrag();
        UpdateNavigationControls();
    }

    internal bool HandlesNavigationPositionKey(Key key, ModifierKeys modifiers) =>
        navigationDragStart is not null ||
        NavigationDragHandle.IsKeyboardFocused && modifiers == ModifierKeys.None &&
        key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home;

    private void NavigationDrag_KeyDown(object sender, KeyEventArgs e)
    {
        if (navigationDragStart is not null)
        {
            if (e.Key == Key.Escape)
                CancelNavigationDrag();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None)
            e.Handled = MoveNavigationWithKey(e.Key);
    }

    private bool MoveNavigationWithKey(Key key)
    {
        if (FloatingNavigation.Visibility != Visibility.Visible ||
            key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home))
            return false;
        var previous = NavigationPosition;
        if (key == Key.Home)
            SetNavigationPosition(new Point(1, 1));
        else
            MoveNavigation(key switch
            {
                Key.Left => new Vector(-16, 0),
                Key.Right => new Vector(16, 0),
                Key.Up => new Vector(0, -16),
                _ => new Vector(0, 16)
            });
        CommitNavigationPosition(previous);
        return true;
    }

    private void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action })
            NavigateTranscript(action);
    }

    private void Navigation_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (navigationDragStart is not null)
        {
            e.Handled = true;
            return;
        }
        if (scroll is null)
            return;
        Grid_PreviewMouseWheel(sender, e);
        scroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = Mouse.MouseWheelEvent });
        e.Handled = true;
    }
}
