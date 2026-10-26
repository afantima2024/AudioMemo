using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioMemo.AI;
using AudioMemo.Controls;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static object? Call(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void VerifyPolishedWorkspace(string language)
    {
        VerifyMainSpeechLanguage(language);
        using var folder = new TestFolder();
        var settings = new AppSettings { UiLanguage = language, SaveFolder = folder.Root, HistoryVisible = false };
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings);
        try
        {
            Invoke(window, "ApplySettingsToControls");
            Invoke(window, "UpdateControls");
            Invoke(window, "UpdateRecordingDisplay");
            Invoke(window, "UpdateModelStatus");
            var root = Assert.IsType<Grid>(window.Content);
            foreach (var size in new[] { new Size(1440, 900), new Size(1280, 720), new Size(1024, 768), new Size(960, 540), new Size(768, 512), new Size(640, 480), new Size(640, 420), new Size(640, 360), new Size(1440, 900) })
            {
                root.Measure(size);
                root.Arrange(new Rect(size));
                Pump(root);
                foreach (var name in new[] { "NewButton", "StartButton", "StopButton", "AppMenuButton", "MainSpeechLanguageBox", "MicDevice", "PlaybackDevice", "RefreshDevicesButton", "ApplyModelButton" })
                {
                    var control = Assert.IsAssignableFrom<FrameworkElement>(window.FindName(name));
                    var position = control.TranslatePoint(new Point(), root);
                    Assert.True(control.ActualWidth > 20 && control.ActualHeight >= 24, $"{name} is too small at {size}.");
                    Assert.True(position.X >= -1 && position.Y >= -1 && position.X + control.ActualWidth <= size.Width + 1 &&
                        position.Y + control.ActualHeight <= size.Height + 1, $"{name} is outside the {size} workspace: {position}, {control.RenderSize}.");
                }
                var languageBox = Assert.IsType<ComboBox>(window.FindName("MainSpeechLanguageBox"));
                var menuButton = Assert.IsType<Button>(window.FindName("AppMenuButton"));
                Assert.True(languageBox.TranslatePoint(new Point(languageBox.ActualWidth, 0), root).X <=
                    Assert.IsType<StackPanel>(window.FindName("WindowButtons")).TranslatePoint(new Point(), root).X);
                Assert.InRange(Math.Abs(languageBox.TranslatePoint(new Point(0, languageBox.ActualHeight / 2), root).Y -
                    menuButton.TranslatePoint(new Point(0, menuButton.ActualHeight / 2), root).Y), 0, 1);
                var commands = Assert.IsType<WrapPanel>(window.FindName("CaptureCommands"));
                var content = Assert.IsType<Grid>(window.FindName("WorkspaceContent"));
                Assert.Equal(content.ActualWidth < 950 ? 1 : 0, Grid.GetRow(commands));
                var scroller = Assert.IsType<ScrollViewer>(window.FindName("WorkspaceScroller"));
                Assert.Equal(scroller.ViewportHeight < 360 ? Visibility.Visible : Visibility.Collapsed, scroller.ComputedVerticalScrollBarVisibility);
                Assert.Equal(Visibility.Collapsed, scroller.ComputedHorizontalScrollBarVisibility);
                var transcript = Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel"));
                var grid = Assert.IsType<DataGrid>(transcript.FindName("RowsGrid"));
                Assert.True(grid.ActualHeight >= 40,
                    $"Transcript has no usable rows at {size}: grid={grid.RenderSize}, view={transcript.RenderSize}, content={content.RenderSize}.");
                scroller.ScrollToEnd();
                Pump(root);
                var visibleGrid = grid.TransformToAncestor(scroller).TransformBounds(new Rect(grid.RenderSize));
                Assert.True(visibleGrid.Top >= -1 && visibleGrid.Bottom <= scroller.ViewportHeight + 1,
                    $"Transcript cannot be fully brought into view at {size}: {visibleGrid}, viewport={scroller.ViewportHeight}.");
                scroller.ScrollToTop();
                Pump(root);
                var compact = Assert.IsType<DockPanel>(window.FindName("CompactPanePicker")).Visibility == Visibility.Visible;
                if (compact)
                {
                    Call(window, "ShowWorkspacePane", "summary", false);
                    Pump(root);
                }
                Assert.True(Assert.IsType<TextBox>(window.FindName("SummaryBox")).ActualHeight >= 24, $"Main points are clipped at {size}.");
                if (compact)
                {
                    Call(window, "ShowWorkspacePane", "notes", false);
                    Pump(root);
                }
                Assert.True(Assert.IsType<TextBox>(window.FindName("NotesBox")).ActualHeight >= 24, $"Notes are clipped at {size}.");
                Call(window, "ShowWorkspacePane", "transcript", false);
                Pump(root);
                Assert.Null(window.FindName("SummaryProviderText"));
            }
            var tab = Assert.IsType<Button>(window.FindName("HistoryPeekTab"));
            var fresh = Assert.IsType<Button>(window.FindName("NewButton"));
            Assert.Equal(30, tab.ActualHeight);
            Assert.Equal(tab.ActualHeight, fresh.ActualHeight, 1);
            Assert.Equal(0, Assert.IsType<ColumnDefinition>(window.FindName("HistoryColumn")).Width.Value);
            Assert.True(fresh.TranslatePoint(new Point(), root).X >= tab.ActualWidth);
            settings.HistoryVisible = true;
            root.Measure(new Size(640, 480));
            root.Arrange(new Rect(0, 0, 640, 480));
            Pump(root);
            Assert.True(settings.HistoryVisible);
            Assert.Equal(0, Assert.IsType<ColumnDefinition>(window.FindName("HistoryColumn")).ActualWidth);
            root.Measure(new Size(1440, 900));
            root.Arrange(new Rect(0, 0, 1440, 900));
            Pump(root);
            Assert.Equal(210, Assert.IsType<ColumnDefinition>(window.FindName("HistoryColumn")).ActualWidth);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<DockPanel>(window.FindName("CompactPanePicker")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("TranscriptCard")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("MainPointsCard")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("PersonalNotesCard")).Visibility);
            settings.HistoryVisible = false;
            Invoke(window, "UpdateHistoryVisibility");
            Pump(root);

            Call(window, "SetStatus", "One synthetic error", true, "SyntheticTest");
            Assert.Equal("One synthetic error", Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            Assert.Empty(Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            Invoke(window, "UpdateRecordingDisplay");
            Assert.Empty(Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            VerifyWarningRecovery(window, root);

            VerifyShortcutEditorAndRouting(window, settings);
            VerifySpeakerCorrectionDialog();
            VerifyMatchingRowFilter(Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel")), root);
            VerifyDisplayBounds();
        }
        finally { window.Close(); }
    }

    private static void VerifyMainSpeechLanguage(string language)
    {
        using var folder = new TestFolder();
        var preferences = Path.Combine(folder.Root, "preferences");
        var storage = new AppStorage(preferences);
        var original = new AppSettings { SaveFolder = Path.Combine(folder.Root, "recordings"), UiLanguage = language };
        storage.SaveSettings(original);
        var path = Path.Combine(preferences, "settings.json");
        var window = new MainWindow(storage, original);
        var root = Assert.IsType<Grid>(window.Content);
        var initializedField = typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var settingsField = typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? running = null;
        try
        {
            var before = File.ReadAllBytes(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
            var savedAt = File.GetLastWriteTimeUtc(path);
            Invoke(window, "ApplySettingsToControls");
            Invoke(window, "UpdateControls");
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(savedAt, File.GetLastWriteTimeUtc(path));
            var box = Assert.IsType<ComboBox>(window.FindName("MainSpeechLanguageBox"));
            Assert.False(box.IsEditable);
            Assert.Equal(["auto", "zh", "en"], box.Items.Cast<LanguageOption>().Select(option => option.Code));
            Assert.Equal("auto", box.SelectedValue);
            Assert.False(box.IsEnabled);
            initializedField.SetValue(window, true);
            Invoke(window, "UpdateControls");
            Assert.True(box.IsEnabled);
            box.SelectedValue = "zh";
            Assert.Equal("zh", storage.LoadSettings().Language);
            Assert.Equal(language, storage.LoadSettings().UiLanguage);
            Assert.Equal("auto", original.Language);
            Assert.Null(typeof(MainWindow).GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Contains(UiText.Get("SpeechLanguageChineseShort"), Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            var dialog = new SettingsWindow(storage.LoadSettings(), "speech");
            try { Assert.Equal("zh", Assert.IsType<ComboBox>(dialog.FindName("LanguageBox")).SelectedValue); }
            finally { dialog.Close(); }

            string? usedLanguage = null;
            Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> job = (snapshot, _, _) =>
            {
                usedLanguage = snapshot.Language;
                return pending.Task;
            };
            Call(window, "StartAiJob", new RecordingSession(), "TranscribeResume", job);
            running = ActiveUiAiTask(window);
            Assert.Equal("zh", usedLanguage);
            Assert.False(box.IsEnabled);
            box.SelectedValue = "en";
            Assert.Equal("zh", box.SelectedValue);
            Assert.Equal("zh", storage.LoadSettings().Language);
            Assert.Contains(UiText.Get("SessionBusy"), Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            pending.SetResult();
            CompleteUiTask(root, ActiveUiAiTask(window));
            Assert.True(box.IsEnabled);

            var attributes = File.GetAttributes(path);
            try
            {
                File.SetAttributes(path, attributes | FileAttributes.ReadOnly);
                box.SelectedValue = "en";
                Assert.Equal("zh", box.SelectedValue);
                Assert.Equal("zh", storage.LoadSettings().Language);
                Assert.Contains(path, Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            }
            finally { File.SetAttributes(path, attributes); }

            var savedFromSettings = storage.LoadSettings();
            savedFromSettings.Language = "en";
            storage.SaveSettings(savedFromSettings);
            settingsField.SetValue(window, savedFromSettings);
            before = File.ReadAllBytes(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-1));
            savedAt = File.GetLastWriteTimeUtc(path);
            Invoke(window, "ApplySettingsToControls");
            Assert.Equal("en", box.SelectedValue);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(savedAt, File.GetLastWriteTimeUtc(path));
            savedFromSettings.Language = "ja";
            storage.SaveSettings(savedFromSettings);
            Invoke(window, "ApplySettingsToControls");
            Assert.Equal("ja", box.SelectedValue);
            Assert.Equal("ja", storage.LoadSettings().Language);
            box.SelectedValue = "auto";
            Assert.Equal("auto", storage.LoadSettings().Language);
            Assert.Equal(["auto", "zh", "en"], box.Items.Cast<LanguageOption>().Select(option => option.Code));
        }
        finally
        {
            pending.TrySetResult();
            if (running is not null)
                CompleteUiTask(root, running);
            initializedField.SetValue(window, false);
            window.Close();
        }
    }

    private static void VerifyWarningRecovery(MainWindow window, FrameworkElement root)
    {
        var banner = Assert.IsType<Border>(window.FindName("WarningBanner"));
        var message = Assert.IsType<TextBlock>(window.FindName("WarningText"));
        var stamp = Assert.IsType<TextBlock>(window.FindName("WarningStampText"));
        var recordedAt = stamp.Text;
        Assert.False(string.IsNullOrWhiteSpace(recordedAt));
        Call(window, "SetStatus", "Synthetic replay finished", false, "ReplayTest");
        Assert.Equal(recordedAt, stamp.Text);
        Assert.Equal(Visibility.Visible, banner.Visibility);
        var session = new RecordingSession();
        void Run(RecordingSession target, string action, Func<Task> operation) =>
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "ExecuteAiAsync", target, action, operation)));

        Run(session, "UpdateMainPoints", () => Task.FromException(new IOException("Synthetic save failure")));
        Assert.Contains(UiText.Get("UpdateMainPoints"), message.Text);
        Assert.Contains("Synthetic save failure", message.Text);
        Run(new RecordingSession(), "UpdateMainPoints", () => Task.CompletedTask);
        Assert.Equal(Visibility.Visible, banner.Visibility);
        Run(session, "TranscribeResume", () => Task.CompletedTask);
        Assert.Equal(Visibility.Visible, banner.Visibility);
        Run(session, "UpdateMainPoints", () => Task.FromException(new OperationCanceledException()));
        Assert.Equal(Visibility.Visible, banner.Visibility);
        Run(session, "UpdateMainPoints", () => Task.CompletedTask);
        Assert.Equal(Visibility.Collapsed, banner.Visibility);

        Run(session, "UpdateMainPoints", () => Task.FromException(new IOException("Synthetic retry failure")));
        Run(session, "UpdateMainPoints", () =>
        {
            Call(window, "SetStatus", "New unrelated warning", true, "SyntheticTest");
            return Task.CompletedTask;
        });
        Assert.Equal(Visibility.Visible, banner.Visibility);
        Assert.Equal("New unrelated warning", message.Text);
        Call(window, "DismissWarning_Click", window, new RoutedEventArgs());
    }

    private static void VerifyShortcutEditorAndRouting(MainWindow main, AppSettings original)
    {
        var dialog = new SettingsWindow(original, "shortcuts");
        try
        {
            Assert.Equal("shortcuts", Assert.IsType<ListBoxItem>(Assert.IsType<ListBox>(dialog.FindName("SettingsCategories")).SelectedItem).Tag);
            var editor = Assert.IsType<ItemsControl>(dialog.FindName("ShortcutEditGrid"));
            Assert.Null(dialog.FindName("ShortcutHelpGrid"));
            Assert.Equal(ShortcutSettings.Commands.Count, editor.Items.Count);
            object Row(string id) => editor.Items.Cast<object>().Single(row =>
                Assert.IsType<ShortcutCommand>(row.GetType().GetProperty("Command")!.GetValue(row)).Id == id);
            void Set(string id, string value) => Row(id).GetType().GetProperty("Gesture")!.SetValue(Row(id), value);
            var categories = Assert.IsType<ListBox>(dialog.FindName("SettingsCategories"));
            categories.SelectedItem = categories.Items.Cast<ListBoxItem>().Single(item => Equals(item.Tag, "shortcuts"));
            var root = Assert.IsType<DockPanel>(dialog.Content);
            root.Measure(new Size(820, 740));
            root.Arrange(new Rect(0, 0, 820, 740));
            Pump(root);
            var searchRow = Assert.IsAssignableFrom<DependencyObject>(editor.ItemContainerGenerator.ContainerFromItem(Row("search")));
            var capture = Assert.IsType<ShortcutCaptureBox>(FindChild<ShortcutCaptureBox>(searchRow));
            Assert.True(Assert.IsType<bool>(Call(capture, "CaptureKey", Key.F7, ModifierKeys.None)));
            Pump(root);
            Assert.Equal("F7", capture.Text);
            Assert.Equal("F7", Row("search").GetType().GetProperty("DisplayGesture")!.GetValue(Row("search")));
            Set("record", "F7");
            var conflict = Assert.Throws<TargetInvocationException>(() => Invoke(dialog, "ReadShortcuts"));
            Assert.IsType<System.IO.InvalidDataException>(conflict.InnerException);
            Assert.Contains("F7", Assert.IsType<TextBlock>(dialog.FindName("ShortcutStatus")).Text);
            Assert.False(Assert.IsType<Button>(dialog.FindName("SaveButton")).IsEnabled);
            Set("record", "Ctrl+R");
            Assert.True(Assert.IsType<Button>(dialog.FindName("SaveButton")).IsEnabled);
            var editRow = Assert.IsAssignableFrom<DependencyObject>(editor.ItemContainerGenerator.ContainerFromItem(Row("edit")));
            var editCapture = Assert.IsType<ShortcutCaptureBox>(FindChild<ShortcutCaptureBox>(editRow));
            Assert.True(Assert.IsType<bool>(Call(editCapture, "CaptureKey", Key.Delete, ModifierKeys.None)));
            Assert.False(Assert.IsType<bool>(Call(editCapture, "CaptureKey", Key.Tab, ModifierKeys.None)));
            Assert.False(Assert.IsType<bool>(Call(editCapture, "CaptureKey", Key.Escape, ModifierKeys.None)));
            Pump(root);
            Assert.Empty(editCapture.Text);
            Invoke(dialog, "ReadShortcuts");
            var reference = new ShortcutHelpWindow(dialog.Settings.Shortcuts);
            try
            {
                MountDialog(reference, new Size(700, 540));
                var help = Assert.IsType<ItemsControl>(reference.FindName("ShortcutHelpGrid"));
                var search = help.Items.Cast<object>().Single(row =>
                    Equals(row.GetType().GetProperty("Id")!.GetValue(row), "search"));
                var helpRow = Assert.IsAssignableFrom<DependencyObject>(help.ItemContainerGenerator.ContainerFromItem(search));
                Assert.Equal("F7", Assert.IsType<Grid>(FindChild<Grid>(helpRow)).Children.OfType<TextBlock>().Single().Text);
            }
            finally { reference.Close(); }
            Assert.Equal("Ctrl+F", original.Shortcuts.Gesture("search"));
            Assert.Equal("F2", original.Shortcuts.Gesture("edit"));
            Assert.IsType<ComboBox>(dialog.FindName("OutputFormatBox")).SelectedItem = "MP3";
            Assert.IsType<ComboBox>(dialog.FindName("OutputRateBox")).SelectedItem = 44_100;
            Assert.IsType<ComboBox>(dialog.FindName("OutputChannelsBox")).SelectedValue = "1";
            Invoke(dialog, "ReadAudioOutput");
            Assert.Equal("MP3", dialog.Settings.Format);
            Assert.Equal(44_100, dialog.Settings.AudioOutput.SampleRate);
            Assert.Equal(1, dialog.Settings.AudioOutput.Channels);
            Assert.Equal("WAV", original.Format);
            Assert.True(original.AudioOutput.UsesSafetyFormat);

            var transcript = Assert.IsType<TranscriptView>(main.FindName("TranscriptPanel"));
            var searchEvent = typeof(TranscriptView).GetField("SearchRequested", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var handlers = searchEvent.GetValue(transcript);
            var requests = 0;
            try
            {
                searchEvent.SetValue(transcript, null);
                transcript.SearchRequested += () => requests++;
                Assert.True(Assert.IsType<bool>(Call(main, "ExecuteShortcut", Key.F, ModifierKeys.Control, false, false)));
                Assert.True(Assert.IsType<bool>(Call(main, "ExecuteShortcut", Key.F, ModifierKeys.Control, true, false)));
                Assert.Equal(2, requests);
                original.Shortcuts = dialog.Settings.Shortcuts;
                Invoke(main, "UpdateShortcutHints");
                Assert.False(Assert.IsType<bool>(Call(main, "ExecuteShortcut", Key.F, ModifierKeys.Control, false, false)));
                Assert.True(Assert.IsType<bool>(Call(main, "ExecuteShortcut", Key.F7, ModifierKeys.None, true, false)));
                Assert.Equal(3, requests);
                Assert.True(Assert.IsType<bool>(Call(main, "ExecuteShortcut", Key.F7, ModifierKeys.None, true, true)));
                Assert.Equal(3, requests);
                var search = Assert.IsType<Button>(transcript.FindName("SearchButton"));
                Assert.Contains("F7", Assert.IsType<string>(search.ToolTip));
                Assert.DoesNotContain("Ctrl+F", Assert.IsType<string>(search.ToolTip));
                Assert.Empty(Assert.IsType<MenuItem>(transcript.FindName("CorrectRowsMenu")).InputGestureText);
                search.IsEnabled = false;
                Assert.False(Assert.IsType<bool>(Call(main, "ExecuteShortcut", Key.F7, ModifierKeys.None, false, false)));
                search.IsEnabled = true;
                Assert.False(HandleSearchKey(transcript, Key.F, ModifierKeys.Control, false));
                Assert.False(HandleSearchKey(transcript, Key.Enter, ModifierKeys.Control, true));
                Assert.False(HandleSearchKey(transcript, Key.Enter, ModifierKeys.Alt, true));
                Assert.False(Navigate(transcript, Key.End, ModifierKeys.Shift | ModifierKeys.Control));
                Assert.True(Assert.IsType<CheckBox>(main.FindName("LiveTextCheck")).IsEnabled);
            }
            finally
            {
                searchEvent.SetValue(transcript, handlers);
                original.Shortcuts = new();
                Invoke(main, "UpdateShortcutHints");
            }
        }
        finally { dialog.Close(); }
    }

    private static void VerifyMatchingRowFilter(TranscriptView view, FrameworkElement root)
    {
        var document = new TranscriptDocument
        {
            ProcessedFrames = 4L * PcmFormat.SampleRate,
            Segments =
            [
                new(0, 1, "Friday Friday planning.") { SpeakerId = "spkr1" },
                new(1, 2, "Unrelated private sentence.") { SpeakerId = "spkr2" },
                new(2, 3, "Frdiay review.") { SpeakerId = "spkr2" },
                new(3, 4, "Friday close.") { SpeakerId = "spkr1" }
            ]
        };
        view.SetDocument(document, true);
        Pump(root);
        var panel = SearchPanel(view);
        var onlyMatches = Assert.IsType<CheckBox>(panel.FindName("OnlyMatchesCheck"));
        var search = Assert.IsType<TextBox>(panel.FindName("SearchBox"));
        var fuzzy = Assert.IsType<CheckBox>(panel.FindName("FuzzySearchCheck"));
        var grid = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
        search.Text = "Friday";
        onlyMatches.IsChecked = true;
        Assert.Empty(view.VisibleText());
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal([0d, 3d], grid.Items.Cast<TranscriptRow>().Select(row => row.StartSeconds));
        Assert.DoesNotContain("Unrelated", view.VisibleText());
        Assert.Contains("Unrelated", view.AllText());
        var currentRows = grid.Items.Cast<TranscriptRow>().ToArray();
        view.SetDocument(document);
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal(currentRows, grid.Items.Cast<TranscriptRow>());
        fuzzy.IsChecked = true;
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal(3, grid.Items.Count);
        view.SetSpeakerFilter(["spkr2"]);
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal(2, Assert.Single(grid.Items.Cast<TranscriptRow>()).StartSeconds);
        view.CanReplay = view.CanCorrect = true;
        grid.SelectedItem = grid.Items[0];
        Assert.Equal(new PlaybackRange(2, 3), view.SelectedPlaybackRange());
        IReadOnlyList<TranscriptRow>? selected = null;
        view.SpeakerCorrectionRequested += rows => selected = rows;
        Assert.IsType<MenuItem>(view.FindName("CorrectSpeakerMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Equal(2, Assert.Single(selected!).StartSeconds);

        var menu = Assert.IsType<ContextMenu>(view.GetType().GetField("speakerMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view));
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiText.Get("DeselectAllSpeakers")));
        var all = Assert.IsType<MenuItem>(menu.Items[0]);
        all.IsChecked = false;
        all.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Empty(grid.Items);
        all.IsChecked = true;
        all.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal(3, grid.Items.Count);
        Assert.All(menu.Items.OfType<MenuItem>().Skip(1), item => Assert.True(item.IsChecked));

        search.Text = "absent";
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Empty(grid.Items);
        Assert.Equal(UiText.Get("SearchNoMatches"), Assert.IsType<TextBlock>(view.FindName("NoMatchingSpeakers")).Text);
        search.Clear();
        Assert.Equal(4, grid.Items.Count);
        Assert.Equal(view.AllText(), view.VisibleText());
        onlyMatches.IsChecked = false;
        fuzzy.IsChecked = false;
    }

    private static void VerifySpeakerCorrectionDialog()
    {
        var document = AudioEditPlanTests.Document(3,
            new(0, 1, "First.") { SpeakerId = "spkr1" },
            new(1, 2, "Second.") { SpeakerId = "spkr2" },
            new(2, 3, "First again.") { SpeakerId = "spkr1" });
        document.SpeakerNames["spkr1"] = "Alex";
        var plan = TranscriptCorrectionPlan.Create(document, document.ProcessedFrames, [TranscriptPresentation.Rows(document)[0]]);
        var dialog = new SpeakerCorrectionWindow(null, document, plan);
        try
        {
            var root = Assert.IsType<StackPanel>(dialog.Content);
            root.Measure(new Size(480, 500));
            root.Arrange(new Rect(0, 0, 480, 500));
            Pump(root);
            var target = Assert.IsType<ComboBox>(FindChild<ComboBox>(root));
            Assert.Equal(["spkr1", "spkr2"], target.Items.Cast<LanguageOption>().Select(option => option.Code));
            Assert.Equal(["Alex (spkr1)", "spkr2"], target.Items.Cast<LanguageOption>().Select(option => option.Label));
            var merge = Assert.IsType<CheckBox>(FindChild<CheckBox>(root));
            Assert.False(merge.IsChecked);
            Assert.Contains("2", Assert.IsType<TextBlock>(merge.Content).Text);
            target.SelectedValue = "spkr2";
            Assert.Equal("spkr2", target.SelectedValue);
            target.SelectedValue = "spkr1";
            var buttons = root.Children.OfType<StackPanel>().Single();
            Assert.IsType<Button>(buttons.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(dialog.Result);
            Assert.False(string.IsNullOrWhiteSpace(root.Children.OfType<TextBlock>().Last().Text));
        }
        finally { dialog.Close(); }
    }

    private static void VerifyDisplayBounds()
    {
        var type = typeof(AppAppearance).Assembly.GetType("AudioMemo.Services.WindowSizing")!;
        var available = type.GetMethod("AvailableSize", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var dpi in new uint[] { 96, 120, 144, 192 })
        {
            var size = Assert.IsType<Size>(available.Invoke(null, [1920, 1080, dpi]));
            Assert.Equal(1920 * 96d / dpi, size.Width, 5);
            Assert.Equal(1080 * 96d / dpi, size.Height, 5);
        }
        var window = new Window { Width = 1440, Height = 900, MinWidth = 640, MinHeight = 420 };
        try
        {
            var fit = type.GetMethod("FitToArea", BindingFlags.Static | BindingFlags.NonPublic)!;
            var minimum = new Size(640, 420);
            var maximum = new Size(double.PositiveInfinity, double.PositiveInfinity);
            fit.Invoke(null, [window, new Size(500, 300), minimum, maximum]);
            Assert.Equal(500, window.Width);
            Assert.Equal(300, window.Height);
            Assert.Equal(500, window.MinWidth);
            fit.Invoke(null, [window, new Size(1920, 1080), minimum, maximum]);
            Assert.Equal(640, window.MinWidth);
            Assert.Equal(420, window.MinHeight);
            Assert.Equal(640, window.Width);
            Assert.Equal(420, window.Height);
        }
        finally { window.Close(); }
    }
}
