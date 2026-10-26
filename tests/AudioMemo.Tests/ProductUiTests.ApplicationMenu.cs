using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using AudioMemo.Audio;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyApplicationMenu(Application application, string language)
    {
        using var folder = new TestFolder();
        var storage = new AppStorage(folder.File("preferences"));
        var settings = new AppSettings
        {
            SaveFolder = folder.File("recordings"), UiLanguage = language,
            MicrophoneEnabled = false, PlaybackEnabled = false,
            LiveTranscription = false, TranscribeOnStop = false, SummarizeOnStop = false
        };
        storage.SaveSettings(settings);
        var factory = new RecordingEngineTests.FakeFactory();
        var recorder = new RecordingEngine(factory.Create);
        var helpWindows = new List<HelpWindow>();
        var warnings = new List<string>();
        var confirmations = 0;
        var window = new MainWindow(storage, settings, recorder, showHelp: helpWindows.Add,
            showRecordingDeviceWarning: warnings.Add, confirmContinuation: _ => { confirmations++; return true; });
        var root = MountDialog(window, new Size(1440, 900));
        var logo = Assert.IsType<Button>(window.FindName("AppMenuButton"));
        var menu = Assert.IsType<ContextMenu>(window.FindName("AppMenu"));
        var record = Item("AppRecordMenu");
        var stop = Item("AppStopMenu");
        var settingsMenu = Item("AppSettingsMenu");
        var helpMenu = Item("AppHelpMenu");
        var history = Item("AppHistoryMenu");
        var points = Item("AppMainPointsMenu");
        var notes = Item("AppMyNotesMenu");
        var initialized = typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Invoke(window, "ApplySettingsToControls");
            initialized.SetValue(window, true);
            Invoke(window, "UpdateControls");
            Assert.Null(window.FindName("SettingsButton"));
            Assert.Same(menu, logo.ContextMenu);
            Assert.Same(Assert.IsType<Image>(window.FindName("BrandLogo")), logo.Content);
            Assert.Equal(UiText.Get("ApplicationMenu"), AutomationProperties.GetName(logo));
            Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(logo));
            Assert.True(logo.Focusable && logo.IsTabStop);
            Assert.Equal(11, menu.Items.OfType<MenuItem>().Count());
            foreach (var name in new[] { "NewButton", "StartButton", "StopButton", "ImportAudioButton", "OpenLibraryButton",
                "PinHistoryButton", "PanelsButton", "CloseMainPointsButton", "CloseMyNotesButton" })
                Assert.IsType<Button>(window.FindName(name));
            foreach (var (name, id) in new[]
            {
                ("AppNewRecordingMenu", "new"), ("AppRecordMenu", "record"), ("AppStopMenu", "stop"),
                ("AppImportMenu", "import"), ("AppOpenFolderMenu", "library"), ("AppHistoryMenu", "history")
            })
            {
                var item = Item(name);
                Assert.Equal(id, item.Tag);
                Assert.Equal(ShortcutInput.Display(settings.Shortcuts.Gesture(id)), item.InputGestureText);
                Assert.Contains(ShortcutActions.Hint(settings.Shortcuts, id), Assert.IsType<string>(item.ToolTip));
            }
            Assert.Same(settingsMenu, Call(window, "ShortcutTarget", "settings"));
            Assert.Same(helpMenu, Call(window, "ShortcutTarget", "help"));
            Assert.Equal(UiText.Get("StartRecording"), record.Header);
            Assert.True(record.IsEnabled && settingsMenu.IsEnabled && helpMenu.IsEnabled);
            Assert.False(stop.IsEnabled);
            Assert.True(history.StaysOpenOnClick && points.StaysOpenOnClick && notes.StaysOpenOnClick);

            var settingsBefore = File.ReadAllBytes(folder.File(@"preferences\settings.json"));
            Click(helpMenu);
            VerifyHelp(Assert.Single(helpWindows), "F1");
            Assert.True(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.F1, ModifierKeys.None, true, false)));
            Assert.Equal(2, helpWindows.Count);
            settings.Shortcuts.Overrides["help"] = "F8";
            settings.Shortcuts.Overrides["settings"] = "Ctrl+Alt+OemComma";
            settings.Shortcuts.Overrides["record"] = "Ctrl+Alt+R";
            settings.Shortcuts.Overrides["recover"] = "";
            settings.Shortcuts.Overrides["import"] = "F9";
            settings.Shortcuts.Validate();
            Invoke(window, "UpdateShortcutHints");
            Assert.Equal("F8", helpMenu.InputGestureText);
            Assert.Equal("Ctrl+Alt+,", settingsMenu.InputGestureText);
            Assert.Equal("Ctrl+Alt+R", record.InputGestureText);
            Assert.Equal("F9", Item("AppImportMenu").InputGestureText);
            Assert.False(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.F1, ModifierKeys.None, false, false)));
            Assert.True(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.F8, ModifierKeys.None, false, false)));
            Assert.Equal(3, helpWindows.Count);
            VerifyHelp(helpWindows[^1], "F8");
            Assert.Equal(settingsBefore, File.ReadAllBytes(folder.File(@"preferences\settings.json")));
            Assert.Empty(factory.Inputs);
            Assert.False(Directory.Exists(settings.SaveFolder));

            Assert.Equal(UiText.Get("HideRecordingList"), history.Header);
            Click(history);
            Assert.False(storage.LoadSettings().HistoryVisible);
            Assert.Equal(UiText.Get("PinRecordingList"), history.Header);
            Click(history);
            Assert.True(storage.LoadSettings().HistoryVisible);
            Toggle(points, false);
            Toggle(notes, false);
            Assert.False(storage.LoadSettings().MainPointsVisible || storage.LoadSettings().MyNotesVisible);
            Assert.Equal(UiText.Get("ShowMainPoints"), points.Header);
            Assert.Equal(UiText.Get("ShowMyNotes"), notes.Header);
            Assert.False(Item("MainPointsPanelMenu").IsChecked || Item("MyNotesPanelMenu").IsChecked);
            Toggle(Item("MainPointsPanelMenu"), true);
            Toggle(Item("MyNotesPanelMenu"), true);
            Assert.True(points.IsChecked && notes.IsChecked);
            Assert.Equal(UiText.Get("HideMainPointsMenu"), points.Header);
            Assert.Equal(UiText.Get("HideMyNotesMenu"), notes.Header);

            var settingsPath = folder.File(@"preferences\settings.json");
            var saved = File.ReadAllBytes(settingsPath);
            var attributes = File.GetAttributes(settingsPath);
            try
            {
                File.SetAttributes(settingsPath, attributes | FileAttributes.ReadOnly);
                Toggle(points, false);
                Toggle(notes, false);
                Click(history);
                Assert.True(points.IsChecked && notes.IsChecked);
                Assert.True(Item("MainPointsPanelMenu").IsChecked && Item("MyNotesPanelMenu").IsChecked);
                Assert.Equal(UiText.Get("HideMainPointsMenu"), points.Header);
                Assert.Equal(UiText.Get("HideMyNotesMenu"), notes.Header);
                Assert.Equal(UiText.Get("HideRecordingList"), history.Header);
                Assert.Equal(saved, File.ReadAllBytes(settingsPath));
                Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("WarningBanner")).Visibility);
            }
            finally { File.SetAttributes(settingsPath, attributes); }

            foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
            {
                AppAppearance.Apply(application, theme);
                Resize(640, 420);
                Assert.Equal(UiText.Get("ShowRecordingList"), history.Header);
                Click(history);
                Assert.Equal(UiText.Get("HideRecordingList"), history.Header);
                Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("HistoryPanel")).Visibility);
                Click(history);
                Assert.Equal(UiText.Get("ShowRecordingList"), history.Header);
                Assert.True(storage.LoadSettings().HistoryVisible);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("HistoryPanel")).Visibility);
                Resize(1440, 900);
                Assert.Equal(UiText.Get("HideRecordingList"), history.Header);
            }

            foreach (var name in new[] { "aiTask", "recordingCommand", "exportBusy", "selectingModel", "closing" })
            {
                var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
                var previous = field.GetValue(window);
                field.SetValue(window, name == "aiTask" ? new TaskCompletionSource().Task : true);
                try
                {
                    Call(window, "AppMenu_Opened", menu, new RoutedEventArgs());
                    Assert.False(settingsMenu.IsEnabled);
                    Assert.Equal(name != "closing", logo.IsEnabled);
                    Assert.Equal(name != "closing", helpMenu.IsEnabled);
                    Assert.Equal(name != "closing", points.IsEnabled && notes.IsEnabled);
                    Assert.Equal(Assert.IsType<Button>(window.FindName("StartButton")).IsEnabled, record.IsEnabled);
                    Assert.Equal(Assert.IsType<Button>(window.FindName("StopButton")).IsEnabled, stop.IsEnabled);
                    Assert.Equal(Assert.IsType<Button>(window.FindName("ImportAudioButton")).IsEnabled, Item("AppImportMenu").IsEnabled);
                    Assert.False(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.OemComma,
                        ModifierKeys.Control | ModifierKeys.Alt, false, false)));
                    var helpCount = helpWindows.Count;
                    Assert.Equal(name != "closing",
                        Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.F8, ModifierKeys.None, false, false)));
                    Assert.Equal(helpCount + (name == "closing" ? 0 : 1), helpWindows.Count);
                }
                finally { field.SetValue(window, previous); Invoke(window, "UpdateControls"); }
            }

            Click(record);
            CompleteRecordingUiCommand(window, root);
            Assert.Single(warnings);
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.False(Directory.Exists(settings.SaveFolder));
            Assert.Equal(UiText.Get("StartRecording"), record.Header);
            var loading = typeof(MainWindow).GetField("loadingDevices", BindingFlags.Instance | BindingFlags.NonPublic)!;
            loading.SetValue(window, true);
            try
            {
                var microphone = Assert.IsType<ComboBox>(window.FindName("MicDevice"));
                microphone.ItemsSource = new[] { new AudioDevice("synthetic-mic", "Synthetic microphone") };
                microphone.SelectedIndex = 0;
                Assert.IsType<CheckBox>(window.FindName("MicEnabled")).IsChecked = true;
            }
            finally { loading.SetValue(window, false); }
            Click(record);
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(RecorderState.Recording, recorder.State);
            Assert.Equal(UiText.Get("Pause"), record.Header);
            Assert.True(stop.IsEnabled && helpMenu.IsEnabled);
            Assert.False(settingsMenu.IsEnabled || Item("AppNewRecordingMenu").IsEnabled || Item("AppImportMenu").IsEnabled);
            factory.Inputs[^1].EmitTone();
            var session = recorder.Session!;
            Assert.IsType<TextBox>(window.FindName("NotesBox")).Text = "Private notes kept when hiding a pane.";
            Toggle(notes, false);
            Assert.Equal("Private notes kept when hiding a pane.", File.ReadAllText(session.NotesPath));
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("PersonalNotesCard")).Visibility);
            Toggle(notes, true);
            Click(helpMenu);
            Assert.Equal(RecorderState.Recording, recorder.State);
            Click(record);
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(RecorderState.Paused, recorder.State);
            Assert.Equal(UiText.Get("Resume"), record.Header);
            Click(record);
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(RecorderState.Recording, recorder.State);
            Click(stop);
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Equal(UiText.Get("ContinueRecording"), record.Header);
            Assert.True(settingsMenu.IsEnabled);
            Click(record);
            CompleteRecordingUiCommand(window, root);
            Assert.Equal(1, confirmations);
            Assert.Equal(session.Id, recorder.Session!.Id);
            Click(stop);
            CompleteRecordingUiCommand(window, root);
            Assert.Single(AppStorage.LoadSessions(settings.SaveFolder).Sessions);
            Assert.All(factory.Inputs, input => Assert.False(input.IsRunning));
        }
        finally
        {
            CompleteRecordingUiCommand(window, root);
            CompleteUiTask(root, recorder.StopAsync());
            initialized.SetValue(window, false);
            foreach (var help in helpWindows)
                help.Close();
            window.Close();
            AppAppearance.Apply(application, AppThemes.Light);
        }

        MenuItem Item(string name) => Assert.IsType<MenuItem>(window.FindName(name));
        void Toggle(MenuItem item, bool visible) { item.IsChecked = visible; Click(item); Pump(root); }
        void Resize(double width, double height)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            Pump(root);
        }
        void VerifyHelp(HelpWindow help, string gesture)
        {
            Assert.Null(help.Owner);
            Assert.Equal(UiText.Get("HelpAndAbout"), help.Title);
            Assert.Null(help.FindName("SaveButton"));
            var close = Assert.IsType<Button>(help.FindName("CloseHelpButton"));
            Assert.True(close.IsCancel && close.IsDefault);
            var credits = Assert.IsType<TextBlock>(help.FindName("AboutCredits")).Text;
            Assert.Contains("Afanti Ma", credits);
            Assert.Contains("AI", credits);
            Assert.Contains(typeof(MainWindow).Assembly.GetName().Version!.ToString(3),
                Assert.IsType<TextBlock>(help.FindName("AboutVersion")).Text);
            Assert.Null(help.FindName("ShortcutHelpGrid"));
            Assert.Equal(gesture, ShortcutInput.Display(help.Shortcuts.Gesture("help")));
            Assert.Equal(UiText.Get("AboutCopyright"), Assert.IsType<TextBlock>(help.FindName("AboutCopyright")).Text);
            Assert.IsType<Button>(help.FindName("UserGuideButton"));
            Assert.IsType<Button>(help.FindName("ShortcutsButton"));
            var helpRoot = MountDialog(help, new Size(700, 540));
            foreach (var size in new[] { new Size(700, 540), new Size(380, 280) })
            {
                helpRoot.Measure(size);
                helpRoot.Arrange(new Rect(size));
                Pump(helpRoot);
                Assert.True(close.TranslatePoint(new Point(close.ActualWidth, close.ActualHeight), helpRoot).Y <= size.Height);
                var scroll = Assert.IsType<ScrollViewer>(help.FindName("HelpDetails"));
                Assert.Equal(Visibility.Collapsed, scroll.ComputedHorizontalScrollBarVisibility);
                Assert.True(size.Height < 300 ? scroll.ScrollableHeight > 0 : scroll.ScrollableHeight <= 1);
            }
        }
    }
}
