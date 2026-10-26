using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyHistoryMultiSelection(string language)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.File("library"), UiLanguage = language };
        var sessions = Enumerable.Range(0, 28).Select(index => CreateHistoryRecording(settings, index)).ToArray();
        var factory = new RecordingEngineTests.FakeFactory();
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, new RecordingEngine(factory.Create),
            confirmRecycleHistory: _ => false, recycleHistorySession: (_, _) => throw new InvalidOperationException("Not expected."));
        var initialized = typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Invoke(window, "ApplySettingsToControls");
            initialized.SetValue(window, true);
            Invoke(window, "RefreshHistory");
            var root = Assert.IsType<Grid>(window.Content);
            root.Measure(new Size(900, 600));
            root.Arrange(new Rect(0, 0, 900, 600));
            Pump(root);
            var history = Assert.IsType<ListBox>(window.FindName("HistoryList"));
            var all = Assert.IsType<CheckBox>(window.FindName("SelectAllHistoryCheck"));
            var count = Assert.IsType<TextBlock>(window.FindName("LibraryHint"));
            Assert.Equal(SelectionMode.Extended, history.SelectionMode);
            Assert.Equal(UiText.Get("HistorySelectionHint"), history.ToolTip);
            Assert.Equal(UiText.Get("SelectAllRecordings"), all.ToolTip);
            Assert.True(all.IsEnabled);
            Assert.False(all.IsChecked);

            var items = history.Items.Cast<RecordingSession>().ToArray();
            history.SelectedItem = items[1];
            var preview = items[1].Folder;
            history.SelectedItems.Add(items[3]);
            history.SelectedItems.Add(items[20]);
            var selection = HistorySelectedFolders(history);
            Assert.Equal(3, selection.Length);
            Assert.Null(all.IsChecked);
            Assert.Equal(UiText.Format("RecordingSelectionCount", items.Length, 3), count.Text);
            Assert.Equal(preview, HistoryPreview(window)?.Folder);
            foreach (var name in new[] { "StartButton", "TranscribeButton", "IdentifySpeakersButton",
                "SummarizeButton", "UpdateMyNotesButton", "RecoverButton" })
                Assert.False(Assert.IsType<Button>(window.FindName(name)).IsEnabled, name);
            Assert.True(Assert.IsType<Button>(window.FindName("OpenSessionButton")).IsEnabled);
            var transcript = Assert.IsType<AudioMemo.Controls.TranscriptView>(window.FindName("TranscriptPanel"));
            Assert.False(transcript.CanExportAudio);
            Assert.False(Assert.IsType<MenuItem>(transcript.FindName("ExportAudioMenu")).IsEnabled);
            Assert.False(Assert.IsType<TextBox>(window.FindName("NotesBox")).IsEnabled);
            foreach (var name in new[] { "ContinueRecordingMenu", "RecoverSessionMenu", "CopySessionMenu", "RestoreSilenceMenu",
                "TranscribeSessionMenu", "IdentifySpeakersMenu", "SummarizeSessionMenu" })
                Assert.False(Assert.IsType<MenuItem>(window.FindName(name)).IsEnabled, name);
            foreach (var name in new[] { "RenameSessionMenu", "OpenSessionMenu", "ExportTranscriptMenu", "ExportMp3Menu" })
                Assert.True(Assert.IsType<MenuItem>(window.FindName(name)).IsEnabled, name);
            var recycle = Assert.IsType<MenuItem>(window.FindName("DeleteSessionMenu"));
            Assert.True(recycle.IsEnabled);
            Assert.Equal(UiText.Format("RecycleSelectedSessions", 3), recycle.Header);
            var invalidAction = Assert.Throws<TargetInvocationException>(() => Invoke(window, "RequireIdleSession"));
            Assert.Equal(UiText.Get("SelectOneRecording"), invalidAction.InnerException?.Message);

            history.ScrollIntoView(items[^1]);
            Pump(root);
            Assert.Equal(selection, HistorySelectedFolders(history));
            history.ScrollIntoView(items[3]);
            Pump(root);
            var selectedContainer = Assert.IsType<ListBoxItem>(history.ItemContainerGenerator.ContainerFromItem(items[3]));
            Call(window, "History_RightClick", history, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent, Source = selectedContainer
            });
            Assert.Equal(selection, HistorySelectedFolders(history));

            items[10].Id = items[1].Id;
            items[10].Save();
            Invoke(window, "RefreshHistory");
            Pump(root);
            Assert.Equal(selection, HistorySelectedFolders(history));
            Assert.DoesNotContain(items[10].Folder, HistorySelectedFolders(history));
            Assert.Equal(preview, HistoryPreview(window)?.Folder);
            Assert.Equal(2, history.Items.Cast<RecordingSession>().Count(item => item.Id == items[1].Id));

            settings.HistoryVisible = false;
            Invoke(window, "UpdateHistoryVisibility");
            Call(window, "RevealHistory", all, new RoutedEventArgs());
            Pump(root);
            Assert.Equal(selection, HistorySelectedFolders(history));
            settings.HistoryVisible = true;
            Invoke(window, "UpdateHistoryVisibility");
            Pump(root);
            Assert.Equal(selection, HistorySelectedFolders(history));

            var unselected = history.Items.Cast<RecordingSession>().Single(item => item.Folder == items[0].Folder);
            history.ScrollIntoView(unselected);
            Pump(root);
            var unselectedContainer = Assert.IsType<ListBoxItem>(history.ItemContainerGenerator.ContainerFromItem(unselected));
            var rightClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent, Source = unselectedContainer
            };
            var busy = typeof(MainWindow).GetField("exportBusy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            busy.SetValue(window, true);
            Invoke(window, "UpdateControls");
            Assert.False(history.IsEnabled);
            Assert.False(all.IsEnabled);
            Assert.False(recycle.IsEnabled);
            foreach (var name in new[] { "RenameSessionMenu", "ExportTranscriptMenu", "ExportMp3Menu" })
                Assert.False(Assert.IsType<MenuItem>(window.FindName(name)).IsEnabled, name);
            Assert.False(ApplicationCommands.SelectAll.CanExecute(null, history));
            Call(window, "History_RightClick", history, rightClick);
            Assert.Equal(selection, HistorySelectedFolders(history));
            busy.SetValue(window, false);
            Invoke(window, "UpdateControls");
            Call(window, "History_RightClick", history, rightClick);
            Assert.Equal(unselected.Folder, Assert.Single(HistorySelectedFolders(history)));
            Assert.Equal(unselected.Folder, HistoryPreview(window)?.Folder);
            Assert.True(Assert.IsType<Button>(window.FindName("TranscribeButton")).IsEnabled);
            Assert.True(Assert.IsType<TextBox>(window.FindName("NotesBox")).IsEnabled);

            Assert.False(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.A, ModifierKeys.Control, false, false)));
            Assert.Contains(ApplicationCommands.SelectAll.InputGestures.Cast<InputGesture>(),
                gesture => gesture is KeyGesture { Key: Key.A, Modifiers: ModifierKeys.Control });
            Assert.True(ApplicationCommands.SelectAll.CanExecute(null, history));
            ApplicationCommands.SelectAll.Execute(null, history);
            Assert.Equal(items.Length, history.SelectedItems.Count);
            Assert.True(all.IsChecked);
            Click(all);
            Assert.Empty(history.SelectedItems);
            Assert.False(all.IsChecked);
            Assert.Null(HistoryPreview(window));
            Assert.Equal(UiText.Get("WorkspaceTitle"), Assert.IsType<TextBlock>(window.FindName("WorkspaceTitle")).Text);
            Click(all);
            Assert.Equal(items.Length, history.SelectedItems.Count);
            Assert.True(all.IsChecked);
            Invoke(window, "ClearWorkspace");
            Invoke(window, "UpdateControls");
            Assert.Empty(history.SelectedItems);
            Assert.False(all.IsChecked);
            history.SelectedItem = history.Items[1];
            history.SelectedItems.Add(history.Items[3]);
            var removed = HistoryPreview(window)!;
            history.SelectedItems.Remove(history.SelectedItems.Cast<RecordingSession>().Single(item => item.Folder == removed.Folder));
            Assert.Equal(Assert.Single(HistorySelectedFolders(history)), HistoryPreview(window)?.Folder);
            history.SelectAll();
            var imported = CreateHistoryRecording(settings, 99);
            Call(window, "DisplayImportedSession", imported);
            Assert.Equal(imported.Folder, Assert.Single(HistorySelectedFolders(history)));
            Assert.Equal(imported.Folder, HistoryPreview(window)?.Folder);
            Assert.All(sessions, session => Assert.True(File.Exists(session.WavePath)));
            Assert.Null(typeof(MainWindow).GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Empty(factory.Inputs);
        }
        finally
        {
            initialized.SetValue(window, false);
            window.Close();
        }
    }

    private static void VerifyHistoryBatchActions(string language)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.File("library"), UiLanguage = language };
        for (var index = 0; index < 4; index++)
            CreateHistoryRecording(settings, index);
        var closeMessages = new List<string>();
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, new RecordingEngine(),
            confirmCloseProcessing: message => { closeMessages.Add(message); return true; });
        var root = MountDialog(window, new Size(1100, 760));
        var initialized = typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Invoke(window, "ApplySettingsToControls");
            initialized.SetValue(window, true);
            Invoke(window, "RefreshHistory");
            var history = Assert.IsType<ListBox>(window.FindName("HistoryList"));
            history.SelectAll();
            var ordered = history.Items.Cast<RecordingSession>().ToArray();
            var selection = HistorySelectedFolders(history);
            var prompts = new List<string>();
            window.RenameHistorySelection((session, number, count) =>
            {
                Assert.Equal(4, count);
                prompts.Add(session.Folder);
                return number <= 2 ? $"Renamed {number}" : null;
            });
            Assert.Equal(ordered.Take(3).Select(session => session.Folder), prompts);
            Assert.Equal(selection, HistorySelectedFolders(history));
            Assert.Equal(UiText.Format("HistoryRenameCancelled", 2, 4),
                Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            Assert.Equal("Renamed 1", history.Items.Cast<RecordingSession>().Single(session => session.Folder == ordered[0].Folder).Title);
            Assert.Equal("Renamed 2", history.Items.Cast<RecordingSession>().Single(session => session.Folder == ordered[1].Folder).Title);
            Assert.Equal("Synthetic history 01", history.Items.Cast<RecordingSession>().Single(session => session.Folder == ordered[2].Folder).Title);
            window.RenameHistorySelection((_, number, _) => $"Batch meeting {number}");
            Assert.Equal(UiText.Format("HistoryRenamed", 4), Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            Assert.Equal(selection, HistorySelectedFolders(history));
            var blocked = history.Items.Cast<RecordingSession>().ElementAt(1);
            var attributes = File.GetAttributes(blocked.MetadataPath);
            try
            {
                File.SetAttributes(blocked.MetadataPath, attributes | FileAttributes.ReadOnly);
                window.RenameHistorySelection((_, number, _) => $"Partial name {number}");
                Assert.Equal("Partial name 1", history.Items.Cast<RecordingSession>().First().Title);
                Assert.Equal("Batch meeting 2", history.Items.Cast<RecordingSession>().ElementAt(1).Title);
                Assert.Equal(selection, HistorySelectedFolders(history));
                Assert.Contains(UiText.Format("HistoryRenameFailed", 1, 4, "__error__").Split("__error__")[0],
                    Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            }
            finally { File.SetAttributes(blocked.MetadataPath, attributes); }
            var destination = folder.File("exports");
            Directory.CreateDirectory(destination);
            var selected = history.Items.Cast<RecordingSession>().ToArray();
            CompleteUiTask(root, window.ExportHistorySelectionAsync(selected, destination, HistoryExportKind.Transcript));
            Pump(root);
            Assert.Equal(UiText.Format("HistoryExportComplete", 4, destination),
                Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            Assert.Equal(4, Directory.GetFiles(destination).Length);
            Assert.All(selected, session => Assert.True(File.Exists(Path.Combine(destination, session.Title + ".txt"))));
            Assert.Equal(selection, HistorySelectedFolders(history));
            Assert.True(history.IsEnabled);
            Assert.All(selected, session => Assert.Null(PendingProcessingStore.Load(session)));

            history.SelectedItems.Clear();
            history.SelectedItem = selected[0];
            var draft = AppStorage.LoadTranscript(selected[0]);
            draft.IsDraft = true;
            AppStorage.SaveTranscript(selected[0], draft);
            Call(window, "LoadSessionContent", selected[0]);
            Invoke(window, "UpdateControls");
            Click(Assert.IsType<Button>(window.FindName("SummarizeButton")));
            Assert.Contains(UiText.Get("SummaryRequiresFinalTranscript"), Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            Assert.Null(typeof(MainWindow).GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.False(settings.CloudConsent);

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var busy = typeof(MainWindow).GetField("exportBusy", flags)!;
            var exporting = typeof(MainWindow).GetField("exportingHistory", flags)!;
            var cancellation = new CancellationTokenSource();
            typeof(MainWindow).GetField("exportCancellation", flags)!.SetValue(window, cancellation);
            busy.SetValue(window, true);
            exporting.SetValue(window, true);
            var finishing = FinishExportAsync();
            typeof(MainWindow).GetField("exportTask", flags)!.SetValue(window, finishing);
            var closing = PrepareTestClose(window);
            CompleteUiTask(root, Task.WhenAll(finishing, closing));
            Assert.True(closing.Result);
            Assert.Equal(UiText.Get("ConfirmCloseBatchExport"), Assert.Single(closeMessages));
            Assert.All(selected, session => Assert.Null(PendingProcessingStore.Load(session)));

            async Task FinishExportAsync()
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                finally { busy.SetValue(window, false); exporting.SetValue(window, false); }
            }
        }
        finally { initialized.SetValue(window, false); window.Close(); }
    }

    private static void VerifyHistoryBatchRecycle(string language)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.File("library"), UiLanguage = language };
        var originals = Enumerable.Range(0, 4).Select(index => CreateHistoryRecording(settings, index)).ToArray();
        var stash = folder.File("synthetic-recycled");
        Directory.CreateDirectory(stash);
        var approved = false;
        var cancel = false;
        var failSecond = false;
        var confirmations = new List<string[]>();
        var operations = new List<string>();
        var factory = new RecordingEngineTests.FakeFactory();
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, new RecordingEngine(factory.Create),
            confirmRecycleHistory: sessions =>
            {
                confirmations.Add(sessions.Select(session => session.Folder).ToArray());
                return approved;
            },
            recycleHistorySession: (library, session) =>
            {
                SessionCommands.ValidateDeletion(library, session);
                operations.Add(session.Folder);
                if (cancel)
                    throw new OperationCanceledException("Synthetic recycle cancellation.");
                if (failSecond && operations.Count == 2)
                    throw new IOException("Synthetic recycle failure.");
                Directory.Move(session.Folder, Path.Combine(stash, Path.GetFileName(session.Folder)));
            });
        var initialized = typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Invoke(window, "ApplySettingsToControls");
            initialized.SetValue(window, true);
            Invoke(window, "RefreshHistory");
            var root = Assert.IsType<Grid>(window.Content);
            root.Measure(new Size(1000, 700));
            root.Arrange(new Rect(0, 0, 1000, 700));
            Pump(root);
            var history = Assert.IsType<ListBox>(window.FindName("HistoryList"));
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "RecycleSelectedHistoryAsync")));
            Assert.Empty(confirmations);
            Assert.Empty(operations);
            Assert.Contains(UiText.Get("SelectSession"), Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);

            var items = history.Items.Cast<RecordingSession>().ToArray();
            history.SelectedItem = items[0];
            history.SelectedItems.Add(items[1]);
            history.SelectedItems.Add(items[2]);
            var targets = history.SelectedItems.Cast<RecordingSession>().ToArray();
            var selectedFolders = HistorySelectedFolders(history);
            var before = originals.ToDictionary(session => session.Folder, session => EditedSessionTests.Hashes(session.Folder));
            var confirmation = MainWindow.CreateRecycleHistoryDialog(null, targets);
            try
            {
                Assert.Equal(UiText.Format("RecycleSelectedSessions", 3), confirmation.Title);
                var body = Assert.IsType<StackPanel>(confirmation.Content);
                var buttons = Assert.IsType<StackPanel>(body.Children[1]).Children.Cast<Button>().ToArray();
                Assert.True(buttons.Single(button => (MessageBoxResult)button.Tag == MessageBoxResult.No).IsDefault);
                Assert.Equal(MessageBoxResult.No, confirmation.Result);
                var scroller = Assert.IsType<ScrollViewer>(Assert.IsType<DockPanel>(body.Children[0]).Children[1]);
                var message = Assert.IsType<TextBlock>(scroller.Content).Text;
                Assert.All(targets, session =>
                {
                    Assert.Contains(session.Title, message);
                    Assert.Contains(session.Folder, message);
                });
                Assert.DoesNotContain(items[3].Folder, message);
            }
            finally { confirmation.Close(); }

            var validId = items[2].Id;
            items[2].Id = Guid.NewGuid().ToString("N");
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "RecycleSelectedHistoryAsync")));
            Assert.Empty(confirmations);
            Assert.Empty(operations);
            Assert.All(originals, session => Assert.Equal(before[session.Folder], EditedSessionTests.Hashes(session.Folder)));
            items[2].Id = validId;
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "RecycleSelectedHistoryAsync")));
            Assert.Single(confirmations);
            Assert.Equal(targets.Select(session => session.Folder), confirmations[0]);
            Assert.Empty(operations);
            Assert.Equal(selectedFolders, HistorySelectedFolders(history));
            Assert.All(originals, session => Assert.Equal(before[session.Folder], EditedSessionTests.Hashes(session.Folder)));

            Assert.IsType<TextBox>(window.FindName("NotesBox")).Text = "Pending note saved before batch recycling.";
            approved = failSecond = true;
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "RecycleSelectedHistoryAsync")));
            Assert.Equal(2, confirmations.Count);
            Assert.Equal(new[] { items[0].Folder, items[1].Folder }, operations);
            Assert.False(Directory.Exists(items[0].Folder));
            Assert.Equal("Pending note saved before batch recycling.",
                File.ReadAllText(Path.Combine(stash, Path.GetFileName(items[0].Folder), "notes.md")));
            Assert.Equal(new[] { items[1].Folder, items[2].Folder }.Order(), HistorySelectedFolders(history));
            Assert.Equal(items[1].Folder, HistoryPreview(window)?.Folder);
            Assert.Equal(before[items[3].Folder], EditedSessionTests.Hashes(items[3].Folder));
            Assert.Contains(UiText.Format("RecycleSelectionFailed", 1, 3, items[1].Title, "Synthetic recycle failure."),
                Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            Assert.False(Assert.IsType<bool>(typeof(MainWindow).GetField("exportBusy",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)));

            operations.Clear();
            cancel = true;
            failSecond = false;
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "RecycleSelectedHistoryAsync")));
            Assert.Single(operations);
            Assert.Equal(3, confirmations.Count);
            Assert.Equal(2, history.SelectedItems.Count);
            Assert.Equal(UiText.Format("RecycleSelectionCancelled", 0, 2),
                Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);

            operations.Clear();
            cancel = false;
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "RecycleSelectedHistoryAsync")));
            Assert.Equal(4, confirmations.Count);
            Assert.Equal(new[] { items[1].Folder, items[2].Folder }, operations);
            Assert.Equal(UiText.Format("SessionsRecycled", 2), Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            Assert.Empty(history.SelectedItems);
            Assert.Null(HistoryPreview(window));
            Assert.Equal(items[3].Folder, Assert.Single(history.Items.Cast<RecordingSession>()).Folder);
            Assert.Equal(before[items[3].Folder], EditedSessionTests.Hashes(items[3].Folder));
            Assert.Equal(3, Directory.GetDirectories(stash).Length);

            history.SelectedIndex = 0;
            var last = Assert.IsType<RecordingSession>(history.SelectedItem);
            var singleDialog = MainWindow.CreateRecycleHistoryDialog(null, [last]);
            try { Assert.Equal(UiText.Get("RecycleSession"), singleDialog.Title); }
            finally { singleDialog.Close(); }
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "RecycleSelectedHistoryAsync")));
            Assert.Single(confirmations[^1]);
            Assert.Empty(history.Items);
            Assert.False(Assert.IsType<CheckBox>(window.FindName("SelectAllHistoryCheck")).IsEnabled);
            Assert.Equal(UiText.Get("SessionRecycled"), Assert.IsType<TextBlock>(window.FindName("StatusText")).Text);
            Assert.Equal(UiText.Get("FirstRecordingHint"), Assert.IsType<TextBlock>(window.FindName("LibraryHint")).Text);
        }
        finally
        {
            initialized.SetValue(window, false);
            window.Close();
        }
    }

    private static void VerifyClosingHistoryRecycle(string language)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.File("library"), UiLanguage = language };
        var sessions = Enumerable.Range(0, 3).Select(index => CreateHistoryRecording(settings, index)).ToArray();
        var stash = folder.File("synthetic-recycled");
        Directory.CreateDirectory(stash);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var operations = new List<string>();
        var closePrompts = new List<string>();
        var allowClose = false;
        var confirmations = 0;
        var factory = new RecordingEngineTests.FakeFactory();
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, new RecordingEngine(factory.Create),
            confirmCloseProcessing: message =>
            {
                closePrompts.Add(message);
                return allowClose;
            },
            confirmRecycleHistory: _ => { confirmations++; return true; },
            recycleHistorySession: (library, session) =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Synthetic recycle operation was not released.");
                SessionCommands.ValidateDeletion(library, session);
                Directory.Move(session.Folder, Path.Combine(stash, Path.GetFileName(session.Folder)));
                operations.Add(session.Folder);
            });
        var initialized = typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var root = Assert.IsType<Grid>(window.Content);
        Task? recycling = null;
        Task<bool>? closingTask = null;
        try
        {
            Invoke(window, "ApplySettingsToControls");
            initialized.SetValue(window, true);
            Invoke(window, "RefreshHistory");
            var history = Assert.IsType<ListBox>(window.FindName("HistoryList"));
            history.SelectAll();
            recycling = Assert.IsAssignableFrom<Task>(Call(window, "RecycleSelectedHistoryAsync"));
            CompleteUiTask(root, Task.Run(() => Assert.True(entered.Wait(TimeSpan.FromSeconds(5)))));
            Assert.False(history.IsEnabled);
            Assert.False(Assert.IsType<CheckBox>(window.FindName("SelectAllHistoryCheck")).IsEnabled);
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "RecycleSelectedHistoryAsync")));
            Assert.Equal(1, confirmations);
            Assert.True(Assert.IsType<bool>(typeof(MainWindow).GetField("exportBusy",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)));
            var declined = Assert.IsAssignableFrom<Task<bool>>(Call(window, "PrepareCloseAsync"));
            CompleteUiTask(root, declined);
            Assert.False(declined.Result);
            Assert.False(recycling.IsCompleted);
            allowClose = true;
            closingTask = Assert.IsAssignableFrom<Task<bool>>(Call(window, "PrepareCloseAsync"));
            Assert.False(closingTask.IsCompleted);
            Assert.Equal(2, closePrompts.Count);
            Assert.All(closePrompts, message => Assert.Equal(UiText.Get("ConfirmCloseRecycling"), message));
            release.Set();
            CompleteUiTask(root, Task.WhenAll(recycling, closingTask));
            Assert.True(closingTask.Result);
            Assert.Single(operations);
            Assert.Equal(2, sessions.Count(session => Directory.Exists(session.Folder)));
            Assert.All(sessions.Where(session => Directory.Exists(session.Folder)),
                session => Assert.False(File.Exists(PendingProcessingStore.PathFor(session))));
            Assert.Empty(factory.Inputs);
        }
        finally
        {
            release.Set();
            if (recycling is not null)
                CompleteUiTask(root, recycling);
            if (closingTask is not null)
                CompleteUiTask(root, closingTask);
            initialized.SetValue(window, false);
            window.Close();
        }
    }

    private static RecordingSession CreateHistoryRecording(AppSettings settings, int index)
    {
        var session = AppStorage.CreateSession(settings, $"Synthetic history {index:D2}");
        session.CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index);
        using (var writer = new DurableWaveFile(session.WavePath))
            writer.Write(new byte[PcmFormat.BytesPerSecond]);
        session.Frames = PcmFormat.SampleRate;
        session.Status = "Saved";
        AppStorage.SaveTranscript(session, new TranscriptDocument
        {
            ProcessedFrames = session.Frames,
            Segments = [new(0, 1, $"Transcript for {session.Title}.")]
        });
        File.WriteAllText(session.NotesPath, $"Notes for {session.Title}.");
        File.WriteAllText(session.SummaryPath, $"Summary for {session.Title}.");
        session.Save();
        return session;
    }

    private static RecordingSession? HistoryPreview(MainWindow window) =>
        (RecordingSession?)typeof(MainWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);

    private static string[] HistorySelectedFolders(ListBox history) =>
        history.SelectedItems.Cast<RecordingSession>().Select(session => session.Folder).Order().ToArray();
}
