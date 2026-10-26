using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shell;
using AudioMemo.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyOverallSummaryWorkspace(string language)
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.UiLanguage = language;
        settings.SaveFolder = Path.Combine(folder.Root, "recordings");
        settings.SummarizeOnStop = true;
        settings.IdentifySpeakersOnStop = false;
        var storage = new AppStorage(Path.Combine(folder.Root, "preferences"));
        storage.SaveSettings(settings);
        var session = AppStorage.CreateSession(settings, "Synthetic completed recording");
        session.Frames = 12L * PcmFormat.SampleRate;
        session.Status = "Saved";
        var document = new TranscriptDocument
        {
            Revision = "synthetic-overall-summary",
            ProcessedFrames = session.Frames,
            Segments = [new(2, 12, "Alex will prepare the slides.") { SpeakerId = "spkr1" }],
            SpeakerNames = new() { ["spkr1"] = "Alex" }
        };
        AppStorage.SaveTranscript(session, document);
        File.WriteAllText(session.NotesPath, "PRIVATE PERSONAL NOTE");
        var window = new MainWindow(storage, settings);
        var root = Assert.IsType<Grid>(window.Content);
        var service = new OverallUiSummary();
        Task? running = null;
        try
        {
            Invoke(window, "ApplySettingsToControls");
            typeof(MainWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);
            Call(window, "LoadSessionContent", session);
            root.Measure(new Size(1280, 720));
            root.Arrange(new Rect(0, 0, 1280, 720));
            Pump(root);
            var pipeline = new AiPipeline(summaryService: service);
            Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> work = (snapshot, progress, cancellation) =>
                pipeline.CompleteTranscriptionAsync(session, snapshot, document, progress, cancellation);
            Call(window, "StartAiJob", session, "TranscribeResume", work);
            running = ActiveUiAiTask(window);
            CompleteUiTask(root, service.OverallRequested.Task);
            Pump(root);
            var summary = Assert.IsType<TextBox>(window.FindName("SummaryBox"));
            Assert.Contains("00:00:02 - 00:00:12", summary.Text);
            Assert.DoesNotContain(UiText.Get("OverallSummary"), summary.Text);
            Assert.True(Assert.IsType<ProgressBar>(window.FindName("ProcessingProgress")).IsIndeterminate);
            Assert.Equal(TaskbarItemProgressState.Indeterminate, window.TaskbarItemInfo.ProgressState);
            Assert.Equal(UiText.Get("OverallSummaryProgress"), Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);

            service.OverallCompletion.SetResult("## Main points\n  - The project is ready.\n\n## Action items\n  - [spkr1] will prepare the slides.");
            CompleteUiTask(root, running);
            var heading = "# " + UiText.Get("OverallSummary");
            var start = summary.Text.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(start > 0);
            Assert.Contains("00:00:02 - 00:00:12", summary.Text[..start]);
            Assert.DoesNotContain("00:00:", summary.Text[start..]);
            Assert.Contains("  - Alex will prepare the slides.", summary.Text[start..]);
            Assert.Equal(1, summary.Text.Split(heading).Length - 1);
            Assert.Equal(TaskbarItemProgressState.Normal, window.TaskbarItemInfo.ProgressState);
            Assert.False(Assert.IsType<ProgressBar>(window.FindName("ProcessingProgress")).IsIndeterminate);
            Assert.Equal(UiText.Get("SummarySaved"), Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            Assert.True(summary.IsReadOnly);
            Assert.Equal(1, service.MainPointsCalls);
            Assert.Equal(1, service.OverallCalls);

            var before = EditedSessionTests.Hashes(session.Folder);
            var expected = summary.Text;
            Call(window, "ClearWorkspace");
            typeof(MainWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);
            Call(window, "LoadSessionContent", session);
            Pump(root);
            Assert.Equal(expected, summary.Text);
            Assert.Same(running, ActiveUiAiTask(window));
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
            Assert.Equal("PRIVATE PERSONAL NOTE", Assert.IsType<TextBox>(window.FindName("NotesBox")).Text);
        }
        finally
        {
            service.OverallCompletion.TrySetCanceled();
            if (running is not null)
                CompleteUiTask(root, running);
            window.Close();
        }
    }

    private sealed class OverallUiSummary : ISummaryService
    {
        private const string MainPoints = "## Main points\n[00:00:02 - 00:00:12]\n  [spkr1] will prepare the slides.";
        public TaskCompletionSource OverallRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> OverallCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MainPointsCalls { get; private set; }
        public int OverallCalls { get; private set; }

        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            Assert.False(live);
            Assert.DoesNotContain("PRIVATE", transcript);
            MainPointsCalls++;
            return Task.FromResult(MainPoints);
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            Assert.Equal(MainPoints, mainPoints);
            OverallCalls++;
            OverallRequested.TrySetResult();
            return OverallCompletion.Task.WaitAsync(cancellationToken);
        }
    }
}
