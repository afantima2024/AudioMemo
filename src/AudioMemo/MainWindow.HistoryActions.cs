using System.Windows;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;
using Microsoft.Win32;

namespace AudioMemo;

public partial class MainWindow
{
    private bool exportingHistory;

    private RecordingSession[] RequireIdleHistorySelection()
    {
        if (RecordingActive || AiBusy || recordingCommand || exportBusy || closing)
            throw new InvalidOperationException(UiText.Get("SessionBusy"));
        var sessions = SelectedHistorySessions();
        if (sessions.Length == 0)
            throw new InvalidOperationException(UiText.Get("SelectSession"));
        return sessions;
    }

    internal void RenameHistorySelection(Func<RecordingSession, int, int, string?> prompt)
    {
        var renamed = 0;
        RecordingSession[] sessions = [];
        try
        {
            sessions = RequireIdleHistorySelection();
            SaveNotes();
            foreach (var session in sessions)
            {
                var title = prompt(session, renamed + 1, sessions.Length);
                if (title is null)
                {
                    SetStatus(UiText.Format("HistoryRenameCancelled", renamed, sessions.Length));
                    return;
                }
                SessionCommands.Rename(session, title);
                if (selected is not null && HistorySelectionPath(selected).Equals(HistorySelectionPath(session), StringComparison.OrdinalIgnoreCase))
                    selected.Title = session.Title;
                renamed++;
            }
            SetStatus(sessions.Length == 1 ? UiText.Get("SessionRenamed") : UiText.Format("HistoryRenamed", renamed));
        }
        catch (Exception ex)
        {
            ShowError(sessions.Length > 1
                ? new InvalidOperationException(UiText.Format("HistoryRenameFailed", renamed, sessions.Length, ex.Message), ex)
                : ex);
        }
        finally
        {
            if (sessions.Length > 0)
            {
                try { RefreshHistory(); }
                catch (Exception ex) { ShowError(ex); }
            }
            UpdateControls();
        }
    }

    private async Task ChooseHistoryExportAsync(HistoryExportKind kind)
    {
        try
        {
            var sessions = RequireIdleHistorySelection();
            var picker = new OpenFolderDialog
            {
                Title = UiText.Get(kind == HistoryExportKind.Mp3 ? "ChooseMp3ExportFolder" : "ChooseTranscriptExportFolder"),
                Multiselect = false
            };
            if (picker.ShowDialog(this) == true)
                await ExportHistorySelectionAsync(sessions, picker.FolderName, kind);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    internal async Task ExportHistorySelectionAsync(RecordingSession[] sessions, string destination, HistoryExportKind kind)
    {
        var ownsBusyState = false;
        var completed = 0;
        try
        {
            RequireIdleHistorySelection();
            exportCancellation?.Dispose();
            var cancellation = exportCancellation = new();
            exportBusy = exportingHistory = ownsBusyState = true;
            UpdateControls();
            var progress = new Progress<HistoryExportProgress>(update =>
            {
                if (!exportingHistory || !ReferenceEquals(exportCancellation, cancellation))
                    return;
                completed = update.Completed;
                if (!closing)
                    SetStatus(UiText.Format("HistoryExportProgress", update.Completed, update.Total, update.Title));
            });
            var task = HistoryExportService.ExportAsync(sessions, destination, kind, progress, exportCancellation.Token);
            exportTask = task;
            completed = await task;
            if (!closing)
                SetStatus(UiText.Format("HistoryExportComplete", completed, destination));
        }
        catch (OperationCanceledException)
        {
            if (!closing)
                SetStatus(UiText.Format("HistoryExportCancelled", completed, sessions.Length));
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (ownsBusyState)
                exportBusy = exportingHistory = false;
            UpdateControls();
        }
    }
}
