using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioMemo.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyRecordingDeviceWarnings(string language)
    {
        using var folder = new TestFolder();
        var storage = new AppStorage(folder.File("preferences"));
        var settings = new AppSettings
        {
            SaveFolder = folder.File("recordings"), UiLanguage = language,
            LiveTranscription = false, TranscribeOnStop = false, SummarizeOnStop = false
        };
        storage.SaveSettings(settings);
        var factory = new RecordingEngineTests.FakeFactory();
        var failResume = false;
        var recorder = new RecordingEngine((selection, loopback, packet, error) =>
        {
            var input = Assert.IsType<RecordingEngineTests.FakeInput>(factory.Create(selection, loopback, packet, error));
            if (selection.DeviceId == "not-running")
                input.OnStart = input.RequestStop;
            else if (failResume)
                input.OnStart = input.Fail;
            return input;
        });
        var warnings = new List<string>();
        var confirmations = 0;
        var window = new MainWindow(storage, settings, recorder,
            confirmContinuation: _ => { confirmations++; return true; },
            showRecordingDeviceWarning: warnings.Add);
        var root = MountDialog(window, new Size(1080, 720));
        var initialized = typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var loadingDevices = typeof(MainWindow).GetField("loadingDevices", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pendingTitle = typeof(MainWindow).GetField("pendingTitle", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var mic = Assert.IsType<ComboBox>(window.FindName("MicDevice"));
        var playback = Assert.IsType<ComboBox>(window.FindName("PlaybackDevice"));
        var micEnabled = Assert.IsType<CheckBox>(window.FindName("MicEnabled"));
        var playbackEnabled = Assert.IsType<CheckBox>(window.FindName("PlaybackEnabled"));
        var start = Assert.IsType<Button>(window.FindName("StartButton"));
        var stop = Assert.IsType<Button>(window.FindName("StopButton"));
        var history = Assert.IsType<ListBox>(window.FindName("HistoryList"));
        var pendingName = "Keep this title after correcting the devices";
        try
        {
            Call(window, "ApplySettingsToControls");
            initialized.SetValue(window, true);
            pendingTitle.SetValue(window, pendingName);
            Call(window, "RefreshHistory");
            Configure(false, null, false, null);
            ClickStart();
            VerifyRejected(UiText.Get("AtLeastOneDevice"));
            Assert.Empty(factory.Inputs);

            Configure(true, null, false, null);
            ClickStart();
            VerifyRejected(UiText.Format("ChooseWorkingDevice", UiText.Get("Microphone")));
            Assert.Empty(factory.Inputs);

            Configure(true, new("broken", UiText.Get("UnavailableDevice")), false, null);
            ClickStart();
            VerifyRejected("Test device is unavailable.");
            Assert.All(factory.Inputs, input => Assert.False(input.IsRunning));

            Configure(true, new("broken", "Missing microphone"), true, new("broken", "Missing playback"));
            Assert.True(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.R, ModifierKeys.Control, false, false)));
            CompleteRecordingUiCommand(window, root);
            VerifyRejected("Missing microphone");
            Assert.Contains("Missing playback", warnings[^1]);

            Configure(false, null, true, new("not-running", "Playback stopped at startup"));
            ClickStart();
            VerifyRejected(UiText.Get("DeviceStoppedDuringStart"));
            Assert.Equal(5, warnings.Count);
            Assert.Equal(0, confirmations);
            Assert.Equal(pendingName, pendingTitle.GetValue(window));

            Configure(false, null, true, new("working-playback", "Working playback"));
            ClickStart();
            Assert.Equal(RecorderState.Recording, recorder.State);
            var session = Assert.IsType<RecordingSession>(recorder.Session);
            Assert.Equal(pendingName, session.Title);
            Assert.Single(history.Items);
            Assert.Single(AppStorage.LoadSessions(settings.SaveFolder).Sessions);
            Assert.Equal(5, warnings.Count);

            ClickStart();
            Assert.Equal(RecorderState.Paused, recorder.State);
            var paused = RecordingStartupTests.PausedFileHashes(session);
            var pausedFrames = recorder.Progress.Frames;
            failResume = true;
            ClickStart();
            Assert.Equal(RecorderState.Paused, recorder.State);
            Assert.Equal(pausedFrames, recorder.Progress.Frames);
            Assert.Equal(paused, RecordingStartupTests.PausedFileHashes(session));
            Assert.Single(history.Items);
            Assert.Equal(6, warnings.Count);
            Assert.Equal(UiText.Get("Resume"), start.Content);
            failResume = false;
            ClickStart();
            Assert.Equal(RecorderState.Recording, recorder.State);
            Click(stop);
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(RecorderState.Idle, recorder.State);

            var beforeContinuation = EditedSessionTests.Hashes(session.Folder);
            Configure(true, new("broken", "Missing microphone"), false, null);
            Click(Assert.IsType<MenuItem>(window.FindName("ContinueRecordingMenu")));
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(1, confirmations);
            Assert.Equal(7, warnings.Count);
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Same(session, recorder.Session);
            Assert.Equal(beforeContinuation, EditedSessionTests.Hashes(session.Folder));
            Assert.Single(history.Items);
            Assert.Equal(session.Id, Assert.IsType<RecordingSession>(history.SelectedItem).Id);
            Assert.Equal(session.Id, Assert.Single(AppStorage.LoadSessions(settings.SaveFolder).Sessions).Id);
            Assert.All(factory.Inputs, input => Assert.False(input.IsRunning));
        }
        finally
        {
            CompleteRecordingUiCommand(window, root);
            CompleteUiTask(root, recorder.StopAsync());
            initialized.SetValue(window, false);
            window.Close();
        }

        void Configure(bool useMic, AudioDevice? microphone, bool usePlayback, AudioDevice? output)
        {
            loadingDevices.SetValue(window, true);
            try
            {
                micEnabled.IsChecked = useMic;
                playbackEnabled.IsChecked = usePlayback;
                mic.ItemsSource = microphone is null ? Array.Empty<AudioDevice>() : new[] { microphone };
                playback.ItemsSource = output is null ? Array.Empty<AudioDevice>() : new[] { output };
                mic.SelectedItem = microphone;
                playback.SelectedItem = output;
            }
            finally { loadingDevices.SetValue(window, false); }
            Call(window, "UpdateControls");
        }

        void ClickStart()
        {
            Assert.True(start.IsEnabled);
            Click(start);
            CompleteRecordingUiCommand(window, root);
        }

        void VerifyRejected(string reason)
        {
            Assert.Contains(reason, warnings[^1]);
            Assert.Contains(UiText.Format("RecordingDeviceSetupWarning", ""), warnings[^1]);
            Assert.Equal(Visibility.Visible, Assert.IsAssignableFrom<FrameworkElement>(window.FindName("WarningBanner")).Visibility);
            Assert.Contains(reason, Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Null(recorder.Session);
            Assert.False(Directory.Exists(settings.SaveFolder));
            Assert.Empty(history.Items);
            Assert.Empty(AppStorage.LoadSessions(settings.SaveFolder).Sessions);
            Assert.True(start.IsEnabled);
            Assert.False(stop.IsEnabled);
        }
    }
}
