using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyWorkspaceSetup(Application application, string language)
    {
        VerifySummaryRefreshAndStatusBar(application, language);
        VerifySetupWizard(application, language);
        VerifySetupDownloadsAndCancellation(language);
    }

    private static void VerifySummaryRefreshAndStatusBar(Application application, string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        File.Delete(Path.Combine(session.Folder, "summary.json"));
        File.Delete(session.SummaryPath);
        var audio = File.ReadAllBytes(session.WavePath);
        var settings = OllamaTests.LocalSettings();
        settings.ReviewUnclearTranscript = false;
        settings.UiLanguage = language;
        settings.SaveFolder = folder.Root;
        settings.SetupCompleted = true;
        var storage = new AppStorage(folder.File("preferences"));
        storage.SaveSettings(settings);
        var summary = new SummaryFreshnessTests.Capture();
        var prompts = new List<string>();
        var approve = false;
        var window = new MainWindow(storage, settings,
            new RecordingEngine(new RecordingEngineTests.FakeFactory().Create),
            pipeline: new AiPipeline(summaryService: summary), confirmRepeatSummary: message =>
            {
                prompts.Add(message);
                return approve;
            });
        var root = Assert.IsType<Grid>(window.Content);
        try
        {
            Invoke(window, "ApplySettingsToControls");
            typeof(MainWindow).GetField("initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Invoke(window, "RefreshHistory");
            Assert.Null(typeof(MainWindow).GetField("libraryStatisticsTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
            var history = Assert.IsType<ListBox>(window.FindName("HistoryList"));
            history.SelectedIndex = 0;
            Call(window, "LoadSessionContent", session);
            Assert.Equal("LLM", Assert.IsType<TextBlock>(window.FindName("ProviderLabel")).Text);
            Assert.Equal(UiText.Get("NewConversationButton"), Assert.IsType<Button>(window.FindName("NewButton")).Content);
            Assert.Null(window.FindName("SummaryProviderText"));
            var chrome = WindowChrome.GetWindowChrome(window);
            Assert.True(chrome.CaptionHeight >= 40);
            Assert.False(chrome.UseAeroCaptionButtons);
            Assert.Equal(WindowStyle.None, window.WindowStyle);

            foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
            {
                AppAppearance.Apply(application, theme);
                foreach (var size in new[] { new Size(1440, 900), new Size(1080, 720), new Size(640, 480) })
                {
                    root.Measure(size);
                    root.Arrange(new Rect(size));
                    Pump(root);
                    var header = Assert.IsType<Border>(window.FindName("WindowHeader"));
                    var close = Assert.IsType<Button>(window.FindName("CloseWindowButton"));
                    var menuButton = Assert.IsType<Button>(window.FindName("AppMenuButton"));
                    Assert.Equal(size.Width, header.ActualWidth, 1);
                    Assert.Equal(header.ActualHeight, chrome.CaptionHeight, 1);
                    Assert.Equal(size.Width, close.TranslatePoint(new Point(close.ActualWidth, 0), root).X, 1);
                    Assert.True(menuButton.TranslatePoint(new Point(menuButton.ActualWidth, 0), root).X <
                        Assert.IsType<StackPanel>(window.FindName("WindowButtons")).TranslatePoint(new Point(), root).X);
                    Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(close));
                    Assert.True(WindowChrome.GetIsHitTestVisibleInChrome(menuButton));
                    Assert.Equal(UiText.Get("CloseAudioMemo"), AutomationProperties.GetName(close));
                    var logo = Assert.IsType<Image>(window.FindName("BrandLogo"));
                    var brand = Assert.IsType<TextBlock>(window.FindName("BrandText"));
                    Assert.Same(AppIcons.Logo, logo.Source);
                    Assert.Equal(24, logo.ActualWidth);
                    Assert.Equal(24, logo.ActualHeight);
                    Assert.False(logo.IsHitTestVisible);
                    Assert.False(logo.Focusable);
                    Assert.Same(logo, menuButton.Content);
                    Assert.True(logo.TranslatePoint(new Point(), root).X >= 0);
                    if (brand.Visibility == Visibility.Visible)
                        Assert.True(logo.TranslatePoint(new Point(logo.ActualWidth, 0), root).X <
                            brand.TranslatePoint(new Point(), root).X);
                    foreach (var name in new[] { "AppMenuButton", "MinimizeWindowButton", "MaximizeWindowButton", "CloseWindowButton" })
                    {
                        var caption = Assert.IsType<Button>(window.FindName(name));
                        Assert.True(caption.Focusable && caption.IsTabStop);
                        Assert.Same(window.FindResource("CaptionKeyboardFocus"), caption.FocusVisualStyle);
                        Assert.DoesNotContain(caption.Template.Triggers.OfType<Trigger>(),
                            trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty ||
                                trigger.Property == UIElement.IsFocusedProperty);
                        var focusTemplate = Assert.IsType<ControlTemplate>(caption.FocusVisualStyle.Setters.OfType<Setter>()
                            .Single(setter => setter.Property == Control.TemplateProperty).Value);
                        Assert.Equal(new Thickness(1), Assert.IsType<Border>(focusTemplate.LoadContent()).BorderThickness);
                    }
                    if (size.Width == 1440)
                    {
                        var title = Assert.IsType<TextBlock>(window.FindName("MainPointsTitle"));
                        var update = Assert.IsType<Button>(window.FindName("SummarizeButton"));
                        var paneClose = Assert.IsType<Button>(window.FindName("CloseMainPointsButton"));
                        Assert.Null(window.FindName("ExportButton"));
                        Assert.True(title.TranslatePoint(new Point(title.ActualWidth, 0), root).X <= update.TranslatePoint(new Point(), root).X);
                        Assert.True(update.TranslatePoint(new Point(update.ActualWidth, 0), root).X <= paneClose.TranslatePoint(new Point(), root).X);
                        Assert.InRange(Math.Abs(update.TranslatePoint(new Point(0, update.ActualHeight / 2), root).Y -
                            title.TranslatePoint(new Point(0, title.ActualHeight / 2), root).Y), 0, 1);
                    }
                }
            }
            window.WindowState = WindowState.Maximized;
            Invoke(window, "UpdateWindowButtons");
            Assert.Equal(UiText.Get("WindowRestore"), AutomationProperties.GetName(
                Assert.IsType<Button>(window.FindName("MaximizeWindowButton"))));
            window.WindowState = WindowState.Normal;
            Invoke(window, "UpdateWindowButtons");
            Assert.Equal(UiText.Get("WindowMaximize"), AutomationProperties.GetName(
                Assert.IsType<Button>(window.FindName("MaximizeWindowButton"))));

            Summarize();
            Assert.Empty(prompts);
            Assert.Single(summary.Batches);
            var provenance = Assert.IsType<TextBox>(window.FindName("SummaryBox"));
            var summaryTitle = Assert.IsType<TextBlock>(window.FindName("MainPointsTitle"));
            Assert.Equal(60000, ToolTipService.GetShowDuration(summaryTitle));
            Assert.Equal(SummaryPresentation.ForDisplay(AppStorage.LoadSummaryText(session)), provenance.Text);
            Assert.Contains(settings.OllamaModel, Assert.IsType<string>(summaryTitle.ToolTip).Split('\n')[0]);
            var firstModel = settings.OllamaModel;
            settings.OllamaModel = "new-selected-model";
            Invoke(window, "UpdateModelStatus");
            Assert.Contains(firstModel, Assert.IsType<string>(summaryTitle.ToolTip).Split('\n')[0]);
            Assert.Contains(UiText.Format("SelectedLlmModel", SummaryProviders.Label(settings.SummaryProvider, UiText.Current),
                settings.OllamaModel), Assert.IsType<string>(summaryTitle.ToolTip));
            Assert.DoesNotContain(firstModel, provenance.Text);
            Assert.DoesNotContain("new-selected-model", provenance.Text);
            var before = EditedSessionTests.Hashes(session.Folder);
            Summarize();
            Assert.Single(prompts);
            Assert.Contains("new-selected-model", prompts[0]);
            Assert.Single(summary.Batches);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
            approve = true;
            Summarize();
            Assert.Equal(2, summary.Batches.Count);
            Assert.Empty(summary.Batches[1].Previous);
            Assert.Contains("new-selected-model", Assert.IsType<string>(summaryTitle.ToolTip).Split('\n')[0]);
            Assert.DoesNotContain("new-selected-model", provenance.Text);
            Assert.NotNull(AppStorage.LoadSummaryDocument(session)!.GenerationId);

            approve = false;
            var promptCount = prompts.Count;
            AppStorage.RenameSpeaker(session, "spkr1", "Changed speaker");
            Call(window, "LoadSessionContent", session);
            Summarize();
            Assert.Equal(promptCount, prompts.Count);
            Assert.Equal(3, summary.Batches.Count);
            AppStorage.SetSpeakerColor(session, "spkr1", "#445566");
            File.WriteAllText(session.NotesPath, "PRIVATE NEW NOTE");
            Call(window, "LoadSessionContent", session);
            Summarize();
            Assert.Equal(promptCount + 1, prompts.Count);
            Assert.Equal(3, summary.Batches.Count);
            Assert.Equal(audio, File.ReadAllBytes(session.WavePath));
            Assert.DoesNotContain(summary.Batches, batch => batch.Text.Contains("PRIVATE", StringComparison.Ordinal));

            var menu = Assert.IsType<ContextMenu>(window.FindName("StatusBarMenu"));
            Call(window, "StatusBarMenu_Opened", menu, new RoutedEventArgs());
            var options = menu.Items.OfType<MenuItem>().Where(item => item.Tag is string).ToArray();
            Assert.Equal(6, options.Length);
            Assert.All(options, item => Assert.True(item.IsCheckable && item.StaysOpenOnClick));
            foreach (var item in options)
            {
                item.IsChecked = true;
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(storage.LoadSettings().StatusBar.Get((string)item.Tag));
            }
            FinishStatistics();
            Call(window, "UpdateStatusInformation", false);
            FinishStatistics();
            Assert.Equal(UiText.Format("StatusRecordingCount", 1),
                Assert.IsType<TextBlock>(window.FindName("RecordingCountStatusText")).Text);
            Assert.DoesNotContain(UiText.Get("StatusCalculating"),
                Assert.IsType<TextBlock>(window.FindName("LibrarySizeStatusText")).Text);
            Assert.DoesNotContain(UiText.Get("StatusUnavailable"),
                Assert.IsType<TextBlock>(window.FindName("FreeDiskStatusText")).Text);
            var sizeText = Assert.IsType<TextBlock>(window.FindName("LibrarySizeStatusText"));
            var shownSize = sizeText.Text;
            sizeText.Text = "Newer library result";
            var staleVersion = (long)typeof(MainWindow)
                .GetField("statisticsVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)! - 1;
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "ReadLibraryStatisticsAsync", staleVersion,
                folder.Root, new[] { session.Folder }, true, false)));
            Assert.Equal("Newer library result", sizeText.Text);
            sizeText.Text = shownSize;
            foreach (var name in new[] { "Messages", "Llm" })
            {
                var item = options.Single(item => Equals(item.Tag, name));
                item.IsChecked = false;
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.False(storage.LoadSettings().StatusBar.Get(name));
            }
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(window.FindName("ModelStatusText")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(window.FindName("StatusText")).Visibility);
            Call(window, "SetStatus", "Synthetic warning still visible", true, "SyntheticTest");
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("WarningBanner")).Visibility);
            Call(window, "ClearWorkspace");
            Assert.Empty(provenance.Text);
            Assert.Equal(UiText.Get("MainPointsNotGenerated"), Assert.IsType<string>(summaryTitle.ToolTip).Split('\n')[0].TrimEnd('\r'));
        }
        finally
        {
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "CancelAiAsync")));
            FinishStatistics();
            window.Close();
            AppAppearance.Apply(application, AppThemes.Light);
        }

        void Summarize()
        {
            Call(window, "Summarize_Click", window, new RoutedEventArgs());
            CompleteUiTask(root, ActiveUiAiTask(window));
            Pump(root);
        }

        void FinishStatistics()
        {
            if (typeof(MainWindow).GetField("libraryStatisticsTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is Task task)
                CompleteUiTask(root, task);
        }
    }

    private static void VerifySetupWizard(Application application, string language)
    {
        using var folder = new TestFolder();
        var storage = new AppStorage(folder.File("preferences"));
        var original = new AppSettings { UiLanguage = language, SaveFolder = folder.File("recordings") };
        var environment = new FakeSetupEnvironment();
        var wizard = new SetupWizardWindow(storage, original, environment);
        var root = Assert.IsType<DockPanel>(wizard.Content);
        try
        {
            CompleteUiTask(root, wizard.RefreshAudioAsync());
            var microphone = Assert.IsType<CheckBox>(wizard.FindName("WizardMicEnabled"));
            var playback = Assert.IsType<CheckBox>(wizard.FindName("WizardPlaybackEnabled"));
            microphone.IsChecked = false;
            playback.IsChecked = false;
            SetupNext(wizard, root);
            Assert.Equal(0, SetupStep(wizard));
            Assert.Contains(UiText.Get("AtLeastOneDevice"), Assert.IsType<TextBlock>(wizard.FindName("WizardStatus")).Text);
            CompleteUiTask(root, wizard.RefreshAudioAsync());
            Assert.False(microphone.IsChecked);
            microphone.IsChecked = true;
            environment.AudioError = true;
            SetupNext(wizard, root);
            Assert.Equal(0, SetupStep(wizard));
            Assert.Contains("Synthetic device access failure", Assert.IsType<TextBlock>(wizard.FindName("WizardStatus")).Text);
            environment.AudioError = false;
            SetupNext(wizard, root);
            Assert.Equal(1, SetupStep(wizard));
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(wizard.FindName("SkipButton")).Visibility);
            var saving = Assert.IsType<TextBox>(wizard.FindName("WizardFolderBox"));
            saving.Text = "relative-folder";
            SetupNext(wizard, root);
            Assert.Equal(1, SetupStep(wizard));
            saving.Text = original.SaveFolder;
            SetupNext(wizard, root);
            Assert.Equal(2, SetupStep(wizard));
            Call(wizard, "Skip_Click", wizard, new RoutedEventArgs());
            Assert.Equal(3, SetupStep(wizard));
            Assert.False(Assert.IsType<CheckBox>(wizard.FindName("WizardLiveTextCheck")).IsChecked);
            Assert.IsType<CheckBox>(wizard.FindName("WizardAutoSummaryCheck")).IsChecked = true;
            Call(wizard, "Skip_Click", wizard, new RoutedEventArgs());
            Assert.Equal(4, SetupStep(wizard));
            Assert.Contains(original.SaveFolder, Assert.IsType<TextBlock>(wizard.FindName("ReviewText")).Text);
            Assert.Equal(0, environment.Downloads);
            var pending = wizard.ValidateAndBuildAsync(CancellationToken.None);
            CompleteUiTask(root, pending);
            var configured = pending.GetAwaiter().GetResult();
            Assert.True(configured.SetupCompleted);
            Assert.False(configured.LiveTranscription);
            Assert.False(configured.TranscribeOnStop);
            Assert.False(configured.SummarizeOnStop);
            Assert.True(configured.SummarySetupDeferred);
            Assert.Equal("synthetic-microphone", configured.MicrophoneId);
            Assert.False(configured.PlaybackEnabled);
            Assert.False(original.SetupCompleted);
            Assert.True(original.TranscribeOnStop);
            Assert.False(storage.LoadSettings().SetupCompleted);
            wizard.Settings.Language = "ja";
            Invoke(wizard, "LoadFields");
            Assert.Equal("ja", Assert.IsType<ComboBox>(wizard.FindName("WizardSpeechLanguageBox")).SelectedValue);
            Invoke(wizard, "ApplyLanguage");
            Assert.Equal("ja", Assert.IsType<ComboBox>(wizard.FindName("WizardSpeechLanguageBox")).SelectedValue);

            foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
            {
                AppAppearance.Apply(application, theme);
                for (var step = 0; step < 5; step++)
                {
                    typeof(SetupWizardWindow).GetField("step", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wizard, step);
                    Invoke(wizard, "UpdateStep");
                    root.Measure(new Size(540, 388));
                    root.Arrange(new Rect(0, 0, 540, 388));
                    Pump(root);
                    var next = Assert.IsType<Button>(wizard.FindName("NextButton"));
                    Assert.True(next.TranslatePoint(new Point(next.ActualWidth, next.ActualHeight), root).Y <= root.ActualHeight + 1);
                    Assert.True(next.ActualWidth >= 100);
                    Assert.NotEmpty(Assert.IsType<TextBlock>(wizard.FindName("StepTitle")).Text);
                }
            }
            var settings = new SettingsWindow(configured, "appearance");
            var nestedSettings = new SettingsWindow(configured, "summary", allowSetupReset: false);
            try
            {
                Assert.Equal(Visibility.Visible, Assert.IsType<Button>(settings.FindName("RunSetupButton")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Button>(nestedSettings.FindName("RunSetupButton")).Visibility);
                Func<IProgress<double>, CancellationToken, Task> fakeDownload = (_, token) => Task.Delay(Timeout.Infinite, token);
                var downloading = Assert.IsAssignableFrom<Task>(Call(settings, "RunDownloadAsync", "Synthetic download", fakeDownload));
                Assert.False(Assert.IsType<Button>(settings.FindName("RunSetupButton")).IsEnabled);
                Call(settings, "CancelDownload_Click", settings, new RoutedEventArgs());
                CompleteUiTask(Assert.IsAssignableFrom<FrameworkElement>(settings.Content), downloading);
                Assert.True(Assert.IsType<Button>(settings.FindName("RunSetupButton")).IsEnabled);
                settings.ShowInTaskbar = false;
                new WindowInteropHelper(settings).EnsureHandle();
                Assert.False(settings.IsVisible);
                var reset = new SetupResetWindow(settings);
                try
                {
                    Assert.Equal(SetupResetMode.KeepSettings, reset.Choice);
                    var keep = Assert.IsType<RadioButton>(typeof(SetupResetWindow)
                        .GetField("keep", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reset));
                    Assert.True(keep.IsChecked);
                    Assert.Contains(UiText.Get("SetupKeepSettings"), Assert.IsType<TextBlock>(keep.Content).Text);
                }
                finally { reset.Close(); }
            }
            finally { settings.Close(); nestedSettings.Close(); }
        }
        finally
        {
            wizard.Close();
            AppAppearance.Apply(application, AppThemes.Light);
        }
    }

    private static void VerifySetupDownloadsAndCancellation(string language)
    {
        using var folder = new TestFolder();
        var storage = new AppStorage(folder.File("preferences"));
        var settings = new AppSettings
        {
            UiLanguage = language, SetupCompleted = true, SaveFolder = folder.File("recordings"),
            LiveTranscription = true, TranscribeOnStop = false, IdentifySpeakersOnStop = false
        };
        var environment = new FakeSetupEnvironment { HoldDownload = true };
        var wizard = new SetupWizardWindow(storage, settings, environment);
        var root = Assert.IsType<DockPanel>(wizard.Content);
        var closed = false;
        wizard.Closed += (_, _) => closed = true;
        try
        {
            CompleteUiTask(root, wizard.RefreshAudioAsync());
            Assert.IsType<CheckBox>(wizard.FindName("WizardPlaybackEnabled")).IsChecked = false;
            typeof(SetupWizardWindow).GetField("step", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(wizard, 2);
            Invoke(wizard, "UpdateStep");
            environment.SpeechRuntimeAvailable = false;
            SetupNext(wizard, root);
            Assert.Equal(2, SetupStep(wizard));
            Assert.Contains(UiText.Get("SetupRuntimeMissing"), Assert.IsType<TextBlock>(wizard.FindName("WizardStatus")).Text);
            environment.SpeechRuntimeAvailable = true;
            Call(wizard, "DownloadSpeech_Click", wizard, new RoutedEventArgs());
            Assert.False(Assert.IsType<Button>(wizard.FindName("NextButton")).IsEnabled);
            Assert.Equal(Visibility.Visible, Assert.IsType<Button>(wizard.FindName("CancelOperationButton")).Visibility);
            Call(wizard, "CancelOperation_Click", wizard, new RoutedEventArgs());
            CompleteUiTask(root, SetupOperation(wizard));
            Assert.True(Assert.IsType<Button>(wizard.FindName("NextButton")).IsEnabled);
            Assert.Equal(UiText.Get("DownloadCancelled"), Assert.IsType<TextBlock>(wizard.FindName("WizardStatus")).Text);
            environment.HoldDownload = false;
            Call(wizard, "DownloadSpeech_Click", wizard, new RoutedEventArgs());
            CompleteUiTask(root, SetupOperation(wizard));
            Assert.Contains("small", environment.Models);
            SetupNext(wizard, root);
            Assert.Equal(2, SetupStep(wizard));
            Assert.Contains(UiText.Get("SetupSpeechNotReady"), Assert.IsType<TextBlock>(wizard.FindName("WizardStatus")).Text);
            Call(wizard, "DownloadBase_Click", wizard, new RoutedEventArgs());
            CompleteUiTask(root, SetupOperation(wizard));
            SetupNext(wizard, root);
            Assert.Equal(3, SetupStep(wizard));
            environment.HoldDownload = true;
            Call(wizard, "DownloadBase_Click", wizard, new RoutedEventArgs());
            var running = SetupOperation(wizard);
            wizard.Close();
            CompleteUiTask(root, running);
            Pump(root);
            Assert.True(closed);
            Assert.False(storage.LoadSettings().SetupCompleted);
        }
        finally
        {
            if (!closed)
            {
                Call(wizard, "CancelOperation_Click", wizard, new RoutedEventArgs());
                CompleteUiTask(root, SetupOperation(wizard));
                wizard.Close();
            }
        }

        var freshEnvironment = new FakeSetupEnvironment { HoldDownload = true };
        freshEnvironment.Models.Add("base");
        var fresh = new SetupWizardWindow(storage,
            new AppSettings { UiLanguage = language, SaveFolder = folder.File("fresh-recordings") }, freshEnvironment);
        var freshRoot = Assert.IsType<DockPanel>(fresh.Content);
        try
        {
            CompleteUiTask(freshRoot, fresh.RefreshAudioAsync());
            Call(fresh, "DownloadSpeech_Click", fresh, new RoutedEventArgs());
            Call(fresh, "CancelOperation_Click", fresh, new RoutedEventArgs());
            CompleteUiTask(freshRoot, SetupOperation(fresh));
            Pump(freshRoot);
            Assert.False(Assert.IsType<CheckBox>(fresh.FindName("WizardLiveTextCheck")).IsChecked);
            Assert.False(Assert.IsType<CheckBox>(fresh.FindName("WizardTranscribeCheck")).IsChecked);
            freshEnvironment.HoldDownload = false;
            Call(fresh, "DownloadSpeech_Click", fresh, new RoutedEventArgs());
            CompleteUiTask(freshRoot, SetupOperation(fresh));
            Pump(freshRoot);
            Assert.True(Assert.IsType<CheckBox>(fresh.FindName("WizardLiveTextCheck")).IsChecked);
            Assert.True(Assert.IsType<CheckBox>(fresh.FindName("WizardTranscribeCheck")).IsChecked);
            fresh.Settings.SummaryProvider = SummaryProviders.Ollama;
            typeof(SetupWizardWindow).GetField("step", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fresh, 3);
            Call(fresh, "Skip_Click", fresh, new RoutedEventArgs());
            var validated = fresh.ValidateAndBuildAsync(CancellationToken.None);
            CompleteUiTask(freshRoot, validated);
            Assert.True(validated.Result.SummarySetupDeferred);
            Assert.False(validated.Result.SummarizeOnStop);
            Assert.False(storage.LoadSettings().SetupCompleted);
        }
        finally
        {
            Call(fresh, "CancelOperation_Click", fresh, new RoutedEventArgs());
            CompleteUiTask(freshRoot, SetupOperation(fresh));
            fresh.Close();
        }
    }

    private static void SetupNext(SetupWizardWindow wizard, FrameworkElement root)
    {
        Call(wizard, "Next_Click", wizard, new RoutedEventArgs());
        CompleteUiTask(root, SetupOperation(wizard));
    }

    private static int SetupStep(SetupWizardWindow wizard) =>
        (int)typeof(SetupWizardWindow).GetField("step", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wizard)!;

    private static Task SetupOperation(SetupWizardWindow wizard) =>
        (Task?)typeof(SetupWizardWindow).GetField("operationTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wizard)
            ?? Task.CompletedTask;

    private sealed class FakeSetupEnvironment : ISetupEnvironment
    {
        public bool AudioError { get; set; }
        public bool SpeechRuntimeAvailable { get; set; } = true;
        public bool SpeakerModelsReady => false;
        public bool HoldDownload { get; set; }
        public int Downloads { get; private set; }
        public HashSet<string> Models { get; } = [];
        public bool IsSpeechModelReady(string model) => Models.Contains(model);
        public Task<SetupAudioDevices> ListAudioAsync(CancellationToken cancellationToken) => Task.FromResult(
            new SetupAudioDevices([new("synthetic-microphone", "Synthetic microphone")], []));
        public Task ValidateAudioAsync(RecordingSources sources, CancellationToken cancellationToken)
        {
            sources.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            return AudioError ? Task.FromException(new IOException("Synthetic device access failure.")) : Task.CompletedTask;
        }
        public async Task DownloadSpeechAsync(string model, IProgress<double> progress, CancellationToken cancellationToken)
        {
            Downloads++;
            progress.Report(50);
            if (HoldDownload)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Models.Add(model);
            progress.Report(100);
        }
        public Task DownloadSpeakersAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No speaker download was requested by this synthetic fixture.");
    }
}
