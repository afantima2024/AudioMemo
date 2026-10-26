using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyProcessingTiming(Application application, string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var settings = new AppSettings { SaveFolder = folder.Root, UiLanguage = language };
        var time = new ProcessingTimingTests.ManualTime();
        var recorder = new RecordingEngine(new RecordingEngineTests.FakeFactory().Create);
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, recorder,
            confirmCloseProcessing: _ => true, processingTimeProvider: time);
        var root = Assert.IsType<Grid>(window.Content);
        var clock = Assert.IsType<DispatcherTimer>(typeof(MainWindow)
            .GetField("clock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<AiUpdate>? updates = null;
        try
        {
            Invoke(window, "ApplySettingsToControls");
            typeof(MainWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);
            var label = Assert.IsType<TextBlock>(window.FindName("ProcessingTimingText"));
            Assert.Equal(Visibility.Collapsed, label.Visibility);
            Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> operation = (_, progress, token) =>
            {
                updates = progress;
                progress.Report(new(session.Id, "Synthetic processing", WorkProgress: new("transcription", 0.8)));
                return completion.Task.WaitAsync(token);
            };
            Call(window, "StartAiJob", session, "TranscribeResume", operation);
            Pump(root);
            Assert.Equal(UiText.Format("ProcessingEstimating", "00:00:00"), label.Text);
            time.Advance(TimeSpan.FromSeconds(20));
            updates!.Report(new(session.Id, "Synthetic chunk", WorkProgress: new("transcription", 0.9)));
            Pump(root);
            var timing = Assert.IsType<ProcessingTiming>(typeof(MainWindow)
                .GetField("processingTiming", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Equal(timing.Display(UiText.Current).Text, label.Text);
            Assert.NotNull(timing.Remaining);
            Assert.Equal(Visibility.Visible, label.Visibility);
            Assert.Contains(UiText.Get("ProcessingTimingHint"), Assert.IsType<string>(label.ToolTip));
            var additional = Assert.IsType<long>(Call(window, "BeginProcessing"));
            Call(window, "FinishProcessing", additional, ProcessingOutcome.Completed);
            Assert.Equal(TimeSpan.FromSeconds(20), timing.Elapsed);
            Assert.NotNull(timing.Remaining);

            foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
            {
                AppAppearance.Apply(application, theme);
                foreach (var size in new[] { new Size(640, 420), new Size(1440, 900) })
                {
                    root.Measure(size);
                    root.Arrange(new Rect(size));
                    Pump(root);
                    var indicator = Assert.IsType<DockPanel>(window.FindName("ProcessingIndicator"));
                    var progress = Assert.IsType<ProgressBar>(window.FindName("ProcessingProgress"));
                    var labelBounds = label.TransformToAncestor(root).TransformBounds(new Rect(label.RenderSize));
                    var progressBounds = progress.TransformToAncestor(root).TransformBounds(new Rect(progress.RenderSize));
                    Assert.True(labelBounds.Right <= size.Width && labelBounds.Bottom <= size.Height);
                    Assert.True(progressBounds.Right <= labelBounds.Left);
                    Assert.True(progress.ActualWidth > 100);
                    Assert.True(indicator.ActualHeight < 22, "Timing belongs in the existing compact processing row.");
                    Assert.Equal(BrushColor(application.FindResource("TextFillColorSecondaryBrush")), BrushColor(label.Foreground));
                }
            }

            updates.Report(new(session.Id, "Synthetic model setup", WorkProgress: new("speakers")));
            Pump(root);
            Assert.Null(timing.Remaining);
            clock.Start();
            time.Advance(TimeSpan.FromSeconds(1));
            CompleteUiTask(root, Task.Delay(250));
            Assert.Equal(UiText.Format("ProcessingEstimating", "00:00:21"), label.Text);
            completion.SetResult();
            CompleteUiTask(root, ActiveUiAiTask(window));
            Assert.Equal(UiText.Format("ProcessingElapsed", "00:00:21"), label.Text);
            time.Advance(TimeSpan.FromHours(1));
            Invoke(window, "UpdateRecordingDisplay");
            Assert.Equal(UiText.Format("ProcessingElapsed", "00:00:21"), label.Text);
            Call(window, "OnActivated", EventArgs.Empty);
            Assert.Equal(Visibility.Collapsed, label.Visibility);

            var stale = updates;
            Call(window, "StartAiJob", session, "TranscribeResume",
                new Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task>((_, progress, token) =>
                {
                    progress.Report(new(session.Id, "Synthetic new run", WorkProgress: new("transcription", 0)));
                    return Task.Delay(Timeout.InfiniteTimeSpan, token);
                }));
            stale.Report(new(session.Id, "Stale update", WorkProgress: new("obsolete", double.NaN)));
            Pump(root);
            Assert.Equal(UiText.Format("ProcessingEstimating", "00:00:00"), label.Text);
            time.Advance(TimeSpan.FromSeconds(5));
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "CancelAiAsync")));
            Assert.Equal(UiText.Format("ProcessingElapsed", "00:00:05"), label.Text);
            Assert.False(timing.IsRunning);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        }
        finally
        {
            completion.TrySetResult();
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "CancelAiAsync")));
            clock.Stop();
            window.Close();
            AppAppearance.Apply(application, AppThemes.Light);
        }
    }
}
