using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;
using Microsoft.Win32;

namespace AudioMemo;

public partial class SettingsWindow : Window
{
    private static string InheritModel => UiText.Get("InheritModel");
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? download;
    private CancellationTokenSource? localOperation;
    private readonly OllamaService ollama = new();
    private IReadOnlyList<OllamaModel> localModels = [];
    private bool initialized;
    private bool categoryModelsRead;
    private readonly Func<AppSettings, CancellationToken, Task>? validateSummary;
    private readonly bool summaryOnly;
    private bool validatingSelection;
    public AppSettings Settings { get; private set; }
    internal SetupResetMode? SetupRequest { get; private set; }

    public SettingsWindow(AppSettings settings, string category = "appearance", bool allowSetupReset = true,
        Func<AppSettings, CancellationToken, Task>? validateSummary = null, string? validationError = null, bool summaryOnly = false)
    {
        this.validateSummary = validateSummary;
        this.summaryOnly = summaryOnly;
        Settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
            ?? throw new InvalidDataException("Could not read settings.");
        InitializeComponent();
        RunSetupButton.Visibility = allowSetupReset ? Visibility.Visible : Visibility.Collapsed;
        OllamaModelBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(OllamaModel_TextChanged));
        CloudEndpointBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler(CloudEndpoint_TextChanged));
        Language = XmlLanguage.GetLanguage(settings.UiLanguage);
        InterfaceLanguageBox.ItemsSource = UiText.InterfaceLanguages;
        InterfaceLanguageBox.SelectedValue = settings.UiLanguage;
        ThemeBox.ItemsSource = AppThemes.Options(UiText.Current);
        ThemeBox.SelectedValue = settings.Theme;
        ShowMainPointsCheck.IsChecked = settings.MainPointsVisible;
        ShowMyNotesCheck.IsChecked = settings.MyNotesVisible;
        RainbowRecordingIndicatorCheck.IsChecked = settings.Appearance.RainbowRecordingIndicator;
        ButtonStyleBox.ItemsSource = ButtonStyles.Options(UiText.Current);
        ButtonStyleBox.SelectedValue = settings.Appearance.ButtonStyle;
        SetColorControls(settings.Appearance);
        InitializeShortcuts();
        SaveFolderBox.Text = settings.SaveFolder;
        OutputFormatBox.ItemsSource = new[] { "WAV", "MP3" };
        OutputFormatBox.SelectedItem = settings.Format;
        OutputRateBox.ItemsSource = new[] { 44_100, 48_000 };
        OutputRateBox.SelectedItem = settings.AudioOutput.SampleRate;
        OutputChannelsBox.ItemsSource = new[] { new LanguageOption("1", UiText.Get("AudioMono")), new LanguageOption("2", UiText.Get("AudioStereo")) };
        OutputChannelsBox.SelectedValue = settings.AudioOutput.Channels.ToString(CultureInfo.InvariantCulture);
        MicGain.Value = settings.MicrophoneGain;
        PlaybackGain.Value = settings.PlaybackGain;
        AutoStopOnSilenceCheck.IsChecked = settings.AutoStopOnSilence;
        SilenceTimeoutBox.Text = settings.SilenceTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        SpeechModelBox.ItemsSource = SpeechModels.Available;
        SpeechModelBox.SelectedValue = settings.SpeechModel;
        AccelerationBox.ItemsSource = new[]
        {
            new LanguageOption("auto", UiText.Get("AccelerationAuto")),
            new LanguageOption("cpu", UiText.Get("AccelerationCpu")),
            new LanguageOption("vulkan", UiText.Get("AccelerationVulkan"))
        };
        AccelerationBox.SelectedValue = settings.SpeechAcceleration;
        FastLiveDraftsCheck.IsChecked = settings.FastLiveDrafts;
        UseSpeechVocabularyCheck.IsChecked = settings.SpeechVocabularyEnabled;
        SpeechVocabularyBox.Text = string.Join(Environment.NewLine, settings.SpeechVocabularyTerms);
        UpdateSpeechVocabularyStatus();
        SpeakersOnStopCheck.IsChecked = settings.IdentifySpeakersOnStop;
        SpeakerCountBox.Text = settings.SpeakerCount.ToString(CultureInfo.InvariantCulture);
        LanguageBox.ItemsSource = SpeechLanguages.GetOptions(UiText.Current);
        LanguageBox.SelectedValue = settings.Language;
        CliPathBox.Text = settings.CopilotExecutable;
        SummaryModelBox.ItemsSource = new[] { InheritModel };
        SummaryModelBox.Text = string.IsNullOrWhiteSpace(settings.SummaryModel) ? InheritModel : settings.SummaryModel;
        SummaryProviderBox.ItemsSource = SummaryProviders.GetOptions(UiText.Current);
        SummaryProviderBox.SelectedValue = settings.SummaryProvider;
        OllamaEndpointBox.Text = settings.OllamaEndpoint;
        OllamaModelBox.Text = settings.OllamaModel;
        OllamaContextBox.ItemsSource = new[] { 8192, 16384, 32768, 65536, 131072, 262144 };
        OllamaContextBox.Text = settings.OllamaContextTokens.ToString(CultureInfo.InvariantCulture);
        OllamaTimeoutBox.Text = settings.OllamaTimeoutMinutes.ToString(CultureInfo.InvariantCulture);
        GuideModelsBox.ItemsSource = OllamaModelCatalog.Recommendations;
        GuideModelsBox.SelectedIndex = 0;
        TranscribeOnStopCheck.IsChecked = settings.TranscribeOnStop;
        ReviewUnclearTranscriptCheck.IsChecked = settings.ReviewUnclearTranscript;
        LiveTranscriptionCheck.IsChecked = settings.LiveTranscription;
        SummarizeOnStopCheck.IsChecked = settings.SummarizeOnStop;
        LiveSummaryCheck.IsChecked = settings.LiveSummary;
        CloudConsentCheck.IsChecked = settings.CloudConsent;
        initialized = true;
        UpdateSpeechStatus();
        UpdateProviderPanels();
        SettingsCategories.SelectedItem = SettingsCategories.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, category)) ?? throw new ArgumentException("Unknown settings category.", nameof(category));
        UpdateCategory();
        if (validateSummary is not null)
        {
            ModelValidationNotice.Visibility = Visibility.Visible;
            ModelValidationNotice.Text = UiText.Get("LlmValidationNotice");
        }
        if (summaryOnly)
        {
            SettingsCategories.IsEnabled = false;
            SaveButton.Content = UiText.Get("LlmTestAndSelect");
        }
        if (validationError is not null)
            SettingsStatus.Text = UiText.Format("LlmSelectionNeedsSetup", validationError);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => UpdateCategory();

    private async void Category_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || !ReferenceEquals(e.Source, SettingsCategories))
            return;
        UpdateCategory();
        if (IsLoaded && SummaryPage.Visibility == Visibility.Visible && !categoryModelsRead)
        {
            categoryModelsRead = true;
            await ReadModelsAsync();
        }
    }

    private void UpdateCategory()
    {
        var selected = (SettingsCategories.SelectedItem as ListBoxItem)?.Tag as string ?? "appearance";
        AppearancePage.Visibility = selected == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        RecordingPage.Visibility = selected == "recording" ? Visibility.Visible : Visibility.Collapsed;
        SpeechPage.Visibility = selected == "speech" ? Visibility.Visible : Visibility.Collapsed;
        SummaryPage.Visibility = selected == "summary" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = selected == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        SettingsDetails.ScrollToTop();
    }

    private void ButtonStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ButtonPreview is not null && ButtonStyleBox.SelectedValue is string style)
            ButtonPreview.Resources["ButtonStyleId"] = style;
    }

    private void SetColorControls(AppearanceSettings appearance)
    {
        LightSelectionColor.Color = appearance.Light.Selection;
        DarkSelectionColor.Color = appearance.Dark.Selection;
        LightMatchColor.Color = appearance.Light.Match;
        DarkMatchColor.Color = appearance.Dark.Match;
        LightActiveMatchColor.Color = appearance.Light.ActiveMatch;
        DarkActiveMatchColor.Color = appearance.Dark.ActiveMatch;
        LightComparisonDifferentColor.Color = appearance.LightComparison.Different;
        DarkComparisonDifferentColor.Color = appearance.DarkComparison.Different;
        LightComparisonMatchingColor.Color = appearance.LightComparison.Matching;
        DarkComparisonMatchingColor.Color = appearance.DarkComparison.Matching;
        LightComparisonUnclearColor.Color = appearance.LightComparison.Unclear;
        DarkComparisonUnclearColor.Color = appearance.DarkComparison.Unclear;
    }

    private void ResetColors_Click(object sender, RoutedEventArgs e) => SetColorControls(new());

    private void RunSetup_Click(object sender, RoutedEventArgs e)
    {
        if (download is not null || localOperation is not null || cloudOperation is not null)
            return;
        var dialog = new SetupResetWindow(this);
        if (dialog.ShowDialog() == true)
        {
            SetupRequest = dialog.Choice;
            DialogResult = true;
        }
    }

    private void ReadAppearance()
    {
        var appearance = new AppearanceSettings
        {
            ButtonStyle = (string?)ButtonStyleBox.SelectedValue ?? throw new InvalidDataException(UiText.Get("InvalidButtonStyle")),
            RainbowRecordingIndicator = RainbowRecordingIndicatorCheck.IsChecked == true,
            Light = new() { Selection = LightSelectionColor.Color, Match = LightMatchColor.Color, ActiveMatch = LightActiveMatchColor.Color },
            Dark = new() { Selection = DarkSelectionColor.Color, Match = DarkMatchColor.Color, ActiveMatch = DarkActiveMatchColor.Color },
            LightComparison = new()
            {
                Different = LightComparisonDifferentColor.Color, Matching = LightComparisonMatchingColor.Color,
                Unclear = LightComparisonUnclearColor.Color
            },
            DarkComparison = new()
            {
                Different = DarkComparisonDifferentColor.Color, Matching = DarkComparisonMatchingColor.Color,
                Unclear = DarkComparisonUnclearColor.Color
            }
        };
        appearance.Validate();
        Settings.Appearance = appearance;
        Settings.MainPointsVisible = ShowMainPointsCheck.IsChecked == true;
        Settings.MyNotesVisible = ShowMyNotesCheck.IsChecked == true;
    }

    private async Task ReadModelsAsync()
    {
        if (CloudLlmCatalog.IsProvider((string?)SummaryProviderBox.SelectedValue))
            return;
        if ((string?)SummaryProviderBox.SelectedValue == SummaryProviders.Ollama)
        {
            await ReadLocalModelsAsync();
            return;
        }
        RefreshModelsButton.IsEnabled = false;
        SummaryProviderBox.IsEnabled = false;
        try
        {
            var executable = CopilotService.FindExecutable(CliPathBox.Text);
            CliDetectedText.Text = executable;
            var configured = CopilotService.ReadConfiguration();
            CurrentModelText.Text = configured.Model is { Length: > 0 }
                ? UiText.Format("CurrentCliModel", configured.Model)
                : UiText.Format("RecentCliModel", configured.RecentModels.FirstOrDefault() ?? UiText.Get("None"));
            var selection = SummaryModelBox.Text;
            var models = await new CopilotService().DiscoverModelsAsync(CliPathBox.Text, lifetime.Token);
            SummaryModelBox.ItemsSource = models.Prepend(InheritModel).Distinct().ToArray();
            SummaryModelBox.Text = selection;
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPILOT_PROVIDER_BASE_URL")))
            {
                SignInButton.IsEnabled = false;
                SettingsStatus.Text = UiText.Get("CustomProvider");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SettingsStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
            SummaryProviderBox.IsEnabled = localOperation is null && cloudOperation is null;
        }
    }

    private async void SummaryProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || updatingCloudFields)
            return;
        try
        {
            ReadCloudFields();
            UpdateProviderPanels();
            await ReadModelsAsync();
        }
        catch (Exception ex)
        {
            if (shownCloudProvider is not null)
            {
                updatingCloudFields = true;
                SummaryProviderBox.SelectedValue = shownCloudProvider;
                updatingCloudFields = false;
            }
            SettingsStatus.Text = UiText.Format("ErrorDetails", ex.Message);
        }
    }

    private void UpdateProviderPanels()
    {
        var local = (string?)SummaryProviderBox.SelectedValue == SummaryProviders.Ollama;
        var cloud = CloudLlmCatalog.IsProvider((string?)SummaryProviderBox.SelectedValue);
        LocalSettingsPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        CopilotSettingsPanel.Visibility = local || cloud ? Visibility.Collapsed : Visibility.Visible;
        CloudSettingsPanel.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
        SettingsStatus.Text = "";
        ShowCloudFields(cloud ? (string)SummaryProviderBox.SelectedValue : null);
        if (local)
            UpdateLocalMemoryHint();
    }

    private async Task ReadLocalModelsAsync() => await RunLocalOperationAsync(async cancellationToken =>
    {
        LocalModelStatus.Text = UiText.Get("ReadingLocalModels");
        var selectedModel = OllamaModelBox.Text;
        var models = await ollama.DiscoverModelsAsync(OllamaEndpointBox.Text.Trim(), cancellationToken);
        localModels = models.Where(model => !model.IsRemote && model.SizeBytes > 0).ToArray();
        OllamaModelBox.ItemsSource = localModels;
        OllamaModelBox.Text = selectedModel;
        LocalModelStatus.Text = localModels.Count == 0 ? UiText.Get("NoLocalModels")
            : UiText.Format("LocalModelsFound", localModels.Count, models.Count - localModels.Count);
        UpdateLocalMemoryHint();
    });

    private async Task RunLocalOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (localOperation is not null)
            return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        localOperation = cancellation;
        LocalPerformanceWarning.Text = "";
        UpdateModelOperationControls();
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { LocalModelStatus.Text = UiText.Get("LocalCheckCancelled"); }
        catch (Exception ex) { LocalModelStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
        finally
        {
            localOperation = null;
            UpdateModelOperationControls();
        }
    }

    private void UpdateModelOperationControls()
    {
        var ready = localOperation is null && cloudOperation is null && !validatingSelection;
        ReadLocalModelsButton.IsEnabled = ready;
        TestLocalModelButton.IsEnabled = ready;
        SummaryProviderBox.IsEnabled = ready;
        OllamaEndpointBox.IsEnabled = ready;
        OllamaModelBox.IsEnabled = ready;
        OllamaContextBox.IsEnabled = ready;
        OllamaTimeoutBox.IsEnabled = ready;
        CancelLocalTestButton.Visibility = localOperation is null ? Visibility.Collapsed : Visibility.Visible;
        CloudEndpointBox.IsEnabled = ready;
        CloudModelBox.IsEnabled = ready;
        CloudApiKeyBox.IsEnabled = ready && credentialEndpoint is not null;
        ForgetCloudKeyCheck.IsEnabled = ready && credentialEndpoint is not null;
        DisableCloudThinkingCheck.IsEnabled = ready;
        CloudApiConsentCheck.IsEnabled = ready && credentialEndpoint is not null;
        ReadCloudModelsButton.IsEnabled = ready;
        TestCloudModelButton.IsEnabled = ready;
        CancelCloudTestButton.Visibility = cloudOperation is null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = ready && download is null && shortcutsValid;
        RunSetupButton.IsEnabled = ready && download is null;
        SettingsDetails.IsEnabled = !validatingSelection;
        SettingsCategories.IsEnabled = !summaryOnly && !validatingSelection;
    }

    private void ReadLocalSettings(AppSettings target)
    {
        target.OllamaEndpoint = OllamaEndpointBox.Text.Trim();
        target.OllamaModel = OllamaModelBox.Text.Trim();
        if (!int.TryParse(OllamaContextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var context) ||
            !int.TryParse(OllamaTimeoutBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            throw new InvalidDataException(UiText.Get("InvalidLocalLimits"));
        target.OllamaContextTokens = context;
        target.OllamaTimeoutMinutes = minutes;
    }

    private async void ReadLocalModels_Click(object sender, RoutedEventArgs e) => await ReadLocalModelsAsync();

    private async void TestLocalModel_Click(object sender, RoutedEventArgs e) => await RunLocalOperationAsync(async cancellationToken =>
    {
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings))
            ?? throw new InvalidDataException("Could not copy settings.");
        snapshot.SummaryProvider = SummaryProviders.Ollama;
        ReadLocalSettings(snapshot);
        LocalModelStatus.Text = UiText.Get("TestingLocalModel");
        IProgress<string> warnings = new Progress<string>(message =>
        {
            if (!LocalPerformanceWarning.Text.Contains(message, StringComparison.Ordinal))
                LocalPerformanceWarning.Text += (LocalPerformanceWarning.Text.Length == 0 ? "" : Environment.NewLine) + message;
        });
        var result = await ollama.TestAsync(snapshot, warnings.Report, cancellationToken);
        LocalModelStatus.Text = result.TokensPerSecond is { } speed
            ? UiText.Format("LocalTestPassed", result.Elapsed.TotalSeconds, speed)
            : UiText.Format("LocalTestPassedNoSpeed", result.Elapsed.TotalSeconds);
    });

    private void CancelLocalTest_Click(object sender, RoutedEventArgs e) => localOperation?.Cancel();

    private void OllamaModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized)
            UpdateLocalMemoryHint();
    }

    private void OllamaModel_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (initialized)
            UpdateLocalMemoryHint();
    }

    private void OllamaEndpoint_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized)
            return;
        var selectedModel = OllamaModelBox.Text;
        localModels = [];
        OllamaModelBox.ItemsSource = null;
        OllamaModelBox.Text = selectedModel;
        LocalModelStatus.Text = UiText.Get("LocalModelChoiceHint");
        LocalPerformanceWarning.Text = "";
    }

    private void UpdateLocalMemoryHint()
    {
        try
        {
            var memory = ComputerMemory.Read();
            ComputerMemoryText.Text = UiText.Format("ComputerMemory",
                memory.TotalBytes / (1024d * 1024 * 1024), memory.AvailableBytes / (1024d * 1024 * 1024));
            var model = localModels.FirstOrDefault(item => item.Name.Equals(OllamaModelBox.Text, StringComparison.OrdinalIgnoreCase));
            LocalPerformanceWarning.Text = model is null ? "" :
                LocalModelDiagnostics.MemoryWarning(model.SizeBytes, memory, UiText.Current) ?? "";
        }
        catch (Win32Exception ex)
        {
            LocalPerformanceWarning.Text = UiText.Format("LocalMemoryUnavailable", ex.Message);
        }
    }

    private void GuideModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GuideModelsBox.SelectedItem is LocalModelRecommendation recommendation)
            ModelDownloadCommand.Text = recommendation.Command;
    }

    private void DownloadOllama_Click(object sender, RoutedEventArgs e) => OpenModelLink(OllamaModelCatalog.DownloadUrl);

    private void OpenModelPage_Click(object sender, RoutedEventArgs e)
    {
        if (GuideModelsBox.SelectedItem is LocalModelRecommendation recommendation)
            OpenModelLink(recommendation.PageUrl);
    }

    private void OpenModelLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { SettingsStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void CopyDownloadCommand_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ModelDownloadCommand.Text);
            SettingsStatus.Text = UiText.Get("LocalCommandCopied");
        }
        catch (Exception ex) { SettingsStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = UiText.Get("ChooseSavingFolder"), Multiselect = false };
        if (Directory.Exists(SaveFolderBox.Text))
            dialog.InitialDirectory = SaveFolderBox.Text;
        if (dialog.ShowDialog(this) == true)
            SaveFolderBox.Text = dialog.FolderName;
    }

    private void BrowseCli_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = UiText.Get("ChooseCliExecutable"), Filter = UiText.Get("CliFileFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) == true)
            CliPathBox.Text = dialog.FileName;
    }

    private void SpeechModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized)
            UpdateSpeechStatus();
    }

    private void UpdateSpeechStatus()
    {
        if (SpeechModelBox.SelectedItem is not SpeechModel model)
            return;
        var downloaded = SpeechModels.IsDownloaded(model.Id);
        SpeechModelStatus.Text = UiText.Get(downloaded ? "Downloaded" : "NotDownloaded");
        DownloadButton.Content = UiText.Get(downloaded ? "DownloadAgain" : "DownloadModel");
        BaseModelStatus.Text = UiText.Get(SpeechModels.IsDownloaded("base") ? "BaseModelReady" : "BaseModelMissing");
        DownloadBaseButton.IsEnabled = download is null && !SpeechModels.IsDownloaded("base");
        SpeakerModelsStatus.Text = UiText.Get(SpeakerModels.IsDownloaded ? "SpeakerModelsReady" : "SpeakerModelsMissing");
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (SpeechModelBox.SelectedItem is SpeechModel model)
            await RunDownloadAsync(model.Id, (progress, token) => SpeechModels.DownloadAsync(model.Id, progress, token));
    }

    private async void DownloadBase_Click(object sender, RoutedEventArgs e) =>
        await RunDownloadAsync("Whisper Base", (progress, token) => SpeechModels.DownloadAsync("base", progress, token));

    private async void DownloadSpeakers_Click(object sender, RoutedEventArgs e) =>
        await RunDownloadAsync(UiText.Get("SpeakerModels"), SpeakerModels.DownloadAsync);

    private async Task RunDownloadAsync(string label, Func<IProgress<double>, CancellationToken, Task> operation)
    {
        if (download is not null)
            return;
        download = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        DownloadButton.IsEnabled = false;
        DownloadBaseButton.IsEnabled = false;
        DownloadSpeakersButton.IsEnabled = false;
        SpeechModelBox.IsEnabled = false;
        UpdateModelOperationControls();
        CancelDownloadButton.Visibility = Visibility.Visible;
        DownloadProgress.Visibility = Visibility.Visible;
        var progress = new Progress<double>(percent =>
        {
            DownloadProgress.Value = percent;
            SettingsStatus.Text = UiText.Format("ModelDownloadProgress", label, percent);
        });
        try
        {
            SettingsStatus.Text = UiText.Get("ModelDownloading");
            await operation(progress, download.Token);
            SettingsStatus.Text = UiText.Get("ModelDownloaded");
        }
        catch (OperationCanceledException) { SettingsStatus.Text = UiText.Get("DownloadCancelled"); }
        catch (Exception ex) { SettingsStatus.Text = UiText.Format("DownloadFailed", ex.Message); }
        finally
        {
            download.Dispose();
            download = null;
            DownloadButton.IsEnabled = true;
            DownloadSpeakersButton.IsEnabled = true;
            SpeechModelBox.IsEnabled = true;
            UpdateModelOperationControls();
            CancelDownloadButton.Visibility = Visibility.Collapsed;
            DownloadProgress.Visibility = Visibility.Collapsed;
            UpdateSpeechStatus();
        }
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => download?.Cancel();

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new AppSettings { CopilotExecutable = CliPathBox.Text.Trim() };
            CopilotService.Login(settings);
            SettingsStatus.Text = UiText.Get("CompleteSignIn");
        }
        catch (Exception ex) { SettingsStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await ReadModelsAsync();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (await PrepareSaveAsync())
            DialogResult = true;
    }

    internal async Task<bool> PrepareSaveAsync()
    {
        if (validatingSelection)
            return false;
        try
        {
            Settings.UiLanguage = (InterfaceLanguageBox.SelectedItem as LanguageOption)?.Code
                ?? throw new InvalidOperationException(UiText.Get("InvalidInterfaceLanguage"));
            ReadTheme();
            ReadAppearance();
            ReadShortcuts();
            Settings.SaveFolder = SaveFolderBox.Text.Trim();
            ReadAudioOutput();
            ReadRecordingAutomation();
            Settings.MicrophoneGain = MicGain.Value;
            Settings.PlaybackGain = PlaybackGain.Value;
            Settings.SpeechModel = (SpeechModelBox.SelectedItem as SpeechModel)?.Id
                ?? throw new InvalidOperationException(UiText.Get("ChooseSpeechModel"));
            Settings.SpeechAcceleration = (string?)AccelerationBox.SelectedValue
                ?? throw new InvalidOperationException(UiText.Get("InvalidSpeechAcceleration"));
            Settings.FastLiveDrafts = FastLiveDraftsCheck.IsChecked == true;
            ReadSpeechVocabulary();
            if (!int.TryParse(SpeakerCountBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var speakers))
                throw new InvalidDataException(UiText.Get("InvalidSpeakerCount"));
            Settings.SpeakerCount = speakers;
            Settings.Language = (LanguageBox.SelectedItem as LanguageOption)?.Code
                ?? throw new InvalidOperationException(UiText.Get("InvalidSpeechLanguage"));
            Settings.CopilotExecutable = CliPathBox.Text.Trim();
            Settings.SummaryModel = SummaryModelBox.Text == InheritModel ? "" : SummaryModelBox.Text.Trim();
            Settings.SummaryProvider = (string?)SummaryProviderBox.SelectedValue
                ?? throw new InvalidOperationException(UiText.Get("InvalidSummaryProvider"));
            ReadCloudFields();
            if (Settings.UsesLocalSummaries)
                ReadLocalSettings(Settings);
            Settings.TranscribeOnStop = TranscribeOnStopCheck.IsChecked == true;
            Settings.ReviewUnclearTranscript = ReviewUnclearTranscriptCheck.IsChecked == true;
            Settings.LiveTranscription = LiveTranscriptionCheck.IsChecked == true;
            Settings.SummarizeOnStop = SummarizeOnStopCheck.IsChecked == true;
            Settings.LiveSummary = LiveSummaryCheck.IsChecked == true;
            Settings.CloudConsent = CloudConsentCheck.IsChecked == true;
            if (Settings.CanSummarize)
                Settings.SummarySetupDeferred = false;
            Settings.Validate();
            if ((Settings.SummarizeOnStop || Settings.LiveSummary) && !Settings.CanSummarize)
                throw new InvalidOperationException(UiText.Get("ConsentRequiredForAuto"));
            if ((Settings.SummarizeOnStop || Settings.LiveSummary) && Settings.UsesLocalSummaries &&
                string.IsNullOrWhiteSpace(Settings.OllamaModel))
                throw new InvalidOperationException(UiText.Get("ChooseLocalModel"));
            if ((Settings.SummarizeOnStop || Settings.LiveSummary) && Settings.UsesCloudApi)
            {
                CloudLlmCatalog.ValidateModel(Settings.GetCloudProfile().Model, required: true);
                CloudApiKey.Read(Settings.SummaryProvider, Settings.GetCloudProfile());
            }
            if (Settings.LiveSummary && !Settings.LiveTranscription)
                throw new InvalidOperationException(UiText.Get("LiveTextRequired"));
            if (Settings.SummarizeOnStop && !Settings.TranscribeOnStop && !Settings.LiveTranscription)
                throw new InvalidOperationException(UiText.Get("TranscriptionRequired"));
            if (validateSummary is not null)
            {
                validatingSelection = true;
                UpdateModelOperationControls();
                SettingsStatus.Text = UiText.Get("LlmValidatingSelection");
                await validateSummary(SummaryConfigurationService.Copy(Settings), lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
            }
            return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { SettingsStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
        finally
        {
            validatingSelection = false;
            UpdateModelOperationControls();
        }
        return false;
    }

    private void ReadAudioOutput()
    {
        Settings.Format = OutputFormatBox.SelectedItem as string ?? throw new InvalidDataException(UiText.Get("InvalidAudioFormat"));
        Settings.AudioOutput = new()
        {
            SampleRate = OutputRateBox.SelectedItem is int rate ? rate : throw new InvalidDataException(UiText.Get("InvalidAudioOutput")),
            Channels = OutputChannelsBox.SelectedValue is string channels
                ? int.Parse(channels, CultureInfo.InvariantCulture) : throw new InvalidDataException(UiText.Get("InvalidAudioOutput"))
        };
        Settings.AudioOutput.Validate();
    }

    private void ReadRecordingAutomation()
    {
        if (!int.TryParse(SilenceTimeoutBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 1 or > 86400)
            throw new InvalidDataException(UiText.Get("InvalidSilenceTimeout"));
        Settings.AutoStopOnSilence = AutoStopOnSilenceCheck.IsChecked == true;
        Settings.SilenceTimeoutSeconds = seconds;
        Settings.IdentifySpeakersOnStop = SpeakersOnStopCheck.IsChecked == true;
    }

    private void ReadTheme()
    {
        Settings.Theme = (string?)ThemeBox.SelectedValue
            ?? throw new InvalidOperationException(UiText.Get("InvalidTheme"));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        lifetime.Cancel();
        download?.Cancel();
        localOperation?.Cancel();
        cloudOperation?.Cancel();
        CloudApiKeyBox.Clear();
    }

    private void ReadSpeechVocabulary()
    {
        Settings.SpeechVocabularyTerms = SpeechVocabulary.Parse(SpeechVocabularyBox.Text);
        Settings.SpeechVocabularyEnabled = UseSpeechVocabularyCheck.IsChecked == true;
    }

    private void SpeechVocabulary_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateSpeechVocabularyStatus();

    private void UpdateSpeechVocabularyStatus()
    {
        if (SpeechVocabularyStatus is null || SpeechVocabularyBox is null)
            return;
        try
        {
            var hint = SpeechVocabulary.CreatePrompt(SpeechVocabulary.Parse(SpeechVocabularyBox.Text));
            SpeechVocabularyStatus.Text = UiText.Format("SpeechVocabularyStatus", hint.UsedTerms, hint.TotalTerms) +
                (hint.Text.Length == 0 ? "" : Environment.NewLine + hint.Text);
        }
        catch (Exception ex)
        {
            SpeechVocabularyStatus.Text = UiText.Format("ErrorDetails", ex.Message);
            MessageJournal.Record(this, SpeechVocabularyStatus.Text, UiText.Get("SpeechVocabularyTitle"), MessageSeverity.Error);
        }
    }
}
