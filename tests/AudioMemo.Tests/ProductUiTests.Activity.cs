using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyRecordingActivity(string language, Action<string> phase)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            UiLanguage = language,
            IdentifySpeakersOnStop = false,
            TranscribeOnStop = false
        };
        settings.Appearance.RainbowRecordingIndicator = language == "zh-CN";
        phase("recording automation settings");
        VerifyRecordingAutomationSettings(settings);
        settings.AutoStopOnSilence = true;
        settings.SilenceTimeoutSeconds = 1;
        var factory = new RecordingEngineTests.FakeFactory();
        var recorder = new RecordingEngine(factory.Create);
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, recorder);
        var root = Assert.IsType<Grid>(window.Content);
        try
        {
            Invoke(window, "ApplySettingsToControls");
            Invoke(window, "UpdateControls");
            root.Measure(new Size(768, 512));
            root.Arrange(new Rect(0, 0, 768, 512));
            Pump(root);
            var indicator = Assert.IsType<DockPanel>(window.FindName("ProcessingIndicator"));
            var progress = Assert.IsType<ProgressBar>(window.FindName("ProcessingProgress"));
            var label = Assert.IsType<TextBlock>(window.FindName("ProcessingLabel"));
            Assert.Null(window.TaskbarItemInfo.Overlay);
            Assert.Same(AppIcons.Logo, window.Icon);
            Assert.Equal(TaskbarItemProgressState.None, window.TaskbarItemInfo.ProgressState);
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);

            phase("recording overlays and automatic stop");
            CompleteUiTask(root, recorder.StartAsync(settings, "Synthetic silence stop",
                new(new SourceSelection("synthetic-mic", "Synthetic microphone", 1), null)));
            Invoke(window, "UpdateControls");
            Assert.False(Assert.IsType<ComboBox>(window.FindName("MainSpeechLanguageBox")).IsEnabled);
            Invoke(window, "UpdateRecordingDisplay");
            var recording = Assert.IsType<DrawingImage>(window.Icon);
            var pulse = Assert.IsType<DispatcherTimer>(typeof(MainWindow)
                .GetField("recordingPulseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            Assert.Null(window.TaskbarItemInfo.Overlay);
            VerifyTaskbarHeart(recording, BadgeColor(recording));
            var animates = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
            Assert.Equal(animates, pulse.IsEnabled);
            Assert.Equal(AppIcons.RecordingFrameInterval, pulse.Interval);
            if (animates)
            {
                var changes = new HashSet<ImageSource>();
                var property = DependencyPropertyDescriptor.FromProperty(Window.IconProperty, typeof(Window));
                EventHandler changed = (_, _) => changes.Add(window.Icon);
                property.AddValueChanged(window, changed);
                try
                {
                    window.WindowState = WindowState.Minimized;
                    Assert.False(window.IsVisible);
                    CompleteUiTask(root, Task.Delay(350));
                    Assert.True(changes.Count >= 2, "The recording icon must advance while the window is not visible.");
                    var expectedFrames = Enumerable.Range(0, settings.Appearance.RainbowRecordingIndicator
                            ? AppIcons.RainbowRecordingFrameCount : AppIcons.RecordingFrameCount)
                        .Select(index => AppIcons.RecordingAt(AppIcons.RecordingFrameInterval * index,
                            settings.Appearance.RainbowRecordingIndicator)).ToHashSet();
                    Assert.All(changes, icon => Assert.Contains(icon, expectedFrames));
                }
                finally
                {
                    property.RemoveValueChanged(window, changed);
                    window.WindowState = WindowState.Normal;
                }
            }
            else
                Assert.Same(AppIcons.Recording, recording);
            var light = Assert.IsType<System.Windows.Shapes.Ellipse>(window.FindName("RecordingDot"));
            Assert.Equal(14, light.Width);
            Assert.Equal(14, light.Height);
            CompleteUiTask(root, recorder.PauseAsync());
            Invoke(window, "UpdateControls");
            Assert.False(Assert.IsType<ComboBox>(window.FindName("MainSpeechLanguageBox")).IsEnabled);
            Invoke(window, "UpdateRecordingDisplay");
            var paused = Assert.IsType<DrawingImage>(window.Icon);
            VerifyTaskbarHeart(paused, Colors.DarkOrange);
            Assert.False(pulse.IsEnabled);
            CompleteUiTask(root, Task.Delay(160));
            Assert.Same(paused, window.Icon);
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "StopIfSilentAsync")));
            Assert.Equal(RecorderState.Paused, recorder.State);
            CompleteUiTask(root, recorder.ResumeAsync());
            Invoke(window, "UpdateRecordingDisplay");
            Assert.Same(animates ? AppIcons.RecordingAt(TimeSpan.Zero, settings.Appearance.RainbowRecordingIndicator)
                : AppIcons.Recording, window.Icon);
            Assert.Equal(animates, pulse.IsEnabled);
            var liveCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> live = (_, _, _) => liveCompletion.Task;
            Call(window, "StartAiJob", recorder.Session, "TranscribeResume", live);
            Assert.Equal(TaskbarItemProgressState.None, window.TaskbarItemInfo.ProgressState);
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);
            CompleteUiTask(root, RecordingEngineTests.WaitForSilenceAsync(recorder));
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "StopIfSilentAsync")));
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Null(window.TaskbarItemInfo.Overlay);
            Assert.Same(AppIcons.Logo, window.Icon);
            Assert.False(pulse.IsEnabled);
            var session = Assert.IsType<RecordingSession>(recorder.Session);
            Assert.Equal("Saved", session.Status);
            Assert.True(session.Frames >= PcmFormat.SampleRate);
            using (var audio = File.OpenRead(session.WavePath))
                Assert.Equal(session.Frames, DurableWaveFile.ValidateAndGetFrames(audio));
            Assert.Contains(UiText.Format("RecordingStoppedOnSilence", 1),
                Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            Assert.Equal(TaskbarItemProgressState.Indeterminate, window.TaskbarItemInfo.ProgressState);
            Assert.True(progress.IsIndeterminate);
            liveCompletion.SetResult();
            CompleteUiTask(root, ActiveUiAiTask(window));
            Assert.Equal(TaskbarItemProgressState.Normal, window.TaskbarItemInfo.ProgressState);
            var saved = EditedSessionTests.Hashes(session.Folder);
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "StopIfSilentAsync")));
            Assert.Equal(saved, EditedSessionTests.Hashes(session.Folder));

            phase("processing completion and activation");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = Assert.IsAssignableFrom<Task>(Call(window, "ExecuteAiAsync", session, "UpdateMainPoints",
                new Func<Task>(() => completion.Task)));
            Assert.Equal(Visibility.Visible, indicator.Visibility);
            Assert.True(progress.IsIndeterminate);
            Assert.Equal(TaskbarItemProgressState.Indeterminate, window.TaskbarItemInfo.ProgressState);
            Call(window, "OnActivated", EventArgs.Empty);
            Assert.Equal(TaskbarItemProgressState.Indeterminate, window.TaskbarItemInfo.ProgressState);
            completion.SetResult();
            CompleteUiTask(root, pending);
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(1, progress.Value);
            Assert.Equal(TaskbarItemProgressState.Normal, window.TaskbarItemInfo.ProgressState);
            Assert.Equal(1, window.TaskbarItemInfo.ProgressValue);
            Assert.Equal(UiText.Get("ProcessingComplete"), label.Text);
            if (!SystemParameters.HighContrast)
                Assert.Equal(Colors.ForestGreen, Assert.IsType<SolidColorBrush>(progress.Foreground).Color);
            Invoke(window, "UpdateControls");
            Invoke(window, "UpdateRecordingDisplay");
            Assert.Equal(TaskbarItemProgressState.Normal, window.TaskbarItemInfo.ProgressState);
            Call(window, "OnActivated", EventArgs.Empty);
            Assert.Equal(TaskbarItemProgressState.None, window.TaskbarItemInfo.ProgressState);
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);

            phase("processing failure and cancellation");
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "ExecuteAiAsync", session, "UpdateMainPoints",
                new Func<Task>(() => Task.FromException(new IOException("Synthetic processing failure"))))));
            Assert.Equal(TaskbarItemProgressState.Error, window.TaskbarItemInfo.ProgressState);
            Assert.Equal(UiText.Get("ProcessingFailed"), label.Text);
            Call(window, "Window_PreviewMouseDown", window, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left));
            Assert.Equal(TaskbarItemProgressState.None, window.TaskbarItemInfo.ProgressState);
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "ExecuteAiAsync", session, "UpdateMainPoints",
                new Func<Task>(() => Task.FromException(new OperationCanceledException())))));
            Assert.Equal(TaskbarItemProgressState.Paused, window.TaskbarItemInfo.ProgressState);
            Assert.Equal(UiText.Get("ProcessingCancelled"), label.Text);

            phase("queued and stale processing updates");
            IProgress<AiUpdate>? obsoleteProgress = null;
            Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> withWarning = (_, updates, _) =>
            {
                obsoleteProgress = updates;
                updates.Report(new(session.Id, "Synthetic queued warning", IsWarning: true));
                return Task.CompletedTask;
            };
            Call(window, "StartAiJob", session, "UpdateMainPoints", withWarning);
            CompleteUiTask(root, ActiveUiAiTask(window));
            Assert.Equal(TaskbarItemProgressState.Paused, window.TaskbarItemInfo.ProgressState);
            Assert.Equal(UiText.Get("ProcessingWarning"), label.Text);
            var nextCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IProgress<AiUpdate>? finishedProgress = null;
            Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> next = (_, updates, _) =>
            {
                finishedProgress = updates;
                return nextCompletion.Task;
            };
            Call(window, "StartAiJob", session, "UpdateMainPoints", next);
            obsoleteProgress!.Report(new(session.Id, "Obsolete warning must not affect this job", IsWarning: true));
            nextCompletion.SetResult();
            CompleteUiTask(root, ActiveUiAiTask(window));
            Assert.Equal(TaskbarItemProgressState.Normal, window.TaskbarItemInfo.ProgressState);
            Assert.DoesNotContain("Obsolete warning", Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            finishedProgress!.Report(new(session.Id, "Late warning after completion", IsWarning: true));
            Pump(root);
            Assert.DoesNotContain("Late warning", Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            foreach (var size in new[] { new Size(640, 420), new Size(1280, 720) })
            {
                phase($"processing indicator layout at {size}");
                root.Measure(size);
                root.Arrange(new Rect(size));
                Pump(root);
                var bounds = progress.TransformToAncestor(root).TransformBounds(new Rect(progress.RenderSize));
                Assert.True(bounds.Left >= 0 && bounds.Right <= size.Width && bounds.Bottom <= size.Height);
                Assert.True(progress.ActualWidth > 100 && progress.ActualHeight >= 4);
            }
            if (!SystemParameters.HighContrast)
            {
                var host = new Grid { Width = 300, Height = 20 };
                indicator.Children.Remove(progress);
                host.Children.Add(progress);
                try
                {
                    host.Measure(new Size(300, 20));
                    host.Arrange(new Rect(0, 0, 300, 20));
                    Pump(host);
                    var pixels = RenderPixels(host);
                    var offset = (10 * 300 + 150) * 4;
                    Assert.Equal(Colors.ForestGreen, Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]));
                }
                finally
                {
                    host.Children.Remove(progress);
                    indicator.Children.Add(progress);
                }
            }
            Assert.False(Assert.IsType<bool>(typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)));
            Call(window, "OnActivated", EventArgs.Empty);
            Assert.Equal(Visibility.Collapsed, indicator.Visibility);

            phase("automatic-stop export failure");
            settings.AudioOutput = new() { Channels = 1 };
            CompleteUiTask(root, recorder.StartAsync(settings, "Synthetic automatic-stop export failure",
                new(new SourceSelection("synthetic-mic", "Synthetic microphone", 1), null)));
            var failedExport = Assert.IsType<RecordingSession>(recorder.Session);
            Directory.CreateDirectory(failedExport.OutputPath);
            CompleteUiTask(root, RecordingEngineTests.WaitForSilenceAsync(recorder));
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "StopIfSilentAsync")));
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Null(window.TaskbarItemInfo.Overlay);
            Assert.Equal(TaskbarItemProgressState.Error, window.TaskbarItemInfo.ProgressState);
            Assert.Contains(failedExport.OutputPath, Assert.IsType<TextBlock>(window.FindName("WarningText")).Text);
            using (var audio = File.OpenRead(failedExport.WavePath))
                Assert.Equal(failedExport.Frames, DurableWaveFile.ValidateAndGetFrames(audio));
        }
        finally
        {
            phase("recording activity cleanup");
            CompleteUiTask(root, recorder.StopAsync());
            window.Close();
            Assert.False(Assert.IsType<DispatcherTimer>(typeof(MainWindow)
                .GetField("recordingPulseTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)).IsEnabled);
        }
    }

    private static void VerifyHeartbeatRecordingFrames()
    {
        var heartbeat = AppIcons.RecordingFrameInterval * AppIcons.RecordingFrameCount;
        Assert.Equal(TimeSpan.FromSeconds(2.4), heartbeat);
        Assert.Same(AppIcons.Recording, AppIcons.RecordingAt(TimeSpan.Zero));
        var plain = RenderIcon(AppIcons.Logo);
        Color[] spectrum = [Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta];
        foreach (var rainbow in new[] { false, true })
        {
            var count = rainbow ? AppIcons.RainbowRecordingFrameCount : AppIcons.RecordingFrameCount;
            var period = AppIcons.RecordingFrameInterval * count;
            Assert.Equal(TimeSpan.FromSeconds(rainbow ? 14.4 : 2.4), period);
            Assert.Throws<ArgumentOutOfRangeException>(() => AppIcons.RecordingAt(TimeSpan.FromTicks(-1), rainbow));
            var frames = Enumerable.Range(0, count)
                .Select(index => Assert.IsType<DrawingImage>(
                    AppIcons.RecordingAt(AppIcons.RecordingFrameInterval * index, rainbow))).ToArray();
            for (var index = 0; index < frames.Length; index++)
            {
                var phase = index % AppIcons.RecordingFrameCount;
                var color = BadgeColor(frames[index]);
                VerifyTaskbarHeart(frames[index], color);
                var artwork = Assert.IsType<ImageDrawing>(Assert.IsType<DrawingGroup>(frames[index].Drawing).Children[0]);
                Assert.Same(AppIcons.Logo, artwork.ImageSource);
                Assert.Equal(new Rect(0, 0, 64, 64), artwork.Rect);
                Assert.Same(frames[index], AppIcons.RecordingAt(period * 1000 + AppIcons.RecordingFrameInterval * index, rainbow));
                var previous = BadgeColor(frames[(index + count - 1) % count]);
                Assert.InRange(Math.Abs(color.A - previous.A), 0, 65);
                if (rainbow)
                    Assert.All(new[] { color.R - previous.R, color.G - previous.G, color.B - previous.B },
                        delta => Assert.InRange(Math.Abs(delta), 0, 8));
                else
                    Assert.Equal(Colors.Crimson, Color.FromRgb(color.R, color.G, color.B));
                if (phase is > 0 and <= 4 or > 8 and <= 14)
                    Assert.True(color.A <= previous.A);
                if (phase is >= 5 and <= 8 or >= 27)
                    Assert.True(color.A >= previous.A);
                if (phase == 4)
                    Assert.Equal(64, color.A);
                if (phase == 8)
                    Assert.Equal(217, color.A);
                if (phase == AppIcons.RecordingFrameCount / 2)
                {
                    Assert.Equal(0, color.A);
                    Assert.Equal(plain, RenderIcon(frames[index]));
                }
                if (phase == 0)
                {
                    Assert.Equal(rainbow ? spectrum[index / AppIcons.RecordingFrameCount] : Colors.Crimson, color);
                    Assert.Equal(new Rect(12, 12, 40, 40), HeartBounds(frames[index]));
                    var peak = RenderIcon(frames[index]);
                    for (var y = 0; y < 64; y++)
                        for (var x = 0; x < 64; x++)
                            if (x < 12 || x >= 52 || y < 12 || y >= 52)
                                for (var channel = 0; channel < 4; channel++)
                                    Assert.Equal(plain[(y * 64 + x) * 4 + channel], peak[(y * 64 + x) * 4 + channel]);
                }
            }
            Assert.Same(frames[0], AppIcons.RecordingAt(period, rainbow));
            Assert.Equal(new Rect(16, 18, 32, 32), HeartBounds(frames[AppIcons.RecordingFrameCount / 2]));
            Assert.True(HeartBounds(frames[4]).Width < HeartBounds(frames[8]).Width);
            Assert.True(HeartBounds(frames[8]).Width < HeartBounds(frames[0]).Width);
            Assert.True(HeartBounds(frames[0]).Top < HeartBounds(frames[8]).Top);
            Assert.True(HeartBounds(frames[8]).Top < HeartBounds(frames[4]).Top);
            foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64 })
            {
                var background = RenderIcon(AppIcons.Logo, size);
                Assert.Equal(background, RenderIcon(frames[AppIcons.RecordingFrameCount / 2], size));
                foreach (var image in new[] { frames[0], Assert.IsType<DrawingImage>(AppIcons.Paused) })
                {
                    var peak = RenderIcon(image, size);
                    var changed = new List<Point>();
                    for (var y = 0; y < size; y++)
                        for (var x = 0; x < size; x++)
                        {
                            var offset = (y * size + x) * 4;
                            if (!background.AsSpan(offset, 4).SequenceEqual(peak.AsSpan(offset, 4)))
                                changed.Add(new Point(x, y));
                        }
                    Assert.NotEmpty(changed);
                    Assert.Equal(Math.Floor(size * 3.0 / 16), changed.Min(point => point.X));
                    Assert.Equal(Math.Floor(size * 3.0 / 16), changed.Min(point => point.Y));
                    Assert.Equal(Math.Ceiling(size * 13.0 / 16) - 1, changed.Max(point => point.X));
                    Assert.Equal(Math.Ceiling(size * 13.0 / 16) - 1, changed.Max(point => point.Y));
                }
            }
            VerifyNativeRecordingIcon(frames[0], BadgeColor(frames[0]));
        }
    }

    private static void VerifyNativeRecordingIcon(ImageSource image, Color color)
    {
        var window = new Window
        {
            Icon = AppIcons.RecordingAt(AppIcons.RecordingFrameInterval * (AppIcons.RecordingFrameCount / 2)),
            ShowInTaskbar = false
        };
        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Assert.False(window.IsVisible);
            nint[] kinds = [0, 1];
            var backgrounds = kinds.ToDictionary(kind => kind, NativePixels);
            window.Icon = image;
            foreach (var kind in kinds)
            {
                var (pixels, size) = NativePixels(kind);
                var (background, backgroundSize) = backgrounds[kind];
                Assert.Equal(backgroundSize, size);
                var center = size / 2;
                var offset = (center * size + center) * 4;
                Assert.Equal(color, Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]));
                var minimum = Math.Floor(size * 3.0 / 16);
                var maximum = Math.Ceiling(size * 13.0 / 16);
                for (var y = 0; y < size; y++)
                    for (var x = 0; x < size; x++)
                        if (x < minimum || x >= maximum || y < minimum || y >= maximum)
                        {
                            var start = (y * size + x) * 4;
                            Assert.True(background.AsSpan(start, 4).SequenceEqual(pixels.AsSpan(start, 4)));
                        }
            }

            (byte[] Pixels, int Size) NativePixels(nint kind)
            {
                const uint getIcon = 0x007F;
                var icon = SendMessage(handle, getIcon, kind, 0);
                Assert.NotEqual(nint.Zero, icon);
                var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
                Assert.Equal(bitmap.PixelWidth, bitmap.PixelHeight);
                var size = bitmap.PixelWidth;
                var pixels = new byte[size * size * 4];
                converted.CopyPixels(pixels, size * 4, 0);
                return (pixels, size);
            }
        }
        finally { window.Close(); }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    private static Color BadgeColor(DrawingImage image) => Assert.IsType<SolidColorBrush>(
        Assert.IsType<GeometryDrawing>(Assert.IsType<DrawingGroup>(image.Drawing).Children[1]).Brush).Color;

    private static byte[] RenderIcon(ImageSource image, int size = 64)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(image, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[size * size * 4];
        bitmap.CopyPixels(pixels, size * 4, 0);
        return pixels;
    }

    private static Rect HeartBounds(DrawingImage image) =>
        Assert.IsType<GeometryDrawing>(Assert.IsType<DrawingGroup>(image.Drawing).Children[1]).Geometry.Bounds;

    private static void VerifyTaskbarHeart(DrawingImage image, Color color)
    {
        Assert.True(image.IsFrozen);
        Assert.Equal(new Rect(0, 0, 64, 64), image.Drawing.Bounds);
        var badge = Assert.IsType<GeometryDrawing>(Assert.IsType<DrawingGroup>(image.Drawing).Children[1]);
        Assert.Equal(color, Assert.IsType<SolidColorBrush>(badge.Brush).Color);
        var heart = Assert.IsType<StreamGeometry>(badge.Geometry);
        var bounds = heart.Bounds;
        Assert.InRange(bounds.Width, 32 - 0.0001, 40 + 0.0001);
        Assert.InRange(Math.Abs(bounds.Width - bounds.Height), 0, 0.0001);
        Assert.InRange(Math.Abs(bounds.Left + bounds.Width / 2 - 32), 0, 0.0001);
        Assert.InRange(bounds.Top + bounds.Height / 2, 32 - 0.0001, 34 + 0.0001);
        bool Filled(double x, double y) =>
            heart.FillContains(new Point(bounds.Left + x * bounds.Width, bounds.Top + y * bounds.Height));
        Assert.False(Filled(0.5, 0.05));
        Assert.True(Filled(0.25, 0.08));
        Assert.True(Filled(0.75, 0.08));
        Assert.True(Filled(0.5, 0.5));
        Assert.True(Filled(0.5, 0.9));
        Assert.False(Filled(0.1, 0.9));
        Assert.False(Filled(0.9, 0.9));
        Assert.Null(badge.Pen);
    }

    private static Task ActiveUiAiTask(MainWindow window) =>
        Assert.IsAssignableFrom<Task>(typeof(MainWindow).GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));

    private static void VerifyRecordingAutomationSettings(AppSettings original)
    {
        var window = new SettingsWindow(original, "recording");
        try
        {
            var root = Assert.IsType<DockPanel>(window.Content);
            root.Measure(new Size(900, 800));
            root.Arrange(new Rect(0, 0, 900, 800));
            Pump(root);
            var enabled = Assert.IsType<CheckBox>(window.FindName("AutoStopOnSilenceCheck"));
            var timeout = Assert.IsType<TextBox>(window.FindName("SilenceTimeoutBox"));
            var speakers = Assert.IsType<CheckBox>(window.FindName("SpeakersOnStopCheck"));
            var page = Assert.IsType<Border>(window.FindName("RecordingPage"));
            Assert.True(page.IsAncestorOf(enabled));
            Assert.True(page.IsAncestorOf(speakers));
            Assert.False(enabled.IsChecked);
            Assert.False(timeout.IsEnabled);
            Assert.False(speakers.IsChecked);
            Assert.Equal("300", timeout.Text);
            enabled.IsChecked = true;
            timeout.Text = "90";
            speakers.IsChecked = true;
            Pump(root);
            Assert.True(timeout.IsEnabled);
            Invoke(window, "ReadRecordingAutomation");
            Assert.True(window.Settings.AutoStopOnSilence);
            Assert.Equal(90, window.Settings.SilenceTimeoutSeconds);
            Assert.True(window.Settings.IdentifySpeakersOnStop);
            Assert.False(original.AutoStopOnSilence);
            Assert.False(original.IdentifySpeakersOnStop);
            Assert.Equal(300, original.SilenceTimeoutSeconds);
            timeout.Text = "0";
            var error = Assert.Throws<TargetInvocationException>(() => Invoke(window, "ReadRecordingAutomation"));
            Assert.IsType<InvalidDataException>(error.InnerException);
            timeout.Text = "600";
            enabled.IsChecked = false;
            speakers.IsChecked = false;
            Invoke(window, "ReadRecordingAutomation");
            Assert.False(window.Settings.AutoStopOnSilence);
            Assert.False(window.Settings.IdentifySpeakersOnStop);
            Assert.Equal(600, window.Settings.SilenceTimeoutSeconds);
        }
        finally { window.Close(); }
    }
}
