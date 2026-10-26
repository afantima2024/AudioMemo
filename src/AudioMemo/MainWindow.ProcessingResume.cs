using System.Windows;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class MainWindow
{
    private sealed class ActiveProcessingWork(RecordingSession session, List<ProcessingWork> work)
    {
        public RecordingSession Session { get; } = session;
        public List<ProcessingWork> Work { get; } = work;
        public bool Completed { get; set; }
    }

    private readonly Func<string, bool> confirmCloseProcessing;
    private readonly Func<string, bool> confirmResumeProcessing;
    private readonly Func<RecordingSession, ProcessingWorkKind, CancellationToken, Task> exportAudio;
    private readonly List<ActiveProcessingWork> deferredCloseWork = [];
    private ActiveProcessingWork? activeAiWork;
    private ActiveProcessingWork? activeExportWork;
    private ActiveProcessingWork? postRecordingWork;
    private PendingProcessingPlan? selectedPendingProcessing;
    private CancellationTokenSource? exportCancellation;
    private Task? exportTask;
    private TaskCompletionSource<Exception?>? recordingCommandCompletion;
    private bool closeReady;

    private bool ConfirmProcessing(string message, string titleKey) =>
        MessageDialog.ShowMessage(this, message, UiText.Get(titleKey), MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void RememberClosingWork(ActiveProcessingWork? work)
    {
        if (work is not null && !deferredCloseWork.Contains(work))
            deferredCloseWork.Add(work);
    }

    private void ForgetChangedRecordingWork(RecordingSession session)
    {
        var path = HistorySelectionPath(session);
        deferredCloseWork.RemoveAll(work => HistorySelectionPath(work.Session).Equals(path, StringComparison.OrdinalIgnoreCase));
        if (postRecordingWork is not null &&
            HistorySelectionPath(postRecordingWork.Session).Equals(path, StringComparison.OrdinalIgnoreCase))
            postRecordingWork = null;
    }

    private async Task<bool> PrepareCloseAsync()
    {
        if (closeReady)
            return true;
        if (closing)
            return false;
        var busy = RecordingActive || AiBusy || recordingCommand || exportBusy ||
            deferredCloseWork.Any(work => !work.Completed && work.Work.Count > 0);
        if (busy && !confirmCloseProcessing(UiText.Get(recyclingHistory ? "ConfirmCloseRecycling" :
            exportingHistory ? "ConfirmCloseBatchExport" : "ConfirmCloseProcessing")))
            return false;
        closing = true;
        UpdateControls();
        try
        {
            SaveNotes();
            var recordingTask = recordingCommand ? recordingCommandCompletion?.Task : null;
            var currentAiTask = AiBusy ? aiTask : null;
            var currentExportTask = activeExportWork is not null ? exportTask : null;
            var recordingTranscription = activeAiWork is { } aiWork && aiWork.Session.Id == recorder.Session?.Id &&
                aiWork.Work.Any(item => item.Kind == ProcessingWorkKind.Transcribe);
            RememberClosingWork(activeAiWork);
            RememberClosingWork(activeExportWork);
            RememberClosingWork(postRecordingWork);
            SetStatus(UiText.Get("ClosingSafely"));
            StopReplay();
            aiCancellation?.Cancel();
            modelSelectionCancellation?.Cancel();
            exportCancellation?.Cancel();
            var wasRecording = RecordingActive;
            var stoppingRecording = recordingTask is null ? recorder.StopAsync() : null;
            foreach (var window in OwnedWindows.Cast<Window>().ToArray())
                window.Close();
            if (recordingTask is not null && await recordingTask is { } recordingError)
                throw recordingError;
            if (currentAiTask is not null)
                await currentAiTask;
            if (modelSelectionTask is not null)
                await modelSelectionTask;
            if (currentExportTask is not null)
            {
                try { await currentExportTask; }
                catch (OperationCanceledException) when (exportCancellation?.IsCancellationRequested == true) { }
            }
            // Owned editors cancel their own workers; an atomic file publication must finish.
            while (exportBusy || OwnedWindows.Count > 0)
                await Task.Delay(25);

            wasRecording |= RecordingActive;
            var saved = stoppingRecording is null ? await recorder.StopAsync() : await stoppingRecording;
            if (wasRecording && saved is { Frames: > 0, Status: not "Interrupted" })
            {
                var work = new List<ProcessingWork>();
                if (RequiresAudioExport(saved))
                    work.Add(new(ProcessingWorkKind.ExportAudio));
                if (settings.TranscribeOnStop && !recordingTranscription)
                    work.Add(ProcessingWork.ForAi("TranscribeResume", settings));
                RememberClosingWork(new(saved, work));
            }
            RememberClosingWork(postRecordingWork);
            foreach (var group in deferredCloseWork.Where(work => !work.Completed && work.Work.Count > 0 && work.Session.Frames > 0)
                .GroupBy(work => work.Session.Id).ToArray())
            {
                PendingProcessingStore.Defer(group.First().Session, group.SelectMany(work => work.Work));
                foreach (var work in group)
                    work.Completed = true;
                if (selected?.Id == group.Key)
                    selectedPendingProcessing = PendingProcessingStore.Load(selected);
            }

            lifetime.Cancel();
            if (modelRefreshTask is not null)
                await modelRefreshTask;
            clock.Stop();
            notesDebounce.Stop();
            await recorder.DisposeAsync();
            replay.Dispose();
            aiCancellation?.Dispose();
            exportCancellation?.Dispose();
            lifetime.Dispose();
            closeReady = true;
            return true;
        }
        catch
        {
            closing = false;
            UpdateControls();
            throw;
        }
    }

    private static bool RequiresAudioExport(RecordingSession session) =>
        session.RequestedFormat != "WAV" || !session.AudioOutput.UsesSafetyFormat;

    private async Task<RecordingSession> ContinueRecordingAsync(RecordingSession session, RecordingSources sources)
    {
        var pending = PendingProcessingStore.Load(session);
        if (pending is not null)
            PendingProcessingStore.Clear(session);
        try { return await recorder.ContinueAsync(settings, session, sources); }
        catch (Exception recordingError)
        {
            if (pending is not null)
            {
                try
                {
                    // Opening and closing the append writer can refresh the unchanged WAV header.
                    PendingProcessingStore.Save(session, pending with { AudioVersion = AudioSourceVersion.Read(session.WavePath) });
                }
                catch (Exception restoreError)
                {
                    RememberClosingWork(new(session, pending.Work));
                    throw new AggregateException(recordingError, restoreError);
                }
            }
            throw;
        }
    }

    private void CompletePendingWork(RecordingSession session, ProcessingWorkKind kind)
    {
        PendingProcessingStore.Complete(session, kind);
        foreach (var work in deferredCloseWork.Where(work => work.Session.Id == session.Id))
            work.Work.RemoveAll(item => item.Kind == kind);
        if (selected?.Id == session.Id)
            selectedPendingProcessing = PendingProcessingStore.Load(session);
    }

    private void StartPendingProcessing(RecordingSession session, PendingProcessingPlan plan)
    {
        EnsureRecovered(session);
        PendingProcessingStore.VerifyAudio(session, plan);
        var work = plan.Work.ToList();
        foreach (var item in work.Where(item => item.Options is not null))
        {
            var snapshot = item.Options!.Restore(settings);
            if ((item.Kind is ProcessingWorkKind.MainPoints or ProcessingWorkKind.AboutMe ||
                item.Kind == ProcessingWorkKind.Transcribe && snapshot.SummarizeOnStop) &&
                !snapshot.CanSummarize)
                throw new InvalidOperationException(UiText.Get("PendingProcessingConsentRequired"));
            if (item.Kind == ProcessingWorkKind.AboutMe && AppStorage.LoadTranscript(session).Me?.TagId != item.MeTagId)
                throw new InvalidOperationException(UiText.Get("MeTagChanged"));
        }
        PendingProcessingStore.SetPromptOnStartup(session, plan, false);
        selectedPendingProcessing = plan with { PromptOnStartup = false };
        StartAiJobCore(session, "ResumeProcessing", work, async (current, progress, token) =>
        {
            foreach (var item in work.ToArray())
            {
                token.ThrowIfCancellationRequested();
                PendingProcessingStore.VerifyAudio(session, plan);
                var snapshot = item.Options?.Restore(current) ?? current;
                switch (item.Kind)
                {
                    case ProcessingWorkKind.Transcribe:
                        await ai.TranscribeAsync(session, snapshot, () => (session.Frames, false), progress, token,
                            ReviewTranscriptBeforeSummaryAsync);
                        break;
                    case ProcessingWorkKind.MainPoints:
                        await ai.SummarizeAsync(session, snapshot, progress, false, token, item.SummaryGenerationId,
                            ReviewTranscriptBeforeSummaryAsync);
                        break;
                    case ProcessingWorkKind.TranscriptReview:
                        await ReviewTranscriptAsync(session, snapshot, progress, token, automatic: false);
                        break;
                    case ProcessingWorkKind.AboutMe:
                        await GeneratePersonalNotesAsync(session, snapshot, progress, token,
                            item.SummaryGenerationId!, item.MeTagId!);
                        break;
                    case ProcessingWorkKind.Speakers:
                        await ai.IdentifySpeakersAsync(session, snapshot, progress, token);
                        break;
                    case ProcessingWorkKind.UnlabeledSpeakers:
                        await ai.RecheckUnlabeledSpeakersAsync(session, snapshot, progress, token);
                        break;
                    case ProcessingWorkKind.ExportAudio:
                    case ProcessingWorkKind.ExportMp3:
                        progress.Report(new(session.Id, UiText.Get(item.Kind == ProcessingWorkKind.ExportMp3 ?
                            "ExportingMp3" : "ExportingConfiguredAudio"), WorkProgress: new("audio-export")));
                        await exportAudio(session, item.Kind, token);
                        break;
                    default:
                        throw new InvalidOperationException(UiText.Get("InvalidPendingProcessing"));
                }
                CompletePendingWork(session, item.Kind);
                work.Remove(item);
            }
        });
    }

    private async Task OfferPendingProcessingAsync()
    {
        var (sessions, errors) = AppStorage.LoadSessions(settings.SaveFolder);
        if (errors.Count > 0)
            SetStatus(UiText.Format("HistoryReadFailed", string.Join("; ", errors)), true);
        foreach (var session in sessions)
        {
            if (closing || RecordingActive || AiBusy || recordingCommand || exportBusy)
                return;
            try
            {
                var plan = PendingProcessingStore.Load(session);
                if (plan?.PromptOnStartup != true)
                    continue;
                SaveNotes();
                selected = session;
                LoadSessionContent(session);
                RefreshHistory();
                UpdateControls();
                UpdateRecordingDisplay();
                var approved = confirmResumeProcessing(UiText.Format("ConfirmResumeProcessing", session.Title,
                    DescribePendingProcessing(plan)));
                PendingProcessingStore.SetPromptOnStartup(session, plan, false);
                selectedPendingProcessing = plan with { PromptOnStartup = false };
                if (approved)
                {
                    StartPendingProcessing(session, plan);
                    if (aiTask is not null)
                        await aiTask;
                }
                else
                    SetStatus(UiText.Get("ProcessingDeferred"));
            }
            catch (Exception ex) { ShowOperationError(ex, "ResumeProcessing", session.Id); }
            UpdateControls();
        }
    }

    private static string DescribePendingProcessing(PendingProcessingPlan plan) =>
        string.Join(Environment.NewLine, plan.Work.Select(item =>
        {
            var label = UiText.Get(item.LabelKey);
            if (item.Options is not { } options)
                return label;
            if (item.Kind == ProcessingWorkKind.Transcribe)
                label += $" - Whisper {options.SpeechModel} / {options.Language}";
            if (item.Kind is ProcessingWorkKind.MainPoints or ProcessingWorkKind.AboutMe or ProcessingWorkKind.TranscriptReview ||
                item.Kind == ProcessingWorkKind.Transcribe && (options.SummarizeOnStop || options.ReviewUnclearTranscript))
            {
                var modelAction = item.Kind == ProcessingWorkKind.AboutMe ? "SummarizeAboutMe" :
                    item.Kind == ProcessingWorkKind.TranscriptReview ||
                    item.Kind == ProcessingWorkKind.Transcribe && !options.SummarizeOnStop
                        ? "ReviewUnclearTranscript" : "UpdateMainPoints";
                label += Environment.NewLine + UiText.Get(modelAction) + " - " +
                    SummaryProviders.Label(options.SummaryProvider, UiText.Current) + " / " +
                    (options.SummaryModel.Length == 0 ? UiText.Get("CliDefaultModel") : options.SummaryModel) +
                    (options.SummaryEndpoint.Length == 0 ? "" : " / " + options.SummaryEndpoint);
            }
            if (item.Kind == ProcessingWorkKind.Transcribe && options.IdentifySpeakersOnStop)
                label += Environment.NewLine + UiText.Get("IdentifySpeakers");
            if (options.ReviewUnclearTranscript && item.Kind is ProcessingWorkKind.Transcribe or ProcessingWorkKind.MainPoints)
                label += Environment.NewLine + UiText.Get("ReviewUnclearTranscript");
            return label;
        }));
}
