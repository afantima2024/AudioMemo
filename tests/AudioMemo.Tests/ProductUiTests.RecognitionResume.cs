using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyResumableCorrectionWindow(Application application, string language)
    {
        foreach (var discard in new[] { false, true })
        {
            using var folder = new TestFolder();
            var session = EditedSessionTests.MakeSource(folder);
            var before = EditedSessionTests.Hashes(session.Folder);
            var time = new ProcessingTimingTests.ManualTime();
            var first = new TranscriptCorrectionTests.FakeWorker();
            first.Response = async (_, number, token) =>
            {
                if (number == 1)
                    return [new(0, 0.4, "Finished first row.")];
                if (discard && number == 2)
                    return [];
                first.Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return [];
            };
            var resumeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TranscriptCorrectionTests.FakeWorker();
            second.Response = async (_, number, token) =>
            {
                if (number == 1)
                {
                    second.Started.TrySetResult();
                    await resumeRelease.Task.WaitAsync(token);
                }
                return [new(0, 0.4, $"Remaining row {number}.")];
            };
            var workers = new ResumeWorkers(first, second);
            var settings = new AppSettings
            {
                SaveFolder = folder.Root,
                UiLanguage = language,
                Theme = discard ? AppThemes.Light : AppThemes.Dark
            };
            AppAppearance.Apply(application, settings);
            var window = new TranscriptEditWindow(session, TranscriptCorrectionTests.Plan(session, 0, 1, 2),
                settings, false,
                (_, _) => throw new InvalidOperationException("Recognition must not save vocabulary."),
                new SelectedTranscriptionService(workers, (_, _) => Task.CompletedTask), processingTimeProvider: time);
            var root = MountDialog(window, new Size(1060, 740));
            var grid = Assert.IsType<DataGrid>(window.FindName("CorrectionRows"));
            var rows = grid.Items.Cast<TranscriptEditWindow.CorrectionRow>().ToArray();
            var original = rows.Select(row => row.Text).ToArray();
            rows[0].Text = "Manual draft before recognition.";
            rows[1].OnlineText = "An independent online result.";
            var pending = BeginSelectedRecognition(window);
            time.Advance(TimeSpan.FromSeconds(10));
            try
            {
                CompleteUiTask(root, first.Started.Task);
                var timing = Assert.IsType<TextBlock>(window.FindName("RecognitionTimingText"));
                var timer = Assert.IsType<DispatcherTimer>(typeof(TranscriptEditWindow)
                    .GetField("recognitionClock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                Assert.Equal(Visibility.Visible, timing.Visibility);
                Assert.StartsWith(UiText.Format("ProcessingElapsed", "00:00:10"), timing.Text);
                Assert.True(timer.IsEnabled);
                var stopContinue = Assert.IsType<Button>(window.FindName("StopContinueRecognitionButton"));
                var save = Assert.IsType<Button>(window.FindName("SaveCorrectionButton"));
                var model = Assert.IsType<ComboBox>(window.FindName("CorrectionModelBox"));
                Assert.Equal(UiText.Get("Stop"), stopContinue.Content);
                Assert.True(stopContinue.IsEnabled);
                Assert.False(save.IsEnabled);
                Assert.False(grid.IsEnabled);
                AssertThemedTableSurface(application, root, grid);
                Assert.Equal("Finished first row.", rows[0].Text);
                Assert.Equal(original, rows.Select(row => row.LocalText));
                Assert.Equal(TranscriptComparisonState.Different, rows[0].ComparisonState);
                Assert.Equal(discard ? TranscriptComparisonState.Unclear : TranscriptComparisonState.NotCompared, rows[1].ComparisonState);
                Click(stopContinue);
                CompleteUiTask(root, pending);
                time.Advance(TimeSpan.FromHours(2));
                Invoke(window, "UpdateRecognitionTiming");
                Assert.Equal(UiText.Format("ProcessingElapsed", "00:00:10"), timing.Text);
                Assert.False(timer.IsEnabled);
                Assert.True(first.Disposed);
                Assert.True(save.IsEnabled);
                Assert.True(grid.IsEnabled);
                AssertThemedTableSurface(application, root, grid);
                Assert.False(model.IsEnabled);
                Assert.False(Assert.IsType<Button>(window.FindName("SaveVocabularyButton")).IsEnabled);
                Assert.False(Assert.IsType<Button>(window.FindName("UploadOnlineButton")).IsEnabled);
                Assert.Equal(UiText.Get("ContinueRecognition"), stopContinue.Content);
                Assert.Equal(original[1..], rows.Skip(1).Select(row => row.Text));
                Assert.Contains(UiText.Format("SelectionRecognitionStopped", discard ? 2 : 1, 3),
                    Assert.IsType<TextBlock>(window.FindName("CorrectionStatus")).Text);
                Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
                rows[0].Text = "Reviewed finished row.";
                if (discard)
                {
                    var cancel = Assert.IsType<Button>(window.FindName("CancelRecognitionButton"));
                    Assert.Equal(UiText.Get("DiscardRecognitionPreview"), cancel.Content);
                    Click(cancel);
                    Assert.Equal("Manual draft before recognition.", rows[0].Text);
                    Assert.Equal(original[0], rows[0].LocalText);
                    Assert.Equal(TranscriptComparisonState.Different, rows[0].ComparisonState);
                    Assert.All(rows.Skip(1), row =>
                    {
                        Assert.False(row.HasCorrection);
                        Assert.False(row.Unrecognized);
                        Assert.Equal(TranscriptComparisonState.NotCompared, row.ComparisonState);
                    });
                    Assert.True(model.IsEnabled);
                    Assert.Equal(Visibility.Collapsed, stopContinue.Visibility);
                    Assert.Equal(1, workers.OpenCount);
                }
                else
                {
                    Click(stopContinue);
                    CompleteUiTask(root, second.Started.Task);
                    Assert.True(timer.IsEnabled);
                    time.Advance(TimeSpan.FromSeconds(5));
                    Assert.False(grid.IsEnabled);
                    AssertThemedTableSurface(application, root, grid);
                    resumeRelease.SetResult();
                    CompleteUiTask(root, WaitForRecognitionIdleAsync(window));
                    Assert.Equal(UiText.Format("ProcessingElapsed", "00:00:15"), timing.Text);
                    Assert.False(timer.IsEnabled);
                    Assert.Equal(2, workers.OpenCount);
                    Assert.Equal(2, second.Chunks.Count);
                    Assert.True(second.Disposed);
                    Assert.Equal(["Reviewed finished row.", "Remaining row 1.", "Remaining row 2."], rows.Select(row => row.Text));
                    Assert.Equal(original, rows.Select(row => row.LocalText));
                    Assert.All(rows, row => Assert.Equal(TranscriptComparisonState.Different, row.ComparisonState));
                    Assert.True(model.IsEnabled);
                    Assert.True(save.IsEnabled);
                    Assert.Equal(Visibility.Collapsed, stopContinue.Visibility);
                    AssertThemedTableSurface(application, root, grid);
                }
                Assert.Equal(original, rows.Select(row => row.LocalText));
                Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
                Assert.All(first.Paths.Concat(second.Paths), path => Assert.False(File.Exists(path)));
            }
            finally
            {
                first.Started.TrySetResult();
                resumeRelease.TrySetResult();
                Click(Assert.IsType<Button>(window.FindName("CancelRecognitionButton")));
                CompleteUiTask(root, pending);
                CompleteUiTask(root, WaitForRecognitionIdleAsync(window));
                window.Close();
                AppAppearance.Apply(application, AppThemes.Light);
            }
        }
    }

    private static void VerifyUnfinishedRowPreview(string language)
    {
        using var folder = new TestFolder();
        var session = SelectedTranscriptionResumeTests.MakeLongSession(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var first = new TranscriptCorrectionTests.FakeWorker();
        first.Response = async (_, number, token) =>
        {
            if (number == 1)
                return [new(0, 5, "First completed excerpt.")];
            first.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        };
        var second = new TranscriptCorrectionTests.FakeWorker
        {
            Response = (_, number, _) => Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(2, 5, $"Remaining chunk {number}.")])
        };
        var window = new TranscriptEditWindow(session, TranscriptCorrectionTests.Plan(session, 0),
            new() { SaveFolder = folder.Root, UiLanguage = language }, false, (_, _) => { },
            new SelectedTranscriptionService(new ResumeWorkers(first, second), (_, _) => Task.CompletedTask));
        var root = Assert.IsType<Grid>(window.Content);
        root.Measure(new Size(1060, 740));
        root.Arrange(new Rect(0, 0, 1060, 740));
        var pending = BeginSelectedRecognition(window);
        try
        {
            CompleteUiTask(root, first.Started.Task);
            var stop = Assert.IsType<Button>(window.FindName("StopContinueRecognitionButton"));
            Click(stop);
            CompleteUiTask(root, pending);
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(window.FindName("PartialRecognitionPanel")).Visibility);
            var excerpt = Assert.IsType<TextBox>(window.FindName("PartialRecognitionText"));
            Assert.True(excerpt.IsReadOnly);
            Assert.Equal("First completed excerpt.", excerpt.Text);
            var row = Assert.Single(Assert.IsType<DataGrid>(window.FindName("CorrectionRows")).Items.Cast<TranscriptEditWindow.CorrectionRow>());
            Assert.Equal("Original complete row.", row.Text);
            Assert.Equal("Original complete row.", row.LocalText);
            Assert.Equal(TranscriptComparisonState.NotCompared, row.ComparisonState);
            Assert.Equal(100d * 30 / 65, Assert.IsType<ProgressBar>(window.FindName("RecognitionProgress")).Value, 6);
            Assert.True(Assert.IsType<Button>(window.FindName("SaveCorrectionButton")).IsEnabled);
            Click(stop);
            CompleteUiTask(root, WaitForRecognitionIdleAsync(window));
            Assert.Equal(2, second.Chunks.Count);
            Assert.Contains("First completed excerpt.", row.Text);
            Assert.Contains("Remaining chunk 2.", row.Text);
            Assert.Equal("Original complete row.", row.LocalText);
            Assert.Equal(TranscriptComparisonState.Different, row.ComparisonState);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(window.FindName("PartialRecognitionPanel")).Visibility);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        }
        finally
        {
            Click(Assert.IsType<Button>(window.FindName("CancelRecognitionButton")));
            CompleteUiTask(root, pending);
            CompleteUiTask(root, WaitForRecognitionIdleAsync(window));
            window.Close();
        }
    }

    private static async Task WaitForRecognitionIdleAsync(TranscriptEditWindow window)
    {
        var operation = typeof(TranscriptEditWindow).GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        while (operation.GetValue(window) is not null)
            await Task.Delay(5);
    }

    private sealed class ResumeWorkers(params ISpeechWorker[] workers) : ILocalTranscriber
    {
        private readonly Queue<ISpeechWorker> remaining = new(workers);
        public string WorkerExecutable => "unused-resume-test-worker";
        public int OpenCount { get; private set; }
        public Task<ISpeechWorker> OpenAsync(SpeechModel model, string language, string acceleration, bool liveDraft,
            CancellationToken cancellationToken, IReadOnlyList<string>? vocabulary = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return Task.FromResult(remaining.Dequeue());
        }
    }
}
