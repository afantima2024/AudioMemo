using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;
using Microsoft.Win32;

namespace AudioMemo;

public partial class SetupWizardWindow : Window
{
    private readonly AppStorage storage;
    internal MessageHistoryStore MessageHistory => storage.Messages;
    private readonly ISetupEnvironment environment;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string previousLanguage = UiText.Current.Culture.Name;
    private readonly bool newProfile;
    private CancellationTokenSource? operationCancellation;
    private Task? operationTask;
    private int step;
    private bool initialized;
    private bool audioListed;
    private bool busy;
    private bool closeRequested;
    private bool allowClose;
    private bool completed;
    public AppSettings Settings { get; private set; }

    public SetupWizardWindow(AppStorage storage, AppSettings settings) : this(storage, settings, new SetupEnvironment()) { }

    internal SetupWizardWindow(AppStorage storage, AppSettings settings, ISetupEnvironment environment)
    {
        this.storage = storage;
        this.environment = environment;
        newProfile = !settings.SetupCompleted;
        Settings = SetupValidation.Prepare(settings, SetupResetMode.KeepSettings);
        if (newProfile)
        {
            Settings.SpeechModel = "base";
            Settings.FastLiveDrafts = false;
            Settings.LiveTranscription = false;
            Settings.TranscribeOnStop = false;
            Settings.IdentifySpeakersOnStop = false;
        }
        InitializeComponent();
        WizardLanguageBox.ItemsSource = UiText.InterfaceLanguages;
        WizardFormatBox.ItemsSource = new[] { "WAV", "MP3" };
        WizardSpeechModelBox.ItemsSource = SpeechModels.Available;
        LoadFields();
        initialized = true;
        ApplyLanguage();
        Closed += (_, _) =>
        {
            closeRequested = true;
            lifetime.Cancel();
            lifetime.Dispose();
            if (!completed)
                UiText.Current.ChangeLanguage(previousLanguage);
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAudioAsync();
    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshAudioAsync();

    internal Task RefreshAudioAsync() => RunOperationAsync(async cancellation =>
    {
        var devices = await environment.ListAudioAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        var micId = (WizardMicDevice.SelectedItem as AudioDevice)?.Id ?? Settings.MicrophoneId;
        var playbackId = (WizardPlaybackDevice.SelectedItem as AudioDevice)?.Id ?? Settings.PlaybackId;
        WizardMicDevice.ItemsSource = devices.Microphones;
        WizardPlaybackDevice.ItemsSource = devices.Playback;
        WizardMicDevice.SelectedItem = devices.Microphones.FirstOrDefault(device => device.Id == micId) ?? devices.Microphones.FirstOrDefault();
        WizardPlaybackDevice.SelectedItem = devices.Playback.FirstOrDefault(device => device.Id == playbackId) ?? devices.Playback.FirstOrDefault();
        if (newProfile && !audioListed)
        {
            WizardMicEnabled.IsChecked = devices.Microphones.Count > 0;
            WizardPlaybackEnabled.IsChecked = devices.Playback.Count > 0;
        }
        audioListed = true;
        WizardStatus.Text = devices.Microphones.Count + devices.Playback.Count == 0
            ? UiText.Get("SetupNoDevices") : UiText.Get("SetupDevicesListed");
    });

    private void LoadFields()
    {
        WizardLanguageBox.SelectedValue = Settings.UiLanguage;
        WizardMicEnabled.IsChecked = Settings.MicrophoneEnabled;
        WizardPlaybackEnabled.IsChecked = Settings.PlaybackEnabled;
        if (WizardMicDevice.Items.Count > 0)
            WizardMicDevice.SelectedValue = Settings.MicrophoneId;
        if (WizardPlaybackDevice.Items.Count > 0)
            WizardPlaybackDevice.SelectedValue = Settings.PlaybackId;
        WizardFolderBox.Text = Settings.SaveFolder;
        WizardFormatBox.SelectedItem = Settings.Format;
        WizardSpeechModelBox.SelectedValue = Settings.SpeechModel;
        LoadSpeechLanguages(preserveSelection: false);
        WizardLiveTextCheck.IsChecked = Settings.LiveTranscription;
        WizardTranscribeCheck.IsChecked = Settings.TranscribeOnStop;
        WizardFastDraftsCheck.IsChecked = Settings.FastLiveDrafts;
        WizardSpeakersCheck.IsChecked = Settings.IdentifySpeakersOnStop;
        WizardAutoSummaryCheck.IsChecked = Settings.SummarizeOnStop;
        WizardLiveSummaryCheck.IsChecked = Settings.LiveSummary;
    }

    private void LoadSpeechLanguages(bool preserveSelection = true)
    {
        var language = (preserveSelection ? WizardSpeechLanguageBox.SelectedValue as string : null) ?? Settings.Language;
        var options = SpeechLanguages.GetOptions(UiText.Current).ToList();
        if (options.All(option => option.Code != language))
            options.Add(new(language, UiText.Format("SpeechLanguageCurrent", language)));
        WizardSpeechLanguageBox.ItemsSource = options;
        WizardSpeechLanguageBox.SelectedValue = language;
    }

    private void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized)
            ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        if (WizardLanguageBox.SelectedValue is not string language)
            return;
        UiText.Current.ChangeLanguage(language);
        Language = XmlLanguage.GetLanguage(language);
        LoadSpeechLanguages();
        WizardSpeechModelBox.Items.Refresh();
        UpdateStep();
    }

    private void UpdateStep()
    {
        var pages = new[] { AudioPage, StoragePage, SpeechPage, LlmPage, ReviewPage };
        var titles = new[] { "SetupAudioStep", "SetupStorageStep", "SetupSpeechStep", "SetupLlmStep", "SetupReviewStep" };
        for (var index = 0; index < pages.Length; index++)
            pages[index].Visibility = index == step ? Visibility.Visible : Visibility.Collapsed;
        StepTitle.Text = UiText.Format("SetupStepTitle", step + 1, pages.Length, UiText.Get(titles[step]));
        NextButton.Content = UiText.Get(step == pages.Length - 1 ? "SetupFinish" : "SetupNext");
        SkipButton.Visibility = step is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
        UpdateResourceStatus();
        UpdateNavigation();
        WizardScroller.ScrollToTop();
        if (step == 4)
            UpdateReview();
    }

    private void UpdateNavigation()
    {
        WizardPages.IsEnabled = WizardLanguageBox.IsEnabled = !busy && !closeRequested;
        BackButton.IsEnabled = !busy && !closeRequested && step > 0;
        NextButton.IsEnabled = SkipButton.IsEnabled = !busy && !closeRequested;
    }

    private void UpdateResourceStatus()
    {
        if (WizardSpeechModelBox.SelectedValue is string model)
            WizardSpeechStatus.Text = UiText.Get(environment.IsSpeechModelReady(model) ? "Downloaded" : "NotDownloaded");
        WizardBaseStatus.Text = UiText.Get(environment.IsSpeechModelReady("base") ? "BaseModelReady" : "BaseModelMissing");
        WizardSpeakersStatus.Text = UiText.Get(environment.SpeakerModelsReady ? "SpeakerModelsReady" : "SpeakerModelsMissing");
        RuntimeStatus.Text = UiText.Get(environment.SpeechRuntimeAvailable ? "SetupRuntimeReady" : "SetupRuntimeMissing");
        WizardLlmStatus.Text = UiText.Format("SetupSelectedLlm", SummaryProviders.Label(Settings.SummaryProvider, UiText.Current),
            string.IsNullOrWhiteSpace(Settings.ActiveSummaryModel) ? UiText.Get("CliDefaultModel") : Settings.ActiveSummaryModel);
    }

    private RecordingSources ReadSources()
    {
        SourceSelection? Selection(CheckBox enabled, ComboBox list, double gain)
        {
            if (enabled.IsChecked != true)
                return null;
            if (list.SelectedItem is not AudioDevice device)
                throw new InvalidOperationException(UiText.Get("SetupChooseDevice"));
            return new(device.Id, device.Name, (float)gain);
        }
        var sources = new RecordingSources(Selection(WizardMicEnabled, WizardMicDevice, Settings.MicrophoneGain),
            Selection(WizardPlaybackEnabled, WizardPlaybackDevice, Settings.PlaybackGain));
        sources.Validate();
        return sources;
    }

    private void ReadFields()
    {
        var sources = ReadSources();
        Settings.UiLanguage = (string?)WizardLanguageBox.SelectedValue ?? throw new InvalidDataException(UiText.Get("InvalidInterfaceLanguage"));
        Settings.MicrophoneEnabled = sources.Microphone is not null;
        Settings.PlaybackEnabled = sources.Playback is not null;
        Settings.MicrophoneId = (WizardMicDevice.SelectedItem as AudioDevice)?.Id;
        Settings.PlaybackId = (WizardPlaybackDevice.SelectedItem as AudioDevice)?.Id;
        Settings.SaveFolder = WizardFolderBox.Text.Trim();
        Settings.Format = WizardFormatBox.SelectedItem as string ?? throw new InvalidDataException(UiText.Get("InvalidAudioFormat"));
        Settings.SpeechModel = (string?)WizardSpeechModelBox.SelectedValue ?? throw new InvalidDataException(UiText.Get("ChooseSpeechModel"));
        Settings.Language = (string?)WizardSpeechLanguageBox.SelectedValue ?? throw new InvalidDataException(UiText.Get("InvalidSpeechLanguage"));
        Settings.LiveTranscription = WizardLiveTextCheck.IsChecked == true;
        Settings.TranscribeOnStop = WizardTranscribeCheck.IsChecked == true;
        Settings.FastLiveDrafts = WizardFastDraftsCheck.IsChecked == true;
        Settings.IdentifySpeakersOnStop = WizardSpeakersCheck.IsChecked == true;
        Settings.SummarizeOnStop = WizardAutoSummaryCheck.IsChecked == true;
        Settings.LiveSummary = WizardLiveSummaryCheck.IsChecked == true;
    }

    private void ValidateSpeech()
    {
        if ((Settings.LiveTranscription || Settings.TranscribeOnStop) && !environment.SpeechRuntimeAvailable)
            throw new InvalidOperationException(UiText.Get("SetupRuntimeMissing"));
        if (Settings.LiveTranscription && !environment.IsSpeechModelReady(Settings.FastLiveDrafts ? "base" : Settings.SpeechModel) ||
            Settings.TranscribeOnStop && !environment.IsSpeechModelReady(Settings.SpeechModel))
            throw new InvalidOperationException(UiText.Get("SetupSpeechNotReady"));
        if (Settings.IdentifySpeakersOnStop && (Settings.TranscribeOnStop || Settings.LiveTranscription) && !environment.SpeakerModelsReady)
            throw new InvalidOperationException(UiText.Get("SetupSpeakersNotReady"));
    }

    private void ValidateSummary()
    {
        if ((Settings.SummarizeOnStop || Settings.LiveSummary) && !Settings.CanSummarize)
            throw new InvalidOperationException(UiText.Get("ConsentRequiredForAuto"));
        if ((Settings.SummarizeOnStop || Settings.LiveSummary) && Settings.UsesLocalSummaries && string.IsNullOrWhiteSpace(Settings.OllamaModel))
            throw new InvalidOperationException(UiText.Get("ChooseLocalModel"));
        if ((Settings.SummarizeOnStop || Settings.LiveSummary) && Settings.UsesCloudApi)
            CloudApiKey.Read(Settings.SummaryProvider, Settings.GetCloudProfile());
        if (Settings.LiveSummary && !Settings.LiveTranscription)
            throw new InvalidOperationException(UiText.Get("LiveTextRequired"));
        if (Settings.SummarizeOnStop && !Settings.TranscribeOnStop && !Settings.LiveTranscription)
            throw new InvalidOperationException(UiText.Get("TranscriptionRequired"));
    }

    internal async Task<AppSettings> ValidateAndBuildAsync(CancellationToken cancellationToken)
    {
        ReadFields();
        Settings.Validate();
        await environment.ValidateAudioAsync(ReadSources(), cancellationToken);
        await Task.Run(() => SetupValidation.CheckFolder(Settings.SaveFolder, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSpeech();
        ValidateSummary();
        var result = SetupValidation.Prepare(Settings, SetupResetMode.KeepSettings);
        result.SetupCompleted = true;
        result.SummarySetupDeferred |= !result.CanSummarize ||
            result.UsesLocalSummaries && string.IsNullOrWhiteSpace(result.OllamaModel);
        return result;
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await RunOperationAsync(async cancellation =>
    {
        ReadFields();
        WizardStatus.Text = UiText.Get("SetupChecking");
        switch (step)
        {
            case 0: await environment.ValidateAudioAsync(ReadSources(), cancellation); break;
            case 1: await Task.Run(() => SetupValidation.CheckFolder(Settings.SaveFolder, cancellation), cancellation); break;
            case 2: ValidateSpeech(); break;
            case 3:
                ValidateSummary();
                Settings.SummarySetupDeferred = false;
                break;
            case 4:
                var result = await ValidateAndBuildAsync(cancellation);
                cancellation.ThrowIfCancellationRequested();
                storage.SaveSettings(result);
                Settings = result;
                completed = allowClose = true;
                DialogResult = true;
                return;
        }
        cancellation.ThrowIfCancellationRequested();
        step++;
        WizardStatus.Text = "";
        UpdateStep();
    });

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && step > 0)
        {
            step--;
            WizardStatus.Text = "";
            UpdateStep();
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (busy || step is not (2 or 3))
            return;
        try
        {
            ReadFields();
            if (step == 2)
                WizardLiveTextCheck.IsChecked = WizardTranscribeCheck.IsChecked = WizardSpeakersCheck.IsChecked = false;
            else
                Settings.SummarySetupDeferred = true;
            WizardAutoSummaryCheck.IsChecked = WizardLiveSummaryCheck.IsChecked = false;
            step++;
            WizardStatus.Text = UiText.Get(step == 3 ? "SetupSpeechLater" : "SetupLlmLater");
            UpdateStep();
        }
        catch (Exception ex) { WizardStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = UiText.Get("ChooseSavingFolder"), Multiselect = false };
        if (Directory.Exists(WizardFolderBox.Text))
            dialog.InitialDirectory = WizardFolderBox.Text;
        if (dialog.ShowDialog(this) == true)
            WizardFolderBox.Text = dialog.FolderName;
    }

    private void SpeechModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initialized)
            UpdateResourceStatus();
    }

    private async void DownloadSpeech_Click(object sender, RoutedEventArgs e)
    {
        if (WizardSpeechModelBox.SelectedValue is not string model)
            return;
        var downloaded = await DownloadAsync(model, (progress, cancellation) => environment.DownloadSpeechAsync(model, progress, cancellation));
        if (downloaded && newProfile && !closeRequested && !completed &&
            environment.IsSpeechModelReady(model) && environment.SpeechRuntimeAvailable)
            WizardLiveTextCheck.IsChecked = WizardTranscribeCheck.IsChecked = true;
    }

    private async void DownloadBase_Click(object sender, RoutedEventArgs e) =>
        await DownloadAsync("Whisper Base", (progress, cancellation) => environment.DownloadSpeechAsync("base", progress, cancellation));

    private async void DownloadSpeakers_Click(object sender, RoutedEventArgs e) =>
        await DownloadAsync(UiText.Get("SpeakerModels"), environment.DownloadSpeakersAsync);

    private async Task<bool> DownloadAsync(string label, Func<IProgress<double>, CancellationToken, Task> download)
    {
        var downloaded = false;
        await RunOperationAsync(async cancellation =>
        {
            WizardProgress.Visibility = CancelOperationButton.Visibility = Visibility.Visible;
            WizardProgress.Value = 0;
            WizardStatus.Text = UiText.Get("ModelDownloading");
            var progress = new Progress<double>(value =>
            {
                if (closeRequested || completed || cancellation.IsCancellationRequested ||
                    operationCancellation is null || operationCancellation.Token != cancellation)
                    return;
                WizardProgress.Value = value;
                WizardStatus.Text = UiText.Format("ModelDownloadProgress", label, value);
            });
            await download(progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            WizardStatus.Text = UiText.Get("ModelDownloaded");
            downloaded = true;
        });
        return downloaded;
    }

    private void ConfigureLlm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadFields();
            var next = SetupValidation.Prepare(Settings, SetupResetMode.KeepSettings);
            if (Equals(((Button)sender).Tag, SummaryProviders.Ollama))
                next.SummaryProvider = SummaryProviders.Ollama;
            else if (next.UsesLocalSummaries)
                next.SummaryProvider = SummaryProviders.Copilot;
            var dialog = new SettingsWindow(next, "summary", allowSetupReset: false) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                Settings = dialog.Settings;
                LoadFields();
                ApplyLanguage();
            }
            UpdateResourceStatus();
        }
        catch (Exception ex) { WizardStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void OpenRuntime_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist")
            { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { WizardStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void UpdateReview()
    {
        ReadFields();
        var sources = ReadSources();
        ReviewText.Text = UiText.Format("SetupReview",
            sources.Microphone?.Name ?? UiText.Get("AudioSourceDisabled"),
            sources.Playback?.Name ?? UiText.Get("AudioSourceDisabled"), Settings.SaveFolder, Settings.Format,
            UiText.Get(Settings.LiveTranscription ? "SetupEnabled" : "SetupDisabled"),
            Settings.SpeechModel, SummaryProviders.Label(Settings.SummaryProvider, UiText.Current),
            string.IsNullOrWhiteSpace(Settings.ActiveSummaryModel) ? UiText.Get("CliDefaultModel") : Settings.ActiveSummaryModel);
    }

    private Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (busy || closeRequested)
            return Task.CompletedTask;
        busy = true;
        UpdateNavigation();
        operationTask = ExecuteOperationAsync(operation);
        return operationTask;
    }

    private async Task ExecuteOperationAsync(Func<CancellationToken, Task> operation)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operationCancellation = cancellation;
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!closeRequested)
                WizardStatus.Text = UiText.Get("DownloadCancelled");
        }
        catch (Exception ex) { WizardStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
        finally
        {
            operationCancellation = null;
            busy = false;
            WizardProgress.Visibility = CancelOperationButton.Visibility = Visibility.Collapsed;
            if (!closeRequested && !completed)
            {
                UpdateResourceStatus();
                UpdateNavigation();
            }
        }
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e) => operationCancellation?.Cancel();

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowClose || operationTask is not { IsCompleted: false })
            return;
        e.Cancel = true;
        if (closeRequested)
            return;
        closeRequested = true;
        UpdateNavigation();
        operationCancellation?.Cancel();
        await operationTask;
        allowClose = true;
        Close();
    }
}
