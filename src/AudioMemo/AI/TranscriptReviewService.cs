using System.IO;
using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.AI;

internal sealed record TranscriptReviewAnalysis(TranscriptDocument Source, TranscriptReviewReport Report,
    bool AlreadyAccepted = false)
{
    public bool CanResume => !Report.AnalysisComplete && Report.CompletedBatches > 0;
}

internal sealed class TranscriptReviewService(ISummaryService summaries)
{
    internal const string FileName = "transcript-review.json";

    internal static TranscriptReviewReport? Load(RecordingSession session)
    {
        var path = Path.Combine(session.Folder, FileName);
        if (!File.Exists(path))
            return null;
        var report = JsonSerializer.Deserialize<TranscriptReviewReport>(AtomicFile.ReadText(path))
            ?? throw new InvalidDataException(UiText.Get("TranscriptReviewInvalidState"));
        TranscriptReview.Validate(report);
        return report;
    }

    internal async Task<TranscriptReviewAnalysis> AnalyzeAsync(RecordingSession session, AppSettings settings,
        IProgress<AiUpdate> progress, CancellationToken token, bool automatic, bool force = false)
    {
        token.ThrowIfCancellationRequested();
        settings.Validate();
        var language = new UiText(settings.UiLanguage);
        var source = AppStorage.LoadTranscript(session);
        if (source.IsDraft || source.ProcessedFrames != session.Frames || session.Frames <= 0 ||
            !source.Segments.Any(segment => !string.IsNullOrWhiteSpace(segment.Text)))
            throw new InvalidOperationException(language["TranscriptReviewNeedsFinal"]);
        var plan = TranscriptCorrectionPlan.CreateForSegments(source, session.Frames, [0]);
        TranscriptCorrectionService.VerifySource(session, plan);
        var fingerprint = AudioEditPlan.ContentFingerprint(source);
        TranscriptReviewReport? saved = null;
        string? savedError = null;
        if (!force)
        {
            try { saved = Load(session); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                savedError = language.FormatValue("TranscriptReviewSavedStateError", ex.Message);
            }
        }
        if (automatic && !force && saved?.AcceptedFingerprint == fingerprint)
        {
            TranscriptCorrectionService.VerifySource(session, plan);
            return new(source, saved, AlreadyAccepted: true);
        }
        var report = !force && saved is not null && saved.SourceFingerprint == fingerprint &&
            saved.Provider == settings.SummaryProvider && saved.Model == settings.ActiveSummaryModel &&
            saved.Endpoint == settings.ActiveSummaryEndpoint ? saved : NewReport();
        var batches = TranscriptReview.Batches(source).ToArray();
        try
        {
            TranscriptReview.Validate(report, source);
            if (report.CompletedBatches > batches.Length || report.AnalysisComplete && report.CompletedBatches != batches.Length)
                throw new InvalidDataException(language["TranscriptReviewInvalidState"]);
        }
        catch (InvalidDataException ex)
        {
            report = NewReport();
            savedError = language.FormatValue("TranscriptReviewSavedStateError", ex.Message);
        }
        if (savedError is not null)
        {
            report.Error = savedError;
            progress.Report(new(session.Id, savedError, IsWarning: true));
            return new(source, report);
        }
        if (report.AnalysisComplete)
        {
            TranscriptCorrectionService.VerifySource(session, plan);
            return new(source, report);
        }
        report.Error = null;
        report.ModelUnavailable = !settings.CanSummarize ||
            settings.UsesLocalSummaries && string.IsNullOrWhiteSpace(settings.OllamaModel);
        if (report.ModelUnavailable)
        {
            report.Error = language["TranscriptReviewNeedsLlm"];
            progress.Report(new(session.Id, report.Error, IsWarning: true));
            Save(session, report, token);
            return new(source, report);
        }
        for (var index = report.CompletedBatches; index < batches.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            TranscriptCorrectionService.VerifySource(session, plan);
            progress.Report(new(session.Id, language.FormatValue("TranscriptReviewProgress", index + 1, batches.Length),
                WorkProgress: new("transcript-review", (double)index / batches.Length)));
            IReadOnlyList<TranscriptUncertainty> found;
            try
            {
                var prompt = TranscriptReview.Prompt(batches[index], source, settings.UiLanguage);
                for (var attempt = 0; ; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (attempt > 0)
                        TranscriptCorrectionService.VerifySource(session, plan);
                    var response = await summaries.GenerateStructuredPromptAsync(settings, prompt,
                        message => progress.Report(new(session.Id, message, IsWarning: true)), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        found = TranscriptReview.Parse(response, batches[index]);
                        break;
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidDataException)
                    {
                        if (attempt > 0)
                            throw new InvalidDataException(language["TranscriptReviewFormatFailed"], ex);
                        progress.Report(new(session.Id,
                            language.FormatValue("TranscriptReviewRetrying", index + 1, batches.Length),
                            WorkProgress: new("transcript-review", (double)index / batches.Length)));
                        prompt = """
                            Your previous response could not be validated. Retry this same part once.
                            Return one complete JSON object matching the required schema, with no extra text.
                            Use exact quotes and integer segment IDs from the supplied data, not corrected wording.
                            Escape all line breaks, tabs, quotation marks, and backslashes inside JSON strings.
                            """ + "\n" + prompt;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                token.ThrowIfCancellationRequested();
                report.Error = language.FormatValue("TranscriptReviewIncomplete", ex.Message);
                TranscriptCorrectionService.VerifySource(session, plan);
                Save(session, report, token);
                progress.Report(new(session.Id, report.Error, IsWarning: true));
                return new(source, report);
            }
            TranscriptCorrectionService.VerifySource(session, plan);
            report.Items = report.Items.Concat(found).GroupBy(item => (item.SegmentIndex, item.Quote))
                .Select(group => group.FirstOrDefault(item => item.Source == "llm") ?? group.First())
                .OrderBy(item => item.SegmentIndex).ToList();
            report.CompletedBatches = index + 1;
            Save(session, report, token);
        }
        token.ThrowIfCancellationRequested();
        TranscriptCorrectionService.VerifySource(session, plan);
        report.AnalysisComplete = true;
        report.CheckedAt = DateTimeOffset.Now;
        Save(session, report, token);
        return new(source, report);

        TranscriptReviewReport NewReport() => new()
        {
            SourceFingerprint = fingerprint,
            Provider = settings.SummaryProvider,
            Model = settings.ActiveSummaryModel,
            Endpoint = settings.ActiveSummaryEndpoint,
            Items = TranscriptReview.Markers(source, language).ToList()
        };
    }

    internal static TranscriptDocument Accept(RecordingSession session, TranscriptReviewAnalysis analysis,
        IReadOnlyDictionary<int, string> corrections, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TranscriptReview.Validate(analysis.Report, analysis.Source);
        var allowed = analysis.Report.Items.Select(item => item.SegmentIndex).ToHashSet();
        if (corrections.Keys.Any(index => !allowed.Contains(index)) ||
            analysis.Report.SourceFingerprint != AudioEditPlan.ContentFingerprint(analysis.Source))
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        var indices = corrections.Keys.Order().ToArray();
        var plan = TranscriptCorrectionPlan.CreateForSegments(analysis.Source, session.Frames,
            indices.Length == 0 ? [0] : indices);
        lock (AppStorage.TranscriptSync)
        {
            TranscriptCorrectionService.VerifySource(session, plan);
            var updated = indices.Length == 0 ? AppStorage.LoadTranscript(session) :
                TranscriptCorrectionService.Save(session, plan, indices.Select(index => corrections[index]).ToArray(), token);
            if (indices.Length == 0 && !plan.Matches(updated))
                throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
            analysis.Report.AcceptedFingerprint = AudioEditPlan.ContentFingerprint(updated);
            try { Save(session, analysis.Report, token); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(UiText.Format("TranscriptReviewDecisionSaveFailed", ex.Message), ex);
            }
            return updated;
        }
    }

    private static void Save(RecordingSession session, TranscriptReviewReport report, CancellationToken token) =>
        AtomicFile.WriteJson(Path.Combine(session.Folder, FileName), report, token);
}
