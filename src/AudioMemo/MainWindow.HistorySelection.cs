using System.IO;
using System.Windows;
using System.Windows.Input;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class MainWindow
{
    private readonly Func<IReadOnlyList<RecordingSession>, bool> confirmRecycleHistory;
    private readonly Action<string, RecordingSession> recycleHistorySession;
    private bool recyclingHistory;
    private bool MultipleHistorySelection => HistoryList.SelectedItems.Count > 1;

    private static string HistorySelectionPath(RecordingSession session) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.Folder));

    private RecordingSession[] SelectedHistorySessions()
    {
        var selectedItems = HistoryList.SelectedItems.Cast<RecordingSession>().ToHashSet();
        return HistoryList.Items.Cast<RecordingSession>().Where(selectedItems.Contains).ToArray();
    }

    private void UpdateHistorySelectionSummary()
    {
        var count = HistoryList.Items.Count;
        var selectedCount = HistoryList.SelectedItems.Count;
        SelectAllHistoryCheck.IsEnabled = HistoryList.IsEnabled && count > 0;
        SelectAllHistoryCheck.IsChecked = selectedCount == 0 ? false : selectedCount == count ? true : null;
        LibraryHint.Text = count == 0 ? UiText.Get("FirstRecordingHint") :
            selectedCount == 0 ? UiText.Format("RecordingCount", count) :
            UiText.Format("RecordingSelectionCount", count, selectedCount);
        DeleteSessionMenu.Header = selectedCount > 1 ? UiText.Format("RecycleSelectedSessions", selectedCount) :
            UiText.Get("RecycleSession");
    }

    private void SelectAllHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!HistoryList.IsEnabled)
            return;
        if (HistoryList.SelectedItems.Count == HistoryList.Items.Count)
            HistoryList.UnselectAll();
        else
            HistoryList.SelectAll();
    }

    private void SelectAllHistory_CanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = HistoryList.IsEnabled && HistoryList.Items.Count > 0;
        e.Handled = true;
    }

    private void SelectAllHistory_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (HistoryList.IsEnabled)
            HistoryList.SelectAll();
        e.Handled = true;
    }

    internal static MessageDialog CreateRecycleHistoryDialog(Window? owner, IReadOnlyList<RecordingSession> sessions)
    {
        if (sessions.Count == 0)
            throw new InvalidOperationException(UiText.Get("SelectSession"));
        var text = sessions.Count == 1
            ? UiText.Format("ConfirmRecycleSession", sessions[0].Title, sessions[0].Folder)
            : UiText.Format("ConfirmRecycleSessions", sessions.Count, string.Join(Environment.NewLine + Environment.NewLine,
                sessions.Select(session => session.Title + Environment.NewLine + session.Folder)));
        return new(owner, text, sessions.Count == 1 ? UiText.Get("RecycleSession") :
            UiText.Format("RecycleSelectedSessions", sessions.Count), MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No);
    }

    private bool ConfirmRecycleHistory(IReadOnlyList<RecordingSession> sessions)
    {
        var dialog = CreateRecycleHistoryDialog(this, sessions);
        dialog.ShowDialog();
        return dialog.Result == MessageBoxResult.Yes;
    }

    private async Task RecycleSelectedHistoryAsync()
    {
        var ownsBusyState = false;
        RecordingSession[] sessions = [];
        RecordingSession? current = null;
        Exception? error = null;
        var recycled = 0;
        try
        {
            if (RecordingActive || AiBusy || recordingCommand || exportBusy || closing)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            sessions = SelectedHistorySessions();
            if (sessions.Length == 0)
                throw new InvalidOperationException(UiText.Get("SelectSession"));
            var library = settings.SaveFolder;
            foreach (var session in sessions)
            {
                current = session;
                SessionCommands.ValidateDeletion(library, session);
            }
            if (!confirmRecycleHistory(sessions))
                return;
            current = selected;
            SaveNotes();
            StopReplay();
            exportBusy = recyclingHistory = ownsBusyState = true;
            UpdateControls();
            foreach (var session in sessions)
            {
                if (closing)
                    throw new OperationCanceledException();
                current = session;
                SetStatus(UiText.Format("RecyclingSessions", recycled, sessions.Length));
                await Task.Run(() => recycleHistorySession(library, session));
                recycled++;
                ForgetChangedRecordingWork(session);
            }
            if (!closing)
                SetStatus(sessions.Length == 1 ? UiText.Get("SessionRecycled") : UiText.Format("SessionsRecycled", recycled));
        }
        catch (OperationCanceledException)
        {
            if (!closing)
                SetStatus(sessions.Length > 1 ? UiText.Format("RecycleSelectionCancelled", recycled, sessions.Length) :
                    UiText.Get("DeletionCancelled"));
        }
        catch (Exception ex)
        {
            error = sessions.Length > 1 && current is not null ?
                new IOException(UiText.Format("RecycleSelectionFailed", recycled, sessions.Length, current.Title, ex.Message), ex) : ex;
        }
        finally
        {
            if (ownsBusyState)
            {
                exportBusy = recyclingHistory = false;
                try { RefreshHistory(); }
                catch (Exception ex) { error = error is null ? ex : new AggregateException(error, ex); }
            }
            UpdateControls();
            if (error is not null)
                ShowError(error);
        }
    }
}
