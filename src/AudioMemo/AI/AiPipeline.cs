using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.AI;

public sealed record AiWorkProgress(string Phase, double? Fraction = null);
public sealed record AiUpdate(string SessionId, string Status, string? Transcript = null, string? Summary = null,
    bool IsWarning = false, TranscriptDocument? Document = null, string? SpeechRuntime = null, string? SummaryModel = null,
    AiWorkProgress? WorkProgress = null);
public delegate Task TranscriptReviewHandler(RecordingSession session, AppSettings settings,
    IProgress<AiUpdate> progress, CancellationToken cancellationToken);
public sealed class SummaryDocument
{
    private string text = "";
    private string overallText = "";

    public int SummarizedSegments { get; set; }
    public int SegmentOffset { get; set; }
    public int TextPolicyVersion { get; set; }
    public string TranscriptRevision { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public int SourceSegments { get; set; }
    public long SourceFrames { get; set; }
    public string? GenerationId { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    [JsonRequired]
    public string Text
    {
        get => text;
        set => text = ChineseText.ToSimplified(value);
    }
    public string Model { get; set; } = "";
    public string Provider { get; set; } = SummaryProviders.Copilot;
    public string Endpoint { get; set; } = "";
    public bool Provisional { get; set; }
    public int OverallTextPolicyVersion { get; set; }
    public string OverallText
    {
        get => overallText;
        set => overallText = ChineseText.ToSimplified(value);
    }
    [JsonIgnore] public bool HasOverallSummary => !Provisional &&
        OverallTextPolicyVersion == SummaryPrompt.OverallTextPolicyVersion && !string.IsNullOrWhiteSpace(OverallText);

    public string ToText(UiText language) => HasOverallSummary
        ? SummaryPresentation.AppendOverall(Text, OverallText, language["OverallSummary"])
        : Text;

    public bool MatchesSourcePrefix(TranscriptDocument transcript)
    {
        if (SourceSegments < 0 || SourceFrames < 0 || SourceFingerprint is null)
            throw new InvalidDataException("Invalid saved summary source.");
        return TranscriptRevision == transcript.Revision &&
            (SourceFingerprint.Length == 0 ||
                SourceSegments <= transcript.Segments.Count &&
                SourceFingerprint == SummaryInput.Fingerprint(transcript, SourceSegments));
    }

    public void RecordSource(TranscriptDocument transcript)
    {
        TranscriptRevision = transcript.Revision;
        SourceSegments = transcript.Segments.Count;
        SourceFrames = transcript.ProcessedFrames;
        SourceFingerprint = SummaryInput.Fingerprint(transcript);
        GeneratedAt = DateTimeOffset.Now;
    }
}

public sealed partial class AiPipeline
{
    internal const long ChunkFrames = 30L * PcmFormat.SampleRate;
    internal const long LiveChunkFrames = 2L * PcmFormat.SampleRate;
    internal const long OverlapFrames = 2L * PcmFormat.SampleRate;
    private readonly ILocalTranscriber transcriber;
    private readonly ISummaryService summarizer;
    private readonly ISpeakerService? speakerService;
    private readonly Func<SpeechModel, CancellationToken, Task> verifyModel;

    public AiPipeline(ILocalTranscriber? localTranscriber = null, ISummaryService? summaryService = null,
        ISpeakerService? speakerService = null)
        : this(localTranscriber, summaryService, speakerService, SpeechModels.VerifyAsync) { }

    internal AiPipeline(ILocalTranscriber? localTranscriber, ISummaryService? summaryService,
        ISpeakerService? speakerService, Func<SpeechModel, CancellationToken, Task> verifyModel)
    {
        transcriber = localTranscriber ?? new LocalTranscriber();
        summarizer = summaryService ?? new SummaryService();
        this.speakerService = speakerService;
        this.verifyModel = verifyModel;
    }

    public async Task TranscribeAsync(RecordingSession session, AppSettings settings,
        Func<(long Frames, bool IsRecording)> recording, IProgress<AiUpdate> progress, CancellationToken cancellationToken,
        TranscriptReviewHandler? reviewTranscript = null)
    {
        var text = new UiText(settings.UiLanguage);
        settings.Validate();
        var transcript = AppStorage.LoadTranscript(session);
        var preservedFrames = TranscriptContinuation.PreservedFrames(transcript, session.PreservedTranscriptFrames);
        var initial = recording();
        var live = initial.IsRecording;
        if (!live && transcript.Segments.Any(segment => segment.IsUserEdited &&
                segment.EndSeconds > (double)preservedFrames / PcmFormat.SampleRate) &&
            (transcript.IsDraft || (transcript.Model.Length > 0 && transcript.Model != settings.SpeechModel) ||
                (transcript.Language.Length > 0 && transcript.Language != settings.Language)))
            throw new InvalidOperationException(text["ProtectTranscriptCorrections"]);
        var liveModel = live && settings.FastLiveDrafts ? "base" : settings.SpeechModel;
        if (live)
        {
            transcript.IsDraft = true;
            transcript.Model = liveModel;
        }
        else if ((transcript.Model.Length > 0 && transcript.Model != settings.SpeechModel) ||
            (transcript.Language.Length > 0 && transcript.Language != settings.Language))
            transcript.IsDraft = true;
        if (transcript.ProcessedFrames == 0 && transcript.Revision.Length == 0)
            transcript.Revision = Guid.NewGuid().ToString("N");
        if (live || !transcript.IsDraft && transcript.ProcessedFrames != initial.Frames)
            await TranscribePassAsync(session, settings, recording, progress, transcript, SpeechModels.Get(liveModel),
                null, preservedFrames, cancellationToken, live && !settings.TranscribeOnStop).ConfigureAwait(false);

        if (live && !settings.TranscribeOnStop)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppStorage.SaveAiTranscript(session, transcript);
            ReportTranscript(session, transcript, text["LiveTextKeptForLater"], progress);
            return;
        }

        if (transcript.IsDraft)
        {
            var checkpoint = Path.Combine(session.Folder, "transcript.refining.json");
            var refined = File.Exists(checkpoint)
                ? JsonSerializer.Deserialize<TranscriptDocument>(AtomicFile.ReadText(checkpoint))
                    ?? throw new InvalidDataException("Empty refinement checkpoint.")
                : NewRefinement();
            if (refined.Model != settings.SpeechModel || refined.Language != settings.Language || refined.ProcessedFrames > session.Frames ||
                !TranscriptContinuation.MatchesPrefix(refined, transcript, preservedFrames))
                refined = NewRefinement();
            progress.Report(new(session.Id, text["RefiningTranscript"]));
            await TranscribePassAsync(session, settings, () => (session.Frames, false), progress, refined,
                SpeechModels.Get(settings.SpeechModel), checkpoint, preservedFrames, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            AppStorage.SaveAiTranscript(session, refined);
            if (transcript.Me is not null && refined.Me is null)
                progress.Report(new(session.Id, text["MeTagCleared"], IsWarning: true));
            if (File.Exists(checkpoint))
                File.Delete(checkpoint);
            transcript = refined;
        }
        await CompleteTranscriptionAsync(session, settings, transcript, progress, cancellationToken, reviewTranscript).ConfigureAwait(false);

        TranscriptDocument NewRefinement() =>
            TranscriptContinuation.CreateRefinement(transcript, preservedFrames, settings.SpeechModel, settings.Language);
    }

    internal async Task CompleteTranscriptionAsync(RecordingSession session, AppSettings settings,
        TranscriptDocument transcript, IProgress<AiUpdate> progress, CancellationToken cancellationToken,
        TranscriptReviewHandler? reviewTranscript = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = new UiText(settings.UiLanguage);
        if (settings.IdentifySpeakersOnStop && transcript.Segments.Count > 0 && !transcript.SpeakersIdentified)
        {
            try
            {
                var hadMe = transcript.Me is not null;
                progress.Report(new(session.Id, text["IdentifyingSpeakers"], WorkProgress: new("speakers")));
                var analyzed = session.PreservedTranscriptFrames > 0 ? transcript.Copy() : transcript;
                await (speakerService ?? new SpeakerService(transcriber.WorkerExecutable))
                    .IdentifyAsync(session, analyzed, settings.SpeakerCount, cancellationToken,
                        update => ReportSpeakerProgress(session, text, progress, update)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(analyzed, transcript))
                    TranscriptContinuation.ApplySpeakers(transcript, analyzed,
                        TranscriptContinuation.PreservedFrames(transcript, session.PreservedTranscriptFrames));
                else
                    transcript.Me = null;
                AppStorage.SaveAiTranscript(session, transcript, newSpeakerSet: session.PreservedTranscriptFrames == 0);
                if (hadMe && transcript.Me is null)
                    progress.Report(new(session.Id, text["MeTagCleared"], IsWarning: true));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress.Report(new(session.Id, text.FormatValue("SpeakerAnalysisFailed", ex.Message), IsWarning: true));
            }
        }
        ReportTranscript(session, transcript, text["TranscriptionComplete"], progress, new("saving"));
        if (settings.ReviewUnclearTranscript && transcript.Segments.Count > 0 && reviewTranscript is not null)
            await reviewTranscript(session, settings, progress, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.SummarizeOnStop && settings.CanSummarize)
            await SummarizeAsync(session, settings, progress, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task IdentifySpeakersAsync(RecordingSession session, AppSettings settings,
        IProgress<AiUpdate> progress, CancellationToken cancellationToken)
    {
        settings.Validate();
        var text = new UiText(settings.UiLanguage);
        var transcript = AppStorage.LoadTranscript(session);
        if (transcript.IsDraft || transcript.Segments.Count == 0 || transcript.ProcessedFrames != session.Frames)
            throw new InvalidOperationException(text["FinalTranscriptRequired"]);
        var hadMe = transcript.Me is not null;
        progress.Report(new(session.Id, text["IdentifyingSpeakers"], WorkProgress: new("speakers")));
        await (speakerService ?? new SpeakerService(transcriber.WorkerExecutable))
            .IdentifyAsync(session, transcript, settings.SpeakerCount, cancellationToken,
                update => ReportSpeakerProgress(session, text, progress, update)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        transcript.Me = null;
        AppStorage.SaveAiTranscript(session, transcript, newSpeakerSet: true);
        progress.Report(new(session.Id, text["SpeakersIdentified"], Summary: AppStorage.LoadSummaryText(session),
            Document: transcript.Copy(), WorkProgress: new("saving")));
        if (hadMe && transcript.Me is null)
            progress.Report(new(session.Id, text["MeTagCleared"], IsWarning: true));
    }

    public async Task RecheckUnlabeledSpeakersAsync(RecordingSession session, AppSettings settings,
        IProgress<AiUpdate> progress, CancellationToken cancellationToken)
    {
        settings.Validate();
        var text = new UiText(settings.UiLanguage);
        var transcript = AppStorage.LoadTranscript(session);
        var version = AudioSourceVersion.Read(session.WavePath);
        progress.Report(new(session.Id, text["RecheckingUnlabeledSpeakers"], WorkProgress: new("speakers")));
        var plan = await Task.Run(() => UnlabeledSpeakerMatching.Create(transcript, session.Frames, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var matches = await (speakerService ?? new SpeakerService(transcriber.WorkerExecutable))
            .MatchUnlabeledAsync(session, plan, cancellationToken,
                update => progress.Report(new(session.Id,
                    update.Phase == "speaker-features" ? text.FormatValue("MatchingUnlabeledProgress", update.Fraction) :
                        text["RecheckingUnlabeledSpeakers"], WorkProgress: update))).ConfigureAwait(false);
        var result = UnlabeledSpeakerMatching.Apply(transcript, plan, matches, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (version != AudioSourceVersion.Read(session.WavePath) ||
            AudioEditPlan.ContentFingerprint(AppStorage.LoadTranscript(session)) != plan.SourceFingerprint)
            throw new InvalidOperationException(text["EditSourceChanged"]);
        if (result.Revision != transcript.Revision)
        {
            var selection = TranscriptCorrectionPlan.Create(transcript, session.Frames,
                TranscriptPresentation.Rows(transcript).Where(row => row.SpeakerId is null).ToArray());
            using var audio = TranscriptCorrectionService.OpenAudio(session, selection);
            if (version != AudioSourceVersion.Read(session.WavePath))
                throw new InvalidOperationException(text["EditSourceChanged"]);
            TranscriptCorrectionService.Commit(session, selection, result, cancellationToken);
        }
        var assigned = matches.Count(match => match.SpeakerId is not null);
        progress.Report(new(session.Id, text.FormatValue("UnlabeledSpeakersChecked", assigned, matches.Length - assigned),
            Summary: AppStorage.LoadSummaryText(session), Document: result.Copy(), WorkProgress: new("saving")));
    }

    private static void ReportSpeakerProgress(RecordingSession session, UiText text, IProgress<AiUpdate> progress,
        AiWorkProgress update)
    {
        var status = update.Phase switch
        {
            "speaker-features" => text.FormatValue("SpeakerFeaturesProgress", update.Fraction),
            "speaker-finalizing" => text["SpeakerFinalizing"],
            _ => text["IdentifyingSpeakers"]
        };
        progress.Report(new(session.Id, status, WorkProgress: update));
    }

    private async Task TranscribePassAsync(RecordingSession session, AppSettings settings,
        Func<(long Frames, bool IsRecording)> recording, IProgress<AiUpdate> progress, TranscriptDocument transcript,
        SpeechModel model, string? checkpoint, long preservedFrames, CancellationToken cancellationToken, bool liveOnly = false)
    {
        var text = new UiText(settings.UiLanguage);
        Directory.CreateDirectory(AppStorage.WorkFolder);
        progress.Report(new(session.Id, text["CheckingModel"], WorkProgress: new("speech-model")));
        await verifyModel(model, cancellationToken).ConfigureAwait(false);
        transcript.Model = model.Id;
        transcript.Language = settings.Language;
        var lastSummaryFrame = transcript.ProcessedFrames;
        session.TranscriptionModel = model.Id;
        session.TranscriptionLanguage = settings.Language;
        var acceleration = transcript.IsDraft && settings.FastLiveDrafts ? "cpu" : settings.SpeechAcceleration;
        ISpeechWorker? worker = null;
        var vocabulary = settings.SpeechVocabularyEnabled ? settings.SpeechVocabularyTerms : [];
        var hints = SpeechVocabulary.CreatePrompt(vocabulary);
        Task? liveSummary = null;
        using var liveSummaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var phase = checkpoint is null ? "transcription" : "refinement";
        var progressKey = checkpoint is null ? "TranscribingProgress" : "RefiningTranscriptProgress";
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (available, isRecording) = recording();
                if (liveOnly && !isRecording)
                    break;
                var pending = available - transcript.ProcessedFrames;
                if (pending < 0)
                    throw new InvalidDataException("Transcript progress exceeds the saved audio length.");
                if (pending == 0 && !isRecording)
                    break;
                var chunkFrames = isRecording ? LiveChunkFrames : ChunkFrames;
                if (isRecording && worker is null)
                {
                    worker = await transcriber.OpenAsync(model, settings.Language, acceleration, true, cancellationToken, vocabulary)
                        .ConfigureAwait(false);
                    ReportVocabularyLimit();
                    progress.Report(new(session.Id, text.FormatValue("SpeechRuntimeStatus", model.Id, worker.Runtime),
                        SpeechRuntime: model.Id + " / " + worker.Runtime,
                        IsWarning: acceleration == "auto" && worker.Runtime.StartsWith("Cpu", StringComparison.OrdinalIgnoreCase)));
                }
                if (pending < chunkFrames && isRecording)
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var end = Math.Min(available, transcript.ProcessedFrames + (pending > ChunkFrames ? ChunkFrames : chunkFrames));
                var start = Math.Max(preservedFrames, transcript.ProcessedFrames - OverlapFrames);
                var temporary = Path.Combine(AppStorage.WorkFolder, $"speech-{Guid.NewGuid():N}.wav");
                try
                {
                    progress.Report(new(session.Id,
                        text.FormatValue(progressKey,
                            TranscriptDocument.Timestamp((double)transcript.ProcessedFrames / PcmFormat.SampleRate),
                            TranscriptDocument.Timestamp((double)available / PcmFormat.SampleRate)),
                        WorkProgress: new(phase, isRecording ? null : (double)transcript.ProcessedFrames / available)));
                    var rms = await Task.Run(() => AudioExport.WriteSpeechChunk(session.WavePath, temporary,
                        start, end - start, cancellationToken), cancellationToken).ConfigureAwait(false);
                    if (rms >= 0.0001)
                    {
                        if (worker is null)
                        {
                            progress.Report(new(session.Id, text["CheckingModel"], WorkProgress: new("speech-model")));
                            worker = await transcriber.OpenAsync(model, settings.Language, acceleration, transcript.IsDraft, cancellationToken, vocabulary)
                                .ConfigureAwait(false);
                            ReportVocabularyLimit();
                            progress.Report(new(session.Id, text.FormatValue("SpeechRuntimeStatus", model.Id, worker.Runtime),
                                SpeechRuntime: model.Id + " / " + worker.Runtime,
                                WorkProgress: new(phase, isRecording ? null : (double)transcript.ProcessedFrames / available)));
                            if (worker.StartupWarning is not null)
                                progress.Report(new(session.Id, text.FormatValue("GpuFallback", worker.StartupWarning), IsWarning: true));
                        }
                        var segments = await worker.TranscribeAsync(temporary, cancellationToken).ConfigureAwait(false);
                        var previousCount = transcript.Segments.Count;
                        TranscriptMerger.Append(transcript, segments, (double)start / PcmFormat.SampleRate,
                            (double)transcript.ProcessedFrames / PcmFormat.SampleRate, (double)end / PcmFormat.SampleRate);
                        if (transcript.Segments.Count != previousCount)
                            transcript.SpeakersIdentified = false;
                    }
                    transcript.ProcessedFrames = end;
                    var workProgress = new AiWorkProgress(phase, isRecording ? null : (double)end / available);
                    if (checkpoint is null)
                    {
                        AppStorage.SaveAiTranscript(session, transcript);
                        ReportTranscript(session, transcript, text[transcript.IsDraft ? "DraftTranscriptSaved" : "TranscriptSaved"],
                            progress, workProgress);
                    }
                    else
                    {
                        AtomicFile.WriteJson(checkpoint, transcript);
                        progress.Report(new(session.Id, text.FormatValue(progressKey,
                            TranscriptDocument.Timestamp((double)end / PcmFormat.SampleRate),
                            TranscriptDocument.Timestamp((double)available / PcmFormat.SampleRate)), WorkProgress: workProgress));
                    }
                }
                catch
                {
                    if (worker is not null)
                    {
                        var failedWorker = worker;
                        worker = null;
                        await failedWorker.DisposeAsync().ConfigureAwait(false);
                    }
                    throw;
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                if (isRecording && recording().IsRecording && settings.LiveSummary && settings.CanSummarize &&
                    transcript.ProcessedFrames - lastSummaryFrame >= 3 * ChunkFrames && (liveSummary is null || liveSummary.IsCompleted))
                {
                    if (liveSummary is not null)
                        await liveSummary.ConfigureAwait(false);
                    liveSummary = UpdateLiveSummaryAsync();
                    lastSummaryFrame = transcript.ProcessedFrames;
                }
            }
        }
        finally
        {
            liveSummaryCancellation.Cancel();
            if (worker is not null)
                await worker.DisposeAsync().ConfigureAwait(false);
            if (liveSummary is not null)
                await liveSummary.ConfigureAwait(false);
        }

        void ReportVocabularyLimit()
        {
            if (hints.UsedTerms < hints.TotalTerms)
                progress.Report(new(session.Id, text.FormatValue("SpeechHintsLimited", hints.UsedTerms, hints.TotalTerms), IsWarning: true));
        }

        async Task UpdateLiveSummaryAsync()
        {
            try { await SummarizeAsync(session, settings, progress, true, liveSummaryCancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (liveSummaryCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress.Report(new(session.Id, text.FormatValue("LiveSummaryFailed", ex.Message), IsWarning: true));
            }
        }
    }

    private static void ReportTranscript(RecordingSession session, TranscriptDocument transcript, string status,
        IProgress<AiUpdate> progress, AiWorkProgress? workProgress = null)
    {
        var snapshot = transcript.Copy();
        progress.Report(new(session.Id, status, snapshot.ToText(), Document: snapshot, WorkProgress: workProgress));
    }

    public async Task SummarizeAsync(RecordingSession session, AppSettings settings, IProgress<AiUpdate> progress,
        bool live, CancellationToken cancellationToken, string? generationId = null,
        TranscriptReviewHandler? reviewTranscript = null)
    {
        var text = new UiText(settings.UiLanguage);
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.CanSummarize)
            throw new InvalidOperationException(text["ConsentNotApproved"]);
        if (generationId is not null && !Guid.TryParseExact(generationId, "N", out _))
            throw new ArgumentException("Invalid summary generation identifier.", nameof(generationId));
        settings = ResolveSummarySettings(settings);
        var transcript = AppStorage.LoadTranscript(session);
        if (!live && (transcript.IsDraft || transcript.ProcessedFrames != session.Frames))
            throw new InvalidOperationException(text["SummaryRequiresFinalTranscript"]);
        if (transcript.Segments.Count == 0)
            throw new InvalidOperationException(text["NoTranscript"]);
        if (!live && settings.ReviewUnclearTranscript && reviewTranscript is not null)
        {
            await reviewTranscript(session, settings, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transcript = AppStorage.LoadTranscript(session);
            if (transcript.IsDraft || transcript.ProcessedFrames != session.Frames || transcript.Segments.Count == 0)
                throw new InvalidOperationException(text["SummaryRequiresFinalTranscript"]);
        }
        var statePath = Path.Combine(session.Folder, "summary.json");
        var summary = AppStorage.LoadSummaryDocument(session) ?? new SummaryDocument();
        var endpoint = settings.ActiveSummaryEndpoint;
        if (summary.SummarizedSegments > transcript.Segments.Count || summary.Model != settings.ActiveSummaryModel ||
            summary.Provider != settings.SummaryProvider || summary.Endpoint != endpoint ||
            summary.TextPolicyVersion != SummaryPrompt.TextPolicyVersion || !summary.MatchesSourcePrefix(transcript) ||
            generationId is not null && summary.GenerationId != generationId)
            // Old offsets may refer to text whose UTF-16 length changes when simplified.
            summary = new SummaryDocument { GenerationId = generationId };
        if (summary.Text is null || summary.Text.Length > SummaryPrompt.MaximumPreviousSummaryCharacters ||
            summary.OverallText is null || summary.OverallText.Length > SummaryPrompt.MaximumPreviousSummaryCharacters ||
            summary.SummarizedSegments < 0 || summary.SegmentOffset < 0)
            throw new InvalidDataException("Invalid saved summary state.");
        var batches = SummaryPrompt.ResumableBatches(transcript.Segments, summary.SummarizedSegments,
            summary.SegmentOffset, settings.UsesLocalSummaries ? 4000 : SummaryPrompt.MaximumBatchCharacters).ToArray();
        Action<string> warning = message => progress.Report(new(session.Id, message, IsWarning: true));
        var modelLabel = SummaryProviders.Label(settings.SummaryProvider, text) + " / " +
            (settings.ActiveSummaryModel.Length == 0 ? text["CliDefaultModel"] : settings.ActiveSummaryModel);
        for (var index = 0; index < batches.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = batches[index];
            var statusKey = settings.UsesLocalSummaries ? live ? "LocalLiveSummaryProgress" : "LocalSummaryProgress" :
                settings.UsesCloudApi ? live ? "CloudLiveSummaryProgress" : "CloudSummaryProgress" :
                live ? "LiveSummaryProgress" : "SummaryProgress";
            progress.Report(new(session.Id, text.FormatValue(statusKey, index + 1, batches.Length),
                SummaryModel: modelLabel, WorkProgress: live ? null : new("main-points", (double)index / batches.Length)));
            var result = await summarizer.SummarizeBatchAsync(settings, batch.Text, summary.Text, live, warning, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            summary.Text = result;
            summary.OverallText = "";
            summary.OverallTextPolicyVersion = 0;
            summary.Model = settings.ActiveSummaryModel;
            summary.Provider = settings.SummaryProvider;
            summary.Endpoint = endpoint;
            summary.TextPolicyVersion = SummaryPrompt.TextPolicyVersion;
            summary.RecordSource(transcript);
            summary.Provisional = live || index + 1 < batches.Length;
            summary.SummarizedSegments = batch.NextSegment;
            summary.SegmentOffset = batch.NextOffset;
            SaveSummary(session, statePath, summary, text, cancellationToken);
            progress.Report(new(session.Id, text.FormatValue("SummaryCheckpointSaved", index + 1, batches.Length),
                Summary: TranscriptPresentation.RenderSpeakerReferences(summary.Text, transcript),
                WorkProgress: live ? null : new("main-points", (double)(index + 1) / batches.Length)));
        }

        if (batches.Length == 0)
        {
            if (!live && summary.Provisional)
            {
                progress.Report(new(session.Id, text["FinalizingMainPoints"], SummaryModel: modelLabel,
                    WorkProgress: new("main-points")));
                summary.Text = await summarizer.SummarizeBatchAsync(settings, "", summary.Text, false, warning, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                summary.Provisional = false;
                summary.OverallText = "";
                summary.OverallTextPolicyVersion = 0;
                summary.RecordSource(transcript);
                SaveSummary(session, statePath, summary, text, cancellationToken);
            }
            else
                SaveSummaryExport(session, summary, text, cancellationToken);
        }
        if (!live && !summary.HasOverallSummary)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new(session.Id, text["OverallSummaryProgress"],
                Summary: TranscriptPresentation.RenderSpeakerReferences(summary.Text, transcript), SummaryModel: modelLabel,
                WorkProgress: new("overall-summary")));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await summarizer.SummarizeOverallAsync(settings, summary.Text, warning, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(result) || result.Length > SummaryPrompt.MaximumPreviousSummaryCharacters)
                    throw new InvalidDataException(text["InvalidOverallSummary"]);
                result = SummaryPresentation.WithoutRecordingTimestamps(ChineseText.ToSimplified(result));
                if (string.IsNullOrWhiteSpace(result) || result.Length > SummaryPrompt.MaximumPreviousSummaryCharacters)
                    throw new InvalidDataException(text["InvalidOverallSummary"]);
                summary.OverallText = result;
                summary.OverallTextPolicyVersion = SummaryPrompt.OverallTextPolicyVersion;
                summary.RecordSource(transcript);
                SaveSummary(session, statePath, summary, text, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(text.FormatValue("OverallSummaryFailed", ex.Message), ex);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new(session.Id, text[live ? "LiveSummarySaved" : "SummarySaved"],
            Summary: TranscriptPresentation.RenderSpeakerReferences(summary.ToText(text), transcript),
            WorkProgress: live ? null : new("saving")));
    }

    internal Task<TranscriptReviewAnalysis> ReviewTranscriptAsync(RecordingSession session, AppSettings settings,
        IProgress<AiUpdate> progress, CancellationToken token, bool automatic, bool force = false) =>
        Task.Run(() => new TranscriptReviewService(summarizer).AnalyzeAsync(session, ResolveSummarySettings(settings),
            progress, token, automatic, force), token);

    private static void SaveSummary(RecordingSession session, string statePath, SummaryDocument summary, UiText language,
        CancellationToken cancellationToken)
    {
        AtomicFile.WriteJson(statePath, summary, cancellationToken);
        SaveSummaryExport(session, summary, language, cancellationToken);
    }

    private static void SaveSummaryExport(RecordingSession session, SummaryDocument summary, UiText language,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rendered = TranscriptPresentation.RenderSpeakerReferences(summary.ToText(language), AppStorage.LoadTranscript(session));
        if (!File.Exists(session.SummaryPath) || AtomicFile.ReadText(session.SummaryPath) != rendered)
            AtomicFile.WriteText(session.SummaryPath, rendered, cancellationToken);
    }
}
