using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyRecordingWorkflow(string language)
    {
        using var folder = new TestFolder();
        var history = EditedSessionTests.MakeSource(folder);
        history.AudioOutput = new() { SampleRate = 44_100, Channels = 1 };
        history.Save();
        var oldAudio = File.ReadAllBytes(history.WavePath);
        var oldTranscript = File.ReadAllBytes(history.TranscriptPath);
        var oldSummary = File.ReadAllBytes(history.SummaryPath);
        var oldMp3 = File.ReadAllBytes(history.Mp3Path);
        var storage = new AppStorage(folder.File("preferences"));
        var settings = new AppSettings
        {
            SaveFolder = folder.Root, UiLanguage = language, MicrophoneEnabled = true, PlaybackEnabled = false,
            MicrophoneId = "mic-a", PlaybackId = "playback-a", Format = "MP3",
            LiveTranscription = false, TranscribeOnStop = false, IdentifySpeakersOnStop = false, SummarizeOnStop = false
        };
        storage.SaveSettings(settings);
        var settingsPath = Path.Combine(folder.Root, "preferences", "settings.json");
        var factory = new RecordingEngineTests.FakeFactory();
        var recorder = new RecordingEngine(factory.Create);
        var approve = false;
        var requests = new List<RecordingSources>();
        var allowContinuation = false;
        var continuationRequests = new List<RecordingSession>();
        var window = new MainWindow(storage, settings, recorder, confirmDeviceChange: sources =>
        {
            requests.Add(sources);
            return approve;
        }, confirmContinuation: session =>
        {
            continuationRequests.Add(session);
            return allowContinuation;
        });
        var root = Assert.IsType<Grid>(window.Content);
        var initialized = typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loadingDevices = typeof(MainWindow).GetField("loadingDevices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Invoke(window, "ApplySettingsToControls");
            var mic = Assert.IsType<ComboBox>(window.FindName("MicDevice"));
            var playback = Assert.IsType<ComboBox>(window.FindName("PlaybackDevice"));
            var micEnabled = Assert.IsType<CheckBox>(window.FindName("MicEnabled"));
            var playbackEnabled = Assert.IsType<CheckBox>(window.FindName("PlaybackEnabled"));
            mic.ItemsSource = new[] { new AudioDevice("mic-a", "Microphone A"), new AudioDevice("mic-b", "Microphone B") };
            playback.ItemsSource = new[] { new AudioDevice("playback-a", "Playback A"),
                new AudioDevice("playback-b", "Playback B"), new AudioDevice("broken", "Unavailable playback") };
            mic.SelectedValue = "mic-a";
            playback.SelectedValue = "playback-a";
            initialized.SetValue(window, true);
            Invoke(window, "RefreshHistory");
            var list = Assert.IsType<ListBox>(window.FindName("HistoryList"));
            list.SelectedItem = list.Items.Cast<RecordingSession>().Single(session => session.Id == history.Id);
            var start = Assert.IsType<Button>(window.FindName("StartButton"));
            var stop = Assert.IsType<Button>(window.FindName("StopButton"));
            var menu = Assert.IsType<MenuItem>(window.FindName("ContinueRecordingMenu"));
            var recover = Assert.IsType<MenuItem>(window.FindName("RecoverSessionMenu"));
            Assert.Equal(UiText.Get("ContinueRecording"), start.Content);
            Assert.True(start.IsEnabled);
            Assert.True(menu.IsEnabled);
            Assert.True(recover.IsEnabled);
            var beforeDecline = EditedSessionTests.Hashes(history.Folder);
            var settingsBeforeDecline = File.ReadAllBytes(settingsPath);
            Click(start);
            Click(menu);
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Empty(factory.Inputs);
            Assert.Equal(2, continuationRequests.Count);
            Assert.All(continuationRequests, session => Assert.Equal(history.Id, session.Id));
            Assert.Equal(beforeDecline, EditedSessionTests.Hashes(history.Folder));
            Assert.Equal(settingsBeforeDecline, File.ReadAllBytes(settingsPath));
            Assert.Equal(UiText.Get("ContinueRecordingCancelled"), Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            allowContinuation = true;
            foreach (var size in new[] { new Size(1440, 900), new Size(640, 360) })
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                Pump(root);
                var position = start.TranslatePoint(new Point(), root);
                Assert.True(position.X >= 0 && position.X + start.ActualWidth <= size.Width);
                Assert.True(start.ActualWidth >= start.DesiredSize.Width - 1);
            }
            using (var interruptedHeader = new FileStream(history.WavePath, FileMode.Open, FileAccess.Write))
                DurableWaveFile.WriteHeader(interruptedHeader, 0);
            Click(start);
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Contains(UiText.Get("ContinueNeedsRecovery"), Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            Click(recover);
            Assert.Equal(oldAudio, File.ReadAllBytes(history.WavePath));
            Call(window, "DismissWarning_Click", window, new RoutedEventArgs());
            Assert.IsType<TextBox>(window.FindName("NotesBox")).Text = "Personal note kept while continuing.";
            Click(start);
            Assert.Equal(RecorderState.Recording, recorder.State);
            Assert.Equal(history.Id, recorder.Session!.Id);
            Assert.False(list.IsEnabled);
            Assert.False(recover.IsEnabled);
            Assert.Empty(requests);
            Assert.True(mic.IsEnabled);
            factory.Inputs[0].EmitTone();

            root.Measure(new Size(1440, 900));
            root.Arrange(new Rect(0, 0, 1440, 900));
            Pump(root);
            var panels = Assert.IsType<Button>(window.FindName("PanelsButton"));
            Assert.Equal(Visibility.Collapsed, panels.Visibility);
            Assert.False(Assert.IsType<MenuItem>(window.FindName("AppSettingsMenu")).IsEnabled);
            Assert.True(Assert.IsType<Button>(window.FindName("AppMenuButton")).IsEnabled);
            Assert.True(Assert.IsType<MenuItem>(window.FindName("AppHelpMenu")).IsEnabled);
            Click(Assert.IsType<Button>(window.FindName("CloseMyNotesButton")));
            Pump(root);
            Assert.False(storage.LoadSettings().MyNotesVisible);
            Assert.Equal("Personal note kept while continuing.", File.ReadAllText(recorder.Session.NotesPath));
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("PersonalNotesCard")).Visibility);
            Assert.Equal(Assert.IsType<Border>(window.FindName("TranscriptCard")).ActualHeight,
                Assert.IsType<Border>(window.FindName("MainPointsCard")).ActualHeight, 1);
            Click(Assert.IsType<Button>(window.FindName("CloseMainPointsButton")));
            Pump(root);
            Assert.False(storage.LoadSettings().MainPointsVisible);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(window.FindName("NotesPane")).Visibility);
            Assert.Equal(Visibility.Visible, panels.Visibility);
            var workspace = Assert.IsType<Grid>(window.FindName("ReadingWorkspace"));
            Assert.Equal(workspace.ActualWidth, Assert.IsType<Border>(window.FindName("TranscriptCard")).ActualWidth, 1);
            foreach (var name in new[] { "MainPointsPanelMenu", "MyNotesPanelMenu" })
            {
                var choice = Assert.IsType<MenuItem>(window.FindName(name));
                Assert.False(choice.IsChecked);
                choice.IsChecked = true;
                Click(choice);
            }
            Pump(root);
            Assert.Equal(Visibility.Collapsed, panels.Visibility);
            Assert.True(storage.LoadSettings().MainPointsVisible);
            Assert.True(storage.LoadSettings().MyNotesVisible);
            Assert.False(storage.LoadSettings().TranscribeOnStop);
            Assert.False(storage.LoadSettings().LiveTranscription);
            Assert.False(storage.LoadSettings().SummarizeOnStop);
            Assert.Equal(RecorderState.Recording, recorder.State);
            Assert.True(factory.Inputs[0].IsRunning);

            var before = File.ReadAllBytes(settingsPath);
            mic.SelectedValue = "mic-b";
            CompleteRecordingUiCommand(window, root);
            Assert.Equal("mic-b", Assert.Single(requests).Microphone?.DeviceId);
            Assert.Equal("mic-a", recorder.Sources.Microphone?.DeviceId);
            Assert.Equal("mic-a", mic.SelectedValue);
            Assert.Equal(before, File.ReadAllBytes(settingsPath));
            Assert.True(Assert.Single(factory.Inputs).IsRunning);
            Assert.Equal(UiText.Get("DeviceChangeCancelled"), Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);

            approve = true;
            var attributes = File.GetAttributes(settingsPath);
            try
            {
                File.SetAttributes(settingsPath, attributes | FileAttributes.ReadOnly);
                mic.SelectedValue = "mic-b";
                CompleteRecordingUiCommand(window, root);
                Assert.Equal("mic-b", recorder.Sources.Microphone?.DeviceId);
                Assert.Equal("mic-b", mic.SelectedValue);
                Assert.Equal("mic-a", storage.LoadSettings().MicrophoneId);
                Assert.Contains(settingsPath, Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            }
            finally { File.SetAttributes(settingsPath, attributes); }
            Assert.Null(window.FindName("ApplyDevicesButton"));
            Assert.True(Assert.IsType<bool>(Call(window, "ExecuteShortcut", System.Windows.Input.Key.D,
                System.Windows.Input.ModifierKeys.Control, false, false)));
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(2, requests.Count);
            Assert.Equal("mic-b", recorder.Sources.Microphone?.DeviceId);
            Assert.Equal("mic-b", storage.LoadSettings().MicrophoneId);
            Assert.False(factory.Inputs[0].IsRunning);
            Assert.True(factory.Inputs[^1].IsRunning);
            playback.SelectedValue = "playback-b";
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(2, requests.Count);
            Assert.Null(recorder.Sources.Playback);
            playbackEnabled.IsChecked = true;
            Click(playbackEnabled);
            Assert.Equal(3, requests.Count);
            Assert.Equal("playback-b", recorder.Sources.Playback?.DeviceId);

            micEnabled.IsChecked = false;
            Click(micEnabled);
            Assert.Equal(4, requests.Count);
            Assert.Null(recorder.Sources.Microphone);
            playbackEnabled.IsChecked = false;
            Click(playbackEnabled);
            Assert.Equal(4, requests.Count);
            Assert.True(playbackEnabled.IsChecked);
            Assert.Contains(UiText.Get("AtLeastOneDevice"), Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            var remaining = factory.Inputs[^1];
            playback.SelectedValue = "broken";
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(5, requests.Count);
            Assert.Equal("playback-b", playback.SelectedValue);
            Assert.Equal("playback-b", recorder.Sources.Playback?.DeviceId);
            Assert.True(remaining.IsRunning);
            Assert.Contains("Test device is unavailable.", Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);

            loadingDevices.SetValue(window, true);
            playback.SelectedValue = "playback-a";
            loadingDevices.SetValue(window, false);
            Invoke(window, "ReflectActiveSources");
            Assert.Equal(5, requests.Count);
            Assert.Equal("playback-b", playback.SelectedValue);
            Click(start);
            Assert.Equal(RecorderState.Paused, recorder.State);
            Assert.Equal(4, continuationRequests.Count);
            var inputCount = factory.Inputs.Count;
            playback.SelectedValue = "playback-a";
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(6, requests.Count);
            Assert.Equal(RecorderState.Paused, recorder.State);
            Assert.Equal(inputCount, factory.Inputs.Count);
            Assert.Equal("playback-a", recorder.Sources.Playback?.DeviceId);
            Click(start);
            Assert.Equal(RecorderState.Recording, recorder.State);
            Assert.Equal(4, continuationRequests.Count);
            Assert.Equal("playback-a", factory.Inputs[^1].Selection.DeviceId);
            factory.Inputs[^1].EmitTone();
            CompleteUiTask(root, Task.Delay(140));
            Click(stop);
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.True(list.IsEnabled);
            Assert.Equal(UiText.Get("ContinueRecording"), start.Content);
            var continued = recorder.Session!;
            Assert.True(continued.Frames > history.Frames);
            Assert.Equal("WAV", continued.RequestedFormat);
            Assert.Equal(44_100, continued.AudioOutput.SampleRate);
            Assert.Equal(1, continued.AudioOutput.Channels);
            var audio = File.ReadAllBytes(continued.WavePath);
            Assert.Equal(oldAudio.AsSpan(DurableWaveFile.HeaderSize).ToArray(),
                audio.AsSpan(DurableWaveFile.HeaderSize, oldAudio.Length - DurableWaveFile.HeaderSize).ToArray());
            Assert.Equal(oldTranscript, File.ReadAllBytes(continued.TranscriptPath));
            Assert.Equal(oldSummary, File.ReadAllBytes(continued.SummaryPath));
            Assert.Equal(oldMp3, File.ReadAllBytes(continued.Mp3Path));
            Assert.Equal("Personal note kept while continuing.", File.ReadAllText(continued.NotesPath));
            using (var output = new WaveFileReader(continued.OutputPath))
            {
                Assert.Equal(44_100, output.WaveFormat.SampleRate);
                Assert.Equal(1, output.WaveFormat.Channels);
                Assert.InRange(output.TotalTime.TotalSeconds, continued.Duration.TotalSeconds - 0.01, continued.Duration.TotalSeconds + 0.01);
            }
            Assert.Single(AppStorage.LoadSessions(folder.Root).Sessions);
            Click(menu);
            Assert.Equal(RecorderState.Recording, recorder.State);
            Assert.Equal(5, continuationRequests.Count);
            Assert.Equal(history.Id, recorder.Session!.Id);
            Assert.Equal(2, recorder.Session.Continuations.Count);
            Click(stop);

            Invoke(window, "ClearWorkspace");
            Invoke(window, "UpdateControls");
            Assert.Equal(UiText.Get("StartRecording"), start.Content);
            Click(start);
            Assert.NotEqual(history.Id, recorder.Session!.Id);
            factory.Inputs[^1].EmitTone();
            CompleteUiTask(root, Task.Delay(100));
            Click(stop);
            Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
            Assert.Equal(6, requests.Count);
            Assert.Equal(5, continuationRequests.Count);
        }
        finally
        {
            CompleteRecordingUiCommand(window, root);
            CompleteUiTask(root, recorder.StopAsync());
            initialized.SetValue(window, false);
            window.Close();
            Pump(root);
        }

        void Click(FrameworkElement control)
        {
            control.RaiseEvent(new RoutedEventArgs(control is MenuItem ? MenuItem.ClickEvent : ButtonBase.ClickEvent));
            CompleteRecordingUiCommand(window, root);
        }
    }

    private static void CompleteRecordingUiCommand(MainWindow window, FrameworkElement root)
    {
        var field = typeof(MainWindow).GetField("recordingCommand", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var clock = Stopwatch.StartNew();
        while (Assert.IsType<bool>(field.GetValue(window)) && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            Pump(root);
            Thread.Sleep(5);
        }
        Assert.False(Assert.IsType<bool>(field.GetValue(window)), "The synthetic recording command did not finish.");
        Pump(root);
    }
}
