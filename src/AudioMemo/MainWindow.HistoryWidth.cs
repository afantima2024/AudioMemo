using System.Text.Json;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace AudioMemo;

public partial class MainWindow
{
    private double? resizingHistoryWidth;
    private double MaximumHistoryWidth => Math.Clamp(WorkspaceBody.ActualWidth - (HistoryPinned ? 604 : 60), 180, 640);
    private double VisibleHistoryWidth => Math.Clamp(resizingHistoryWidth ?? settings.HistoryWidth, 180, MaximumHistoryWidth);

    private void HistoryWidth_DragStarted(object sender, DragStartedEventArgs e)
    {
        resizingHistoryWidth = VisibleHistoryWidth;
        historyPeek.Interact(historyPeekClock.Elapsed);
    }

    private void HistoryWidth_DragDelta(object sender, DragDeltaEventArgs e)
    {
        resizingHistoryWidth = Math.Clamp((resizingHistoryWidth ?? VisibleHistoryWidth) + e.HorizontalChange, 180, MaximumHistoryWidth);
        UpdateHistoryVisibility();
    }

    private void HistoryWidth_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        var width = resizingHistoryWidth;
        resizingHistoryWidth = null;
        try
        {
            if (!e.Canceled && width is not null)
                SaveHistoryWidth(width.Value);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            historyPeek.UpdatePointer(PointerInHistory, historyPeekClock.Elapsed);
            UpdateHistoryVisibility();
        }
    }

    private void SaveHistoryWidth(double width)
    {
        var next = JsonSerializer.Deserialize<Core.Models.AppSettings>(JsonSerializer.Serialize(settings))!;
        next.HistoryWidth = Math.Clamp(width, 180, MaximumHistoryWidth);
        storage.SaveSettings(next);
        settings = next;
        UpdateHistoryVisibility();
    }

    private void HistoryWidth_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && HistoryResizeGrip.IsDragging)
        {
            HistoryResizeGrip.CancelDrag();
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right or Key.Home)
        {
            try { SaveHistoryWidth(e.Key == Key.Home ? 210 : VisibleHistoryWidth + (e.Key == Key.Left ? -16 : 16)); }
            catch (Exception ex) { ShowError(ex); }
            e.Handled = true;
        }
    }
}
