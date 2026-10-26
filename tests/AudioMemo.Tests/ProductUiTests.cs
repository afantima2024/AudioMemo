using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using AudioMemo.Controls;
using AudioMemo.Audio;
using AudioMemo.Services;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Tests;

[CollectionDefinition("Product UI", DisableParallelization = true)]
public sealed class ProductUiCollection;

[Collection("Product UI")]
public sealed partial class ProductUiTests
{
    [Fact]
    public void AllProviderSettingsAndWorkspaceRenderInBothLanguagesWithoutOpeningAWindow()
    {
        Exception? failure = null;
        var phase = "initializing";
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        void ReportPhase(string value) => phase = $"{value} (started at {elapsed.Elapsed.TotalSeconds:F1}s)";
        var thread = new Thread(() =>
        {
            var previousLanguage = UiText.Current.Culture.Name;
            System.Windows.Application? application = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                application = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                AppAppearance.Apply(application, AppThemes.Light);
                application.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                {
                    Source = new Uri("/AudioMemo;component/Resources/AppStyles.xaml", UriKind.Relative)
                });
                ReportPhase("taskbar heartbeat frames and native icons");
                VerifyHeartbeatRecordingFrames();
                foreach (var language in new[] { "en", "zh-CN" })
                {
                    UiText.Current.ChangeLanguage(language);
                    ReportPhase($"{language}: compact copyright, user guide, and shortcut reference");
                    VerifyCompactHelpAndGuide(application, language);
                    ReportPhase($"{language}: model selection, message history, and recoverable editing");
                    VerifyWorkspaceRefinement(language);
                    ReportPhase($"{language}: transcript uncertainty review and approval");
                    VerifyTranscriptUncertaintyReview(application, language);
                    ReportPhase($"{language}: safe close, deferred processing, and reopening");
                    VerifyProcessingResume(language);
                    ReportPhase($"{language}: transcript selection and compact rows");
                    VerifyTranscriptSelectionPersistence(language);
                    ReportPhase($"{language}: floating transcript navigation");
                    VerifyFloatingTranscriptNavigation(application, language);
                    VerifyTranscriptNavigationPersistence(language);
                    ReportPhase($"{language}: display-only speaker colors and shared unlabeled colors");
                    VerifyUnlabeledSpeakerColors();
                    foreach (var provider in new[] { SummaryProviders.Copilot, SummaryProviders.Ollama }
                        .Concat(CloudLlmCatalog.Providers.Select(item => item.Id)))
                    {
                        ReportPhase($"{language}: {provider} settings");
                        var settings = new AppSettings
                        {
                            UiLanguage = language,
                            SummaryProvider = provider,
                            OllamaModel = "deepseek-r1:671b",
                            SpeechVocabularyTerms = ["ExistingName"]
                        };
                        var window = new SettingsWindow(settings);
                        try
                        {
                            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
                            var local = Assert.IsType<StackPanel>(window.FindName("LocalSettingsPanel"));
                            var copilot = Assert.IsType<StackPanel>(window.FindName("CopilotSettingsPanel"));
                            Assert.Equal(provider == SummaryProviders.Ollama
                                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed, local.Visibility);
                            Assert.Equal(provider == SummaryProviders.Copilot
                                ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed, copilot.Visibility);
                            var cloud = Assert.IsType<StackPanel>(window.FindName("CloudSettingsPanel"));
                            Assert.Equal(CloudLlmCatalog.IsProvider(provider) ? Visibility.Visible : Visibility.Collapsed, cloud.Visibility);
                            Assert.Equal(provider, Assert.IsType<ComboBox>(window.FindName("SummaryProviderBox")).SelectedValue);
                            Assert.Equal("deepseek-r1:671b", Assert.IsType<ComboBox>(window.FindName("OllamaModelBox")).Text);
                            Assert.Equal("ollama pull qwen2.5:7b", Assert.IsType<TextBox>(window.FindName("ModelDownloadCommand")).Text);
                            Assert.Equal(new UiText(language)["SaveSettings"], Assert.IsType<Button>(window.FindName("SaveButton")).Content);
                            var speechLanguage = Assert.IsType<ComboBox>(window.FindName("LanguageBox"));
                            Assert.False(speechLanguage.IsEditable);
                            Assert.Equal(["auto", "zh", "en"], speechLanguage.Items.Cast<LanguageOption>().Select(option => option.Code));
                            Assert.Equal(new UiText(language)["SpeechLanguageChinese"],
                                speechLanguage.Items.Cast<LanguageOption>().Single(option => option.Code == "zh").Label);
                            Assert.False(window.Settings.CloudConsent);
                            Assert.True(Assert.IsType<CheckBox>(window.FindName("ReviewUnclearTranscriptCheck")).IsChecked);
                            Assert.Equal(new[] { "auto", "cpu", "vulkan" },
                                Assert.IsType<ComboBox>(window.FindName("AccelerationBox")).Items.Cast<LanguageOption>().Select(option => option.Code));
                            Assert.Equal(3, Assert.IsType<ComboBox>(window.FindName("SpeechModelBox")).Items.Count);
                            var vocabulary = Assert.IsType<TextBox>(window.FindName("SpeechVocabularyBox"));
                            Assert.Equal(SpeechVocabulary.MaximumInputLength, vocabulary.MaxLength);
                            Assert.Equal("ExistingName", vocabulary.Text);
                            vocabulary.Text = "AudioMemo\r\n\u5343\u554f";
                            Assert.IsType<CheckBox>(window.FindName("UseSpeechVocabularyCheck")).IsChecked = false;
                            Invoke(window, "ReadSpeechVocabulary");
                            Assert.Equal(["AudioMemo", "\u5343\u95ee"], window.Settings.SpeechVocabularyTerms);
                            Assert.False(window.Settings.SpeechVocabularyEnabled);
                            Assert.True(settings.SpeechVocabularyEnabled);
                            Assert.Equal(["ExistingName"], settings.SpeechVocabularyTerms);
                            if (CloudLlmCatalog.IsProvider(provider))
                                VerifyCloudSettings(window, settings, provider);
                        }
                        finally { window.Close(); }
                    }
                    ReportPhase($"{language}: recording workspace");
                    VerifyRecordingWorkspace(language);
                    VerifyTranscriptReviewControls(language);
                    VerifyRecoveryDialog(language);
                    VerifyCloudModelWorkspace(language);
                    VerifyTranscriptCorrectionDialog(language);
                    VerifyTranscriptCorrectionCancellation(language);
                    ReportPhase($"{language}: import, history width, and online comparison");
                    VerifyImportedHistoryAndWidth(language);
                    VerifyVideoTrackDialog(application, language);
                    VerifySlimDividers(application, language);
                    ReportPhase($"{language}: elapsed processing time and step estimates");
                    VerifyProcessingTiming(application, language);
                    ReportPhase($"{language}: unified header, summary refresh, status options, and first-use setup");
                    VerifyWorkspaceSetup(application, language);
                    ReportPhase($"{language}: logo menu and standalone help");
                    VerifyApplicationMenu(application, language);
                    ReportPhase($"{language}: Me speaker tag and editable personal summaries");
                    VerifyPersonalNotes(application, language);
                    ReportPhase($"{language}: whole-recording export from transcript and pane shortcuts");
                    VerifyTranscriptAudioExport(language);
                    ReportPhase($"{language}: recording multi-selection");
                    VerifyHistoryMultiSelection(language);
                    VerifyHistoryBatchActions(language);
                    ReportPhase($"{language}: confirmed batch recycling");
                    VerifyHistoryBatchRecycle(language);
                    ReportPhase($"{language}: closing during batch recycling");
                    VerifyClosingHistoryRecycle(language);
                    VerifyOnlineComparisonWindow(language);
                    VerifyOnlineComparisonCancellation(language);
                    VerifyComparisonColors(application, language);
                    ReportPhase($"{language}: polished workspace");
                    VerifyPolishedWorkspace(language);
                    ReportPhase($"{language}: workspace controls, resumable recognition, and dialog appearance");
                    VerifyWorkspaceImprovements(application, language, value => ReportPhase($"{language}: {value}"));
                    ReportPhase($"{language}: final overall summary");
                    VerifyOverallSummaryWorkspace(language);
                    ReportPhase($"{language}: device confirmation and session continuation");
                    VerifyRecordingDeviceWarnings(language);
                    VerifyRecordingWorkflow(language);
                    VerifyRecordingActivity(language, value => ReportPhase($"{language}: {value}"));
                    ReportPhase($"{language}: closing completed hidden workspaces");
                    CloseHiddenWorkspaces();
                    ReportPhase($"{language}: search and themes");
                    VerifySearchAndThemes(application, language);
                    CloseHiddenWorkspaces();
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                UiText.Current.ChangeLanguage(previousLanguage);
                application?.Shutdown();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(300)), $"The in-memory WPF workspace check did not finish. Last phase: {phase}.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyCloudSettings(SettingsWindow window, AppSettings original, string provider)
    {
        var definition = CloudLlmCatalog.Get(provider);
        var model = Assert.IsType<ComboBox>(window.FindName("CloudModelBox"));
        var endpoint = Assert.IsType<ComboBox>(window.FindName("CloudEndpointBox"));
        var password = Assert.IsType<PasswordBox>(window.FindName("CloudApiKeyBox"));
        var consent = Assert.IsType<CheckBox>(window.FindName("CloudApiConsentCheck"));
        Assert.Equal(definition.Models, model.Items.Cast<string>());
        Assert.Equal(definition.DefaultEndpoint, endpoint.Text);
        Assert.False(consent.IsChecked);
        Assert.Empty(password.Password);
        Assert.Equal(definition.SupportsModelListing ? Visibility.Visible : Visibility.Collapsed,
            Assert.IsType<Button>(window.FindName("ReadCloudModelsButton")).Visibility);
        Assert.Equal(provider == CloudLlmCatalog.Qwen ? Visibility.Visible : Visibility.Collapsed,
            Assert.IsType<TextBlock>(window.FindName("QwenEndpointHint")).Visibility);
        Assert.Equal(new UiText(original.UiLanguage)[provider == CloudLlmCatalog.Doubao ? "DoubaoModelHint" : "CloudPresetHint"],
            Assert.IsType<TextBlock>(window.FindName("CloudModelHint")).Text);
        model.Text = provider == CloudLlmCatalog.Doubao ? "ep-synthetic-test-deployment" : "custom-text-model";
        password.Password = CloudLlmTests.TestKey;
        consent.IsChecked = true;
        Invoke(window, "ReadCloudFields");
        Assert.True(window.Settings.CanSummarize);
        Assert.Equal(CloudLlmTests.TestKey, CloudApiKey.Read(provider, window.Settings.GetCloudProfile(provider)));
        Assert.Empty(password.Password);
        Assert.DoesNotContain(CloudLlmTests.TestKey, System.Text.Json.JsonSerializer.Serialize(window.Settings));
        Assert.Empty(original.CloudModels);

        var selection = Assert.IsType<ComboBox>(window.FindName("SummaryProviderBox"));
        var other = provider == CloudLlmCatalog.Qwen ? CloudLlmCatalog.DeepSeek : CloudLlmCatalog.Qwen;
        selection.SelectedValue = other;
        Assert.Equal(other, selection.SelectedValue);
        Assert.False(consent.IsChecked);
        Assert.False(CloudApiKey.IsConfigured(other, window.Settings.GetCloudProfile(other)));
        selection.SelectedValue = provider;
        Assert.Equal(provider == CloudLlmCatalog.Doubao ? "ep-synthetic-test-deployment" : "custom-text-model", model.Text);
        Assert.True(consent.IsChecked);
        Assert.Empty(password.Password);

        if (provider == CloudLlmCatalog.Qwen)
        {
            endpoint.Text = definition.Endpoints[1];
            Invoke(window, "ReadCloudFields");
            Assert.False(window.Settings.CanSummarize);
            Assert.False(consent.IsChecked);
            Assert.False(CloudApiKey.IsConfigured(provider, window.Settings.GetCloudProfile(provider)));
            password.Password = "synthetic-second-endpoint-key";
            consent.IsChecked = true;
            Invoke(window, "ReadCloudFields");
            Assert.True(window.Settings.CanSummarize);
            Assert.Equal("synthetic-second-endpoint-key", CloudApiKey.Read(provider, window.Settings.GetCloudProfile(provider)));
            endpoint.Text = definition.DefaultEndpoint;
            Invoke(window, "ReadCloudFields");
            Assert.False(consent.IsChecked);
            Assert.False(window.Settings.CanSummarize);
            Assert.Equal(CloudLlmTests.TestKey, CloudApiKey.Read(provider, window.Settings.GetCloudProfile(provider)));
        }

        var validModel = model.Text;
        model.Text = "invalid model with spaces";
        selection.SelectedValue = other;
        Assert.Equal(provider, selection.SelectedValue);
        Assert.Contains(new UiText(original.UiLanguage)["InvalidCloudModel"],
            Assert.IsType<TextBlock>(window.FindName("SettingsStatus")).Text);
        model.Text = validModel;
        password.Password = "synthetic-replacement-key";
        Assert.IsType<CheckBox>(window.FindName("ForgetCloudKeyCheck")).IsChecked = true;
        selection.SelectedValue = other;
        Assert.Equal(provider, selection.SelectedValue);
        Assert.Contains(new UiText(original.UiLanguage)["CloudKeyEditConflict"],
            Assert.IsType<TextBlock>(window.FindName("SettingsStatus")).Text);
        Assert.Equal(CloudLlmTests.TestKey, CloudApiKey.Read(provider, window.Settings.GetCloudProfile(provider)));
        password.Clear();
        Invoke(window, "ReadCloudFields");
        Assert.False(CloudApiKey.IsConfigured(provider, window.Settings.GetCloudProfile(provider)));
        Assert.Empty(original.CloudModels);
        Assert.Null(window.DialogResult);
    }

    private static void VerifyCloudModelWorkspace(string language)
    {
        using var folder = new TestFolder();
        foreach (var provider in CloudLlmCatalog.Providers)
        {
            var settings = CloudLlmTests.CloudSettings(provider.Id);
            settings.UiLanguage = language;
            settings.SaveFolder = folder.Root;
            var window = new MainWindow(new AppStorage(folder.File("preferences")), settings);
            Invoke(window, "ApplySettingsToControls");
            Invoke(window, "UpdateModelStatus");
            Invoke(window, "UpdateControls");
            var model = Assert.IsType<ComboBox>(window.FindName("MainModelBox"));
            var providerBox = Assert.IsType<ComboBox>(window.FindName("MainProviderBox"));
            Assert.Equal(provider.Id, providerBox.SelectedValue);
            Assert.Equal("unit-model", model.Text);
            Assert.Equal(provider.Models, model.Items.Cast<string>());
            Assert.Equal(5, providerBox.Items.Count);
            var status = Assert.IsType<TextBlock>(window.FindName("ModelStatusText")).Text;
            Assert.Contains(new UiText(language)[provider.LabelKey], status);
            Assert.Contains("unit-model", status);
            Assert.Contains(new Uri(settings.ActiveSummaryEndpoint).Host,
                Assert.IsType<TextBlock>(window.FindName("ModelStatusText")).ToolTip?.ToString());
            Assert.Null(window.FindName("SummaryProviderText"));
            Assert.Empty(Assert.IsType<TextBox>(window.FindName("SummaryBox")).Text);
            Assert.True(Assert.IsType<Button>(window.FindName("ApplyModelButton")).IsEnabled);
            var root = Assert.IsType<Grid>(window.Content);
            foreach (var width in new[] { 1080d, 1440d })
            {
                root.Measure(new Size(width, 900));
                root.Arrange(new Rect(0, 0, width, 900));
                Pump(root);
                var apply = Assert.IsType<Button>(window.FindName("ApplyModelButton"));
                var languageBox = Assert.IsType<ComboBox>(window.FindName("MainSpeechLanguageBox"));
                Assert.True(model.ActualWidth > 150);
                Assert.True(apply.TranslatePoint(new Point(apply.ActualWidth, 0), root).X <
                    languageBox.TranslatePoint(new Point(), root).X);
                Assert.True(Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel")).ActualHeight > 400);
            }
        }
    }

    private static void VerifyRecordingWorkspace(string language)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { UiLanguage = language, SaveFolder = folder.Root };
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings);
        Invoke(window, "ApplySettingsToControls");
        Invoke(window, "UpdateControls");
        Assert.Null(window.FindName("PauseButton"));
        Assert.Null(window.FindName("HistoryToggleButton"));
        Assert.Equal(new UiText(language)["StartRecording"], Assert.IsType<Button>(window.FindName("StartButton")).Content);
        Assert.Null(window.FindName("RecordingTitle"));
        Assert.Null(window.FindName("FormatBox"));
        var root = Assert.IsType<System.Windows.Controls.Grid>(window.Content);
        root.Measure(new Size(1440, 900));
        root.Arrange(new Rect(0, 0, 1440, 900));
        var transcript = Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel"));
        var document = new TranscriptDocument
        {
            Segments = Enumerable.Range(0, 150).Select(index => new TranscriptSegment(index * 2, index * 2 + 1,
                $"Synthetic sentence {index}.")).ToList()
        };
        transcript.SetDocument(document);
        Pump(root);
        Assert.True(transcript.ActualHeight > 400, $"Transcript area is only {transcript.ActualHeight}px.");
        Assert.True(transcript.IsFollowing);
        var scroll = FindChild<ScrollViewer>(Assert.IsType<DataGrid>(transcript.FindName("RowsGrid")))!;
        Assert.NotNull(scroll);
        Assert.True(scroll.ScrollableHeight > 0);
        scroll.ScrollToVerticalOffset(100);
        Pump(root);
        Assert.False(transcript.IsFollowing);
        var offset = scroll.VerticalOffset;
        document.Segments.Add(new(301, 303, "Appended sentence."));
        transcript.SetDocument(document);
        Pump(root);
        Assert.False(transcript.IsFollowing);
        Assert.InRange(Math.Abs(scroll.VerticalOffset - offset), 0, 1);
        Assert.DoesNotContain("[00:", transcript.AllText());
        Assert.Contains("[00:05:01]", transcript.AllText(true));
        scroll.ScrollToEnd();
        Pump(root);
        Assert.True(transcript.IsFollowing);
        scroll.ScrollToHome();
        Pump(root);
        Assert.False(transcript.IsFollowing);
        transcript.JumpToLatest();
        Pump(root);
        Assert.True(transcript.IsFollowing);
        Assert.InRange(Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset), 0, 1);
        VerifyTranscriptPaging(transcript, root, scroll);
        document.Segments.Add(new(304, 306, "Following the appended tail."));
        transcript.SetDocument(document);
        Pump(root);
        Assert.True(transcript.IsFollowing);
        Assert.InRange(Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset), 0, 1);
        var grid = Assert.IsType<DataGrid>(transcript.FindName("RowsGrid"));
        grid.SelectedItem = grid.Items[^1];
        document.Segments[^1] = document.Segments[^1] with { Text = "An updated selected sentence." };
        transcript.SetDocument(document);
        Pump(root);
        Assert.Equal("An updated selected sentence.", Assert.IsType<TranscriptRow>(grid.SelectedItem).Text);
        settings.HistoryVisible = false;
        Invoke(window, "UpdateHistoryVisibility");
        Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("HistoryPanel")).Visibility);
        var peekTab = Assert.IsType<Button>(window.FindName("HistoryPeekTab"));
        var history = Assert.IsType<Border>(window.FindName("HistoryPanel"));
        VerifyHistoryTabPosition(window, settings, root);
        Assert.Equal(Visibility.Visible, peekTab.Visibility);
        Assert.Equal(0, Assert.IsType<ColumnDefinition>(window.FindName("HistoryColumn")).Width.Value);
        window.GetType().GetMethod("RevealHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [peekTab, new RoutedEventArgs()]);
        Pump(root);
        Assert.Equal(Visibility.Visible, history.Visibility);
        Assert.Equal(2, Grid.GetColumnSpan(history));
        Assert.Equal(210, history.ActualWidth);
        Assert.Equal(0, Assert.IsType<ColumnDefinition>(window.FindName("HistoryColumn")).Width.Value);
        Assert.False(settings.HistoryVisible);
        var peek = Assert.IsType<HistoryPeekState>(window.GetType().GetField("historyPeek", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
        peek.UpdatePointer(true, TimeSpan.Zero);
        Assert.False(peek.Expire(TimeSpan.FromMinutes(2)));
        peek.UpdatePointer(false, TimeSpan.FromMinutes(2));
        Assert.True(peek.Expire(TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(5)));
        Invoke(window, "UpdateHistoryVisibility");
        Assert.Equal(Visibility.Collapsed, history.Visibility);
        void RevealPreview() => window.GetType().GetMethod("RevealHistory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [peekTab, new RoutedEventArgs()]);
        MouseButtonEventArgs Click(DependencyObject source, MouseButton button = MouseButton.Left, bool handled = false)
        {
            var mouse = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent,
                Source = source,
                Handled = handled
            };
            window.RaiseEvent(mouse);
            return mouse;
        }
        RevealPreview();
        Assert.False(Click(Assert.IsType<ListBox>(window.FindName("HistoryList"))).Handled);
        Assert.True(peek.IsOpen);
        Click(peekTab);
        Assert.True(peek.IsOpen);
        Click(Assert.IsType<ListBox>(window.FindName("HistoryList")).ContextMenu);
        Assert.True(peek.IsOpen);
        Assert.False(Click(Assert.IsType<TextBox>(window.FindName("NotesBox"))).Handled);
        Assert.False(peek.IsOpen);
        Assert.Equal(Visibility.Collapsed, history.Visibility);
        RevealPreview();
        Click(Assert.IsType<Button>(window.FindName("AppMenuButton")), MouseButton.Right, handled: true);
        Assert.False(peek.IsOpen);
        Assert.Equal(Visibility.Collapsed, history.Visibility);
        RevealPreview();
        window.GetType().GetMethod("Window_Deactivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, EventArgs.Empty]);
        Assert.False(peek.IsOpen);
        RevealPreview();
        object[] messageArguments = [nint.Zero, 0x00A1, nint.Zero, nint.Zero, false];
        window.GetType().GetMethod("WindowMessageHook", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, messageArguments);
        Assert.False(peek.IsOpen);
        Assert.False((bool)messageArguments[4]);
        RevealPreview();
        window.GetType().GetMethod("HistoryMenu_Closed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [history, new RoutedEventArgs()]);
        Assert.False(peek.IsOpen);
        settings.HistoryVisible = true;
        Invoke(window, "UpdateHistoryVisibility");
        Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("HistoryPanel")).Visibility);
        Click(Assert.IsType<TextBox>(window.FindName("NotesBox")));
        window.GetType().GetMethod("Window_Deactivated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [window, EventArgs.Empty]);
        Assert.True(settings.HistoryVisible);
        Assert.Equal(Visibility.Visible, history.Visibility);
        foreach (var width in new[] { 1080d, 1440d })
        {
            root.Measure(new Size(width, 900));
            root.Arrange(new Rect(0, 0, width, 900));
            Pump(root);
            var model = Assert.IsType<ComboBox>(window.FindName("MainModelBox"));
            var modelApply = Assert.IsType<Button>(window.FindName("ApplyModelButton"));
            var languageBox = Assert.IsType<ComboBox>(window.FindName("MainSpeechLanguageBox"));
            Assert.True(model.ActualWidth > 150);
            Assert.True(modelApply.TranslatePoint(new Point(modelApply.ActualWidth, 0), root).X <
                languageBox.TranslatePoint(new Point(), root).X);
        }
    }

    private static void VerifyTranscriptCorrectionDialog(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var settings = new AppSettings { UiLanguage = language, SaveFolder = folder.Root };
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        fake.Worker.Response = (_, number, _) => Task.FromResult<IReadOnlyList<TranscriptSegment>>(
            number == 1 ? [new(0, 0.5, "Recognized selection 1.")] : []);
        IReadOnlyList<string>? savedVocabulary = null;
        var window = new TranscriptEditWindow(session, TranscriptCorrectionTests.Plan(session, 0, 2), settings,
            false, (terms, enabled) => { Assert.True(enabled); savedVocabulary = terms; },
            TranscriptCorrectionTests.Service(fake));
        try
        {
            var root = Assert.IsType<Grid>(window.Content);
            root.Measure(new Size(1060, 740));
            root.Arrange(new Rect(0, 0, 1060, 740));
            Pump(root);
            var grid = Assert.IsType<DataGrid>(window.FindName("CorrectionRows"));
            Assert.Equal(2, grid.Items.Count);
            Assert.All(grid.Columns.Take(3), column => Assert.True(column.IsReadOnly));
            Assert.False(grid.Columns[3].IsReadOnly);
            var rows = grid.Items.Cast<TranscriptEditWindow.CorrectionRow>().ToArray();
            var baseline = rows.Select(row => row.LocalText).ToArray();
            var bounds = rows.Select(row => row.Time).ToArray();
            grid.CurrentCell = new DataGridCellInfo(rows[0], grid.Columns[3]);
            Assert.True(grid.BeginEdit());
            Pump(root);
            var editor = Assert.IsType<TextBox>(grid.Columns[3].GetCellContent(rows[0]));
            editor.Text = "AudioMemo corrected preview.";
            editor.Select(0, "AudioMemo".Length);
            Assert.Equal(editor.Text, rows[0].Text);
            var remember = Assert.IsType<Button>(window.FindName("RememberSelectionButton"));
            Assert.True(remember.IsEnabled);
            remember.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            rows[1].Text = "Keep this unsaved manual correction.";
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
            var vocabulary = Assert.IsType<TextBox>(window.FindName("CorrectionVocabularyBox"));
            Assert.Equal(SpeechVocabulary.MaximumInputLength, vocabulary.MaxLength);
            Assert.Equal("AudioMemo", vocabulary.Text);
            Assert.Null(savedVocabulary);
            Assert.IsType<Button>(window.FindName("SaveVocabularyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(["AudioMemo"], savedVocabulary);
            Assert.Empty(settings.SpeechVocabularyTerms);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
            Assert.IsType<ComboBox>(window.FindName("CorrectionModelBox")).SelectedValue = "base";
            Assert.IsType<ComboBox>(window.FindName("CorrectionLanguageBox")).SelectedValue = "en";
            CompleteUiTask(root, BeginSelectedRecognition(window));
            Assert.Equal("Recognized selection 1.", rows[0].Text);
            Assert.Equal("Keep this unsaved manual correction.", rows[1].Text);
            Assert.Equal(baseline, rows.Select(row => row.LocalText));
            Assert.Equal(TranscriptComparisonState.Different, rows[0].ComparisonState);
            Assert.Equal(TranscriptComparisonState.Unclear, rows[1].ComparisonState);
            Assert.True(rows[1].Unrecognized);
            var missingRow = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(rows[1]));
            Assert.Equal(new UiText(language)["SelectionRowNoSpeech"], missingRow.ToolTip);
            Assert.Equal(bounds, rows.Select(row => row.Time));
            Assert.Equal(["AudioMemo"], fake.Vocabulary);
            Assert.Equal("en", fake.Language);
            Assert.Equal("base", fake.Model!.Id);
            Assert.Null(window.ResultDocument);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
            Assert.True(Assert.IsType<Button>(window.FindName("SaveCorrectionButton")).IsEnabled);
            Assert.Equal(new UiText(language)["SaveTranscriptChanges"],
                Assert.IsType<Button>(window.FindName("SaveCorrectionButton")).Content);
        }
        finally { window.Close(); }
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    private static void VerifySearchAndThemes(System.Windows.Application application, string language)
    {
        Color? lightSurface = null;
        foreach (var theme in new[] { AppThemes.Light, AppThemes.Dark })
        {
            AppAppearance.Apply(application, theme);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            using var folder = new TestFolder();
            var settings = new AppSettings { UiLanguage = language, Theme = theme, SaveFolder = folder.Root };
            var window = new SettingsWindow(settings);
            try
            {
                var root = Assert.IsType<DockPanel>(window.Content);
                root.Measure(new Size(980, 780));
                root.Arrange(new Rect(0, 0, 980, 780));
                Pump(root);
                var surface = Assert.IsType<SolidColorBrush>(window.FindResource("SolidBackgroundFillColorQuarternaryBrush")).Color;
                var ink = Assert.IsType<SolidColorBrush>(window.FindResource("TextFillColorPrimaryBrush")).Color;
                var muted = Assert.IsType<SolidColorBrush>(window.FindResource("TextFillColorSecondaryBrush")).Color;
                Assert.Equal(255, surface.A);
                Assert.True((theme == AppThemes.Dark) == (surface.R < 100),
                    $"Theme {theme}: Surface={surface}; window token={window.FindResource("SolidBackgroundFillColorQuarternary")}; " +
                    $"app token={application.FindResource("SolidBackgroundFillColorQuarternary")}; " +
                    $"dictionaries={string.Join(", ", application.Resources.MergedDictionaries.Select(dictionary => dictionary.Source))}");
                if (theme == AppThemes.Light)
                    lightSurface = surface;
                else
                    Assert.NotEqual(lightSurface, surface);
                var assigned = new Dictionary<string, string> { ["spkr1"] = "#C6E8D5", ["spkr2"] = "#66384C" };
                foreach (var (id, color) in assigned)
                {
                    var rgb = RgbColor.Parse(color);
                    var background = Assert.IsType<SolidColorBrush>(SpeakerColors.ForSpeaker(id, assigned, false)).Color;
                    Assert.Equal(AppAppearance.Brush(rgb).Color, background);
                    AssertReadable(AppAppearance.Brush(rgb.ReadableText).Color, background);
                }
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(SpeakerColors.ForSpeaker("spkr3", assigned, false)).Color);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(SpeakerColors.ForSpeaker(null, assigned, false)).Color);
                Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(SpeakerColors.ForSpeaker("spkr1", assigned, true)).Color);
                var selector = Assert.IsType<ComboBox>(window.FindName("ThemeBox"));
                Assert.Equal([AppThemes.Light, AppThemes.Dark, AppThemes.System],
                    selector.Items.Cast<LanguageOption>().Select(option => option.Code));
                Assert.Equal(theme, selector.SelectedValue);
                selector.SelectedValue = AppThemes.System;
                Invoke(window, "ReadTheme");
                Assert.Equal(AppThemes.System, window.Settings.Theme);
                Assert.Equal(theme, settings.Theme);
                Assert.Equal(surface, Assert.IsType<SolidColorBrush>(window.FindResource("SolidBackgroundFillColorQuarternaryBrush")).Color);
                var password = Assert.IsType<PasswordBox>(window.FindName("CloudApiKeyBox"));
                Assert.True(password.Style.BasedOn is not null);
                var input = Assert.IsType<TextBox>(window.FindName("SaveFolderBox"));
                var checkbox = Assert.IsType<CheckBox>(window.FindName("UseSpeechVocabularyCheck"));
                foreach (var displayedTheme in new[] { theme, theme == AppThemes.Light ? AppThemes.Dark : AppThemes.Light, theme })
                {
                    AppAppearance.Apply(application, displayedTheme);
                    Pump(root);
                    var background = Assert.IsType<SolidColorBrush>(input.Background).Color;
                    var foreground = Assert.IsType<SolidColorBrush>(input.Foreground).Color;
                    Assert.Equal(displayedTheme == AppThemes.Dark, background.R < 100);
                    AssertReadable(Blend(foreground, background), background);
                    Assert.Equal(foreground, Assert.IsType<SolidColorBrush>(password.Foreground).Color);
                    Assert.Equal(foreground, Assert.IsType<SolidColorBrush>(selector.Foreground).Color);
                    Assert.Equal(foreground, Assert.IsType<SolidColorBrush>(checkbox.Foreground).Color);
                    Assert.Equal(displayedTheme == AppThemes.Dark, Assert.IsType<SolidColorBrush>(window.Background).Color.R < 100);
                }
                VerifyAppearanceControls(application, window, settings, root);
            }
            finally { window.Close(); }
            VerifyTranscriptSearchControls(language);
            if (theme == AppThemes.Dark)
            {
                VerifyRecordingWorkspace(language);
                VerifyTranscriptCorrectionDialog(language);
                VerifyRecoveryDialog(language);
            }
        }
        AppAppearance.Apply(application, AppThemes.System);
#pragma warning disable WPF0001
        Assert.Equal(ThemeMode.System, application.ThemeMode);
#pragma warning restore WPF0001
        AppAppearance.Apply(application, AppThemes.Light);
        Assert.Throws<InvalidDataException>(() => AppAppearance.Apply(application, "not-a-theme"));
    }

    private static void VerifyTranscriptSearchControls(string language)
    {
        var view = new TranscriptView();
        var root = new Grid { Width = 690, Height = 440 };
        root.Children.Add(view);
        var document = new TranscriptDocument
        {
            ProcessedFrames = 40L * PcmFormat.SampleRate,
            Segments = Enumerable.Range(0, 40).Select(index => new TranscriptSegment(index, index + 1,
                index == 5 ? "Friday Friday planning starts now." : index == 25 ? "Frdiay project review." :
                $"Unrelated sentence number {index}.")
            { SpeakerId = index % 2 == 0 ? "spkr1" : "spkr2" }).ToList()
        };
        view.SetDocument(document, true);
        root.Measure(new Size(690, 440));
        root.Arrange(new Rect(0, 0, 690, 440));
        Pump(root);
        var grid = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
        grid.ScrollIntoView(grid.Items[0]);
        Pump(root);
        var first = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(first.Background).Color);
        document.SpeakersIdentified = true;
        view.SetDocument(document);
        Pump(root);
        Assert.Equal(Colors.Transparent, Assert.IsType<SolidColorBrush>(first.Background).Color);
        document.SetSpeakerColor("spkr1", "#C6E8D5");
        view.SetDocument(document);
        Pump(root);
        var tint = Assert.IsType<SolidColorBrush>(first.Background).Color;
        Assert.NotEqual(Colors.Transparent, tint);
        Assert.Equal(tint, Pixel(first, 8, 4));
        document.RenameSpeaker("spkr1", "Jamie");
        view.SetDocument(document);
        Pump(root);
        first = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(0));
        Assert.Equal(tint, Assert.IsType<SolidColorBrush>(first.Background).Color);
        var unchangedText = view.AllText();
        var searchHost = SearchPanel(view);
        var search = Assert.IsType<TextBox>(searchHost.FindName("SearchBox"));
        var fuzzy = Assert.IsType<CheckBox>(searchHost.FindName("FuzzySearchCheck"));
        var status = Assert.IsType<TextBlock>(searchHost.FindName("SearchStatus"));
        VerifySearchPopup(view, root, searchHost);
        Assert.Equal(TranscriptSearch.MaximumQueryLength, search.MaxLength);
        search.Text = "Friday";
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal(40, grid.Items.Count);
        Assert.Equal(UiText.Format("SearchMatches", 1, 2), status.Text);
        Assert.False(view.IsFollowing);
        Assert.Equal(5, grid.SelectedIndex);
        var selectedText = FindChild<TranscriptTextBox>(Assert.IsType<ContentPresenter>(grid.Columns[^1].GetCellContent(grid.SelectedItem)));
        Assert.NotNull(selectedText);
        Assert.Equal(2, selectedText.Matches.Count);
        Assert.Equal(0, selectedText.ActiveMatch!.Start);
        Assert.NotEmpty(selectedText.MatchRectangles(selectedText.ActiveMatch));
        var highlighted = RenderPixels(selectedText);
        var highlights = selectedText.Matches;
        var active = selectedText.ActiveMatch;
        selectedText.SetMatches([], null);
        Pump(root);
        Assert.NotEqual(highlighted, RenderPixels(selectedText));
        selectedText.SetMatches(highlights, active);
        selectedText.Select(0, 6);
        Assert.Equal("Friday", selectedText.SelectedText);
        selectedText.Select(0, 0);
        Assert.IsType<Button>(searchHost.FindName("SearchNextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(UiText.Format("SearchMatches", 2, 2), status.Text);
        Assert.Equal(7, selectedText.ActiveMatch!.Start);
        Assert.IsType<Button>(searchHost.FindName("SearchNextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(UiText.Format("SearchMatches", 1, 2), status.Text);
        Assert.True(HandleSearchKey(view, Key.F, ModifierKeys.Control, false));
        Assert.Equal(search.Text, search.SelectedText);
        Assert.True(HandleSearchKey(view, Key.F3, ModifierKeys.Shift, false));
        Assert.Equal(UiText.Format("SearchMatches", 2, 2), status.Text);
        Assert.True(HandleSearchKey(view, Key.Enter, ModifierKeys.None, true));
        Assert.Equal(UiText.Format("SearchMatches", 1, 2), status.Text);
        Assert.True(HandleSearchKey(view, Key.Enter, ModifierKeys.Shift, true));
        Assert.Equal(UiText.Format("SearchMatches", 2, 2), status.Text);
        Assert.True(HandleSearchKey(view, Key.F3, ModifierKeys.None, false));
        Assert.Equal(UiText.Format("SearchMatches", 1, 2), status.Text);
        Assert.False(HandleSearchKey(view, Key.Enter, ModifierKeys.None, false));
        Assert.False(HandleSearchKey(view, Key.End, ModifierKeys.Control, false));
        search.Select(0, 0);
        fuzzy.IsChecked = true;
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal(UiText.Format("SearchMatches", 1, 3), status.Text);
        Assert.Equal(40, grid.Items.Count);
        Assert.Equal(unchangedText, view.AllText());
        view.SetSpeakerFilter(["spkr1"]);
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal(20, grid.Items.Count);
        Assert.Equal(new UiText(language)["SearchNoMatches"], status.Text);
        Assert.False(Assert.IsType<Button>(searchHost.FindName("SearchNextButton")).IsEnabled);
        view.SetSpeakerFilter(null);
        CompleteUiTask(root, BeginTranscriptSearch(view));
        fuzzy.IsChecked = false;
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.IsType<Button>(searchHost.FindName("SearchNextButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        document.Segments.Add(new(40, 41, "Friday at the tail.") { SpeakerId = "spkr1" });
        view.SetDocument(document);
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal(UiText.Format("SearchMatches", 2, 3), status.Text);
        Assert.Equal(41, grid.Items.Count);
        search.Text = "project";
        var obsolete = BeginTranscriptSearch(view);
        search.Text = "definitely absent";
        CompleteUiTask(root, Task.WhenAll(obsolete, BeginTranscriptSearch(view)));
        Assert.Equal(new UiText(language)["SearchNoMatches"], status.Text);
        search.Text = "Friday";
        var pending = BeginTranscriptSearch(view);
        view.JumpToLatest();
        CompleteUiTask(root, pending);
        Assert.True(view.IsFollowing);
        Assert.Equal(grid.Items.Count - 1, grid.SelectedIndex);
        Assert.IsType<Button>(searchHost.FindName("ClearSearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Empty(status.Text);
        Assert.Equal(41, grid.Items.Count);
        search.Text = "Friday";
        view.SetDocument(new(), reset: true);
        Assert.Empty(search.Text);
        Assert.Empty(grid.Items);
        VerifyActualRowColors(view, root, grid);
        VerifyWrappedSearchNavigation(view, root, grid, search, language);
    }

    private static bool HandleSearchKey(TranscriptView view, Key key, ModifierKeys modifiers, bool searchFocused) =>
        Assert.IsType<bool>(view.GetType().GetMethod("HandleSearchKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(view, [key, modifiers, searchFocused]));

    private static void VerifyWrappedSearchNavigation(TranscriptView view, Grid root, DataGrid grid, TextBox search, string language)
    {
        var phrase = language == "en" ? "The project review continues. " : "\u9879\u76ee\u8ba1\u5212\u6b63\u5728\u8ba8\u8bba\u3002";
        var text = string.Concat(Enumerable.Repeat(phrase, 180)) + " UniqueNeedle \U0001f4dd";
        view.SetDocument(new()
        {
            Segments = [new(0, 120, text) { SpeakerId = "spkr1" }],
            SpeakersIdentified = true
        }, reset: true);
        Pump(root);
        var scroll = FindChild<ScrollViewer>(grid)!;
        scroll.ScrollToTop();
        Pump(root);
        search.Text = "UniqueNeedle";
        CompleteUiTask(root, BeginTranscriptSearch(view));
        Assert.Equal(UiText.Format("SearchMatches", 1, 1), Assert.IsType<TextBlock>(SearchPanel(view).FindName("SearchStatus")).Text);
        Assert.Same(scroll, view.GetType().GetField("scroll", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view));
        var box = FindChild<TranscriptTextBox>(Assert.IsType<ContentPresenter>(grid.Columns[^1].GetCellContent(grid.SelectedItem)))!;
        var rectangle = Assert.Single(box.MatchRectangles(box.ActiveMatch!));
        var viewport = FindChild<ScrollContentPresenter>(scroll)!;
        var visible = box.TransformToAncestor(viewport).TransformBounds(rectangle);
        Assert.True(visible.Top >= -1 && visible.Bottom <= viewport.ActualHeight + 1,
            $"Matched text is outside the viewport: {visible}, viewport height {viewport.ActualHeight}; " +
            $"scroll={scroll.VerticalOffset}/{scroll.ScrollableHeight}, extent={scroll.ExtentHeight}, viewport={scroll.ViewportHeight}; " +
            $"row height={((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0)).ActualHeight}, " +
            $"box height={box.ActualHeight}, text extent={box.ExtentHeight}, text offset={box.VerticalOffset}.");
        Assert.True(scroll.VerticalOffset > 0);
        Assert.False(view.IsFollowing);
        Assert.True(HandleSearchKey(view, Key.Escape, ModifierKeys.None, true));
        Assert.Empty(search.Text);
        Assert.Empty(box.Matches);
        Assert.False(HandleSearchKey(view, Key.Escape, ModifierKeys.None, true));

        var wrapped = new TranscriptTextBox
        {
            Text = "\u4f1a\u8bae\u8bb0\u5f55 \U0001f4dd Alpha \u9879\u76ee\u8ba1\u5212",
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap,
            IsReadOnly = true,
            Width = 85,
            Style = (Style)view.FindResource("TranscriptCellText")
        };
        var host = new Grid { Width = 85, Height = 200 };
        host.Children.Add(wrapped);
        host.Measure(new Size(85, 200));
        host.Arrange(new Rect(0, 0, 85, 200));
        Pump(host);
        var ranges = wrapped.MatchRectangles(new(0, 0, wrapped.Text.Length));
        Assert.True(ranges.Count >= 2);
        Assert.All(ranges, range => Assert.True(range.Width > 2 && range.Height > 0 && range.Right <= wrapped.ActualWidth + 1));
        wrapped.Select(0, wrapped.Text.Length);
        Assert.Equal(wrapped.Text, wrapped.SelectedText);
    }

    private static Task BeginTranscriptSearch(TranscriptView view) =>
        Assert.IsAssignableFrom<Task>(view.GetType().GetMethod("RunSearchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null));

    private static byte[] RenderPixels(FrameworkElement element)
    {
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(element.RenderSize),
                Stretch = Stretch.Fill
            }, null, new Rect(element.RenderSize));
        image.Render(visual);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels;
    }

    private static Color Blend(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        return Color.FromRgb((byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }

    private static void AssertReadable(Color foreground, Color background)
    {
        static double Channel(byte value)
        {
            var fraction = value / 255d;
            return fraction <= 0.04045 ? fraction / 12.92 : Math.Pow((fraction + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var first = Luminance(foreground);
        var second = Luminance(background);
        var contrast = (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
        Assert.True(contrast >= 4.5, $"Text contrast is only {contrast:F2}: {foreground} on {background}.");
    }

    private static void VerifyTranscriptCorrectionCancellation(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        fake.Worker.Response = async (path, _, token) =>
        {
            fake.Worker.HeldAudio = File.OpenRead(path);
            fake.Worker.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        };
        var window = new TranscriptEditWindow(session, TranscriptCorrectionTests.Plan(session, 0),
            new() { UiLanguage = language, SaveFolder = folder.Root }, false,
            (_, _) => throw new InvalidOperationException("Vocabulary must not be saved by closing the preview."),
            TranscriptCorrectionTests.Service(fake));
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var root = Assert.IsType<Grid>(window.Content);
        root.Measure(new Size(1060, 740));
        root.Arrange(new Rect(0, 0, 1060, 740));
        var pending = BeginSelectedRecognition(window);
        try
        {
            CompleteUiTask(root, fake.Worker.Started.Task);
            Assert.False(Assert.IsType<Button>(window.FindName("SaveCorrectionButton")).IsEnabled);
            Assert.False(Assert.IsType<DataGrid>(window.FindName("CorrectionRows")).IsEnabled);
            Assert.Equal(Visibility.Visible, Assert.IsType<Button>(window.FindName("CancelRecognitionButton")).Visibility);
            window.Close();
            CompleteUiTask(root, pending);
            Assert.True(closed);
            Assert.True(fake.Worker.Disposed);
            Assert.All(fake.Worker.Paths, path => Assert.False(File.Exists(path)));
            Assert.Null(window.ResultDocument);
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        }
        finally
        {
            if (!closed)
            {
                window.Close();
                CompleteUiTask(root, pending);
            }
        }
    }

    private static Task BeginSelectedRecognition(TranscriptEditWindow window) =>
        Assert.IsAssignableFrom<Task>(window.GetType()
            .GetMethod("RetranscribeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null));

    private static void CompleteUiTask(FrameworkElement root, Task pending)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!pending.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            Pump(root);
            Thread.Sleep(5);
        }
        Assert.True(pending.IsCompleted, "The synthetic editor operation did not finish.");
        pending.GetAwaiter().GetResult();
        Pump(root);
    }

    private static void CloseHiddenWorkspaces()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        foreach (var window in Application.Current.Windows.OfType<MainWindow>().ToArray())
        {
            Assert.False(Assert.IsType<bool>(typeof(MainWindow).GetField("recordingCommand", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)));
            Assert.Equal(RecorderState.Idle, Assert.IsType<RecordingEngine>(
                typeof(MainWindow).GetField("recorder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)).State);
            window.Close();
        }
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.Empty(Application.Current.Windows.OfType<MainWindow>());
    }

    private static void VerifyTranscriptReviewControls(string language)
    {
        var view = new TranscriptView();
        var host = new Grid { Width = 760, Height = 430 };
        host.Children.Add(view);
        var document = new TranscriptDocument
        {
            ProcessedFrames = 4L * PcmFormat.SampleRate,
            Revision = "review-controls",
            Segments =
            [
                new(0, 1, "First voice.") { SpeakerId = "spkr1" },
                new(1, 2, "Second voice.") { SpeakerId = "spkr2" },
                new(2, 3, "First voice again.") { SpeakerId = "spkr1" },
                new(3, 4, "Unlabeled voice.")
            ],
            SpeakerNames = new() { ["spkr1"] = "Alex" }
        };
        view.SetDocument(document, reset: true);
        host.Measure(new Size(760, 430));
        host.Arrange(new Rect(0, 0, 760, 430));
        Pump(host);
        var grid = Assert.IsType<DataGrid>(view.FindName("RowsGrid"));
        Assert.Equal(4, grid.Items.Count);
        view.SetSpeakerFilter(["spkr1"]);
        Pump(host);
        Assert.Equal(2, grid.Items.Count);
        Assert.DoesNotContain("Second", view.VisibleText());
        Assert.Contains("Second", view.AllText());
        Assert.Equal(new UiText(language)["CopyFilteredText"], Assert.IsType<Button>(view.FindName("CopyVisibleButton")).Content);
        document.RenameSpeaker("spkr1", "Jamie");
        view.SetDocument(document);
        Pump(host);
        Assert.All(grid.Items.Cast<TranscriptRow>(), row => Assert.Equal("Jamie", row.Speaker));
        view.CanReplay = true;
        grid.SelectedItems.Add(grid.Items[1]);
        grid.SelectedItems.Add(grid.Items[0]);
        Assert.Equal(new PlaybackRange(0, 3), view.SelectedPlaybackRange());
        Assert.True(Assert.IsType<Button>(view.FindName("PlayRangeButton")).IsEnabled);
        PlaybackRange? requested = null;
        Exception? failure = null;
        view.ReplayRequested += range => requested = range;
        view.OperationFailed += error => failure = error;
        var cellText = grid.Columns[0].GetCellContent(grid.Items[1]);
        Assert.NotNull(cellText);
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.PreviewMouseDoubleClickEvent,
            Source = cellText
        });
        Assert.Null(failure);
        Assert.Equal(new PlaybackRange(2, 3), requested);
        view.CanEdit = true;
        view.SelectAllVisibleRows();
        IReadOnlyList<TranscriptRow>? editing = null;
        view.EditRequested += selection => editing = selection;
        Assert.IsType<Button>(view.FindName("MuteRowsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(editing);
        Assert.Equal(2, editing.Count);
        Assert.All(editing, row => Assert.Equal("spkr1", row.SpeakerId));
        var editPlan = AudioEditPlan.Create(document, document.ProcessedFrames, editing);
        Assert.Equal(2, editPlan.MutedRanges.Count);
        Assert.Equal(2L * PcmFormat.SampleRate, editPlan.MutedFrames);
        Assert.Equal(document.ProcessedFrames, editPlan.EditedFrames);
        view.CanCorrect = true;
        IReadOnlyList<TranscriptRow>? correction = null;
        var reTranscribe = false;
        view.CorrectionRequested += (selection, rerun) => { correction = selection; reTranscribe = rerun; };
        Assert.IsType<Button>(view.FindName("CorrectRowsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.NotNull(correction);
        Assert.False(reTranscribe);
        Assert.Equal(2, correction.Count);
        Assert.All(correction, row => Assert.Equal("spkr1", row.SpeakerId));
        Assert.IsType<MenuItem>(view.FindName("RetranscribeRowsMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.True(reTranscribe);
        view.CanReplay = false;
        Assert.True(Assert.IsType<Button>(view.FindName("CorrectRowsButton")).IsEnabled);
        Assert.False(Assert.IsType<MenuItem>(view.FindName("RetranscribeRowsMenu")).IsEnabled);
        grid.SelectedItems.Clear();
        Assert.False(Assert.IsType<Button>(view.FindName("CorrectRowsButton")).IsEnabled);
        grid.SelectedItems.Add(grid.Items[1]);
        Assert.IsType<Button>(view.FindName("CorrectRowsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(grid.Items[1], Assert.Single(correction));
        view.CanReplay = true;
        view.CanCorrect = false;
        Assert.False(Assert.IsType<Button>(view.FindName("CorrectRowsButton")).IsEnabled);
        Assert.False(Assert.IsType<MenuItem>(view.FindName("RetranscribeRowsMenu")).IsEnabled);
        using (var previewFolder = new TestFolder())
        {
            var preview = new EditSessionWindow(new()
            {
                Title = "Editing preview",
                Frames = document.ProcessedFrames,
                RequestedFormat = "MP3",
                Status = "Saved"
            }, editPlan, previewFolder.Root);
            try
            {
                var previewRoot = Assert.IsType<Grid>(preview.Content);
                previewRoot.Measure(new Size(680, 660));
                previewRoot.Arrange(new Rect(0, 0, 680, 660));
                Pump(previewRoot);
                Assert.Equal(2, Assert.IsType<ListBox>(preview.FindName("RangesList")).Items.Count);
                Assert.Contains("00:00:02.000", Assert.IsType<TextBlock>(preview.FindName("StatisticsText")).Text);
                Assert.Contains("00:00:04.000", Assert.IsType<TextBlock>(preview.FindName("StatisticsText")).Text);
                Assert.Equal(1, Assert.IsType<ComboBox>(preview.FindName("FormatBox")).SelectedIndex);
                Assert.Equal(new UiText(language)["CreateEditedCopy"], Assert.IsType<Button>(preview.FindName("CreateButton")).Content);
                Assert.Null(preview.ResultSession);
                Assert.Empty(Directory.GetFileSystemEntries(previewFolder.Root));
            }
            finally { preview.Close(); }
        }
        view.CanEdit = false;
        Assert.False(Assert.IsType<Button>(view.FindName("MuteRowsButton")).IsEnabled);
        view.CanReplay = false;
        Assert.False(Assert.IsType<Button>(view.FindName("PlayRangeButton")).IsEnabled);
        requested = null;
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.PreviewMouseDoubleClickEvent,
            Source = cellText
        });
        Assert.Null(requested);
        Assert.NotNull(failure);
        view.SetPlayback(new PlaybackSnapshot(0, 3, 2, true));
        Assert.Equal(Visibility.Visible, Assert.IsType<DockPanel>(view.FindName("ReplayTransport")).Visibility);
        Assert.Equal(new UiText(language)["ReplayResume"], Assert.IsType<Button>(view.FindName("ReplayPauseButton")).Content);
        view.SetPlayback(null);
        Assert.Equal(Visibility.Collapsed, Assert.IsType<DockPanel>(view.FindName("ReplayTransport")).Visibility);
        view.SetSpeakerFilter([]);
        Pump(host);
        Assert.Empty(grid.Items.Cast<object>());
        Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(view.FindName("NoMatchingSpeakers")).Visibility);
        view.SetDocument(document, reset: true);
        Pump(host);
        Assert.Equal(4, grid.Items.Count);
        view.CanCorrect = view.CanChangeSpeakers = view.CanSummarizeAboutMe = true;
        view.CanSetSpeakerColors = true;
        grid.SelectedIndex = 0;
        var tagged = document.Copy();
        tagged.Me = new("spkr1", ["Jamie"]);
        view.SetDocument(tagged);
        view.CanCorrect = view.CanChangeSpeakers = view.CanSummarizeAboutMe = true;
        view.CanSetSpeakerColors = true;
        Pump(host);
        Assert.Equal(new UiText(language)["EditMeTag"], Assert.IsType<MenuItem>(view.FindName("MarkMeMenu")).Header);
        Assert.True(Assert.IsType<MenuItem>(view.FindName("RemoveMeMenu")).IsEnabled);
        Assert.True(Assert.IsType<MenuItem>(view.FindName("SummarizeAboutMeMenu")).IsEnabled);
        Assert.True(Assert.IsType<MenuItem>(view.FindName("CorrectSpeakerMenu")).IsEnabled);
        var unlabeled = document.Copy();
        unlabeled.Segments = unlabeled.Segments.Select(segment => segment with { SpeakerId = null }).ToList();
        view.SetDocument(unlabeled);
        grid.SelectedIndex = 0;
        view.CanCorrect = view.CanChangeSpeakers = view.CanSummarizeAboutMe = true;
        view.CanSetSpeakerColors = true;
        Pump(host);
        Assert.True(Assert.IsType<Button>(view.FindName("CorrectRowsButton")).IsEnabled);
        Assert.False(Assert.IsType<MenuItem>(view.FindName("CorrectSpeakerMenu")).IsEnabled);
        Assert.False(Assert.IsType<MenuItem>(view.FindName("MarkMeMenu")).IsEnabled);
        Assert.False(Assert.IsType<MenuItem>(view.FindName("RemoveMeMenu")).IsEnabled);
        Assert.False(Assert.IsType<MenuItem>(view.FindName("SummarizeAboutMeMenu")).IsEnabled);
        Assert.True(Assert.IsType<MenuItem>(view.FindName("SpeakerColorMenu")).IsEnabled);
        Assert.Equal(new UiText(language)["SetUnlabeledSpeakerColor"],
            Assert.IsType<MenuItem>(view.FindName("SpeakerColorMenu")).Header);
        view.SetDocument(new());
        Pump(host);
        Assert.False(Assert.IsType<Button>(view.FindName("CorrectRowsButton")).IsEnabled);
        Assert.False(Assert.IsType<MenuItem>(view.FindName("SpeakerColorMenu")).IsEnabled);
    }

    private static void Pump(FrameworkElement root)
    {
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void VerifyRecoveryDialog(string language)
    {
        using var folder = new TestFolder();
        var original = EditedSessionTests.MakeSource(folder);
        var muted = SilenceRecoveryTests.Mute(folder, original, row => row.SpeakerId == "spkr2").GetAwaiter().GetResult();
        var context = new SilenceRecoveryService().PrepareAsync(folder.Root, muted, CancellationToken.None).GetAwaiter().GetResult();
        var window = new MainWindow(new AppStorage(folder.File("preferences")), new AppSettings { UiLanguage = language, SaveFolder = folder.Root });
        var restoreButton = Assert.IsType<Button>(window.FindName("RestoreSilenceButton"));
        var restoreMenu = Assert.IsType<MenuItem>(window.FindName("RestoreSilenceMenu"));
        var transcriptPanel = Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel"));
        var selectedField = window.GetType().GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var transcriptField = window.GetType().GetField("currentTranscript", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Select(RecordingSession session)
        {
            selectedField.SetValue(window, session);
            transcriptField.SetValue(window, AppStorage.LoadTranscript(session));
            Invoke(window, "UpdateControls");
        }
        void AssertEnabled(bool enabled)
        {
            Assert.Equal(enabled, restoreButton.IsEnabled);
            Assert.Equal(enabled, restoreMenu.IsEnabled);
        }
        Select(original);
        AssertEnabled(false);
        Assert.True(transcriptPanel.CanCorrect);
        Select(muted);
        AssertEnabled(true);
        Assert.True(transcriptPanel.CanCorrect);
        Assert.Equal(new UiText(language)["RestoreMutedSections"], restoreButton.Content);
        Assert.Equal(restoreButton.Content, restoreMenu.Header);
        foreach (var fieldName in new[] { "recordingCommand", "exportBusy", "closing" })
        {
            var field = window.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
            field.SetValue(window, true);
            Invoke(window, "UpdateControls");
            AssertEnabled(false);
            Assert.False(transcriptPanel.CanCorrect);
            Assert.False(transcriptPanel.CanExportAudio);
            Assert.False(Assert.IsType<Button>(window.FindName("UpdateMyNotesButton")).IsEnabled);
            field.SetValue(window, false);
        }
        var aiTaskField = window.GetType().GetField("aiTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        aiTaskField.SetValue(window, new TaskCompletionSource().Task);
        Invoke(window, "UpdateControls");
        AssertEnabled(false);
        Assert.False(transcriptPanel.CanCorrect);
        aiTaskField.SetValue(window, null);
        var currentDocument = Assert.IsType<TranscriptDocument>(transcriptField.GetValue(window));
        currentDocument.IsDraft = true;
        Invoke(window, "UpdateControls");
        AssertEnabled(false);
        Assert.False(transcriptPanel.CanCorrect);
        currentDocument.IsDraft = false;
        currentDocument.ProcessedFrames--;
        Invoke(window, "UpdateControls");
        AssertEnabled(false);
        Assert.False(transcriptPanel.CanCorrect);
        Select(muted);
        AssertEnabled(true);
        var dialog = new EditSessionWindow(context, folder.Root);
        try
        {
            var root = Assert.IsType<Grid>(dialog.Content);
            root.Measure(new Size(680, 660));
            root.Arrange(new Rect(0, 0, 680, 660));
            Pump(root);
            var ranges = Assert.IsType<ListBox>(dialog.FindName("RangesList"));
            var create = Assert.IsType<Button>(dialog.FindName("CreateButton"));
            Assert.Equal(2, ranges.Items.Count);
            Assert.Empty(ranges.SelectedItems);
            Assert.False(create.IsEnabled);
            Assert.Equal(new UiText(language)["RestoreSelectedSections"], create.Content);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(dialog.FindName("NewCopyFields")).Visibility);
            Assert.All(ranges.Items.Cast<RestoreRangeChoice>(), choice =>
            {
                Assert.Contains("REMOVE", choice.Text);
                Assert.Contains("00:00:", choice.Display);
            });
            Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(dialog.FindName("RebuildTranscriptWarning")).Visibility);
            Assert.IsType<Button>(dialog.FindName("ClearRestoreButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(ranges.SelectedItems);
            Assert.False(create.IsEnabled);
            ranges.SelectedItems.Add(ranges.Items[0]);
            Pump(root);
            Assert.True(create.IsEnabled);
            var item = Assert.IsType<ListBoxItem>(ranges.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.True(Descendants<CheckBox>(item).Single().IsChecked);
            Assert.Contains("00:00:00.500", Assert.IsType<TextBlock>(dialog.FindName("StatisticsText")).Text);
            Assert.IsType<Button>(dialog.FindName("RestoreAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, ranges.SelectedItems.Count);
            Assert.Contains("00:00:01.000", Assert.IsType<TextBlock>(dialog.FindName("StatisticsText")).Text);
            Assert.True(ranges.ActualHeight >= 45);
            Assert.Null(dialog.ResultSession);
            Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
        }
        finally { dialog.Close(); }
        var changed = AppStorage.LoadTranscript(muted);
        changed.Model = "small";
        AppStorage.SaveTranscript(muted, changed);
        var changedContext = new SilenceRecoveryService().PrepareAsync(folder.Root, muted, CancellationToken.None).GetAwaiter().GetResult();
        var changedDialog = new EditSessionWindow(changedContext, folder.Root);
        try
        {
            var root = Assert.IsType<Grid>(changedDialog.Content);
            root.Measure(new Size(680, 620));
            root.Arrange(new Rect(0, 0, 680, 620));
            Pump(root);
            var ranges = Assert.IsType<ListBox>(changedDialog.FindName("RangesList"));
            Assert.False(Assert.IsType<Button>(changedDialog.FindName("CreateButton")).IsEnabled);
            ranges.SelectedItems.Add(ranges.Items[0]);
            Pump(root);
            Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(changedDialog.FindName("RebuildTranscriptWarning")).Visibility);
            Assert.True(Assert.IsType<Button>(changedDialog.FindName("CreateButton")).IsEnabled);
            Assert.True(ranges.ActualHeight >= 45, $"Recovery intervals have only {ranges.ActualHeight}px with the rebuild warning.");
        }
        finally { changedDialog.Close(); }
        var fullyMuted = SilenceRecoveryTests.Mute(folder, original, _ => true).GetAwaiter().GetResult();
        Assert.Empty(AppStorage.LoadTranscript(fullyMuted).Segments);
        Select(fullyMuted);
        AssertEnabled(true);
        Assert.False(Assert.IsType<TranscriptView>(window.FindName("TranscriptPanel")).CanEdit);
        selectedField.SetValue(window, null);
    }

    private static void Invoke(object target, string method) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }
}
