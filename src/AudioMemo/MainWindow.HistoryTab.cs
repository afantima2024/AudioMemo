using System.Windows;
using System.Windows.Input;
using AudioMemo.Core.Models;

namespace AudioMemo;

public partial class MainWindow
{
    private Point? historyTabOrigin;
    private double historyTabOriginalPosition;
    private double historyTabOriginalOffset;
    private bool historyTabDragging;

    private void HistoryBody_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateHistoryTabPosition();

    private void UpdateHistoryTabPosition()
    {
        if (HistoryPeekTab is not null && WorkspaceBody is not null)
            HistoryPeekTab.Margin = new Thickness(0,
                HistoryTabPlacement.Offset(settings.HistoryTabPosition, WorkspaceBody.ActualHeight, HistoryPeekTab.Height), 0, 0);
    }

    private void HistoryTab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        BeginHistoryTabDrag(e.GetPosition(WorkspaceBody));
    }

    private void BeginHistoryTabDrag(Point point)
    {
        historyTabOrigin = point;
        historyTabOriginalPosition = settings.HistoryTabPosition;
        historyTabOriginalOffset = HistoryPeekTab.Margin.Top;
        historyTabDragging = false;
    }

    private void HistoryTab_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
            return;
        if (MoveHistoryTabDrag(e.GetPosition(WorkspaceBody)))
            e.Handled = true;
    }

    private bool MoveHistoryTabDrag(Point point)
    {
        if (historyTabOrigin is not { } origin)
            return false;
        var delta = point - origin;
        if (!historyTabDragging)
        {
            if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance &&
                Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance)
                return false;
            historyTabDragging = true;
            CloseHistoryPreview();
        }
        SetHistoryTabPosition(HistoryTabPlacement.Position(historyTabOriginalOffset + delta.Y,
            WorkspaceBody.ActualHeight, HistoryPeekTab.Height), persist: false);
        return true;
    }

    private void HistoryTab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || historyTabOrigin is null)
            return;
        if (historyTabDragging)
        {
            e.Handled = true;
            FinishHistoryTabDrag(commit: true);
        }
        else
            historyTabOrigin = null;
    }

    private void HistoryTab_LostCapture(object sender, MouseEventArgs e) => FinishHistoryTabDrag(commit: false);

    private void HistoryTab_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && historyTabOrigin is not null)
        {
            FinishHistoryTabDrag(commit: false);
            e.Handled = true;
        }
    }

    private void FinishHistoryTabDrag(bool commit)
    {
        if (historyTabOrigin is null)
            return;
        var desired = settings.HistoryTabPosition;
        settings.HistoryTabPosition = historyTabOriginalPosition;
        historyTabOrigin = null;
        historyTabDragging = false;
        HistoryPeekTab.ReleaseMouseCapture();
        try
        {
            if (commit)
                SetHistoryTabPosition(desired, persist: true);
            else
                UpdateHistoryTabPosition();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SetHistoryTabPosition(double position, bool persist)
    {
        HistoryTabPlacement.Offset(position, WorkspaceBody.ActualHeight, HistoryPeekTab.Height);
        var previous = settings.HistoryTabPosition;
        settings.HistoryTabPosition = position;
        try
        {
            if (persist)
                storage.SaveSettings(settings);
        }
        catch
        {
            settings.HistoryTabPosition = previous;
            UpdateHistoryTabPosition();
            throw;
        }
        UpdateHistoryTabPosition();
    }
}
