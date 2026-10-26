using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Controls;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyWorkspaceRefinement(string language)
    {
        VerifyNativeMaximizeHook();
        VerifyModelSwitchTransactions(language);
        VerifyModelSwitchCancellation(language);
        VerifyModelConfigurationDialog(language);
        VerifyMessageHistoryWindow();
        VerifyMuteChoicesAndOriginalPreview(language);
        VerifyUnlabeledSpeakerWorkspace(language);
    }

    private static void VerifyNativeMaximizeHook()
    {
        var window = new Window
        {
            Width = 900, Height = 600, MinWidth = 640, MinHeight = 420,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false
        };
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 40, ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false
        });
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<WindowSizing.MinMaxInfo>());
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            var source = HwndSource.FromHwnd(handle);
            source.RootVisual = window;
            Assert.False(IsSizingWindowVisible(handle));
            typeof(WindowSizing).GetMethod("Attach", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window]);
            var monitor = new SizingMonitor { Size = Marshal.SizeOf<SizingMonitor>() };
            Assert.True(ReadSizingMonitor(MonitorForSizingWindow(handle, 2), ref monitor));
            Marshal.StructureToPtr(new WindowSizing.MinMaxInfo(), memory, false);
            QuerySizingWindow(handle, 0x0024, 0, memory);
            var bounds = Marshal.PtrToStructure<WindowSizing.MinMaxInfo>(memory);
            Assert.Equal(monitor.Work.Left, monitor.Monitor.Left + bounds.MaxPosition.X);
            Assert.Equal(monitor.Work.Top, monitor.Monitor.Top + bounds.MaxPosition.Y);
            Assert.Equal(monitor.Work.Right, monitor.Monitor.Left + bounds.MaxPosition.X + bounds.MaxSize.X);
            Assert.Equal(monitor.Work.Bottom, monitor.Monitor.Top + bounds.MaxPosition.Y + bounds.MaxSize.Y);
            Assert.True(double.IsPositiveInfinity(window.MaxWidth));
            Assert.True(double.IsPositiveInfinity(window.MaxHeight));
            Assert.False(IsSizingWindowVisible(handle));
        }
        finally
        {
            window.Close();
            Marshal.FreeHGlobal(memory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizingMonitor
    {
        public int Size;
        public WindowSizing.NativeRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern nint MonitorForSizingWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadSizingMonitor(nint monitor, ref SizingMonitor info);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint QuerySizingWindow(nint window, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsSizingWindowVisible(nint window);

    private static object? RefinementField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetRefinementField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static AppSettings RefinementSettings(TestFolder folder, string language)
    {
        var settings = OllamaTests.LocalSettings();
        settings.UiLanguage = language;
        settings.SaveFolder = folder.Root;
        settings.LiveTranscription = settings.TranscribeOnStop = settings.SummarizeOnStop = settings.LiveSummary = false;
        settings.IdentifySpeakersOnStop = false;
        settings.MicrophoneEnabled = true;
        settings.PlaybackEnabled = false;
        settings.MicrophoneId = "synthetic-microphone";
        return settings;
    }

    private static void VerifyModelSwitchTransactions(string language)
    {
        using var folder = new TestFolder();
        var settings = RefinementSettings(folder, language);
        var storage = new AppStorage(folder.File("preferences"));
        storage.SaveSettings(settings);
        var tested = new List<string>();
        var configurations = 0;
        Func<AppSettings, CancellationToken, Task> check = (_, _) => Task.CompletedTask;
        Func<AppSettings, string, AppSettings?> configure = (_, _) => null;
        var window = new MainWindow(storage, settings, new RecordingEngine(new RecordingEngineTests.FakeFactory().Create),
            validateSummarySelection: (candidate, token) =>
            {
                tested.Add(candidate.ActiveSummaryModel);
                return check(candidate, token);
            }, configureSummarySelection: (candidate, error) =>
            {
                configurations++;
                return configure(candidate, error);
            });
        var root = Assert.IsType<Grid>(window.Content);
        try
        {
            Invoke(window, "ApplySettingsToControls");
            SetRefinementField(window, "initialized", true);
            var model = Assert.IsType<ComboBox>(window.FindName("MainModelBox"));
            var provider = Assert.IsType<ComboBox>(window.FindName("MainProviderBox"));
            var originalBytes = File.ReadAllBytes(folder.File("preferences\\settings.json"));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            check = (_, token) => completion.Task.WaitAsync(token);
            Apply("synthetic-next");
            Assert.Equal("unit-model", model.Text);
            Assert.Equal(originalBytes, File.ReadAllBytes(folder.File("preferences\\settings.json")));
            Assert.False(provider.IsEnabled);
            Assert.False(Assert.IsType<MenuItem>(window.FindName("AppSettingsMenu")).IsEnabled);
            Assert.False(Assert.IsType<Button>(window.FindName("ImportAudioButton")).IsEnabled);
            Assert.True(Assert.IsType<Button>(window.FindName("StartButton")).IsEnabled);
            Assert.True(Assert.IsType<Button>(window.FindName("CancelAiButton")).IsEnabled);
            settings.StatusBar.Llm = false;
            completion.SetResult();
            Finish();
            AssertSelection("synthetic-next");
            Assert.False(storage.LoadSettings().StatusBar.Llm);
            Assert.Single(tested);
            Assert.Equal(0, configurations);

            check = (_, _) => Task.FromException(new InvalidOperationException("Synthetic unavailable model"));
            Apply("unavailable");
            Finish();
            AssertSelection("synthetic-next");
            Assert.Equal(1, configurations);
            Assert.Contains("Synthetic unavailable model", storage.Messages.Read(null, 200, CancellationToken.None)
                .Entries.Single(entry => entry.What.Contains("Synthetic unavailable model", StringComparison.Ordinal)).What);

            configure = (candidate, _) =>
            {
                var fixedSettings = SummaryConfigurationService.Copy(candidate);
                fixedSettings.OllamaModel = "synthetic-fixed";
                tested.Add(fixedSettings.OllamaModel);
                return fixedSettings;
            };
            var beforeRepair = tested.Count;
            Apply("");
            Finish();
            AssertSelection("synthetic-fixed");
            Assert.Equal(beforeRepair + 2, tested.Count);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("WarningBanner")).Visibility);

            configure = (_, _) => null;
            provider.SelectedValue = CloudLlmCatalog.Qwen;
            Finish();
            Assert.Equal(SummaryProviders.Ollama, provider.SelectedValue);
            AssertSelection("synthetic-fixed");

            check = (_, _) => Task.CompletedTask;
            var path = folder.File("preferences\\settings.json");
            var attributes = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                Apply("cannot-save");
                Finish();
                AssertSelection("synthetic-fixed");
                Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("WarningBanner")).Visibility);
            }
            finally { File.SetAttributes(path, attributes); }

            var unchanged = SummaryConfigurationService.Copy(storage.LoadSettings());
            unchanged.HistoryVisible = !unchanged.HistoryVisible;
            var requests = tested.Count;
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "ValidateChangedSettingsAsync",
                unchanged, CancellationToken.None)));
            Assert.Equal(requests, tested.Count);
            Assert.True(provider.IsEnabled);
            var notice = new TextBlock();
            root.Children.Add(notice);
            MessageJournal.SetReason(notice, "Synthetic notification");
            notice.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            notice.Text = "Authorization: Bearer synthetic-credential";
            Assert.Contains(storage.Messages.Read(null, 200, CancellationToken.None).Entries,
                entry => entry.Reason == "Synthetic notification" && entry.What.Contains("[redacted]", StringComparison.Ordinal));
            Assert.DoesNotContain("synthetic-credential", File.ReadAllText(storage.Messages.Path));
            notice.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            notice.Text = "Unloaded notification";
            Assert.DoesNotContain("Unloaded notification", File.ReadAllText(storage.Messages.Path));
            root.Children.Remove(notice);
            Assert.Null(MessageJournal.GetReason(Assert.IsType<TextBox>(window.FindName("NotesBox"))));
        }
        finally { window.Close(); }

        void Apply(string value)
        {
            Assert.IsType<ComboBox>(window.FindName("MainModelBox")).Text = value;
            Click(Assert.IsType<Button>(window.FindName("ApplyModelButton")));
        }
        void Finish() => CompleteUiTask(root, Assert.IsAssignableFrom<Task>(RefinementField(window, "modelSelectionTask")));
        void AssertSelection(string value)
        {
            Assert.Equal(value, Assert.IsType<ComboBox>(window.FindName("MainModelBox")).Text);
            Assert.Equal(value, Assert.IsType<AppSettings>(RefinementField(window, "settings")).ActiveSummaryModel);
            Assert.Equal(value, storage.LoadSettings().ActiveSummaryModel);
            Assert.False(Assert.IsType<bool>(RefinementField(window, "selectingModel")));
        }
    }

    private static void VerifyModelSwitchCancellation(string language)
    {
        foreach (var action in new[] { "cancel", "record", "close" })
        {
            using var folder = new TestFolder();
            var settings = RefinementSettings(folder, language);
            var storage = new AppStorage(folder.File("preferences"));
            storage.SaveSettings(settings);
            var factory = new RecordingEngineTests.FakeFactory();
            var recorder = new RecordingEngine(factory.Create);
            CancellationToken testedToken = default;
            var window = new MainWindow(storage, settings, recorder, confirmCloseProcessing: _ => true,
                validateSummarySelection: (_, token) =>
                {
                    testedToken = token;
                    return Task.Delay(Timeout.Infinite, token);
                }, configureSummarySelection: (_, _) => throw new InvalidOperationException("Cancellation must not open setup."));
            var root = Assert.IsType<Grid>(window.Content);
            try
            {
                Invoke(window, "ApplySettingsToControls");
                var mic = Assert.IsType<ComboBox>(window.FindName("MicDevice"));
                mic.ItemsSource = new[] { new AudioDevice("synthetic-microphone", "Synthetic microphone") };
                mic.SelectedValue = "synthetic-microphone";
                SetRefinementField(window, "initialized", true);
                Assert.IsType<ComboBox>(window.FindName("MainModelBox")).Text = "pending-model";
                Click(Assert.IsType<Button>(window.FindName("ApplyModelButton")));
                var pending = Assert.IsAssignableFrom<Task>(RefinementField(window, "modelSelectionTask"));
                Assert.False(pending.IsCompleted);
                if (action == "close")
                {
                    var closing = Assert.IsAssignableFrom<Task<bool>>(Call(window, "PrepareCloseAsync"));
                    CompleteUiTask(root, closing);
                    Assert.True(closing.Result);
                }
                else if (action == "record")
                {
                    Click(Assert.IsType<Button>(window.FindName("StartButton")));
                    CompleteRecordingUiCommand(window, root);
                    Assert.Equal(RecorderState.Recording, recorder.State);
                    Assert.Single(factory.Inputs);
                    Click(Assert.IsType<Button>(window.FindName("StopButton")));
                    CompleteRecordingUiCommand(window, root);
                    Assert.Equal(RecorderState.Idle, recorder.State);
                }
                else
                    Click(Assert.IsType<Button>(window.FindName("CancelAiButton")));
                CompleteUiTask(root, pending);
                Assert.True(testedToken.IsCancellationRequested);
                Assert.Equal("unit-model", storage.LoadSettings().ActiveSummaryModel);
                Assert.Equal("unit-model", Assert.IsType<ComboBox>(window.FindName("MainModelBox")).Text);
            }
            finally { window.Close(); }
        }
    }

    private static void VerifyModelConfigurationDialog(string language)
    {
        using var folder = new TestFolder();
        var settings = RefinementSettings(folder, language);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var dialog = new SettingsWindow(settings, "summary", false, (_, token) =>
        {
            checks++;
            return completion.Task.WaitAsync(token);
        }, summaryOnly: true);
        var root = MountDialog(dialog, new Size(800, 740));
        try
        {
            Assert.Equal(UiText.Get("LlmTestAndSelect"), Assert.IsType<Button>(dialog.FindName("SaveButton")).Content);
            Assert.False(Assert.IsType<ListBox>(dialog.FindName("SettingsCategories")).IsEnabled);
            var pending = dialog.PrepareSaveAsync();
            Assert.False(pending.IsCompleted);
            Assert.False(Assert.IsType<ComboBox>(dialog.FindName("SummaryProviderBox")).IsEnabled);
            Assert.False(Assert.IsType<Button>(dialog.FindName("SaveButton")).IsEnabled);
            completion.SetResult();
            CompleteUiTask(root, pending);
            Assert.True(pending.Result);
            Assert.Equal(1, checks);
            Assert.Null(dialog.DialogResult);
        }
        finally { dialog.Close(); }

        var cancelled = new SettingsWindow(settings, "summary", false,
            (_, token) => Task.Delay(Timeout.Infinite, token), summaryOnly: true);
        var cancelledRoot = MountDialog(cancelled, new Size(800, 740));
        var cancelTask = cancelled.PrepareSaveAsync();
        Assert.False(cancelTask.IsCompleted);
        cancelled.Close();
        CompleteUiTask(cancelledRoot, cancelTask);
        Assert.False(cancelTask.Result);

        var about = new HelpWindow(settings.Shortcuts);
        try
        {
            var credit = Assert.IsType<TextBlock>(about.FindName("AboutCredits")).Text;
            Assert.Contains("Afanti Ma", credit);
            Assert.Contains("AI", credit);
        }
        finally { about.Close(); }
        var preferences = new SettingsWindow(settings);
        try { Assert.Null(MessageJournal.GetReason(Assert.IsType<TextBlock>(preferences.FindName("SpeechVocabularyStatus")))); }
        finally { preferences.Close(); }
    }

    private static void VerifyMessageHistoryWindow()
    {
        using var folder = new TestFolder();
        var history = new MessageHistoryStore(folder.Root);
        for (var index = 0; index < 205; index++)
            history.Add($"Synthetic message {index}", "Synthetic operation");
        var window = new MessageHistoryWindow(null, history);
        var root = MountDialog(window, new Size(700, 520));
        var grid = Assert.IsType<DataGrid>(RefinementField(window, "list"));
        try
        {
            Assert.Same(history, MessageJournal.For(window));
            CompleteUiTask(root, window.ReadMoreAsync());
            Assert.Equal(200, grid.Items.Count);
            Assert.Equal("Synthetic message 204", Assert.IsType<MessageEntry>(grid.Items[0]).What);
            CompleteUiTask(root, window.ReadMoreAsync());
            Assert.Equal(205, grid.Items.Count);
            Assert.False(Assert.IsType<Button>(RefinementField(window, "older")).IsEnabled);
            MessageJournal.Record(window, "Synthetic live message", "Synthetic operation");
            Pump(root);
            Assert.Equal(206, grid.Items.Count);
            grid.SelectedIndex = 0;
            Assert.Contains("Synthetic live message", Assert.IsType<TextBox>(RefinementField(window, "details")).Text);
            history.Add("Queued before clearing", "Synthetic operation");
            history.Clear();
            history.Add("Kept after clearing", "Synthetic operation");
            WaitForRead();
            Assert.Equal("Kept after clearing", Assert.IsType<MessageEntry>(Assert.Single(grid.Items.Cast<object>())).What);
            Assert.DoesNotContain("Queued before clearing", File.ReadAllText(history.Path));

            using (var locked = File.Open(history.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                SetRefinementField(window, "before", null);
                CompleteUiTask(root, window.ReadMoreAsync());
                Assert.Contains(UiText.Format("ErrorDetails", ""),
                    Assert.IsType<TextBlock>(RefinementField(window, "status")).Text);
                Assert.NotNull(history.WriteError);
            }
            history.Add("Retry after unlocking", "Synthetic operation");
            CompleteUiTask(root, window.ReadMoreAsync());
            Assert.Null(history.WriteError);
            Assert.Contains(grid.Items.Cast<MessageEntry>(), entry => entry.Severity == MessageSeverity.Error);
        }
        finally { window.Close(); }
        history.Add("Added after closing", "Synthetic operation");
        Pump(root);
        Assert.DoesNotContain(grid.Items.Cast<MessageEntry>(), entry => entry.What == "Added after closing");

        void WaitForRead()
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                Pump(root);
                Thread.Sleep(5);
            } while (Assert.IsType<bool>(RefinementField(window, "reading")) && elapsed.Elapsed.TotalSeconds < 10);
            Assert.False(Assert.IsType<bool>(RefinementField(window, "reading")));
            Pump(root);
        }
    }

    private static void VerifyMuteChoicesAndOriginalPreview(string language)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var document = AppStorage.LoadTranscript(source);
        var plan = AudioEditPlan.Create(document, source.Frames, [TranscriptPresentation.Rows(document)[1]]);
        var dialog = new EditSessionWindow(source, plan, folder.Root);
        var root = MountDialog(dialog, new Size(680, 660));
        try
        {
            var commit = Assert.IsType<Button>(dialog.FindName("CreateButton"));
            Assert.False(commit.IsEnabled);
            var current = Assert.IsType<RadioButton>(dialog.FindName("EditInPlaceChoice"));
            var copy = Assert.IsType<RadioButton>(dialog.FindName("CreateCopyChoice"));
            Assert.False(current.IsChecked);
            Assert.False(copy.IsChecked);
            current.IsChecked = true;
            Assert.True(commit.IsEnabled);
            Assert.Equal(UiText.Get("ApplyMuteChanges"), commit.Content);
            copy.IsChecked = true;
            Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(dialog.FindName("NewCopyFields")).Visibility);
            var title = Assert.IsType<TextBox>(dialog.FindName("TitleInput"));
            var format = Assert.IsType<ComboBox>(dialog.FindName("FormatBox"));
            title.Text = "Retained copy title";
            format.SelectedIndex = 1;
            current.IsChecked = true;
            copy.IsChecked = true;
            Assert.Equal("Retained copy title", title.Text);
            Assert.Equal(1, format.SelectedIndex);
            foreach (var size in new[] { new Size(680, 660), new Size(540, 520) })
            {
                root.Measure(size); root.Arrange(new Rect(size)); Pump(root);
                Assert.True(Assert.IsType<ListBox>(dialog.FindName("RangesList")).ActualHeight >= 45);
                Assert.True(commit.TranslatePoint(new Point(0, commit.ActualHeight), root).Y <= size.Height);
            }
            Assert.Null(dialog.ResultSession);
            Assert.Single(AppStorage.LoadSessions(folder.Root).Sessions);
        }
        finally { dialog.Close(); }

        var muted = AudioRecoveryChoiceTests.Mute(folder, source, _ => true, false).GetAwaiter().GetResult();
        var recovery = new SilenceRecoveryService().PrepareAsync(folder.Root, muted, CancellationToken.None).GetAwaiter().GetResult();
        var before = EditedSessionTests.Hashes(muted.Folder);
        var outputs = new List<RecordingPlaybackTests.FakePlayer>();
        var playback = new RecordingPlayback(() =>
        {
            var output = new RecordingPlaybackTests.FakePlayer();
            outputs.Add(output);
            return output;
        });
        var restore = new EditSessionWindow(recovery, folder.Root, playback);
        var restoreRoot = MountDialog(restore, new Size(680, 660));
        try
        {
            var rows = Assert.IsType<ListBox>(restore.FindName("RangesList"));
            Assert.Equal(4, rows.Items.Count);
            var item = Assert.IsType<ListBoxItem>(rows.ItemContainerGenerator.ContainerFromIndex(1));
            var play = Descendants<Button>(item).Single(button => Equals(button.Content, UiText.Get("PlayMutedRow")));
            Click(play);
            Assert.Empty(rows.SelectedItems);
            Assert.Equal(0.5, playback.Snapshot!.StartSeconds);
            Assert.Equal(1, playback.Snapshot.EndSeconds);
            Assert.False(Assert.IsType<Button>(restore.FindName("CreateButton")).IsEnabled);
            Click(Assert.IsType<Button>(restore.FindName("MutedReplayPause")));
            Assert.True(playback.Snapshot.IsPaused);
            Assert.IsType<Slider>(restore.FindName("MutedReplayProgress")).Value = 0.75;
            Assert.Equal(0.75, playback.Snapshot.PositionSeconds, 5);
            Call(restore, "MutedReplayStop_Click", restore, new RoutedEventArgs());
            Assert.Null(playback.Snapshot);
            Assert.All(outputs, output => Assert.True(output.Disposed));
            Click(play);
            Assert.NotNull(playback.Snapshot);
        }
        finally { restore.Close(); }
        Pump(restoreRoot);
        Assert.Null(playback.Snapshot);
        Assert.All(outputs, output => Assert.True(output.Disposed));
        Assert.Equal(before, EditedSessionTests.Hashes(muted.Folder));
        using var exclusive = File.Open(recovery.Original.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    private static void VerifyUnlabeledSpeakerWorkspace(string language)
    {
        using var folder = new TestFolder();
        var session = UnlabeledSpeakerTests.MakeSession(folder);
        session.Save();
        var original = AppStorage.LoadTranscript(session);
        var notes = File.ReadAllBytes(session.NotesPath);
        var settings = RefinementSettings(folder, language);
        var speakers = new UnlabeledSpeakerTests.FakeSpeakers();
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings,
            new RecordingEngine(new RecordingEngineTests.FakeFactory().Create), pipeline: new AiPipeline(speakerService: speakers));
        var root = Assert.IsType<Grid>(window.Content);
        try
        {
            Invoke(window, "ApplySettingsToControls");
            SetRefinementField(window, "initialized", true);
            Invoke(window, "RefreshHistory");
            Assert.IsType<ListBox>(window.FindName("HistoryList")).SelectedIndex = 0;
            Call(window, "LoadSessionContent", session);
            var transcript = Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel"));
            Assert.True(transcript.CanRecheckUnlabeled);
            Click(Assert.IsType<MenuItem>(window.FindName("RecheckUnlabeledSpeakersMenu")));
            CompleteUiTask(root, ActiveUiAiTask(window));
            Assert.Equal(1, speakers.MatchCalls);
            Assert.Equal("spkr1", AppStorage.LoadTranscript(session).Segments[2].SpeakerId);
            AppStorage.SaveTranscript(session, original);
            PendingProcessingStore.Defer(session, [ProcessingWork.ForAi("RecheckUnlabeledSpeakers", settings)]);
            Call(window, "StartPendingProcessing", session, PendingProcessingStore.Load(session)!);
            CompleteUiTask(root, ActiveUiAiTask(window));
            Assert.Equal(2, speakers.MatchCalls);
            Assert.Null(PendingProcessingStore.Load(session));
            Assert.Equal(notes, File.ReadAllBytes(session.NotesPath));
            Assert.Null(window.FindName("SpeechStatusText"));
            Assert.Null(window.FindName("SummaryProviderText"));
            Assert.Contains(settings.OllamaModel, Assert.IsType<string>(
                Assert.IsType<TextBlock>(window.FindName("TranscriptTitle")).ToolTip));
            VerifyTranscriptHeader(window);

            var standalone = new TranscriptView { CanRestoreMutedRows = true };
            var shown = 0;
            standalone.ShowMutedRowsRequested += () => shown++;
            Click(Assert.IsType<MenuItem>(standalone.FindName("ShowMutedRowsMenu")));
            Assert.Equal(1, shown);
        }
        finally { window.Close(); }
    }

    private static void VerifyTranscriptHeader(MainWindow window)
    {
        var header = Assert.IsType<DockPanel>(window.FindName("TranscriptHeader"));
        var parent = Assert.IsType<DockPanel>(header.Parent);
        var index = parent.Children.IndexOf(header);
        parent.Children.Remove(header);
        var host = new Grid();
        host.Children.Add(header);
        try
        {
            foreach (var width in new[] { 1100d, 520d, 320d })
            {
                host.Width = width;
                host.Measure(new Size(width, 200)); host.Arrange(new Rect(0, 0, width, 200)); Pump(host);
                var title = Assert.IsType<TextBlock>(window.FindName("TranscriptTitle"));
                var actions = Assert.IsType<WrapPanel>(window.FindName("TranscriptActions"));
                Assert.True(title.TranslatePoint(new Point(title.ActualWidth, 0), header).X <=
                    actions.TranslatePoint(new Point(), header).X);
                Assert.Equal(header.ActualWidth, actions.TranslatePoint(new Point(actions.ActualWidth, 0), header).X, 1);
                Assert.All(actions.Children.OfType<Button>(), button =>
                    Assert.InRange(button.TranslatePoint(new Point(button.ActualWidth, 0), host).X, 0, width + 1));
            }
        }
        finally
        {
            host.Children.Clear();
            parent.Children.Insert(index, header);
        }
    }
}
