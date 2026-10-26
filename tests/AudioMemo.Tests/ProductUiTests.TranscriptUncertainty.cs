using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Controls;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyTranscriptUncertaintyReview(Application application, string language)
    {
        VerifyUncertaintyDialog(application, language);
        VerifyUncertaintyPreference(language);
        foreach (var action in new[] { "apply", "keep", "cancel", "recheck", "incomplete", "none", "manual", "resume",
            "unavailable", "savefailure", "formatretry", "formatfailure", "partialretry", "corruptretry" })
            VerifyUncertaintyWorkspace(language, action);
    }

    private static void VerifyUncertaintyPreference(string language)
    {
        using var folder = new TestFolder();
        var settings = TranscriptReviewTests.Settings(folder);
        settings.UiLanguage = language;
        settings.SummarizeOnStop = settings.LiveSummary = false;
        var window = new SettingsWindow(settings);
        var root = MountDialog(window, new Size(1050, 780));
        try
        {
            var check = Assert.IsType<CheckBox>(window.FindName("ReviewUnclearTranscriptCheck"));
            Assert.True(check.IsChecked);
            check.IsChecked = false;
            var save = window.PrepareSaveAsync();
            CompleteUiTask(root, save);
            Assert.True(save.Result);
            Assert.False(window.Settings.ReviewUnclearTranscript);
            Assert.True(settings.ReviewUnclearTranscript);
            var storage = new AppStorage(folder.File("preferences"));
            storage.SaveSettings(window.Settings);
            Assert.False(storage.LoadSettings().ReviewUnclearTranscript);
        }
        finally { window.Close(); }
    }

    private static void VerifyUncertaintyDialog(Application application, string language)
    {
        using var folder = new TestFolder();
        var session = TranscriptReviewTests.MakeSource(folder);
        var settings = TranscriptReviewTests.Settings(folder);
        settings.UiLanguage = language;
        var analysisTask = new TranscriptReviewService(new TranscriptReviewTests.FakeSummary())
            .AnalyzeAsync(session, settings, new TranscriptReviewTests.Updates(), CancellationToken.None, false);
        var analysis = analysisTask.GetAwaiter().GetResult();
        var before = EditedSessionTests.Hashes(session.Folder);
        foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
        {
            AppAppearance.Apply(application, theme);
            var output = new RecordingPlaybackTests.FakePlayer();
            using var playback = new RecordingPlayback(() => output);
            var window = new TranscriptReviewWindow(session, analysis, settings, playback);
            var root = MountDialog(window, new Size(1150, 700));
            try
            {
                Assert.False(window.IsVisible);
                var grid = Assert.IsType<DataGrid>(window.FindName("ReviewRows"));
                Assert.Single(grid.Items);
                Assert.Equal(UiText.Get("TranscriptReviewReason"), grid.Columns[4].Header);
                Assert.True(grid.ActualHeight > 140);
                Assert.Contains("clickpilot", Assert.Single(window.Rows).OriginalText);
                Assert.Contains("clickpilot", window.Rows[0].Reason);
                Assert.False(window.Rows[0].ApplyCorrection);
                Assert.Contains("selected-review-model", Assert.IsType<TextBlock>(window.FindName("ReviewModel")).Text);
                Assert.Equal(UiText.Get("TranscriptReviewAgain"),
                    Assert.IsType<Button>(window.FindName("ReviewAgainButton")).Content);
                Call(window, "Play_Click", window, new RoutedEventArgs());
                Assert.NotNull(playback.Snapshot);
                Assert.True(RecordingPlaybackTests.ReadAll(output.Provider!).Length > 0);
                Call(window, "Pause_Click", window, new RoutedEventArgs());
                Assert.True(playback.Snapshot!.IsPaused);
                Call(window, "Stop_Click", window, new RoutedEventArgs());
                Assert.Null(playback.Snapshot);
                Assert.True(output.Disposed);
                window.Rows[0].Text = "";
                Call(window, "Apply_Click", window, new RoutedEventArgs());
                Assert.False(window.IsClosed);
                Assert.Contains(UiText.Get("InvalidCorrectionText"),
                    Assert.IsType<TextBlock>(window.FindName("ReviewStatus")).Text);
                window.Rows[0].Text = "A deliberate typed correction.";
                Assert.True(window.Rows[0].ApplyCorrection);
                window.Rows[0].ApplyCorrection = false;
                Assert.Empty(window.Corrections);
                Call(window, "Keep_Click", window, new RoutedEventArgs());
                Assert.Equal(TranscriptReviewChoice.Keep, window.Choice);
            }
            finally { if (!window.IsClosed) window.Close(); }
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
            using var exclusive = File.Open(session.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        AppAppearance.Apply(application, AppThemes.Light);
    }

    private static void VerifyUncertaintyWorkspace(string language, string action)
    {
        using var folder = new TestFolder();
        var session = TranscriptReviewTests.MakeSource(folder);
        if (action is "none" or "unavailable")
        {
            var unmarked = AppStorage.LoadTranscript(session);
            unmarked.Segments[1] = unmarked.Segments[1] with { Text = "A clear statement." };
            AppStorage.SaveTranscript(session, unmarked);
        }
        if (action == "partialretry")
        {
            var source = AppStorage.LoadTranscript(session);
            source.Segments[0] = source.Segments[0] with { Text = new string('x', 5000) };
            AppStorage.SaveTranscript(session, source);
        }
        if (action == "corruptretry")
            File.WriteAllText(Path.Combine(session.Folder, TranscriptReviewService.FileName), "{broken");
        var settings = TranscriptReviewTests.Settings(folder);
        settings.UiLanguage = language;
        settings.SetupCompleted = true;
        settings.TranscribeOnStop = false;
        settings.ReviewUnclearTranscript = action is not ("manual" or "resume");
        if (action == "unavailable")
        {
            settings.SummaryProvider = SummaryProviders.Copilot;
            settings.CloudConsent = false;
        }
        var storage = new AppStorage(folder.File("preferences"));
        storage.SaveSettings(settings);
        var summaries = new TranscriptReviewTests.FakeSummary();
        if (action == "none")
            summaries.Reply = (_, _, _) => Task.FromResult("""{"items":[]}""");
        if (action == "incomplete")
            summaries.Reply = (_, _, _) => Task.FromException<string>(new IOException("Synthetic model failure."));
        if (action == "formatretry")
            summaries.Reply = (_, number, _) => Task.FromResult(number == 1 ? "{broken" : TranscriptReviewTests.Finding);
        if (action == "formatfailure")
            summaries.Reply = (_, _, _) => Task.FromResult("{broken");
        if (action == "partialretry")
            summaries.Reply = (_, number, _) => Task.FromResult(number switch
            {
                1 => """{"items":[]}""",
                2 or 3 => "{broken",
                _ => TranscriptReviewTests.Finding
            });
        var dialogs = new List<TranscriptReviewWindow>();
        var requestsAtDialog = new List<int>();
        var reviewRequestsAtDialog = new List<int>();
        FileStream? blockedReport = null;
        var window = new MainWindow(storage, settings, new RecordingEngine(new RecordingEngineTests.FakeFactory().Create),
            pipeline: new AiPipeline(summaryService: summaries), confirmRepeatSummary: _ => true, confirmCloseProcessing: _ => true,
            showTranscriptReview: dialog =>
            {
                dialogs.Add(dialog);
                requestsAtDialog.Add(summaries.MainPointInputs.Count);
                reviewRequestsAtDialog.Add(summaries.Prompts.Count);
                MountDialog(dialog, new Size(1150, 700));
                if (action is ("recheck" or "partialretry" or "corruptretry") && dialogs.Count == 1)
                    Call(dialog, "Recheck_Click", dialog, new RoutedEventArgs());
                else if (action == "cancel")
                    dialog.Close();
                else if (action is "apply" or "savefailure")
                {
                    if (action == "savefailure")
                        blockedReport = new FileStream(Path.Combine(session.Folder, TranscriptReviewService.FileName),
                            FileMode.Open, FileAccess.Read, FileShare.Read);
                    dialog.Rows[0].Text = "The user-approved tool name.";
                    Call(dialog, "Apply_Click", dialog, new RoutedEventArgs());
                }
                else
                {
                    dialog.Rows[0].Text = "This edit must not be saved.";
                    Call(dialog, "Keep_Click", dialog, new RoutedEventArgs());
                }
            });
        var root = MountDialog(window, new Size(1150, 800));
        try
        {
            Invoke(window, "ApplySettingsToControls");
            SetRefinementField(window, "initialized", true);
            Invoke(window, "RefreshHistory");
            SelectProcessingSession(window, session);
            Invoke(window, "UpdateControls");
            Pump(root);
            var transcript = Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel"));
            var reviewMenu = Assert.IsType<MenuItem>(transcript.FindName("ReviewUnclearMenu"));
            Assert.Equal(UiText.Get("ReviewUnclearTranscript"), reviewMenu.Header);
            Assert.True(reviewMenu.IsEnabled);
            Assert.Empty(Assert.IsType<DataGrid>(transcript.FindName("RowsGrid")).SelectedItems);
            var original = File.ReadAllBytes(session.TranscriptPath);
            var audio = File.ReadAllBytes(session.WavePath);
            var notes = File.ReadAllBytes(session.NotesPath);
            if (action == "unavailable")
            {
                Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> automatic = (options, progress, token) =>
                    Assert.IsAssignableFrom<Task>(Call(window, "ReviewTranscriptBeforeSummaryAsync", session, options, progress, token));
                Call(window, "StartAiJob", session, "TranscribeResume", automatic);
            }
            else if (action == "manual")
                reviewMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else if (action == "resume")
            {
                PendingProcessingStore.Defer(session, [ProcessingWork.ForAi("ReviewUnclearTranscript", settings)]);
                Call(window, "StartPendingProcessing", session, PendingProcessingStore.Load(session)!);
            }
            else
                Call(window, "Summarize_Click", window, new RoutedEventArgs());
            CompleteUiTask(root, ActiveUiAiTask(window));
            Assert.All(requestsAtDialog, count => Assert.Equal(0, count));
            Assert.Equal(action is "none" or "unavailable" ? 0 :
                action is "recheck" or "partialretry" or "corruptretry" ? 2 : 1, dialogs.Count);
            Assert.Equal(action switch
            {
                "unavailable" => 0,
                "partialretry" => 4,
                "recheck" or "formatretry" or "formatfailure" => 2,
                _ => 1
            }, summaries.Prompts.Count);
            Assert.Equal(action is "cancel" or "manual" or "resume" or "unavailable" or "savefailure" ? 0 :
                action == "partialretry" ? 2 : 1, summaries.MainPointInputs.Count);
            Assert.Equal(audio, File.ReadAllBytes(session.WavePath));
            Assert.Equal(notes, File.ReadAllBytes(session.NotesPath));
            if (action is "apply" or "savefailure")
            {
                Assert.Equal("The user-approved tool name.", AppStorage.LoadTranscript(session).Segments[1].Text);
                Assert.Equal("The user-approved tool name.", Assert.IsType<TranscriptDocument>(typeof(MainWindow)
                    .GetField("currentTranscript", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)).Segments[1].Text);
                if (action == "apply")
                {
                    Assert.Contains("The user-approved tool name.", Assert.Single(summaries.MainPointInputs));
                    Assert.DoesNotContain("clickpilot", summaries.MainPointInputs[0]);
                }
            }
            else
                Assert.Equal(original, File.ReadAllBytes(session.TranscriptPath));
            if (action == "incomplete")
            {
                Assert.Equal(Visibility.Visible, Assert.IsType<Border>(dialogs[0].FindName("ReviewWarningPanel")).Visibility);
                Assert.Contains("Synthetic model failure", Assert.IsType<TextBlock>(dialogs[0].FindName("ReviewWarning")).Text);
            }
            if (action is "incomplete" or "formatfailure" or "corruptretry")
                Assert.Equal(UiText.Get("TranscriptReviewRetry"),
                    Assert.IsType<Button>(dialogs[0].FindName("ReviewAgainButton")).Content);
            if (action == "formatfailure")
            {
                Assert.Contains(UiText.Get("TranscriptReviewFormatFailed"),
                    Assert.IsType<TextBlock>(dialogs[0].FindName("ReviewWarning")).Text);
                Assert.False(TranscriptReviewService.Load(session)!.AnalysisComplete);
            }
            if (action == "partialretry")
            {
                Assert.Equal([3, 4], reviewRequestsAtDialog);
                Assert.Equal(UiText.Get("TranscriptReviewResume"),
                    Assert.IsType<Button>(dialogs[0].FindName("ReviewAgainButton")).Content);
                Assert.Equal(TranscriptReviewChoice.Resume, dialogs[0].Choice);
                Assert.True(TranscriptReviewService.Load(session)!.AnalysisComplete);
            }
            if (action == "corruptretry")
            {
                Assert.Equal([0, 1], reviewRequestsAtDialog);
                Assert.Equal(TranscriptReviewChoice.Recheck, dialogs[0].Choice);
                Assert.True(TranscriptReviewService.Load(session)!.AnalysisComplete);
            }
            if (action is "cancel" or "unavailable" or "savefailure")
                Assert.Empty(TranscriptReviewService.Load(session)!.AcceptedFingerprint);
            else
                Assert.NotEmpty(TranscriptReviewService.Load(session)!.AcceptedFingerprint);
            if (action == "resume")
                Assert.Null(PendingProcessingStore.Load(session));
            if (action is "apply" or "keep" or "none")
            {
                Call(window, "Summarize_Click", window, new RoutedEventArgs());
                CompleteUiTask(root, ActiveUiAiTask(window));
                Assert.Equal(action == "none" ? 0 : 1, dialogs.Count);
                Assert.Single(summaries.Prompts);
                Assert.Equal(2, summaries.MainPointInputs.Count);
            }
            Invoke(window, "UpdateControls");
            transcript.CanReviewUnclear = false;
            var prior = ActiveUiAiTask(window);
            reviewMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Same(prior, ActiveUiAiTask(window));
        }
        finally
        {
            blockedReport?.Dispose();
            if (typeof(MainWindow).GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)
                is Task { IsCompleted: false })
                CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "CancelAiAsync")));
            window.Close();
        }
    }
}
