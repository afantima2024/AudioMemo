using System.IO;
using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.AI;

public sealed partial class AiPipeline
{
    internal AppSettings ResolveSummarySettings(AppSettings settings)
    {
        var resolvedModel = summarizer.ResolveModel(settings);
        if (settings.SummaryProvider != SummaryProviders.Copilot || resolvedModel == settings.SummaryModel)
            return settings;
        var result = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
            ?? throw new InvalidDataException("Could not copy summary settings.");
        result.SummaryModel = resolvedModel;
        return result;
    }

    internal async Task<PersonalSummaryDocument> SummarizeAboutMeAsync(RecordingSession session, AppSettings settings,
        IProgress<AiUpdate> progress, CancellationToken token, string generationId, string tagId)
    {
        settings.Validate();
        token.ThrowIfCancellationRequested();
        if (!settings.CanSummarize)
            throw new InvalidOperationException(UiText.Get("ConsentNotApproved"));
        if (!Guid.TryParseExact(generationId, "N", out _) || !Guid.TryParseExact(tagId, "N", out _))
            throw new InvalidDataException(UiText.Get("InvalidAboutMeSummary"));
        settings = ResolveSummarySettings(settings);
        var transcript = AppStorage.LoadTranscript(session);
        var me = PersonalSummaryPrompt.RequireMe(transcript);
        if (me.TagId != tagId)
            throw new InvalidOperationException(UiText.Get("MeTagChanged"));
        if (session.Frames <= 0 || transcript.IsDraft || transcript.ProcessedFrames != session.Frames)
            throw new InvalidOperationException(UiText.Get("SummaryRequiresFinalTranscript"));
        var fingerprint = PersonalSummaryPrompt.Fingerprint(transcript);
        var state = PersonalSummaryStore.Load(session);
        if (state is null || state.GenerationId != generationId || !state.Matches(transcript, settings))
        {
            var tracking = PersonalSummaryStore.NotesTracking(session, state);
            state = new()
            {
                GenerationId = generationId, TagId = tagId, SourceFingerprint = fingerprint,
                SourceSegments = transcript.Segments.Count, Provider = settings.SummaryProvider,
                Model = settings.ActiveSummaryModel, Endpoint = settings.ActiveSummaryEndpoint,
                AppliedSection = tracking.Section, AppliedProvider = tracking.Provider, AppliedModel = tracking.Model
            };
        }
        if (state.Completed)
            return state;
        var batches = SummaryPrompt.ResumableBatches(transcript.Segments, state.SummarizedSegments,
            state.SegmentOffset, settings.UsesLocalSummaries ? 4000 : SummaryPrompt.MaximumBatchCharacters).ToArray();
        var text = new UiText(settings.UiLanguage);
        var model = SummaryProviders.Label(settings.SummaryProvider, text) + " / " +
            (settings.ActiveSummaryModel.Length == 0 ? text["CliDefaultModel"] : settings.ActiveSummaryModel);
        for (var index = 0; index < batches.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var batch = batches[index];
            progress.Report(new(session.Id, text.FormatValue("AboutMeProgress", index + 1, batches.Length),
                SummaryModel: model, WorkProgress: new("about-me", (double)index / batches.Length)));
            var prompt = PersonalSummaryPrompt.Create(transcript, batch, state.SummarizedSegments, state.SegmentOffset, state.Text);
            var result = await summarizer.SummarizePromptAsync(settings, prompt,
                warning => progress.Report(new(session.Id, warning, IsWarning: true)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            result = ChineseText.ToSimplified(result).Trim();
            if (result.Length == 0 || result.Length > SummaryPrompt.MaximumPreviousSummaryCharacters)
                throw new InvalidDataException(text["InvalidAboutMeSummary"]);
            if (fingerprint != PersonalSummaryPrompt.Fingerprint(AppStorage.LoadTranscript(session)))
                throw new InvalidOperationException(text["AboutMeSourceChanged"]);
            state.Text = result;
            state.SummarizedSegments = batch.NextSegment;
            state.SegmentOffset = batch.NextOffset;
            state.Completed = index + 1 == batches.Length;
            state.GeneratedAt = DateTimeOffset.Now;
            if (state.Completed)
            {
                var name = transcript.SpeakerName(me.SpeakerId);
                state.NotesSection = "## " + text.FormatValue("AboutMeHeading", name) + Environment.NewLine +
                    text.FormatValue("SummaryGeneratedWith", SummaryProviders.Label(state.Provider, text),
                        state.Model.Length == 0 ? text["SummaryModelUnrecorded"] : state.Model) +
                    " | " + state.GeneratedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", text.Culture) +
                    Environment.NewLine + Environment.NewLine + TranscriptPresentation.RenderSpeakerReferences(result, transcript);
            }
            PersonalSummaryStore.Save(session, state, token);
        }
        if (!state.Completed)
            throw new InvalidDataException(text["InvalidAboutMeSummary"]);
        return state;
    }
}
