using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyVideoTrackDialog(Application application, string language)
    {
        using var folder = new TestFolder();
        foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
        {
            AppAppearance.Apply(application, new AppSettings { UiLanguage = language, Theme = theme });
            var window = new AudioImportWindow(folder.File("synthetic.mkv"), folder.File("library"));
            var running = typeof(AudioImportWindow).GetField("running",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            try
            {
                var tracks = Assert.IsType<ComboBox>(window.FindName("AudioTrackBox"));
                var panel = Assert.IsType<StackPanel>(window.FindName("AudioTrackPanel"));
                var import = Assert.IsType<Button>(window.FindName("ImportSelectedButton"));
                var cancel = Assert.IsType<Button>(window.FindName("CancelImportButton"));
                Assert.Equal(Visibility.Collapsed, panel.Visibility);
                Assert.Equal(Visibility.Collapsed, import.Visibility);
                var information = new VideoMediaInfo(folder.File("synthetic.mkv"), 100, 0, 0, 2,
                    [new(1, "aac", 2, "eng", "English track", false, 0, 2),
                     new(3, "aac", 6, "zho", "Chinese track", true, 0, 2)]);
                Call(window, "ShowAudioTracks", information);
                Assert.Equal(2, tracks.Items.Count);
                Assert.Equal(3, tracks.SelectedValue);
                Assert.True(import.IsEnabled);
                Assert.Equal(UiText.Get("VideoImportSelected"), import.Content);
                Assert.Equal(UiText.Get("VideoChooseTrack"), Assert.IsType<TextBlock>(window.FindName("ImportStatus")).Text);
                tracks.SelectedIndex = -1;
                Assert.False(import.IsEnabled);
                tracks.SelectedIndex = 0;
                Assert.Equal(1, tracks.SelectedValue);
                Assert.True(import.IsEnabled);
                running.SetValue(window, true);
                Call(window, "UpdateTrackActions");
                Assert.False(tracks.IsEnabled);
                Assert.False(import.IsEnabled);
                Assert.True(cancel.IsEnabled);
                running.SetValue(window, false);
                Call(window, "UpdateTrackActions");
                Assert.True(tracks.IsEnabled);
                Assert.True(import.IsEnabled);
                var root = MountDialog(window, new Size(360, 380));
                foreach (var size in new[] { new Size(360, 380), new Size(520, 380) })
                {
                    root.Measure(size);
                    root.Arrange(new Rect(size));
                    Pump(root);
                    Assert.Equal(Visibility.Visible, panel.Visibility);
                    Assert.True(tracks.ActualWidth > 200);
                    foreach (var button in new[] { import, cancel })
                    {
                        var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                        Assert.True(bounds.Left >= 0 && bounds.Right <= size.Width && bounds.Bottom <= size.Height);
                        Assert.True(button.ActualHeight >= button.MinHeight);
                    }
                }
                Assert.Null(window.Result);
                Assert.False(Directory.Exists(folder.File("library")));
                Assert.Contains("English track", Assert.IsType<string>(tracks.ToolTip));
                Assert.Equal(BrushColor(application.FindResource("SolidBackgroundFillColorBaseBrush")), BrushColor(window.Background));
                Assert.Contains("*.mp4", UiText.Get("ImportAudioFilter"));
                Assert.Contains("*.mkv", UiText.Get("ImportAudioFilter"));
                Assert.Contains("*.webm", UiText.Get("ImportAudioFilter"));
            }
            finally { running.SetValue(window, false); window.Close(); }
        }
        AppAppearance.Apply(application, AppThemes.Light);
    }

    private static void VerifyImportedHistoryAndWidth(string language)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var storage = new AppStorage(folder.File("preferences"));
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            UiLanguage = language,
            TranscribeOnStop = true,
            SummarizeOnStop = true,
            LiveSummary = true,
            CloudConsent = true
        };
        storage.SaveSettings(settings);
        var settingsPath = folder.File("preferences\\settings.json");
        var settingsField = typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var initialized = typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var window = new MainWindow(storage, settings);
        try
        {
            Invoke(window, "ApplySettingsToControls");
            initialized.SetValue(window, true);
            var root = Assert.IsType<Grid>(window.Content);
            root.Measure(new Size(1440, 900));
            root.Arrange(new Rect(0, 0, 1440, 900));
            Pump(root);
            var imported = new AudioImportService().ImportAsync(source.WavePath, folder.Root, new Progress<double>(), CancellationToken.None);
            CompleteUiTask(root, imported);
            Call(window, "DisplayImportedSession", imported.Result);
            Invoke(window, "UpdateControls");
            Assert.True(Assert.IsType<Button>(window.FindName("ImportAudioButton")).IsEnabled);
            Assert.Same(window.FindName("ImportAudioButton"), Call(window, "ShortcutTarget", "import"));
            Assert.Same(Assert.IsType<AudioMemo.Controls.TranscriptView>(window.FindName("TranscriptPanel")).FindName("OnlineRowsAction"),
                Call(window, "ShortcutTarget", "online"));
            Assert.Equal("", settings.Shortcuts.Gesture("import"));
            Assert.Equal("", settings.Shortcuts.Gesture("online"));
            Assert.Null(typeof(MainWindow).GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.False(File.Exists(imported.Result.TranscriptPath));
            Assert.False(File.Exists(imported.Result.SummaryPath));

            var history = Assert.IsType<ListBox>(window.FindName("HistoryList"));
            var importedDocument = AudioEditPlanTests.Document((double)imported.Result.Frames / PcmFormat.SampleRate,
                new TranscriptSegment(0, 1, "Stored imported analysis.") { SpeakerId = "spkr1" });
            AppStorage.SaveTranscript(imported.Result, importedDocument);
            history.SelectedItem = history.Items.Cast<RecordingSession>().Single(session => session.Id == source.Id);
            history.SelectedItem = history.Items.Cast<RecordingSession>().Single(session => session.Id == imported.Result.Id);
            var current = Assert.IsType<TranscriptDocument>(
                typeof(MainWindow).GetField("currentTranscript", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Equal("Stored imported analysis.", Assert.Single(current.Segments).Text);
            Assert.Null(typeof(MainWindow).GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));

            var panel = Assert.IsType<Border>(window.FindName("HistoryPanel"));
            var grip = Assert.IsType<Thumb>(window.FindName("HistoryResizeGrip"));
            var all = Assert.IsType<CheckBox>(window.FindName("SelectAllHistoryCheck"));
            var import = Assert.IsType<Button>(window.FindName("ImportAudioButton"));
            var open = Assert.IsType<Button>(window.FindName("OpenLibraryButton"));
            var count = Assert.IsType<TextBlock>(window.FindName("LibraryHint"));
            Assert.Same(count.Parent, all.Parent);
            Assert.IsType<DockPanel>(all.Parent);
            Assert.Equal(Dock.Left, DockPanel.GetDock(all));
            Assert.Same(import.Parent, open.Parent);
            var actions = Assert.IsType<Grid>(import.Parent);
            Assert.Equal(Dock.Bottom, DockPanel.GetDock(actions));
            Assert.Equal(0, Grid.GetColumn(import));
            Assert.Equal(2, Grid.GetColumn(open));
            Assert.Equal(TextAlignment.Right, count.TextAlignment);
            Assert.Equal(TextWrapping.NoWrap, count.TextWrapping);
            Pump(root);
            AssertHistoryControlLayout();
            Call(window, "HistoryWidth_DragStarted", grip, new DragStartedEventArgs(0, 0));
            Call(window, "HistoryWidth_DragDelta", grip, new DragDeltaEventArgs(-30, 0));
            Pump(root);
            Assert.Equal(180, panel.ActualWidth, 1);
            AssertHistoryControlLayout();
            Call(window, "HistoryWidth_DragCompleted", grip, new DragCompletedEventArgs(-30, 0, true));
            Pump(root);
            var originalHeight = panel.ActualHeight;
            var before = File.ReadAllBytes(settingsPath);
            Call(window, "HistoryWidth_DragStarted", grip, new DragStartedEventArgs(0, 0));
            Call(window, "HistoryWidth_DragDelta", grip, new DragDeltaEventArgs(90, 0));
            Pump(root);
            Assert.Equal(300, panel.ActualWidth, 1);
            AssertHistoryControlLayout();
            Assert.Equal(originalHeight, panel.ActualHeight, 1);
            Assert.Equal(before, File.ReadAllBytes(settingsPath));
            Call(window, "HistoryWidth_DragCompleted", grip, new DragCompletedEventArgs(90, 0, false));
            Assert.Equal(300, storage.LoadSettings().HistoryWidth);

            Call(window, "HistoryWidth_DragStarted", grip, new DragStartedEventArgs(0, 0));
            Call(window, "HistoryWidth_DragDelta", grip, new DragDeltaEventArgs(100, 0));
            Call(window, "HistoryWidth_DragCompleted", grip, new DragCompletedEventArgs(100, 0, true));
            Pump(root);
            Assert.Equal(300, panel.ActualWidth, 1);
            Assert.Equal(300, storage.LoadSettings().HistoryWidth);

            Assert.IsType<AppSettings>(settingsField.GetValue(window)).HistoryVisible = false;
            Invoke(window, "UpdateHistoryVisibility");
            Call(window, "RevealHistory", grip, new RoutedEventArgs());
            Pump(root);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal(300, panel.ActualWidth, 1);
            Assert.Equal(originalHeight, panel.ActualHeight, 1);
            AssertHistoryControlLayout();
            Call(window, "HistoryWidth_DragStarted", grip, new DragStartedEventArgs(0, 0));
            Call(window, "HistoryWidth_DragDelta", grip, new DragDeltaEventArgs(180, 0));
            Call(window, "HistoryWidth_DragCompleted", grip, new DragCompletedEventArgs(180, 0, false));
            Pump(root);
            Assert.Equal(480, panel.ActualWidth, 1);
            Assert.Equal(480, storage.LoadSettings().HistoryWidth);
            Assert.Equal(originalHeight, panel.ActualHeight, 1);
            Assert.True(grip.ActualHeight > 100);
            AssertHistoryControlLayout();

            var attributes = File.GetAttributes(settingsPath);
            try
            {
                File.SetAttributes(settingsPath, attributes | FileAttributes.ReadOnly);
                var failure = Assert.Throws<TargetInvocationException>(() => Call(window, "SaveHistoryWidth", 520d));
                Assert.True(failure.InnerException is IOException or UnauthorizedAccessException);
                Assert.Equal(480, Assert.IsType<AppSettings>(settingsField.GetValue(window)).HistoryWidth);
                Assert.Equal(480, storage.LoadSettings().HistoryWidth);
            }
            finally { File.SetAttributes(settingsPath, attributes); }

            Assert.IsType<AppSettings>(settingsField.GetValue(window)).HistoryVisible = true;
            foreach (var size in new[] { new Size(1024, 768), new Size(640, 480), new Size(1440, 900) })
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                Pump(root);
                Assert.Equal(480, Assert.IsType<AppSettings>(settingsField.GetValue(window)).HistoryWidth);
                Assert.Equal(480, storage.LoadSettings().HistoryWidth);
                if (size.Width >= 900)
                {
                    Assert.True(panel.ActualWidth <= 480);
                    var scroller = Assert.IsType<ScrollViewer>(window.FindName("WorkspaceScroller"));
                    Assert.Equal(Visibility.Collapsed, scroller.ComputedHorizontalScrollBarVisibility);
                }
            }
            Assert.Equal(480, panel.ActualWidth, 1);
            before = File.ReadAllBytes(settingsPath);
            var dialog = new SettingsWindow(storage.LoadSettings());
            try
            {
                Assert.Equal(480, dialog.Settings.HistoryWidth);
                Assert.Equal(settings.OnlineSpeech.Provider, dialog.Settings.OnlineSpeech.Provider);
            }
            finally { dialog.Close(); }
            Assert.Equal(before, File.ReadAllBytes(settingsPath));

            void AssertHistoryControlLayout()
            {
                Assert.Equal(count.Text, count.ToolTip);
                Assert.InRange(Math.Abs(all.TranslatePoint(new Point(0, all.ActualHeight / 2), panel).Y -
                    count.TranslatePoint(new Point(0, count.ActualHeight / 2), panel).Y), 0, 0.5);
                Assert.True(all.TranslatePoint(new Point(all.ActualWidth, 0), panel).X <
                    count.TranslatePoint(new Point(), panel).X);
                Assert.InRange(Math.Abs(import.TranslatePoint(new Point(0, import.ActualHeight / 2), panel).Y -
                    open.TranslatePoint(new Point(0, open.ActualHeight / 2), panel).Y), 0, 0.5);
                Assert.True(import.TranslatePoint(new Point(import.ActualWidth, 0), panel).X <
                    open.TranslatePoint(new Point(), panel).X);
                Assert.True(import.TranslatePoint(new Point(), panel).Y >=
                    history.TranslatePoint(new Point(0, history.ActualHeight), panel).Y);
                Assert.True(open.TranslatePoint(new Point(open.ActualWidth, 0), panel).X <= panel.ActualWidth - panel.Padding.Right);
                Assert.InRange(actions.ActualHeight, 30, 36);
                foreach (var button in new[] { import, open })
                {
                    var caption = Descendants<TextBlock>(button).Single();
                    Assert.Equal(button.Content, caption.Text);
                    Assert.Equal(TextTrimming.CharacterEllipsis, caption.TextTrimming);
                    Assert.True(caption.ActualWidth <= button.ActualWidth - button.Padding.Left - button.Padding.Right);
                    Assert.NotNull(button.ToolTip);
                }
            }
        }
        finally
        {
            initialized.SetValue(window, false);
            window.Close();
        }
    }

    private static void VerifyOnlineComparisonWindow(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0, 2);
        var settings = OnlineSpeechTests.Settings(OnlineSpeechCatalog.Qwen, folder.Root);
        settings.UiLanguage = language;
        using var handler = new OnlineSpeechTests.Handler
        {
            Response = (number, _) => Task.FromResult(OnlineSpeechTests.Reply(JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop",
                    message = new { content = number == 1 ? plan.Segments[0].Text : "Improved online second decision." } } }
            })))
        };
        using var client = new HttpClient(handler);
        var service = new SelectedOnlineTranscriptionService(new(client), folder.File("work"));
        OnlineSpeechSettings? savedConnection = null;
        var window = new TranscriptEditWindow(session, plan, settings, false, (_, _) => { }, online: true,
            saveOnlineSettings: value => savedConnection = value, onlineRecognizer: service);
        try
        {
            var root = Assert.IsType<Grid>(window.Content);
            var grid = Assert.IsType<DataGrid>(window.FindName("CorrectionRows"));
            foreach (var size in new[] { new Size(1060, 740), new Size(800, 600), new Size(640, 480), new Size(1060, 740) })
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                Pump(root);
                var scroller = Assert.IsType<ScrollViewer>(window.FindName("CorrectionScroller"));
                Assert.Equal(Visibility.Collapsed, scroller.ComputedHorizontalScrollBarVisibility);
                Assert.True(grid.ActualHeight >= 60, $"Online comparison has no usable rows at {size}: {grid.RenderSize}.");
                scroller.ScrollToEnd();
                Pump(root);
                var save = Assert.IsType<Button>(window.FindName("SaveCorrectionButton"));
                var bounds = save.TransformToAncestor(scroller).TransformBounds(new Rect(save.RenderSize));
                Assert.True(bounds.Top >= -1 && bounds.Bottom <= scroller.ViewportHeight + 1,
                    $"Save is inaccessible at {size}: {bounds}, viewport={scroller.ViewportHeight}.");
                scroller.ScrollToTop();
                Pump(root);
            }
            Assert.Empty(handler.Requests);
            Assert.Null(savedConnection);
            var consent = Assert.IsType<CheckBox>(window.FindName("OnlineConsentCheck"));
            Assert.False(consent.IsChecked);
            var rows = grid.Items.Cast<TranscriptEditWindow.CorrectionRow>().ToArray();
            Assert.All(rows, row => Assert.Null(row.OnlineText));
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "TranscribeOnlineAsync")));
            Assert.Empty(handler.Requests);
            Assert.Contains(UiText.Get("OnlineAudioConsentRequired"), Assert.IsType<TextBlock>(window.FindName("CorrectionStatus")).Text);

            var providers = Assert.IsType<ComboBox>(window.FindName("OnlineProviderBox"));
            Assert.Equal([OnlineSpeechCatalog.Qwen, OnlineSpeechCatalog.OpenAi],
                providers.Items.Cast<LanguageOption>().Select(option => option.Code));
            providers.SelectedValue = OnlineSpeechCatalog.OpenAi;
            Assert.Equal("gpt-transcribe", Assert.IsType<ComboBox>(window.FindName("OnlineModelBox")).SelectedValue);
            Assert.Contains("25", Assert.IsType<TextBlock>(window.FindName("OnlineLimitsText")).Text);
            providers.SelectedValue = OnlineSpeechCatalog.Qwen;
            Assert.Contains("10", Assert.IsType<TextBlock>(window.FindName("OnlineLimitsText")).Text);
            consent.IsChecked = true;
            Assert.IsType<ComboBox>(window.FindName("CorrectionLanguageBox")).SelectedValue = "en";
            Assert.False(consent.IsChecked);
            var endpoint = Assert.IsType<ComboBox>(window.FindName("OnlineEndpointBox"));
            var password = Assert.IsType<PasswordBox>(window.FindName("OnlineKeyBox"));
            password.Password = "synthetic-unsaved-key";
            consent.IsChecked = true;
            endpoint.Text = OnlineSpeechCatalog.Endpoints(OnlineSpeechCatalog.Qwen)[1];
            Assert.False(consent.IsChecked);
            Assert.Empty(password.Password);
            password.Password = CloudLlmTests.TestKey;
            Assert.IsType<Button>(window.FindName("SaveOnlineConnectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(savedConnection);
            Assert.Equal(endpoint.Text, savedConnection.GetProfile().Endpoint);
            Assert.Empty(password.Password);
            Assert.DoesNotContain(CloudLlmTests.TestKey, JsonSerializer.Serialize(savedConnection));
            Assert.Equal(OnlineSpeechCatalog.Endpoints(OnlineSpeechCatalog.Qwen)[0], settings.OnlineSpeech.GetProfile().Endpoint);
            Assert.Empty(handler.Requests);

            rows[0].SetCorrection("An unsaved local correction.");
            rows[1].SetCorrection(rows[1].Text, unrecognized: true);
            consent.IsChecked = true;
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "TranscribeOnlineAsync")));
            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, request => Assert.Equal("dashscope-intl.aliyuncs.com", request.Uri.Host));
            Assert.False(consent.IsChecked);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
            Assert.Equal(["An unsaved local correction.", plan.Segments[1].Text], rows.Select(row => row.Text));
            Assert.Equal(plan.Segments.Select(segment => segment.Text), rows.Select(row => row.LocalText));
            Assert.Equal("Improved online second decision.", rows[1].OnlineText);
            var comparison = OnlineComparisonStore.Load(session, plan)!;
            Assert.NotNull(comparison);
            Assert.Equal(plan.Segments.Select(segment => segment.Text), comparison.Rows.Select(row => row.LocalText));
            Assert.All(comparison.Rows, row => Assert.False(row.LocalUnrecognized));
            Assert.Equal(TranscriptComparisonState.Different, rows[0].ComparisonState);
            Assert.Equal(TranscriptComparisonState.Unclear, rows[1].ComparisonState);
            Assert.IsType<Button>(window.FindName("CompareResultsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(TranscriptComparisonState.Matching, rows[0].ComparisonState);
            Assert.Equal(TranscriptComparisonState.Different, rows[1].ComparisonState);
            Assert.Equal(Visibility.Visible, Assert.IsType<DataGridTextColumn>(window.FindName("OnlineTextColumn")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<DataGridTextColumn>(window.FindName("CorrectedTextColumn")).Visibility);
            Assert.Contains("dashscope-intl.aliyuncs.com", Assert.IsType<TextBlock>(window.FindName("OnlineResultCaption")).Text);
            var differences = Assert.IsType<CheckBox>(window.FindName("OnlyDifferencesCheck"));
            differences.IsChecked = true;
            differences.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(grid.Items.Cast<object>());
            Assert.IsType<Button>(window.FindName("UseOnlineResultsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, grid.Items.Count);
            Assert.Equal(Visibility.Visible, Assert.IsType<DataGridTextColumn>(window.FindName("CorrectedTextColumn")).Visibility);
            Assert.Equal("Improved online second decision.", rows[1].Text);
            Assert.Equal(plan.Segments.Select(segment => segment.Text), rows.Select(row => row.LocalText));
            Assert.All(rows, row => Assert.False(row.Unrecognized));
            Assert.Equal(TranscriptComparisonState.Matching, rows[0].ComparisonState);
            Assert.Equal(TranscriptComparisonState.Different, rows[1].ComparisonState);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
            Assert.Null(window.ResultDocument);
        }
        finally { window.Close(); }
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));

        var cached = OnlineComparisonStore.Load(session, plan)!;
        cached.Rows[0] = cached.Rows[0] with { LocalText = "An old cached local preview.", LocalUnrecognized = true };
        OnlineComparisonStore.Save(session, plan, cached, CancellationToken.None);
        TranscriptCorrectionService.Save(session, plan, ["Newly saved first decision.", plan.Segments[1].Text], CancellationToken.None);
        plan = TranscriptCorrectionTests.Plan(session, 0, 2);
        var afterSave = EditedSessionTests.Hashes(session.Folder);
        var reopened = new TranscriptEditWindow(session, plan, settings, false, (_, _) => { }, online: true, onlineRecognizer: service);
        try
        {
            var rows = Assert.IsType<DataGrid>(reopened.FindName("CorrectionRows")).Items.Cast<TranscriptEditWindow.CorrectionRow>().ToArray();
            Assert.Equal(plan.Segments.Select(segment => segment.Text), rows.Select(row => row.LocalText));
            Assert.Equal(plan.Segments.Select(segment => segment.Text), rows.Select(row => row.Text));
            Assert.All(rows, row =>
            {
                Assert.False(row.Unrecognized);
                Assert.False(row.HasCorrection);
                Assert.Equal(TranscriptComparisonState.NotCompared, row.ComparisonState);
            });
            Call(reopened, "ToggleComparison");
            Assert.Equal(TranscriptComparisonState.Different, rows[0].ComparisonState);
            Assert.Equal(afterSave, EditedSessionTests.Hashes(session.Folder));
            Assert.True(Assert.IsType<Button>(reopened.FindName("CompareResultsButton")).IsEnabled);
            Assert.Contains(UiText.Get("OnlineComparisonLoaded"), Assert.IsType<TextBlock>(reopened.FindName("CorrectionStatus")).Text);
            Assert.Equal(2, handler.Requests.Count);
            File.SetLastWriteTimeUtc(session.WavePath, DateTime.UtcNow.AddSeconds(30));
            Assert.IsType<Button>(reopened.FindName("UseOnlineResultsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(UiText.Get("OnlineComparisonStale"), Assert.IsType<TextBlock>(reopened.FindName("CorrectionStatus")).Text);
            Assert.Equal(plan.Segments.Select(segment => segment.Text),
                Assert.IsType<DataGrid>(reopened.FindName("CorrectionRows")).Items.Cast<TranscriptEditWindow.CorrectionRow>().Select(row => row.Text));
        }
        finally { reopened.Close(); }
    }

    private static void VerifyOnlineComparisonCancellation(string language)
    {
        using var folder = new TestFolder();
        var time = new ProcessingTimingTests.ManualTime();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var settings = OnlineSpeechTests.Settings(OnlineSpeechCatalog.OpenAi, folder.Root);
        settings.UiLanguage = language;
        using var handler = new OnlineSpeechTests.Handler
        {
            Response = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return OnlineSpeechTests.Reply("{}");
            }
        };
        using var client = new HttpClient(handler);
        var window = new TranscriptEditWindow(session, TranscriptCorrectionTests.Plan(session, 0, 2), settings,
            false, (_, _) => { }, online: true, onlineRecognizer: new(new(client), folder.File("work")),
            processingTimeProvider: time);
        var root = Assert.IsType<Grid>(window.Content);
        root.Measure(new Size(1060, 740));
        root.Arrange(new Rect(0, 0, 1060, 740));
        var closed = false;
        window.Closed += (_, _) => closed = true;
        Assert.IsType<CheckBox>(window.FindName("OnlineConsentCheck")).IsChecked = true;
        var pending = Assert.IsAssignableFrom<Task>(Call(window, "TranscribeOnlineAsync"));
        try
        {
            CompleteUiTask(root, handler.Started.Task);
            time.Advance(TimeSpan.FromSeconds(8));
            Invoke(window, "UpdateRecognitionTiming");
            var timing = Assert.IsType<TextBlock>(window.FindName("RecognitionTimingText"));
            Assert.Equal(Visibility.Visible, timing.Visibility);
            Assert.Equal(UiText.Format("ProcessingEstimating", "00:00:08"), timing.Text);
            Assert.False(Assert.IsType<Button>(window.FindName("SaveCorrectionButton")).IsEnabled);
            Assert.False(Assert.IsType<ComboBox>(window.FindName("OnlineProviderBox")).IsEnabled);
            window.Close();
            CompleteUiTask(root, pending);
            Assert.True(closed);
            Assert.Equal(UiText.Format("ProcessingElapsed", "00:00:08"), timing.Text);
            Assert.Single(handler.Requests);
            Assert.Empty(Directory.GetFiles(folder.File("work")));
            Assert.False(Directory.Exists(Path.Combine(session.Folder, "online-comparisons")));
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        }
        finally
        {
            if (!closed)
            {
                window.Close();
                CompleteUiTask(root, pending);
            }
        }
    }
}
