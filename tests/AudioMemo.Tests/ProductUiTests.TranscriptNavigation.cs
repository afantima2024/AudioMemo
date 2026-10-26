using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AudioMemo.Audio;
using AudioMemo.Controls;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyTranscriptNavigationPersistence(string language)
    {
        using var folder = new TestFolder();
        var storage = new AppStorage(folder.File("preferences"));
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        settings.UiLanguage = language;
        settings.TranscriptNavigationX = 0.25;
        settings.TranscriptNavigationY = 0.35;
        storage.SaveSettings(settings);
        var settingsPath = folder.File(@"preferences\settings.json");
        var window = new MainWindow(storage, settings, new RecordingEngine(new RecordingEngineTests.FakeFactory().Create));
        var root = MountDialog(window, new Size(1080, 720));
        var view = Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel"));
        var grip = Assert.IsType<Thumb>(view.FindName("NavigationDragHandle"));
        var settingsField = typeof(MainWindow).GetField("settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var aiTask = typeof(MainWindow).GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Call(window, "ApplySettingsToControls");
            view.SetDocument(new()
            {
                Segments = Enumerable.Range(0, 240).Select(index =>
                    new TranscriptSegment(index, index + 1, $"Synthetic movable toolbar row {index:D3}.")).ToList()
            }, reset: true);
            Pump(root);
            Assert.Equal(new Point(0.25, 0.35), view.NavigationPosition);
            aiTask.SetValue(window, new TaskCompletionSource().Task);
            Call(window, "UpdateControls");
            Assert.False(view.CanEdit || view.CanCorrect);
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(view.FindName("FloatingNavigation")).Visibility);
            Assert.True(grip.IsEnabled);
            var before = File.ReadAllBytes(settingsPath);
            StartDrag();
            Drag(45, 25);
            Assert.Equal(before, File.ReadAllBytes(settingsPath));
            Assert.False(Assert.IsType<bool>(Call(window, "ExecuteShortcut", Key.End, ModifierKeys.Control, false, false)));
            EndDrag();
            var savedPosition = view.NavigationPosition;
            Assert.NotEqual(new Point(0.25, 0.35), savedPosition);
            var saved = storage.LoadSettings();
            Assert.Equal(savedPosition, new Point(saved.TranscriptNavigationX, saved.TranscriptNavigationY));
            Assert.Equal(savedPosition, new Point(
                Assert.IsType<AppSettings>(settingsField.GetValue(window)).TranscriptNavigationX,
                Assert.IsType<AppSettings>(settingsField.GetValue(window)).TranscriptNavigationY));
            Assert.Equal(settings.SaveFolder, saved.SaveFolder);
            Assert.Equal(settings.OllamaModel, saved.OllamaModel);
            Assert.Equal(settings.CloudConsent, saved.CloudConsent);
            Assert.Equal(settings.HistoryWidth, saved.HistoryWidth);
            before = File.ReadAllBytes(settingsPath);

            StartDrag();
            Drag(20, 20);
            EndDrag(canceled: true);
            Assert.Equal(savedPosition, view.NavigationPosition);
            Assert.Equal(before, File.ReadAllBytes(settingsPath));
            Exception? saveError = null;
            view.OperationFailed += error => saveError = error;
            var attributes = File.GetAttributes(settingsPath);
            try
            {
                File.SetAttributes(settingsPath, attributes | FileAttributes.ReadOnly);
                StartDrag();
                Drag(-25, -15);
                EndDrag();
                Assert.True(saveError is IOException or UnauthorizedAccessException);
                Assert.Equal(savedPosition, view.NavigationPosition);
                Assert.Equal(savedPosition, new Point(
                    Assert.IsType<AppSettings>(settingsField.GetValue(window)).TranscriptNavigationX,
                    Assert.IsType<AppSettings>(settingsField.GetValue(window)).TranscriptNavigationY));
                Assert.Equal(before, File.ReadAllBytes(settingsPath));
                Assert.Equal(Visibility.Visible, Assert.IsAssignableFrom<FrameworkElement>(window.FindName("WarningBanner")).Visibility);
            }
            finally { File.SetAttributes(settingsPath, attributes); }

            var dialog = new SettingsWindow(storage.LoadSettings());
            try
            {
                Assert.Equal(savedPosition, new Point(dialog.Settings.TranscriptNavigationX, dialog.Settings.TranscriptNavigationY));
            }
            finally { dialog.Close(); }
            var reopened = new MainWindow(storage, storage.LoadSettings(),
                new RecordingEngine(new RecordingEngineTests.FakeFactory().Create));
            try
            {
                Call(reopened, "ApplySettingsToControls");
                Assert.Equal(savedPosition, Assert.IsType<TranscriptView>(reopened.FindName("TranscriptPanel")).NavigationPosition);
            }
            finally { reopened.Close(); }
            Assert.Equal(before, File.ReadAllBytes(settingsPath));
        }
        finally
        {
            aiTask.SetValue(window, null);
            window.Close();
        }

        void StartDrag() => grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        void Drag(double x, double y) => grip.RaiseEvent(new DragDeltaEventArgs(x, y) { RoutedEvent = Thumb.DragDeltaEvent });
        void EndDrag(bool canceled = false) =>
            grip.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
    }
}
