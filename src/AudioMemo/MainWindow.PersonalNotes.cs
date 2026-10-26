using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo;

public partial class MainWindow
{
    private readonly Func<string, bool> confirmAppendAboutMe;

    private void UpdateMyNotes_Click(object sender, RoutedEventArgs e) => SummarizeAboutMe();

    private void UpdatePersonalNotesModelStatus(string selectedModel)
    {
        var attribution = UiText.Get("PersonalNotesModelUnrecorded");
        try
        {
            var state = selected is null ? null : PersonalSummaryStore.Load(selected);
            var (provider, model) = PersonalSummaryStore.AppliedAttribution(state, NotesBox.Text);
            if (!string.IsNullOrWhiteSpace(provider))
                attribution = UiText.Format("SummaryGeneratedWith", SummaryProviders.Label(provider, UiText.Current),
                    string.IsNullOrWhiteSpace(model) ? UiText.Get("SummaryModelUnrecorded") : model);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            attribution = UiText.Get("PersonalNotesModelUnavailable");
            ShowError(ex, "MyNotes");
        }
        MyNotesTitle.ToolTip = UiText.Format("PersonalNotesModelHint", attribution, selectedModel);
    }

    private bool EnsureSummaryConsent()
    {
        settings.Validate();
        if (settings.UsesCloudApi)
        {
            CloudLlmCatalog.ValidateModel(settings.GetCloudProfile().Model, required: true);
            if (!CloudApiKey.IsConfigured(settings.SummaryProvider, settings.GetCloudProfile()))
                throw new InvalidOperationException(UiText.Get("CloudKeyRequired"));
        }
        if (settings.CanSummarize)
            return true;
        if (settings.UsesLocalSummaries)
            throw new InvalidOperationException(UiText.Get("LocalModelNotSelected"));
        var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
            ?? throw new InvalidOperationException("Could not copy AI settings.");
        var message = next.UsesCloudApi
            ? UiText.Format("CloudConsentMessage", SummaryProviders.Label(next.SummaryProvider, UiText.Current),
                next.ActiveSummaryEndpoint)
            : UiText.Get("ConsentMessage");
        if (MessageDialog.ShowMessage(message, UiText.Get("ConsentTitle"), MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;
        if (next.UsesCloudApi)
            next.GetCloudProfile().ConsentEndpoint = next.ActiveSummaryEndpoint;
        else
            next.CloudConsent = true;
        next.SummarySetupDeferred = false;
        storage.SaveSettings(next);
        settings = next;
        return true;
    }

    private void MarkMe(string speakerId)
    {
        try
        {
            var session = RequireIdleSession();
            var document = AppStorage.LoadTranscript(session);
            if (!document.Segments.Any(segment => segment.SpeakerId == speakerId))
                throw new InvalidOperationException(UiText.Get("ChooseLabeledSpeaker"));
            var dialog = new SelfSpeakerWindow(this, speakerId, document.SpeakerName(speakerId), document.Me);
            if (dialog.ShowDialog() == true && dialog.Value is { } me)
                SaveMeTag(me);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RemoveMe()
    {
        try { SaveMeTag(null); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SaveMeTag(SelfSpeaker? me)
    {
        var session = RequireIdleSession();
        var document = AppStorage.LoadTranscript(session);
        me?.Validate();
        if (me is not null && !document.Segments.Any(segment => segment.SpeakerId == me.SpeakerId))
            throw new InvalidOperationException(UiText.Get("ChooseLabeledSpeaker"));
        if (me is not null && document.Me is { } previous && previous.SpeakerId == me.SpeakerId &&
            previous.Names.SequenceEqual(me.Names, StringComparer.Ordinal))
            me = previous.Copy();
        document.Me = me?.Copy();
        AppStorage.SaveTranscript(session, document);
        currentTranscript = document;
        TranscriptPanel.SetDocument(currentTranscript);
        UpdateControls();
        SetStatus(UiText.Get(me is null ? "MeTagRemoved" : "MeTagSaved"));
    }

    private void SummarizeAboutMe()
    {
        try
        {
            var session = RequireIdleSession();
            EnsureRecovered(session);
            var transcript = AppStorage.LoadTranscript(session);
            var me = PersonalSummaryPrompt.RequireMe(transcript);
            if (transcript.IsDraft || transcript.ProcessedFrames != session.Frames)
                throw new InvalidOperationException(UiText.Get("SummaryRequiresFinalTranscript"));
            if (!EnsureSummaryConsent())
                return;
            var snapshot = ai.ResolveSummarySettings(settings);
            var previous = PersonalSummaryStore.Load(session);
            var generationId = previous is { GenerationId.Length: > 0 } && previous.AppliedGenerationId != previous.GenerationId &&
                previous.Matches(transcript, snapshot) ? previous.GenerationId : Guid.NewGuid().ToString("N");
            StartAiJobCore(session, "SummarizeAboutMe",
                [ProcessingWork.ForAi("SummarizeAboutMe", snapshot, generationId, me.TagId)],
                (run, progress, token) => GeneratePersonalNotesAsync(session, run, progress, token, generationId, me.TagId), snapshot);
        }
        catch (Exception ex) { ShowOperationError(ex, "SummarizeAboutMe", selected?.Id); }
    }

    private async Task GeneratePersonalNotesAsync(RecordingSession session, AppSettings snapshot, IProgress<AiUpdate> progress,
        CancellationToken token, string generationId, string tagId)
    {
        var result = await ai.SummarizeAboutMeAsync(session, snapshot, progress, token, generationId, tagId).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await Dispatcher.InvokeAsync(() => ApplyPersonalNotes(session, result, token), DispatcherPriority.Normal, token);
        progress.Report(new(session.Id, UiText.Get("AboutMeSaved"), WorkProgress: new("saving")));
    }

    private void ApplyPersonalNotes(RecordingSession session, PersonalSummaryDocument result, CancellationToken token)
    {
        Dispatcher.VerifyAccess();
        token.ThrowIfCancellationRequested();
        if (closing)
            throw new OperationCanceledException(token);
        result.Validate();
        if (!result.Completed)
            throw new InvalidDataException(UiText.Get("InvalidAboutMeSummary"));
        VerifyPersonalNotesSource(session, result);
        var displayed = selected?.Id == session.Id;
        var notes = displayed && notesDirty ? NotesBox.Text :
            File.Exists(session.NotesPath) ? AtomicFile.ReadText(session.NotesPath) : "";
        NotesSectionUpdate update;
        try { update = PersonalNotesSection.Update(notes, result.AppliedSection, result.NotesSection); }
        catch (NotesSectionConflictException)
        {
            if (!confirmAppendAboutMe(UiText.Get("ConfirmAppendAboutMe")))
                throw new OperationCanceledException(token);
            token.ThrowIfCancellationRequested();
            VerifyPersonalNotesSource(session, result);
            // Re-read after the modal dialog; notes may have been autosaved or changed while it was open.
            notes = displayed && notesDirty ? NotesBox.Text :
                File.Exists(session.NotesPath) ? AtomicFile.ReadText(session.NotesPath) : "";
            update = PersonalNotesSection.Update(notes, result.AppliedSection, result.NotesSection, appendIfChanged: true);
        }
        token.ThrowIfCancellationRequested();
        AtomicFile.WriteText(session.NotesPath, update.Text, token);
        if (displayed)
        {
            var start = update.MapPosition(NotesBox.SelectionStart);
            var end = update.MapPosition(NotesBox.SelectionStart + NotesBox.SelectionLength);
            var offset = NotesBox.VerticalOffset;
            loadingSession = true;
            try
            {
                NotesBox.Text = update.Text;
                start = Math.Clamp(start, 0, NotesBox.Text.Length);
                NotesBox.Select(start, Math.Clamp(end - start, 0, NotesBox.Text.Length - start));
                NotesBox.ScrollToVerticalOffset(offset);
                notesDirty = false;
                notesDebounce.Stop();
                NotesSaveState.Text = UiText.Get("NotesSaved");
            }
            finally { loadingSession = false; }
        }
        // The ready-to-apply section was checkpointed first, so a retry can recognize an already-written block.
        result.AppliedSection = update.Section;
        result.AppliedGenerationId = result.GenerationId;
        result.AppliedProvider = result.Provider;
        result.AppliedModel = result.Model;
        PersonalSummaryStore.Save(session, result);
        if (displayed)
            UpdateModelStatus();
    }

    private static void VerifyPersonalNotesSource(RecordingSession session, PersonalSummaryDocument result)
    {
        var transcript = AppStorage.LoadTranscript(session);
        if (transcript.Me?.TagId != result.TagId || transcript.IsDraft || transcript.ProcessedFrames != session.Frames ||
            PersonalSummaryPrompt.Fingerprint(transcript) != result.SourceFingerprint)
            throw new InvalidOperationException(UiText.Get("AboutMeSourceChanged"));
        var state = PersonalSummaryStore.Load(session);
        if (state?.GenerationId != result.GenerationId || state.NotesSection != result.NotesSection)
            throw new InvalidOperationException(UiText.Get("AboutMeSourceChanged"));
    }
}
