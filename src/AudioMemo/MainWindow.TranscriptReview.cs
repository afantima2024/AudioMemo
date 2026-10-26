using System.Windows;
using AudioMemo.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class MainWindow
{
    private readonly Action<TranscriptReviewWindow> showTranscriptReview;

    private void ReviewUnclearTranscript()
    {
        try
        {
            var session = RequireIdleSession();
            SaveNotes();
            StopReplay();
            StartAiJob(session, "ReviewUnclearTranscript", (snapshot, progress, token) =>
                ReviewTranscriptAsync(session, snapshot, progress, token, automatic: false));
        }
        catch (Exception ex) { ShowOperationError(ex, "ReviewUnclearTranscript", selected?.Id); }
    }

    private Task ReviewTranscriptBeforeSummaryAsync(RecordingSession session, AppSettings snapshot,
        IProgress<AiUpdate> progress, CancellationToken token) =>
        ReviewTranscriptAsync(session, snapshot, progress, token, automatic: true);

    private async Task ReviewTranscriptAsync(RecordingSession session, AppSettings snapshot,
        IProgress<AiUpdate> progress, CancellationToken token, bool automatic)
    {
        var language = new UiText(snapshot.UiLanguage);
        var force = false;
        while (true)
        {
            var analysis = await ai.ReviewTranscriptAsync(session, snapshot, progress, token, automatic, force)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (analysis.AlreadyAccepted)
                return;
            if (automatic && analysis.Report.Items.Count == 0 && analysis.Report.ModelUnavailable)
                return;
            TranscriptReviewChoice choice;
            IReadOnlyDictionary<int, string> corrections;
            if (automatic && analysis.Report.AnalysisComplete && analysis.Report.Items.Count == 0)
            {
                choice = TranscriptReviewChoice.Keep;
                corrections = new Dictionary<int, string>();
            }
            else
            {
                progress.Report(new(session.Id, language["TranscriptReviewWaiting"],
                    WorkProgress: new("transcript-review-confirm")));
                var decision = await Dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    if (closing)
                        throw new OperationCanceledException(token);
                    SaveNotes();
                    StopReplay();
                    var dialog = new TranscriptReviewWindow(session, analysis, snapshot);
                    using var registration = token.Register(() =>
                        Dispatcher.BeginInvoke(() => { if (!dialog.IsClosed) dialog.Close(); }));
                    try
                    {
                        showTranscriptReview(dialog);
                        return (dialog.Choice, dialog.Corrections);
                    }
                    finally { if (!dialog.IsClosed) dialog.Close(); }
                });
                choice = decision.Choice;
                corrections = decision.Corrections;
            }
            token.ThrowIfCancellationRequested();
            if (choice == TranscriptReviewChoice.Cancel)
                throw new OperationCanceledException(token);
            if (choice is TranscriptReviewChoice.Recheck or TranscriptReviewChoice.Resume)
            {
                force = choice == TranscriptReviewChoice.Recheck;
                continue;
            }
            if (choice == TranscriptReviewChoice.Keep)
                corrections = new Dictionary<int, string>();
            progress.Report(new(session.Id, language["TranscriptReviewSaving"], WorkProgress: new("saving")));
            TranscriptDocument updated;
            try
            {
                updated = await Task.Run(() => TranscriptReviewService.Accept(session, analysis, corrections, token), token)
                    .ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                try
                {
                    var saved = await Task.Run(() => AppStorage.LoadTranscript(session)).ConfigureAwait(false);
                    progress.Report(new(session.Id, failure.Message, Document: saved, IsWarning: true));
                }
                catch (Exception refreshFailure) { throw new AggregateException(failure, refreshFailure); }
                throw;
            }
            progress.Report(new(session.Id, language[analysis.Report.Items.Count == 0 && analysis.Report.AnalysisComplete
                ? "TranscriptReviewNoItems" : "TranscriptReviewSaved"],
                Document: updated.Copy(), WorkProgress: new("saving")));
            return;
        }
    }
}
