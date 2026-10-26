using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class MainWindow
{
    private void CorrectSelectedRows(IReadOnlyList<TranscriptRow> rows, bool reTranscribe)
        => OpenTranscriptCorrection(rows, reTranscribe, false);

    private void OpenTranscriptCorrection(IReadOnlyList<TranscriptRow> rows, bool reTranscribe, bool online)
    {
        var ownsBusyState = false;
        try
        {
            var session = RequireIdleSession();
            if (reTranscribe || online)
                EnsureRecovered(session);
            SaveNotes();
            StopReplay();
            var plan = TranscriptCorrectionPlan.Create(AppStorage.LoadTranscript(session), session.Frames, rows);
            TranscriptCorrectionService.VerifySource(session, plan);
            exportBusy = true;
            ownsBusyState = true;
            UpdateControls();
            var dialog = new TranscriptEditWindow(session, plan, settings, reTranscribe, SaveSpeechVocabulary,
                online: online, saveOnlineSettings: SaveOnlineSpeechSettings) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;
            currentTranscript = AppStorage.LoadTranscript(session);
            TranscriptPanel.SetDocument(currentTranscript);
            summaryContent = AppStorage.LoadSummaryText(session);
            UpdateSpeechStatus();
            UpdateModelStatus();
            RefreshHistory();
            SetStatus(UiText.Get("TranscriptCorrectionsSaved"));
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (ownsBusyState)
                exportBusy = false;
            UpdateControls();
        }
    }

    private void SaveSpeechVocabulary(IReadOnlyList<string> terms, bool enabled)
    {
        var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        next.SpeechVocabularyTerms = SpeechVocabulary.Normalize(terms);
        next.SpeechVocabularyEnabled = enabled;
        storage.SaveSettings(next);
        settings = next;
    }

    private void SaveOnlineSpeechSettings(OnlineSpeechSettings selection)
    {
        var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        next.OnlineSpeech = JsonSerializer.Deserialize<OnlineSpeechSettings>(JsonSerializer.Serialize(selection))!;
        storage.SaveSettings(next);
        settings = next;
    }
}
