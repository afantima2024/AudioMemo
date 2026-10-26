using System.Windows;
using AudioMemo.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;
using Microsoft.Win32;

namespace AudioMemo;

public partial class MainWindow
{
    private void ImportAudio_Click(object sender, RoutedEventArgs e)
    {
        var ownsBusyState = false;
        try
        {
            if (RecordingActive || AiBusy || recordingCommand || exportBusy || closing)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            var picker = new OpenFileDialog
            {
                Title = UiText.Get("ImportAudio"), Filter = UiText.Get("ImportAudioFilter"),
                CheckFileExists = true, Multiselect = false
            };
            if (picker.ShowDialog(this) != true)
                return;
            SaveNotes();
            StopReplay();
            exportBusy = ownsBusyState = true;
            UpdateControls();
            var dialog = new AudioImportWindow(picker.FileName, settings.SaveFolder) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is null)
                return;
            DisplayImportedSession(dialog.Result);
            if (MessageDialog.ShowMessage(this, UiText.Format("ConfirmImportAnalysis", dialog.Result.Title),
                UiText.Get("ImportAudio"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            if (!SpeechModels.IsDownloaded(settings.SpeechModel))
                throw new InvalidOperationException(UiText.Get("ImportModelRequired"));
            StartTranscription(dialog.Result, false);
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (ownsBusyState)
                exportBusy = false;
            UpdateControls();
        }
    }

    private void DisplayImportedSession(RecordingSession session)
    {
        selected = session;
        pendingTitle = "";
        RefreshHistory();
        LoadSessionContent(session);
        UpdateRecordingDisplay();
        SetStatus(UiText.Get("AudioImported"));
    }
}
