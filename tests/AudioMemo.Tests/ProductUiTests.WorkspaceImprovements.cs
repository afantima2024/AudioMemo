using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void Click(FrameworkElement control) =>
        control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : ButtonBase.ClickEvent));

    private static void VerifyWorkspaceImprovements(Application application, string language, Action<string> phase)
    {
        phase("panel combinations and conversation titles");
        VerifyPanelVisibilityAndTitles(language);
        phase("automatic idle-device preferences");
        VerifyAutomaticDevicePreferences(language);
        phase("disabled AI context actions");
        VerifyBusyContextMenus(language);
        phase("replay slider drag and cancellation");
        VerifyReplaySeekingControl();
        phase("re-transcription stop, continue, and discard");
        VerifyResumableCorrectionWindow(application, language);
        phase("partial-row preview and continuation");
        VerifyUnfinishedRowPreview(language);
        phase("neutral and editing dialog colors; stable button sizes");
        VerifySharedDialogAppearance(application, language);
    }

    private static void VerifyPanelVisibilityAndTitles(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var settings = new AppSettings { SaveFolder = folder.Root, UiLanguage = language, HistoryVisible = false };
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings);
        try
        {
            Invoke(window, "ApplySettingsToControls");
            Invoke(window, "UpdateControls");
            Assert.Equal(UiText.Get("MainWindowTitle"), window.Title);
            typeof(MainWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);
            Call(window, "LoadSessionContent", session);
            Invoke(window, "UpdateControls");
            Assert.Equal($"AudioMemo - {session.Title}", window.Title);
            var root = Assert.IsType<Grid>(window.Content);
            var transcript = Assert.IsType<Border>(window.FindName("TranscriptCard"));
            var mainPoints = Assert.IsType<Border>(window.FindName("MainPointsCard"));
            var notes = Assert.IsType<Border>(window.FindName("PersonalNotesCard"));
            var workspace = Assert.IsType<Grid>(window.FindName("ReadingWorkspace"));
            var side = Assert.IsType<Grid>(window.FindName("NotesPane"));
            var noteText = Assert.IsType<TextBox>(window.FindName("NotesBox")).Text;
            foreach (var size in new[] { new Size(1440, 900), new Size(768, 900), new Size(640, 420) })
                foreach (var showMainPoints in new[] { true, false })
                    foreach (var showNotes in new[] { true, false })
                    {
                        settings.MainPointsVisible = showMainPoints;
                        settings.MyNotesVisible = showNotes;
                        Invoke(window, "ApplySettingsToControls");
                        root.Measure(size);
                        root.Arrange(new Rect(size));
                        Pump(root);
                        var choices = Assert.IsType<ComboBox>(window.FindName("CompactPaneBox"));
                        Assert.Equal(1 + (showMainPoints ? 1 : 0) + (showNotes ? 1 : 0), choices.Items.Count);
                        Assert.Equal(showMainPoints, choices.Items.Cast<LanguageOption>().Any(item => item.Code == "summary"));
                        Assert.Equal(showNotes, choices.Items.Cast<LanguageOption>().Any(item => item.Code == "notes"));
                        var compact = Assert.IsType<DockPanel>(window.FindName("CompactPanePicker")).Visibility == Visibility.Visible;
                        Assert.Equal(!compact && showMainPoints && showNotes ? Visibility.Visible : Visibility.Collapsed,
                            Assert.IsType<GridSplitter>(window.FindName("NotesDivider")).Visibility);
                        if (!showMainPoints && !showNotes)
                        {
                            Assert.Equal(Visibility.Collapsed, side.Visibility);
                            Assert.Equal(Visibility.Collapsed, Assert.IsType<GridSplitter>(window.FindName("ReadingDivider")).Visibility);
                            Assert.Equal(workspace.ActualWidth, transcript.ActualWidth, 1);
                            Assert.Equal(workspace.ActualHeight, transcript.ActualHeight, 1);
                            Assert.Equal(0, workspace.ColumnDefinitions[1].ActualWidth);
                            Assert.Equal(0, workspace.RowDefinitions[1].ActualHeight);
                        }
                        else if (size.Width == 1440 && showMainPoints != showNotes)
                        {
                            var visible = showMainPoints ? mainPoints : notes;
                            Assert.Equal(transcript.ActualHeight, visible.ActualHeight, 1);
                            Assert.Equal(side.ActualHeight, visible.ActualHeight, 1);
                            Assert.Equal(side.ActualWidth, visible.ActualWidth, 1);
                        }
                        Call(window, "ShowWorkspacePane", "summary", false);
                        Pump(root);
                        Assert.Equal(showMainPoints, Assert.IsType<bool>(Call(window, "ExecuteShortcut",
                            Key.D2, ModifierKeys.Alt, false, false)));
                        if (!showMainPoints)
                            Assert.Equal(Visibility.Collapsed, mainPoints.Visibility);
                        Call(window, "ShowWorkspacePane", "notes", false);
                        Pump(root);
                        Assert.Equal(showNotes, Assert.IsType<bool>(Call(window, "ExecuteShortcut",
                            Key.D3, ModifierKeys.Alt, false, false)));
                        if (!showNotes)
                            Assert.Equal(Visibility.Collapsed, notes.Visibility);
                        Call(window, "ShowWorkspacePane", "transcript", false);
                        Assert.Equal(noteText, Assert.IsType<TextBox>(window.FindName("NotesBox")).Text);
                    }
            session.Title = "Renamed conversation";
            Invoke(window, "UpdateWorkspaceTitle");
            Assert.Equal("AudioMemo - Renamed conversation", window.Title);
            Invoke(window, "ClearWorkspace");
            Assert.Equal(UiText.Get("MainWindowTitle"), window.Title);
            typeof(MainWindow).GetField("pendingTitle", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, "Pending conversation");
            Invoke(window, "UpdateControls");
            Assert.Equal("AudioMemo - Pending conversation", window.Title);
            Assert.Equal("Pending conversation", Assert.IsType<TextBlock>(window.FindName("WorkspaceTitle")).Text);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));

            var prefix = Assert.IsType<string>(typeof(MainWindow).GetMethod("NewConversationPrefix",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [new DateTime(2026, 10, 21, 9, 4, 53)]));
            Assert.Equal("2026-10-21 090453 - ", prefix);
            Assert.Equal(-1, prefix.IndexOfAny(Path.GetInvalidFileNameChars()));
            var prompt = new TextInputWindow(null, UiText.Get("NameNewConversation"), prefix, 160, true, false);
            try
            {
                var input = Assert.IsType<StackPanel>(prompt.Content).Children.OfType<TextBox>().Single();
                Assert.Equal(prefix, input.Text);
                Assert.Equal(prefix.Length, input.CaretIndex);
                Assert.Equal(0, input.SelectionLength);
                input.SelectedText = "Meeting";
                Assert.Equal(prefix + "Meeting", prompt.Value);
            }
            finally { prompt.Close(); }
        }
        finally { window.Close(); }
    }

    private static void VerifyAutomaticDevicePreferences(string language)
    {
        using var folder = new TestFolder();
        var storage = new AppStorage(folder.File("preferences"));
        var settings = new AppSettings
        {
            SaveFolder = folder.Root, UiLanguage = language, MicrophoneId = "mic-a",
            PlaybackId = "playback-a", MicrophoneEnabled = true, PlaybackEnabled = false
        };
        storage.SaveSettings(settings);
        var confirmations = 0;
        var window = new MainWindow(storage, settings, new RecordingEngine(new RecordingEngineTests.FakeFactory().Create),
            confirmDeviceChange: _ => { confirmations++; return false; });
        var root = Assert.IsType<Grid>(window.Content);
        try
        {
            Invoke(window, "ApplySettingsToControls");
            var mic = Assert.IsType<ComboBox>(window.FindName("MicDevice"));
            var playback = Assert.IsType<ComboBox>(window.FindName("PlaybackDevice"));
            mic.ItemsSource = new[] { new AudioDevice("mic-a", "Mic A"), new AudioDevice("mic-b", "Mic B") };
            playback.ItemsSource = new[] { new AudioDevice("playback-a", "Playback A"), new AudioDevice("playback-b", "Playback B") };
            mic.SelectedValue = "mic-a";
            playback.SelectedValue = "playback-a";
            typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Invoke(window, "UpdateControls");
            Assert.Null(window.FindName("ApplyDevicesButton"));
            mic.SelectedValue = "mic-b";
            CompleteRecordingUiCommand(window, root);
            Assert.Equal("mic-b", storage.LoadSettings().MicrophoneId);
            playback.SelectedValue = "playback-b";
            CompleteRecordingUiCommand(window, root);
            Assert.Equal("playback-b", storage.LoadSettings().PlaybackId);
            Assert.Equal(0, confirmations);
            var enabled = Assert.IsType<CheckBox>(window.FindName("MicEnabled"));
            enabled.IsChecked = false;
            Click(enabled);
            CompleteRecordingUiCommand(window, root);
            Assert.True(enabled.IsChecked);
            Assert.True(storage.LoadSettings().MicrophoneEnabled);
            Assert.Contains(UiText.Get("AtLeastOneDevice"), Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            var path = folder.File("preferences\\settings.json");
            var attributes = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                mic.SelectedValue = "mic-a";
                CompleteRecordingUiCommand(window, root);
                Assert.Equal("mic-b", mic.SelectedValue);
                Assert.Equal("mic-b", storage.LoadSettings().MicrophoneId);
                Assert.Contains(path, Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            }
            finally { File.SetAttributes(path, attributes); }
            mic.SelectedValue = "mic-a";
            CompleteRecordingUiCommand(window, root);
            Assert.Equal("mic-a", storage.LoadSettings().MicrophoneId);
            Assert.Equal(0, confirmations);
        }
        finally { window.Close(); }
    }

    private static void VerifyBusyContextMenus(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var window = new MainWindow(new AppStorage(folder.File("preferences")),
            new() { SaveFolder = folder.Root, UiLanguage = language });
        var root = Assert.IsType<Grid>(window.Content);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progressReady = new TaskCompletionSource<IProgress<AiUpdate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? pending = null;
        try
        {
            typeof(MainWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);
            Call(window, "LoadSessionContent", session);
            Invoke(window, "UpdateControls");
            var view = Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel"));
            var grid = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
            grid.SelectedIndex = 0;
            var speakerActions = new[] { "RenameSpeakerMenu", "SpeakerColorMenu" }
                .Select(name => Assert.IsType<MenuItem>(view.FindName(name))).ToArray();
            Assert.All(speakerActions, item => Assert.True(item.IsEnabled));
            var speakerRequests = 0;
            view.RenameSpeakerRequested += _ => speakerRequests++;
            view.SpeakerColorRequested += _ => speakerRequests++;
            Call(window, "StartAiJob", session, "UpdateMainPoints",
                new Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task>((_, progress, _) =>
                {
                    progressReady.TrySetResult(progress);
                    return hold.Task;
                }));
            pending = ActiveUiAiTask(window);
            CompleteUiTask(root, progressReady.Task);
            Pump(root);
            Assert.False(view.CanCorrect);
            Assert.False(view.CanEdit);
            Assert.True(view.CanReplay);
            Assert.False(view.CanChangeSpeakers);
            Assert.True(view.CanSetSpeakerColors);
            Assert.False(speakerActions[0].IsEnabled);
            Assert.Equal(0.45, speakerActions[0].Opacity, 2);
            Click(speakerActions[0]);
            Assert.True(speakerActions[1].IsEnabled);
            Assert.Equal(1, speakerActions[1].Opacity, 2);
            Assert.Equal(0, speakerRequests);
            var stale = AppStorage.LoadTranscript(session);
            Call(window, "ApplySpeakerColor", session, "spkr1", "#112233", stale.Revision);
            progressReady.Task.Result.Report(new(session.Id, "Synthetic queued AI update.", Document: stale.Copy()));
            Pump(root);
            var shown = Assert.IsType<TranscriptDocument>(typeof(MainWindow)
                .GetField("currentTranscript", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Equal("#112233", shown.SpeakerColor("spkr1"));
            Call(window, "ApplySpeakerColor", session, "spkr1", null, stale.Revision);
            progressReady.Task.Result.Report(new(session.Id, "Synthetic queued AI update.", Document: shown.Copy()));
            Pump(root);
            shown = Assert.IsType<TranscriptDocument>(typeof(MainWindow)
                .GetField("currentTranscript", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Null(shown.SpeakerColor("spkr1"));
            Assert.Null(AppStorage.LoadTranscript(session).SpeakerColor("spkr1"));
            Assert.True(view.CanSetSpeakerColors);
            Assert.False(view.CanChangeSpeakers);
            typeof(MainWindow).GetField("currentTranscript", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, new TranscriptDocument());
            view.SetDocument(new(), reset: true);
            Invoke(window, "UpdateControls");
            Assert.False(view.CanSetSpeakerColors);
            progressReady.Task.Result.Report(new(session.Id, "Synthetic first transcript rows.", Document: shown.Copy()));
            Pump(root);
            Assert.True(view.CanSetSpeakerColors);
            Assert.False(view.CanChangeSpeakers);
            grid.SelectedIndex = 0;
            foreach (var name in new[] { "RenameSessionMenu", "DeleteSessionMenu", "CopySessionMenu", "ExportTranscriptMenu",
                "TranscribeSessionMenu", "IdentifySpeakersMenu", "SummarizeSessionMenu", "ExportMp3Menu",
                "RecoverSessionMenu", "RestoreSilenceMenu", "ContinueRecordingMenu" })
                Assert.False(Assert.IsType<MenuItem>(window.FindName(name)).IsEnabled, name);
            Assert.True(Assert.IsType<MenuItem>(window.FindName("OpenSessionMenu")).IsEnabled);
            Assert.True(Assert.IsType<MenuItem>(view.FindName("CopySelectedMenu")).IsEnabled);
            Assert.True(ContextMenuService.GetShowOnDisabled(Assert.IsType<ListBox>(window.FindName("HistoryList"))));
            hold.SetResult();
            CompleteUiTask(root, pending);
            Assert.All(speakerActions, item => Assert.True(item.IsEnabled));
            Assert.True(view.CanCorrect);
        }
        finally
        {
            hold.TrySetResult();
            if (pending is not null)
                CompleteUiTask(root, pending);
            window.Close();
        }
    }

    private static void VerifyReplaySeekingControl()
    {
        var view = new TranscriptView { CanReplay = true };
        var root = new Grid();
        root.Children.Add(view);
        root.Measure(new Size(800, 500));
        root.Arrange(new Rect(0, 0, 800, 500));
        var requests = new List<double>();
        view.ReplaySeekRequested += requests.Add;
        view.SetPlayback(new(10, 20, 12, false));
        var slider = Assert.IsType<Slider>(view.FindName("ReplayProgress"));
        Assert.True(slider.IsMoveToPointEnabled);
        Assert.Equal(10, slider.Minimum);
        Assert.Equal(20, slider.Maximum);
        Assert.Equal(12, slider.Value);
        Assert.Empty(requests);
        slider.Value = 14;
        Assert.Equal([14d], requests);
        slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        slider.Value = 18;
        view.SetPlayback(new(10, 20, 15.5, false));
        Assert.Equal(18, slider.Value);
        Assert.Single(requests);
        slider.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert.Equal([14d, 18d], requests);
        view.SetPlayback(new(10, 20, 18, true));
        slider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        slider.Value = 19;
        slider.RaiseEvent(new DragCompletedEventArgs(0, 0, true) { RoutedEvent = Thumb.DragCompletedEvent });
        Assert.Equal(18, slider.Value);
        Assert.Equal(2, requests.Count);
        view.SetPlayback(null);
        Assert.Equal(Visibility.Collapsed, Assert.IsAssignableFrom<FrameworkElement>(view.FindName("ReplayTransport")).Visibility);
    }
}
