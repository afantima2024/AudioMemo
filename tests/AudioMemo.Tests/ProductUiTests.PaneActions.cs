using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioMemo.Audio;
using AudioMemo.Controls;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyTranscriptAudioExport(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        settings.UiLanguage = language;
        var before = EditedSessionTests.Hashes(session.Folder);
        var requests = new List<(string Id, string WavePath, long Frames, ProcessingWorkKind Kind)>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new RecordingEngine(new RecordingEngineTests.FakeFactory().Create);
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, recorder,
            exportAudio: (recording, kind, token) =>
            {
                requests.Add((recording.Id, recording.WavePath, recording.Frames, kind));
                return release.Task.WaitAsync(token);
            });
        var root = MountDialog(window, new Size(1080, 720));
        SelectProcessingSession(window, session);
        var view = Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel"));
        var rows = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
        var export = Assert.IsType<MenuItem>(view.FindName("ExportAudioMenu"));
        var updateNotes = Assert.IsType<Button>(window.FindName("UpdateMyNotesButton"));
        try
        {
            Assert.Null(window.FindName("ExportButton"));
            Assert.Contains(export, rows.ContextMenu.Items.Cast<object>());
            Assert.Equal(UiText.Get("ExportWholeRecording"), export.Header);
            Assert.Contains(UiText.Get("ExportWholeRecordingHint"), Assert.IsType<string>(export.ToolTip));
            Assert.Equal(ShortcutInput.Display(settings.Shortcuts.Gesture("export")), export.InputGestureText);
            Assert.Same(export, view.ShortcutTarget("export"));
            Assert.Same(export, Call(window, "ShortcutTarget", "export"));
            Assert.True(view.CanExportAudio && export.IsEnabled);
            view.SetSpeakerFilter(["spkr2"]);
            rows.SelectedIndex = 0;
            Assert.True(rows.Items.Count < AppStorage.LoadTranscript(session).Segments.Count);
            Assert.True(ShortcutActions.Invoke(export));
            Assert.Equal((session.Id, session.WavePath, session.Frames, ProcessingWorkKind.ExportAudio),
                Assert.Single(requests));
            Assert.False(view.CanExportAudio || export.IsEnabled || updateNotes.IsEnabled);
            Assert.False(ShortcutActions.Invoke(export));
            Assert.False(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.E, ModifierKeys.Control, false, false)));
            release.SetResult();
            CompleteExport();
            Assert.True(view.CanExportAudio && export.IsEnabled && updateNotes.IsEnabled);

            foreach (var name in new[] { "recordingCommand", "selectingModel", "closing" })
            {
                var field = typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
                field.SetValue(window, true);
                try
                {
                    Invoke(window, "UpdateControls");
                    Assert.False(view.CanExportAudio || export.IsEnabled || updateNotes.IsEnabled);
                    Assert.False(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.E, ModifierKeys.Control, false, false)));
                }
                finally { field.SetValue(window, false); Invoke(window, "UpdateControls"); }
            }
            var aiTask = typeof(MainWindow).GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
            aiTask.SetValue(window, new TaskCompletionSource().Task);
            try
            {
                Invoke(window, "UpdateControls");
                Assert.True(view.CanReplay);
                Assert.False(view.CanExportAudio || export.IsEnabled || updateNotes.IsEnabled);
                Assert.False(ShortcutActions.Invoke(export));
            }
            finally { aiTask.SetValue(window, null); Invoke(window, "UpdateControls"); }
            Assert.Single(requests);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));

            AppStorage.SaveTranscript(session, new() { ProcessedFrames = session.Frames });
            Call(window, "LoadSessionContent", session);
            Invoke(window, "UpdateControls");
            Assert.Empty(rows.Items.Cast<object>());
            Assert.True(view.CanExportAudio && export.IsEnabled);
            Assert.False(updateNotes.IsEnabled);
            Click(Assert.IsType<Button>(window.FindName("CloseMainPointsButton")));
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("MainPointsCard")).Visibility);
            Assert.True(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.E, ModifierKeys.Control, false, false)));
            CompleteExport();
            Assert.Equal(2, requests.Count);
            Assert.Equal((session.Id, session.WavePath, session.Frames, ProcessingWorkKind.ExportAudio), requests[^1]);
            Call(window, "ClearWorkspace");
            Invoke(window, "UpdateControls");
            Assert.False(view.CanExportAudio || export.IsEnabled || updateNotes.IsEnabled);
        }
        finally
        {
            release.TrySetResult();
            CompleteExport();
            window.Close();
        }

        void CompleteExport()
        {
            if (typeof(MainWindow).GetField("exportTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is Task task)
                CompleteUiTask(root, task);
            Pump(root);
        }
    }
}
