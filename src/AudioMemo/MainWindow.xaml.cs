using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;
using NAudio.CoreAudioApi;
using Microsoft.Win32;

namespace AudioMemo;

public partial class MainWindow : Window
{
    private readonly AppStorage storage;
    private readonly string? settingsLoadError;
    private readonly RecordingEngine recorder;
    private readonly Func<RecordingSources, bool> confirmDeviceChange;
    private readonly Func<RecordingSession, bool> confirmContinuation;
    private readonly Action<string> showRecordingDeviceWarning;
    private readonly Func<string, bool> confirmRepeatSummary;
    private readonly AiPipeline ai;
    private readonly RecordingPlayback replay = new();
    private readonly HistoryPeekState historyPeek = new();
    private readonly Stopwatch historyPeekClock = Stopwatch.StartNew();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer notesDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly CancellationTokenSource lifetime = new();
    private AppSettings settings;
    private RecordingSession? selected;
    private CancellationTokenSource? aiCancellation;
    private Task? aiTask;
    private bool recordingCommand;
    private bool exportBusy;
    private bool loadingSession;
    private bool initialized;
    private bool closing;
    private bool notesDirty;
    private bool loadingModels;
    private bool loadingSpeechLanguage;
    private bool loadingDevices;
    private bool refreshingDevices;
    private bool refreshingModels;
    private Task? modelRefreshTask;
    private HwndSource? windowSource;
    private RecordingSession? contextSession;
    private TranscriptDocument currentTranscript = new();
    private string speechRuntime = "";
    private string summaryContent = "";
    private string pendingTitle = "";
    private long warningVersion;
    private (string SessionId, string OperationKey)? warningOperation;
    private bool AiBusy => aiTask is { IsCompleted: false };
    private bool RecordingActive => recorder.State != RecorderState.Idle;

    public MainWindow(AppStorage storage, AppSettings settings, string? settingsLoadError = null)
        : this(storage, settings, new RecordingEngine(), settingsLoadError) { }

    internal MainWindow(AppStorage storage, AppSettings settings, RecordingEngine recorder, string? settingsLoadError = null,
        Func<RecordingSources, bool>? confirmDeviceChange = null, Func<RecordingSession, bool>? confirmContinuation = null,
        AiPipeline? pipeline = null, Func<string, bool>? confirmCloseProcessing = null,
        Func<string, bool>? confirmResumeProcessing = null,
        Func<RecordingSession, ProcessingWorkKind, CancellationToken, Task>? exportAudio = null,
        Func<IReadOnlyList<RecordingSession>, bool>? confirmRecycleHistory = null,
        Action<string, RecordingSession>? recycleHistorySession = null, TimeProvider? processingTimeProvider = null,
        Func<string, bool>? confirmRepeatSummary = null, Func<string, bool>? confirmAppendAboutMe = null,
        Func<AppSettings, CancellationToken, Task>? validateSummarySelection = null,
        Func<AppSettings, string, AppSettings?>? configureSummarySelection = null,
        Action<string>? showRecordingDeviceWarning = null, Action<HelpWindow>? showHelp = null,
        Action<TranscriptReviewWindow>? showTranscriptReview = null)
    {
        this.storage = storage;
        this.settings = settings;
        processingTiming = new(processingTimeProvider);
        this.recorder = recorder;
        this.settingsLoadError = settingsLoadError;
        this.validateSummarySelection = validateSummarySelection ?? new SummaryConfigurationService().ValidateAsync;
        this.configureSummarySelection = configureSummarySelection ?? ConfigureSummarySelection;
        this.confirmDeviceChange = confirmDeviceChange ?? ConfirmDeviceChange;
        this.confirmContinuation = confirmContinuation ?? ConfirmContinuation;
        this.showRecordingDeviceWarning = showRecordingDeviceWarning ?? (message =>
            MessageDialog.ShowMessage(this, message, UiText.Get("RecordingDeviceSetupRequired"),
                MessageBoxButton.OK, MessageBoxImage.Warning));
        this.showHelp = showHelp ?? (dialog => { dialog.Owner = this; dialog.ShowDialog(); });
        this.showTranscriptReview = showTranscriptReview ?? (dialog => { dialog.Owner = this; dialog.ShowDialog(); });
        this.confirmRepeatSummary = confirmRepeatSummary ?? (message => ConfirmProcessing(message, "UpdateMainPoints"));
        this.confirmAppendAboutMe = confirmAppendAboutMe ?? (message => ConfirmProcessing(message, "SummarizeAboutMe"));
        ai = pipeline ?? new();
        this.confirmCloseProcessing = confirmCloseProcessing ?? (message => ConfirmProcessing(message, "CloseAudioMemo"));
        this.confirmResumeProcessing = confirmResumeProcessing ?? (message => ConfirmProcessing(message, "ResumeProcessing"));
        this.confirmRecycleHistory = confirmRecycleHistory ?? ConfirmRecycleHistory;
        this.recycleHistorySession = recycleHistorySession ?? SessionCommands.Recycle;
        this.exportAudio = exportAudio ?? ((session, kind, token) => Task.Run(() =>
        {
            if (kind == ProcessingWorkKind.ExportMp3)
                AudioExport.ToMp3(session.WavePath, session.Mp3Path, session.Frames, session.AudioOutput, token);
            else
                AudioExport.SaveConfigured(session, token);
        }, token));
        InitializeComponent();
        InitializeActivityFeedback();
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(Window_PreviewMouseDown), handledEventsToo: true);
        Deactivated += Window_Deactivated;
        SourceInitialized += (_, _) =>
        {
            windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            windowSource?.AddHook(WindowMessageHook);
        };
        Closed += (_, _) =>
        {
            if (windowSource is { IsDisposed: false })
                windowSource.RemoveHook(WindowMessageHook);
            windowSource = null;
        };
        Language = XmlLanguage.GetLanguage(settings.UiLanguage);
        TranscriptPanel.RenameSpeakerRequested += RenameSpeaker;
        TranscriptPanel.MarkMeRequested += MarkMe;
        TranscriptPanel.RemoveMeRequested += RemoveMe;
        TranscriptPanel.SummarizeAboutMeRequested += SummarizeAboutMe;
        TranscriptPanel.SpeakerColorRequested += SetSpeakerColor;
        TranscriptPanel.SpeakerCorrectionRequested += CorrectSpeaker;
        TranscriptPanel.SearchRequested += () => TranscriptPanel.ShowSearchWindow(this);
        TranscriptPanel.ReviewUnclearRequested += ReviewUnclearTranscript;
        InitializeShortcuts();
        TranscriptPanel.OperationFailed += error => ShowError(error, "Transcript");
        TranscriptPanel.NavigationPositionChanged += SaveTranscriptNavigationPosition;
        TranscriptPanel.ReplayRequested += ReplayRange;
        TranscriptPanel.ReplaySeekRequested += position =>
        {
            try { replay.Seek(position); UpdateReplayDisplay(); }
            catch (Exception ex) { UpdateReplayDisplay(); ShowError(ex); }
        };
        TranscriptPanel.EditRequested += EditSelectedRows;
        TranscriptPanel.ShowMutedRowsRequested += () => RestoreSilence_Click(this, new RoutedEventArgs());
        TranscriptPanel.ExportAudioRequested += () => Export_Click(this, new RoutedEventArgs());
        TranscriptPanel.RecheckUnlabeledRequested += () => RecheckUnlabeledSpeakers_Click(this, new RoutedEventArgs());
        TranscriptPanel.CorrectionRequested += CorrectSelectedRows;
        TranscriptPanel.OnlineTranscriptionRequested += rows => OpenTranscriptCorrection(rows, false, true);
        TranscriptPanel.ReplayPauseRequested += () =>
        {
            try { replay.TogglePause(); UpdateReplayDisplay(); }
            catch (Exception ex) { ShowError(ex); }
        };
        TranscriptPanel.ReplayStopRequested += () =>
        {
            try { StopReplay(); }
            catch (Exception ex) { ShowError(ex); }
        };
        replay.Completed += error =>
        {
            if (Dispatcher.HasShutdownStarted)
                return;
            Dispatcher.BeginInvoke(() =>
            {
                if (closing)
                    return;
                UpdateReplayDisplay();
                if (error is not null)
                    ShowError(error);
                else if (replay.Snapshot is null)
                    SetStatus(UiText.Get("ReplayFinished"));
            });
        };
        clock.Tick += async (_, _) =>
        {
            UpdateRecordingDisplay();
            UpdateReplayDisplay();
            UpdateHistoryPeek();
            UpdateStatusInformation();
            await StopIfSilentAsync();
        };
        notesDebounce.Tick += (_, _) =>
        {
            notesDebounce.Stop();
            try { SaveNotes(); }
            catch (Exception ex) { ShowError(ex); }
        };
        recorder.Warning += message => Dispatcher.BeginInvoke(() =>
        {
            if (RecordingActive)
                ReflectActiveSources();
            SetStatus(message, true);
        });
        recorder.Failed += message => Dispatcher.BeginInvoke(async () =>
        {
            SetStatus(message, true);
            await RunRecordingCommandAsync(StopRecordingAsync);
        });
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (settingsLoadError is not null)
        {
            MessageDialog.ShowMessage(UiText.Format("SettingsLoadFailed", settingsLoadError),
                UiText.Get("SettingsWindowTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ApplySettingsToControls();
        try
        {
            await RefreshDevicesAsync();
            RefreshHistory();
        }
        catch (Exception ex) { ShowError(ex); }
        if (closing)
            return;
        initialized = true;
        clock.Start();
        UpdateControls();
        UpdateModelStatus();
        UpdateRecordingDisplay();
        if (!settings.UsesCloudApi && !settings.SummarySetupDeferred)
            _ = RefreshMainModelsAsync();
        if (!closing)
            await OfferPendingProcessingAsync();
    }

    private void ApplySettingsToControls()
    {
        MicEnabled.IsChecked = settings.MicrophoneEnabled;
        PlaybackEnabled.IsChecked = settings.PlaybackEnabled;
        LiveTextCheck.IsChecked = settings.LiveTranscription;
        UpdateSpeechLanguageSelection();
        InitializePaneChoices();
        TranscriptPanel.SetNavigationPosition(new Point(settings.TranscriptNavigationX, settings.TranscriptNavigationY));
        loadingModels = true;
        MainProviderBox.ItemsSource = SummaryProviders.GetOptions(UiText.Current);
        MainProviderBox.SelectedValue = settings.SummaryProvider;
        SetMainModelChoices();
        loadingModels = false;
        UpdateHistoryVisibility();
        UpdateShortcutHints();
        UpdateWorkspaceLayout();
        UpdateWindowButtons();
        UpdateStatusInformation(invalidate: true);
    }

    private void UpdateSpeechLanguageSelection()
    {
        loadingSpeechLanguage = true;
        try
        {
            var options = SpeechLanguages.GetOptions(UiText.Current, compact: true).ToList();
            if (options.All(option => option.Code != settings.Language))
                options.Add(new(settings.Language, UiText.Format("SpeechLanguageCurrent", settings.Language)));
            MainSpeechLanguageBox.ItemsSource = options;
            MainSpeechLanguageBox.SelectedValue = settings.Language;
        }
        finally { loadingSpeechLanguage = false; }
    }

    private void MainSpeechLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || loadingSpeechLanguage)
            return;
        try
        {
            if (RecordingActive || AiBusy || recordingCommand || exportBusy || closing)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            if (MainSpeechLanguageBox.SelectedItem is not LanguageOption option)
                throw new InvalidOperationException(UiText.Get("InvalidSpeechLanguage"));
            if (option.Code == settings.Language)
                return;
            var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
                ?? throw new InvalidDataException("Could not copy settings.");
            next.Language = SpeechLanguages.ParseInput(option.Code, UiText.Current);
            storage.SaveSettings(next);
            settings = next;
            UpdateSpeechLanguageSelection();
            SetStatus(UiText.Format("RecordingLanguageSaved", option.Label));
        }
        catch (Exception ex)
        {
            UpdateSpeechLanguageSelection();
            ShowError(ex);
        }
    }

    private async Task RefreshDevicesAsync()
    {
        refreshingDevices = true;
        UpdateControls();
        try
        {
            var micId = (MicDevice.SelectedItem as AudioDevice)?.Id ?? settings.MicrophoneId;
            var playbackId = (PlaybackDevice.SelectedItem as AudioDevice)?.Id ?? settings.PlaybackId;
            var devices = await Task.Run(() => (AudioDevices.List(DataFlow.Capture), AudioDevices.List(DataFlow.Render)));
            loadingDevices = true;
            try
            {
                SetDeviceList(MicDevice, devices.Item1, micId);
                SetDeviceList(PlaybackDevice, devices.Item2, playbackId);
            }
            finally { loadingDevices = false; }
            if (devices.Item1.Count == 0 && devices.Item2.Count == 0)
                SetStatus(UiText.Get("NoAudioDevices"), true);
        }
        finally
        {
            refreshingDevices = false;
            UpdateControls();
        }
    }

    private static void SetDeviceList(ComboBox combo, IReadOnlyList<AudioDevice> devices, string? selectedId)
    {
        var items = devices.ToList();
        if (selectedId is not null && items.All(d => d.Id != selectedId))
            items.Add(new(selectedId, UiText.Get("UnavailableDevice")));
        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(d => d.Id == selectedId) ?? items.FirstOrDefault();
    }

    private RecordingSources ReadSources()
    {
        SourceSelection? Read(CheckBox enabled, ComboBox combo, double gain, string name)
        {
            if (enabled.IsChecked != true)
                return null;
            if (combo.SelectedItem is not AudioDevice device)
                throw new RecordingDeviceException(UiText.Format("ChooseWorkingDevice", name));
            return new(device.Id, device.Name, (float)gain);
        }
        var sources = new RecordingSources(Read(MicEnabled, MicDevice, settings.MicrophoneGain, UiText.Get("Microphone")),
            Read(PlaybackEnabled, PlaybackDevice, settings.PlaybackGain, UiText.Get("Playback")));
        sources.Validate();
        return sources;
    }

    private void SaveControlSettings()
    {
        var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
            ?? throw new InvalidDataException("Could not copy recording settings.");
        next.MicrophoneEnabled = MicEnabled.IsChecked == true;
        next.PlaybackEnabled = PlaybackEnabled.IsChecked == true;
        next.MicrophoneId = (MicDevice.SelectedItem as AudioDevice)?.Id;
        next.PlaybackId = (PlaybackDevice.SelectedItem as AudioDevice)?.Id;
        next.LiveTranscription = LiveTextCheck.IsChecked == true;
        storage.SaveSettings(next);
        settings = next;
    }

    private void ReflectActiveSources()
    {
        loadingDevices = true;
        try
        {
            var sources = recorder.Sources;
            MicEnabled.IsChecked = sources.Microphone is not null;
            PlaybackEnabled.IsChecked = sources.Playback is not null;
            if (sources.Microphone is not null)
                MicDevice.SelectedValue = sources.Microphone.DeviceId;
            if (sources.Playback is not null)
                PlaybackDevice.SelectedValue = sources.Playback.DeviceId;
        }
        finally { loadingDevices = false; }
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunRecordingCommandAsync(async () =>
    {
        try { await StartOrContinueRecordingAsync(); }
        catch (RecordingDeviceException ex)
        {
            if (!closing)
                showRecordingDeviceWarning(UiText.Format("RecordingDeviceSetupWarning", ex.Message));
            throw;
        }
    });

    private async Task StartOrContinueRecordingAsync()
    {
        modelSelectionCancellation?.Cancel();
        if (modelSelectionTask is not null)
            await modelSelectionTask;
        if (recorder.State is RecorderState.Recording or RecorderState.Paused)
        {
            StopReplay();
            await TogglePauseAsync();
            return;
        }
        var sources = ReadSources();
        if (recorder.State == RecorderState.Idle && selected is { } current && !confirmContinuation(current))
        {
            SetStatus(UiText.Get("ContinueRecordingCancelled"));
            return;
        }
        StopReplay();
        await CancelAiAsync();
        AcknowledgeProcessing();
        SaveNotes();
        if (selected is not null)
            EnsureRecovered(selected);
        SaveControlSettings();
        var continuing = selected is not null;
        selected = continuing
            ? await ContinueRecordingAsync(selected!, sources)
            : await recorder.StartAsync(settings, pendingTitle, sources);
        ReflectActiveSources();
        LoadSessionContent(selected);
        SetStatus(UiText.Get(continuing ? "RecordingContinued" : "RecordingStarted"));
        RefreshHistory();
        if (settings.LiveTranscription && !closing)
        {
            if (SpeechModels.IsDownloaded(settings.FastLiveDrafts ? "base" : settings.SpeechModel))
                StartTranscription(selected, true);
            else
                SetStatus(UiText.Get("RecordingWithoutModel"), true);
        }
    }

    private bool ConfirmContinuation(RecordingSession session) =>
        MessageDialog.ShowMessage(this, UiText.Format("ConfirmContinueRecording", session.Title, session.OutputPath),
            UiText.Get("ContinueRecording"), MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task TogglePauseAsync()
    {
        if (recorder.State == RecorderState.Paused)
        {
            await recorder.ResumeAsync();
            ReflectActiveSources();
            SetStatus(UiText.Get("RecordingResumed"));
        }
        else
        {
            await recorder.PauseAsync();
            SetStatus(UiText.Get("RecordingPaused"));
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunRecordingCommandAsync(StopRecordingAsync);

    private async Task StopRecordingAsync()
    {
        if (recorder.State == RecorderState.Idle)
            return;
        var completingLiveTranscription = AiBusy && activeAiOperation == (recorder.Session?.Id, "TranscribeResume");
        var activity = BeginProcessing();
        var outcome = ProcessingOutcome.Completed;
        try
        {
            var session = await recorder.StopAsync();
            UpdateActivityDisplay();
            if (session is null)
                return;
            SaveNotes();
            selected = session;
            pendingTitle = "";
            SetStatus(session.Status == "Interrupted"
                ? UiText.Format("RecordingInterrupted", session.LastError)
                : UiText.Format("RecordingSaved", session.WavePath), session.Status == "Interrupted");
            RefreshHistory();
            if (session.Status == "Interrupted")
                outcome = ProcessingOutcome.Failed;
            if (session.Frames > 0 && session.Status != "Interrupted")
            {
                var work = new List<ProcessingWork>();
                if (RequiresAudioExport(session))
                    work.Add(new(ProcessingWorkKind.ExportAudio));
                if (!completingLiveTranscription && !AiBusy && settings.TranscribeOnStop)
                    work.Add(ProcessingWork.ForAi("TranscribeResume", settings));
                postRecordingWork = new(session, work);
                if (closing)
                    return;
                try
                {
                    await ExportConfiguredAsync(session);
                    work.RemoveAll(item => item.Kind == ProcessingWorkKind.ExportAudio);
                }
                catch (OperationCanceledException) when (closing) { return; }
                catch (Exception ex) { outcome = ProcessingOutcome.Failed; ShowError(ex); }
            }
            if (!closing && !completingLiveTranscription && !AiBusy && settings.TranscribeOnStop && session.Frames > 0 && session.Status != "Interrupted")
            {
                if (SpeechModels.IsDownloaded(settings.SpeechModel))
                    StartTranscription(session, false);
                else
                {
                    outcome = outcome == ProcessingOutcome.Failed ? outcome : ProcessingOutcome.Warning;
                    SetStatus(UiText.Get("SavedWithoutModel"), true);
                }
            }
        }
        catch
        {
            outcome = ProcessingOutcome.Failed;
            throw;
        }
        finally
        {
            if (!closing)
                postRecordingWork = null;
            FinishProcessing(activity, outcome);
        }
    }

    private async void Device_SelectionChanged(object sender, SelectionChangedEventArgs e) => await ApplyLiveDeviceChangeAsync();

    private async void DeviceEnabled_Click(object sender, RoutedEventArgs e) => await ApplyLiveDeviceChangeAsync();

    private Task ApplyLiveDeviceChangeAsync() =>
        initialized && !loadingDevices && !refreshingDevices && !closing &&
        recorder.State is RecorderState.Idle or RecorderState.Recording or RecorderState.Paused
            ? RunRecordingCommandAsync(ApplyDeviceChangesAsync) : Task.CompletedTask;

    private bool ConfirmDeviceChange(RecordingSources sources) =>
        MessageDialog.ShowMessage(this, UiText.Format("ConfirmDeviceChange",
            sources.Microphone?.Name ?? UiText.Get("AudioSourceDisabled"),
            sources.Playback?.Name ?? UiText.Get("AudioSourceDisabled")),
            UiText.Get("ApplyDevices"), MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private async Task ApplyDeviceChangesAsync()
    {
        try
        {
            var sources = ReadSources();
            var switching = RecordingActive && sources != recorder.Sources;
            if (switching)
            {
                if (!confirmDeviceChange(sources))
                {
                    ReflectActiveSources();
                    SetStatus(UiText.Get("DeviceChangeCancelled"));
                    return;
                }
                await recorder.SwitchSourcesAsync(sources);
                ReflectActiveSources();
            }
            SaveControlSettings();
            SetStatus(UiText.Get(switching ? "SourcesUpdated" : "DevicePreferencesSaved"));
        }
        catch
        {
            if (RecordingActive)
                ReflectActiveSources();
            else if (recorder.State == RecorderState.Idle)
            {
                loadingDevices = true;
                try
                {
                    MicEnabled.IsChecked = settings.MicrophoneEnabled;
                    PlaybackEnabled.IsChecked = settings.PlaybackEnabled;
                    if (settings.MicrophoneId is not null)
                        MicDevice.SelectedValue = settings.MicrophoneId;
                    if (settings.PlaybackId is not null)
                        PlaybackDevice.SelectedValue = settings.PlaybackId;
                }
                finally { loadingDevices = false; }
            }
            throw;
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        await RunRecordingCommandAsync(RefreshDevicesAsync);
    }

    private async Task RunRecordingCommandAsync(Func<Task> operation)
    {
        if (recordingCommand || closing)
            return;
        var completion = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        recordingCommandCompletion = completion;
        Exception? failure = null;
        recordingCommand = true;
        UpdateControls();
        try { await operation(); }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) { failure = ex; ShowError(ex); }
        finally
        {
            recordingCommand = false;
            completion.TrySetResult(failure);
            UpdateControls();
            if (recorder.State == RecorderState.Faulted && !closing)
                _ = Dispatcher.BeginInvoke(async () => await RunRecordingCommandAsync(StopRecordingAsync));
        }
    }

    private void StartTranscription(RecordingSession session, bool live)
    {
        StartAiJobCore(session, "TranscribeResume",
            live && !settings.TranscribeOnStop ? [] : [ProcessingWork.ForAi("TranscribeResume", settings)],
            (snapshot, progress, cancellationToken) => ai.TranscribeAsync(session, snapshot,
            () => live && recorder.Session?.Id == session.Id && recorder.State != RecorderState.Idle
                ? (recorder.Progress.CommittedFrames, true) : (session.Frames, false),
            progress, cancellationToken, ReviewTranscriptBeforeSummaryAsync));
    }

    private void StartAiJob(RecordingSession session, string operationKey,
        Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> operation)
        => StartAiJobCore(session, operationKey, [ProcessingWork.ForAi(operationKey, settings)], operation);

    private void StartAiJobCore(RecordingSession session, string operationKey, List<ProcessingWork> work,
        Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> operation, AppSettings? runSettings = null)
    {
        if (AiBusy || selectingModel || closing)
            throw new InvalidOperationException(UiText.Get("CancelCurrentAi"));
        aiCancellation?.Dispose();
        aiCancellation = new CancellationTokenSource();
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(runSettings ?? settings))
            ?? throw new InvalidOperationException("Could not copy AI settings.");
        activeAiWork = new(session, work);
        var jobVersion = ++aiJobVersion;
        var progress = new Progress<AiUpdate>(update =>
        {
            if (jobVersion != aiJobVersion || closing || !processing.IsActive(aiActivityId) || update.SessionId != session.Id)
                return;
            if (update.IsWarning)
                processing.Warn(aiActivityId);
            try
            {
                if (!RecordingActive && processingTiming.IsRunning && update.WorkProgress is { } workProgress)
                {
                    processingTiming.Report(workProgress.Phase, workProgress.Fraction);
                    UpdateProcessingTiming(processing.Outcome);
                }
                if (selected?.Id != update.SessionId)
                    return;
                if (update.Document is not null)
                {
                    currentTranscript = update.Document.Copy();
                    AppStorage.RefreshTranscriptColors(session, currentTranscript);
                    TranscriptPanel.SetDocument(currentTranscript);
                    TranscriptPanel.CanSetSpeakerColors = CanSetSpeakerColors;
                    UpdateSpeechStatus();
                    UpdateModelStatus();
                }
                if (update.SpeechRuntime is not null)
                {
                    speechRuntime = update.SpeechRuntime;
                    UpdateSpeechStatus();
                }
                if (update.SummaryModel is not null)
                {
                    ActiveModelText.Text = UiText.Format("UsingModel", update.SummaryModel);
                    ActiveModelText.ToolTip = ActiveModelText.Text;
                }
                if (update.Summary is not null)
                {
                    summaryContent = update.Summary;
                    UpdateModelStatus();
                }
                SetStatus(update.Status, update.IsWarning);
            }
            catch (Exception ex)
            {
                processing.Warn(aiActivityId);
                ShowOperationError(ex, operationKey, session.Id);
            }
        });
        aiTask = ExecuteAiAsync(session, operationKey, () => operation(snapshot, progress, aiCancellation.Token));
        UpdateControls();
    }

    private async Task ExecuteAiAsync(RecordingSession session, string operationKey, Func<Task> operation)
    {
        var activity = BeginProcessing();
        aiActivityId = activity;
        activeAiOperation = (session.Id, operationKey);
        var outcome = ProcessingOutcome.Completed;
        var work = activeAiWork;
        var priorWarning = warningOperation == (session.Id, operationKey) ? warningVersion : -1;
        try
        {
            await operation();
            if (work is not null)
            {
                foreach (var item in work.Work.ToArray())
                    CompletePendingWork(session, item.Kind);
                work.Completed = true;
            }
        }
        catch (OperationCanceledException)
        {
            outcome = ProcessingOutcome.Cancelled;
            SetStatus(UiText.Get("AiCancelled"));
        }
        catch (Exception ex)
        {
            outcome = ProcessingOutcome.Failed;
            ShowOperationError(ex, operationKey, session.Id);
        }
        finally
        {
            // Progress<T> posts to the dispatcher; drain its final warnings before showing completion.
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (outcome == ProcessingOutcome.Completed && priorWarning == warningVersion && warningOperation == (session.Id, operationKey))
                DismissWarning_Click(this, new RoutedEventArgs());
            try
            {
                if (selected?.Id == session.Id && !closing)
                    RefreshHistory();
            }
            catch (Exception ex)
            {
                outcome = ProcessingOutcome.Failed;
                ShowOperationError(ex, operationKey, session.Id);
            }
            ActiveModelText.Text = "";
            activeAiOperation = null;
            if (ReferenceEquals(activeAiWork, work))
                activeAiWork = null;
            FinishProcessing(activity, outcome);
            _ = Dispatcher.BeginInvoke(UpdateControls);
        }
    }

    private async Task CancelAiAsync()
    {
        if (AiBusy)
        {
            aiCancellation?.Cancel();
            if (aiTask is not null)
                await aiTask;
        }
        UpdateControls();
    }

    private void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = RequireIdleSession();
            EnsureRecovered(session);
            var pending = PendingProcessingStore.Load(session);
            if (pending is null)
                StartTranscription(session, false);
            else
                StartPendingProcessing(session, pending);
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Summarize_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (selected is null)
                return;
            if (MultipleHistorySelection || AiBusy || recordingCommand || exportBusy || closing)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            var transcript = AppStorage.LoadTranscript(selected);
            if (!RecordingActive && (transcript.IsDraft || transcript.ProcessedFrames != selected.Frames))
                throw new InvalidOperationException(UiText.Get("SummaryRequiresFinalTranscript"));
            string? generationId = null;
            if (!RecordingActive && AppStorage.HasCurrentSummary(selected, transcript))
            {
                var model = string.IsNullOrWhiteSpace(settings.ActiveSummaryModel)
                    ? UiText.Get("CliDefaultModel") : settings.ActiveSummaryModel;
                if (!confirmRepeatSummary(UiText.Format("ConfirmRepeatSummary",
                    SummaryProviders.Label(settings.SummaryProvider, UiText.Current), model)))
                    return;
                generationId = Guid.NewGuid().ToString("N");
            }
            else if (!RecordingActive && AppStorage.LoadSummaryDocument(selected) is { HasOverallSummary: true } &&
                AppStorage.SummaryNeedsRefresh(selected, transcript))
                generationId = Guid.NewGuid().ToString("N");
            if (!EnsureSummaryConsent())
                return;
            var session = selected;
            var live = RecordingActive;
            StartAiJobCore(session, "UpdateMainPoints", [ProcessingWork.ForAi("UpdateMainPoints", settings, generationId)],
                (snapshot, progress, cancellationToken) =>
                    ai.SummarizeAsync(session, snapshot, progress, live, cancellationToken, generationId,
                        ReviewTranscriptBeforeSummaryAsync));
        }
        catch (Exception ex) { ShowOperationError(ex, "UpdateMainPoints", selected?.Id); }
    }

    private async void CancelAi_Click(object sender, RoutedEventArgs e)
    {
        modelSelectionCancellation?.Cancel();
        await CancelAiAsync();
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = RequireIdleSession();
            EnsureRecovered(session);
            await ExportConfiguredAsync(session);
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ExportMp3_Click(object sender, RoutedEventArgs e)
    {
        if (MultipleHistorySelection)
        {
            await ChooseHistoryExportAsync(HistoryExportKind.Mp3);
            return;
        }
        try
        {
            var session = RequireIdleSession();
            EnsureRecovered(session);
            await ExportMp3Async(session);
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) { ShowError(ex); }
    }

    private Task ExportConfiguredAsync(RecordingSession session) => StartAudioExport(session, ProcessingWorkKind.ExportAudio);
    private Task ExportMp3Async(RecordingSession session) => StartAudioExport(session, ProcessingWorkKind.ExportMp3);

    private Task StartAudioExport(RecordingSession session, ProcessingWorkKind kind)
    {
        if (closing || exportTask is { IsCompleted: false })
            throw new InvalidOperationException(UiText.Get("SessionBusy"));
        exportCancellation?.Dispose();
        exportCancellation = new();
        activeExportWork = new(session, [new(kind)]);
        return exportTask = ExecuteAudioExportAsync(session, kind, activeExportWork, exportCancellation.Token);
    }

    private async Task ExecuteAudioExportAsync(RecordingSession session, ProcessingWorkKind kind,
        ActiveProcessingWork work, CancellationToken cancellationToken)
    {
        var activity = BeginProcessing();
        var outcome = ProcessingOutcome.Completed;
        exportBusy = true;
        UpdateControls();
        SetStatus(UiText.Get(kind == ProcessingWorkKind.ExportMp3 ? "ExportingMp3" : "ExportingConfiguredAudio"));
        try
        {
            await exportAudio(session, kind, cancellationToken);
            CompletePendingWork(session, kind);
            work.Completed = true;
            SetStatus(UiText.Format("RecordingSaved", kind == ProcessingWorkKind.ExportMp3 ? session.Mp3Path : session.OutputPath));
        }
        catch (OperationCanceledException)
        {
            outcome = ProcessingOutcome.Cancelled;
            throw;
        }
        catch
        {
            outcome = ProcessingOutcome.Failed;
            throw;
        }
        finally
        {
            exportBusy = false;
            if (ReferenceEquals(activeExportWork, work))
                activeExportWork = null;
            FinishProcessing(activity, outcome);
            UpdateControls();
        }
    }

    private static void EnsureRecovered(RecordingSession session)
    {
        if (session.Status is "Recording" or "Paused" or "Interrupted")
            throw new InvalidOperationException(UiText.Get("RecoverFirst"));
        if (!File.Exists(session.WavePath))
            throw new FileNotFoundException(UiText.Get("WaveMissing"));
    }

    private void Recover_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = RequireIdleSession();
            StopReplay();
            session.Frames = DurableWaveFile.Recover(session.WavePath);
            session.Status = "Recovered";
            session.LastError = null;
            session.Save();
            RefreshHistory();
            UpdateControls();
            SetStatus(UiText.Get("WaveRecovered"));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async Task ShowSettingsAsync(string category = "appearance")
    {
        try
        {
            if (RecordingActive || AiBusy || recordingCommand || exportBusy || selectingModel || closing)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            SaveNotes();
            StopReplay();
            var dialog = new SettingsWindow(settings, category, validateSummary: ValidateChangedSettingsAsync) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                if (dialog.SetupRequest is { } resetMode)
                {
                    var setup = new SetupWizardWindow(storage, SetupValidation.Prepare(settings, resetMode))
                    { Owner = this, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                    if (setup.ShowDialog() != true)
                        return;
                    settings = setup.Settings;
                }
                else
                {
                    storage.SaveSettings(dialog.Settings);
                    settings = dialog.Settings;
                }
                UiText.Current.ChangeLanguage(settings.UiLanguage);
                AppAppearance.Apply(Application.Current, settings);
                Language = XmlLanguage.GetLanguage(settings.UiLanguage);
                UpdateWorkspaceTitle();
                WorkspaceSubtitle.Text = selected is null ? ""
                    : UiText.Format("SessionSubtitle", selected.CreatedAt, selected.RequestedFormat);
                NotesSaveState.Text = UiText.Get(selected is null ? "NotesAutoSaved" : "NotesSaved");
                ApplySettingsToControls();
                await RefreshDevicesAsync();
                RefreshHistory();
                UpdateModelStatus();
                UpdateControls();
                UpdateRecordingDisplay();
                TranscriptPanel.SetDocument(currentTranscript);
                UpdateSpeechStatus();
                SetStatus(UiText.Get("SettingsSaved"));
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshHistory()
    {
        var selection = SelectedHistorySessions().Select(HistorySelectionPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentPath = selected is null ? null : HistorySelectionPath(selected);
        if (currentPath is not null && !selection.Contains(currentPath))
            selection = new(StringComparer.OrdinalIgnoreCase) { currentPath };
        var (sessions, errors) = AppStorage.LoadSessions(settings.SaveFolder);
        loadingSession = true;
        try
        {
            HistoryList.ItemsSource = sessions;
            if (sessions.FirstOrDefault(session =>
                HistorySelectionPath(session).Equals(currentPath, StringComparison.OrdinalIgnoreCase)) is { } current)
                HistoryList.SelectedItems.Add(current);
            foreach (var session in sessions.Where(session => selection.Contains(HistorySelectionPath(session))))
                if (!HistoryList.SelectedItems.Contains(session))
                    HistoryList.SelectedItems.Add(session);
        }
        finally { loadingSession = false; }
        SyncHistoryPreview();
        UpdateStatusInformation(invalidate: true);
        if (errors.Count > 0)
            SetStatus(UiText.Format("HistoryReadFailed", string.Join("; ", errors)), true);
    }

    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loadingSession)
            SyncHistoryPreview();
    }

    private void SyncHistoryPreview()
    {
        try
        {
            if (!initialized || RecordingActive || AiBusy || recordingCommand || exportBusy || closing)
                return;
            var sessions = SelectedHistorySessions();
            var currentPath = selected is null ? null : HistorySelectionPath(selected);
            var session = sessions.FirstOrDefault(item =>
                HistorySelectionPath(item).Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                ?? sessions.FirstOrDefault();
            if (session is null)
            {
                if (selected is not null)
                {
                    SaveNotes();
                    ClearWorkspace();
                }
            }
            else if (!HistorySelectionPath(session).Equals(currentPath, StringComparison.OrdinalIgnoreCase))
            {
                SaveNotes();
                StopReplay();
                selected = session;
                LoadSessionContent(session);
            }
            if (sessions.Length > 1)
                StopReplay();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { UpdateControls(); }
    }

    private void LoadSessionContent(RecordingSession session)
    {
        selectedPendingProcessing = null;
        loadingSession = true;
        try
        {
            UpdateWorkspaceTitle();
            WorkspaceSubtitle.Text = UiText.Format("SessionSubtitle", session.CreatedAt, session.RequestedFormat);
            currentTranscript = AppStorage.LoadTranscript(session);
            speechRuntime = "";
            TranscriptPanel.SetDocument(currentTranscript, reset: true);
            UpdateSpeechStatus();
            summaryContent = AppStorage.LoadSummaryText(session);
            NotesBox.Text = File.Exists(session.NotesPath) ? File.ReadAllText(session.NotesPath) : "";
            UpdateModelStatus();
            selectedPendingProcessing = PendingProcessingStore.Load(session);
            notesDirty = false;
            NotesSaveState.Text = UiText.Get("NotesAutoSaved");
        }
        finally { loadingSession = false; }
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveNotes();
            if (RecordingActive || AiBusy || exportBusy || recordingCommand)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            var dialog = CreateNewConversationDialog();
            if (dialog.ShowDialog() != true)
                return;
            ClearWorkspace();
            pendingTitle = dialog.Value;
            UpdateControls();
            SetStatus(UiText.Get("ReadyNewRecording"));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private TextInputWindow CreateNewConversationDialog() => new(this, UiText.Get("NameNewConversation"),
        NewConversationPrefix(DateTime.Now),
        160, allowEmpty: true, selectAll: false);

    private static string NewConversationPrefix(DateTime time) =>
        time.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture) + " - ";

    private void UpdateWorkspaceTitle()
    {
        var name = selected?.Title ?? pendingTitle;
        var named = !string.IsNullOrWhiteSpace(name);
        WorkspaceTitle.Text = named ? name : UiText.Get("WorkspaceTitle");
        Title = named ? $"AudioMemo - {name}" : UiText.Get("MainWindowTitle");
    }

    private void ClearWorkspace()
    {
        StopReplay();
        selected = null;
        selectedPendingProcessing = null;
        loadingSession = true;
        HistoryList.UnselectAll();
        NotesBox.Text = "";
        currentTranscript = new();
        TranscriptPanel.SetDocument(currentTranscript, reset: true);
        speechRuntime = "";
        UpdateSpeechStatus();
        summaryContent = "";
        UpdateModelStatus();
        loadingSession = false;
        notesDirty = false;
        pendingTitle = "";
        UpdateWorkspaceTitle();
        WorkspaceSubtitle.Text = "";
    }

    private void Notes_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || loadingSession || selected is null)
            return;
        notesDirty = true;
        NotesSaveState.Text = UiText.Get("NotesSaving");
        notesDebounce.Stop();
        notesDebounce.Start();
    }

    private void SaveNotes()
    {
        if (!notesDirty || selected is null)
            return;
        AtomicFile.WriteText(selected.NotesPath, NotesBox.Text);
        notesDirty = false;
        NotesSaveState.Text = UiText.Get("NotesSaved");
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => OpenFolder(settings.SaveFolder);
    private void OpenSession_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (closing)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            if (MultipleHistorySelection)
                ExplorerSelection.Open(SelectedHistorySessions().Select(session => session.Folder).ToArray());
            else if (selected is not null)
                OpenFolder(selected.Folder);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateRecordingDisplay()
    {
        var progress = recorder.Progress;
        var active = RecordingActive;
        TimerText.Text = TranscriptDocument.Timestamp((active ? PcmFormat.Duration(progress.Frames) : selected?.Duration ?? TimeSpan.Zero).TotalSeconds);
        LevelMeter.Value = progress.State == RecorderState.Recording ? progress.Peak : 0;
        RecorderStatus.Text = UiText.Get(progress.State switch
        {
            RecorderState.Recording => "StateRecording",
            RecorderState.Paused => "StatePaused",
            RecorderState.Starting => "StateStarting",
            RecorderState.Stopping => "StateStopping",
            RecorderState.Faulted => "StateFaulted",
            _ => selected is null ? "StateReady" : "StateSaved"
        });
        RecordingDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
            progress.State == RecorderState.Recording ? "SystemFillColorCriticalBrush" :
            progress.State == RecorderState.Paused ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        var frames = active ? progress.CommittedFrames : selected?.Frames ?? 0;
        var output = selected?.AudioOutput ?? settings.AudioOutput;
        SavedStatus.Text = UiText.Format("SavedAudioProfile", selected?.RequestedFormat ?? settings.Format,
            output.SampleRate / 1000d, UiText.Get(output.Channels == 1 ? "AudioMono" : "AudioStereo"),
            frames * PcmFormat.BlockAlign / (1024d * 1024));
        AudioHealth.Text = active && (progress.LateFrames > 0 || progress.ClippedSamples > 0 || progress.Discontinuities > 1)
            ? UiText.Format("AudioWarnings", progress.LateFrames, progress.ClippedSamples, progress.Discontinuities)
            : UiText.Get("SafetyWaveRetained");
        UpdateActivityDisplay();
    }

    private void UpdateControls()
    {
        UpdateWorkspaceTitle();
        var active = RecordingActive;
        var busy = AiBusy || selectingModel;
        var multiple = MultipleHistorySelection;
        var canContinue = selected is { Status: "Saved" or "Recovered" } && File.Exists(selected.WavePath);
        StartButton.IsEnabled = !recordingCommand && !exportBusy && !refreshingDevices && !closing &&
            (recorder.State is RecorderState.Recording or RecorderState.Paused ||
                recorder.State == RecorderState.Idle && !multiple && (selected is null || canContinue));
        StartButton.Content = UiText.Get(recorder.State == RecorderState.Paused ? "Resume" : active ? "Pause" :
            selected is null ? "StartRecording" : "ContinueRecording");
        ContinueRecordingMenu.IsEnabled = canContinue && !multiple && !active && !AiBusy && !recordingCommand && !exportBusy && !closing;
        StopButton.IsEnabled = active && !recordingCommand && !closing;
        var canChooseDevices = !recordingCommand && !refreshingDevices && !closing &&
            recorder.State is RecorderState.Idle or RecorderState.Recording or RecorderState.Paused;
        RefreshDevicesButton.IsEnabled = canChooseDevices;
        MicDevice.IsEnabled = canChooseDevices;
        PlaybackDevice.IsEnabled = canChooseDevices;
        MicEnabled.IsEnabled = canChooseDevices;
        PlaybackEnabled.IsEnabled = canChooseDevices;
        LiveTextCheck.IsEnabled = !active && !recordingCommand && !closing;
        var canConfigure = !active && !recordingCommand && !AiBusy && !exportBusy && !selectingModel && !closing;
        AppSettingsMenu.IsEnabled = canConfigure;
        MainSpeechLanguageBox.IsEnabled = initialized && canConfigure;
        HistoryList.IsEnabled = !active && !recordingCommand && !busy && !exportBusy && !closing;
        NewButton.IsEnabled = !active && !recordingCommand && !busy && !exportBusy && !closing;
        ImportAudioButton.IsEnabled = NewButton.IsEnabled && !closing;
        NotesBox.IsEnabled = selected is not null && !multiple && !recyclingHistory && !closing;
        OpenSessionButton.IsEnabled = selected is not null && !closing;
        TranscribeButton.IsEnabled = selected is { Frames: > 0 } && !multiple && !active && !recordingCommand && !AiBusy && !exportBusy && !selectingModel && !closing;
        TranscribeButton.Content = TranscribeSessionMenu.Header = UiText.Get(
            selectedPendingProcessing?.SessionId == selected?.Id && selectedPendingProcessing is not null ? "ResumeProcessing" : "TranscribeResume");
        IdentifySpeakersButton.IsEnabled = TranscribeButton.IsEnabled && currentTranscript.Segments.Count > 0 &&
            !currentTranscript.IsDraft && currentTranscript.ProcessedFrames == selected?.Frames;
        SummarizeButton.IsEnabled = selected is not null && !multiple && currentTranscript.Segments.Count > 0 &&
            File.Exists(selected.TranscriptPath) && !AiBusy && !recordingCommand && !exportBusy && !selectingModel && !closing;
        TranscriptPanel.CanExportAudio = TranscribeButton.IsEnabled;
        CancelAiButton.IsEnabled = (AiBusy || selectingModel) && !closing;
        RecoverButton.Visibility = !active && selected?.Status is "Recording" or "Paused" or "Interrupted"
            ? Visibility.Visible : Visibility.Collapsed;
        RecoverButton.IsEnabled = RecoverSessionMenu.IsEnabled = !active && !busy && !recordingCommand &&
            !exportBusy && !closing && !multiple && selected is not null && File.Exists(selected.WavePath);
        var canChooseModel = !active && !AiBusy && !recordingCommand && !exportBusy && !refreshingModels && !selectingModel && !closing;
        MainProviderBox.IsEnabled = canChooseModel;
        MainModelBox.IsEnabled = canChooseModel;
        ApplyModelButton.IsEnabled = canChooseModel;
        RefreshModelButton.IsEnabled = canChooseModel;
        TranscriptPanel.CanReplay = !active && !multiple && !recordingCommand && !exportBusy && !closing && selected is { Frames: > 0 } &&
            selected.Status is not ("Recording" or "Paused" or "Interrupted") && File.Exists(selected.WavePath);
        TranscriptPanel.CanEdit = TranscriptPanel.CanReplay && !busy && !currentTranscript.IsDraft &&
            currentTranscript.Segments.Count > 0 && currentTranscript.ProcessedFrames == selected?.Frames;
        TranscriptPanel.CanCorrect = !active && !multiple && !busy && !recordingCommand && !exportBusy && !closing &&
            selected is { Frames: > 0 } && selected.Status is not ("Recording" or "Paused" or "Interrupted") &&
            !currentTranscript.IsDraft && currentTranscript.Segments.Count > 0 && currentTranscript.ProcessedFrames == selected.Frames;
        TranscriptPanel.CanReviewUnclear = TranscriptPanel.CanCorrect;
        var idleSession = selected is not null && !multiple && !active && !busy && !recordingCommand && !exportBusy && !closing;
        TranscriptPanel.CanChangeSpeakers = idleSession;
        TranscriptPanel.CanSetSpeakerColors = CanSetSpeakerColors;
        TranscriptPanel.CanSummarizeAboutMe = idleSession && selected is { Frames: > 0 } && currentTranscript.Segments.Count > 0;
        UpdateMyNotesButton.IsEnabled = TranscriptPanel.CanSummarizeAboutMe;
        var idleSelection = HistoryList.SelectedItems.Count > 0 && !active && !busy && !recordingCommand && !exportBusy && !closing;
        RenameSessionMenu.IsEnabled = idleSession || idleSelection;
        DeleteSessionMenu.IsEnabled = HistoryList.SelectedItems.Count > 0 &&
            !active && !busy && !recordingCommand && !exportBusy && !closing;
        CopySessionMenu.IsEnabled = idleSession && currentTranscript.Segments.Count > 0;
        ExportTranscriptMenu.IsEnabled = multiple ? idleSelection : CopySessionMenu.IsEnabled;
        OpenSessionMenu.IsEnabled = OpenSessionButton.IsEnabled;
        TranscribeSessionMenu.IsEnabled = TranscribeButton.IsEnabled;
        IdentifySpeakersMenu.IsEnabled = IdentifySpeakersButton.IsEnabled;
        RecheckUnlabeledSpeakersMenu.IsEnabled = TranscriptPanel.CanRecheckUnlabeled = IdentifySpeakersButton.IsEnabled &&
            currentTranscript.Segments.Any(segment => segment.SpeakerId is null && !string.IsNullOrWhiteSpace(segment.Text)) &&
            currentTranscript.Segments.Any(segment => segment.SpeakerId is not null);
        SummarizeSessionMenu.IsEnabled = idleSession && SummarizeButton.IsEnabled;
        ExportMp3Menu.IsEnabled = multiple ? idleSelection : TranscriptPanel.CanExportAudio;
        CloseMainPointsButton.IsEnabled = CloseMyNotesButton.IsEnabled = PanelsButton.IsEnabled = !closing;
        RestoreSilenceButton.IsEnabled = TranscriptPanel.CanReplay && !busy && !currentTranscript.IsDraft &&
            currentTranscript.ProcessedFrames == selected?.Frames &&
            (selected?.Edit?.Mode is "Silence" or "Restore" ||
                selected?.Edit is { Mode: AudioWorkspaceService.Mode, RemovedRanges.Count: > 0 });
        RestoreSilenceMenu.IsEnabled = RestoreSilenceButton.IsEnabled;
        TranscriptPanel.CanRestoreMutedRows = RestoreSilenceButton.IsEnabled;
        UpdateHistorySelectionSummary();
        UpdateActivityDisplay();
        UpdateApplicationMenu();
    }

    private void UpdateModelStatus()
    {
        var local = SpeechModels.IsDownloaded(settings.SpeechModel) ? $"Whisper {settings.SpeechModel}" : UiText.Get("SpeechModelMissing");
        var provider = SummaryProviders.Label(settings.SummaryProvider, UiText.Current);
        var model = string.IsNullOrWhiteSpace(settings.ActiveSummaryModel)
            ? UiText.Get(settings.UsesLocalSummaries ? "LocalModelNotSelected" : settings.UsesCloudApi ? "ChooseCloudModel" : "CliDefaultModel")
            : settings.ActiveSummaryModel;
        SpeechModelStatusText.Text = local;
        ModelStatusText.Text = settings.SummarySetupDeferred &&
            (!settings.CanSummarize || string.IsNullOrWhiteSpace(settings.ActiveSummaryModel))
            ? UiText.Get("SetupLlmDeferredStatus") : "LLM: " + provider + " / " + model;
        ModelStatusText.ToolTip = ModelStatusText.Text;
        if (settings.UsesCloudApi)
            ModelStatusText.ToolTip += Environment.NewLine + settings.ActiveSummaryEndpoint;
        var attribution = UiText.Get("MainPointsNotGenerated");
        if (selected is not null && AppStorage.LoadSummaryDocument(selected) is { } summary &&
            !string.IsNullOrWhiteSpace(summary.Text))
        {
            attribution = UiText.Format("SummaryGeneratedWith",
                SummaryProviders.Label(summary.Provider, UiText.Current),
                string.IsNullOrWhiteSpace(summary.Model) ? UiText.Get("SummaryModelUnrecorded") : summary.Model);
            if (AppStorage.SummaryNeedsRefresh(selected, currentTranscript))
                attribution += Environment.NewLine + UiText.Get("SummaryNeedsRefresh");
        }
        else if (!string.IsNullOrWhiteSpace(summaryContent))
            attribution = UiText.Get("SummaryModelUnrecorded");
        var selectedModel = UiText.Format("SelectedLlmModel", provider, model);
        if (settings.SummarySetupDeferred && (!settings.CanSummarize || string.IsNullOrWhiteSpace(settings.ActiveSummaryModel)))
            selectedModel += Environment.NewLine + UiText.Get("SetupLlmDeferredStatus");
        MainPointsTitle.ToolTip = attribution + Environment.NewLine + selectedModel;
        UpdatePersonalNotesModelStatus(selectedModel);
        var display = string.IsNullOrWhiteSpace(summaryContent) ? "" : SummaryPresentation.ForDisplay(summaryContent);
        if (SummaryBox.Text != display)
            SummaryBox.Text = display;
        UpdateSpeechStatus();
        UpdateStatusInformation();
    }

    private void UpdateSpeechStatus()
    {
        var speech = (currentTranscript.Segments.Count == 0 ? "" :
            UiText.Get(currentTranscript.IsDraft ? "DraftLabel" : "FinalLabel") + " / ") +
            (speechRuntime.Length > 0 ? speechRuntime : currentTranscript.Model);
        TranscriptTitle.ToolTip = UiText.Format("TranscriptModelHint",
            speech.Length == 0 ? SpeechModelStatusText.Text : speech, ModelStatusText.Text);
    }

    private void SetMainModelChoices()
    {
        var provider = (string?)MainProviderBox.SelectedValue;
        if (CloudLlmCatalog.IsProvider(provider))
        {
            MainModelBox.ItemsSource = CloudLlmCatalog.Get(provider!).Models;
            MainModelBox.Text = settings.GetCloudProfile(provider).Model;
            return;
        }
        var local = (string?)MainProviderBox.SelectedValue == SummaryProviders.Ollama;
        MainModelBox.ItemsSource = local ? Array.Empty<string>() : new[] { UiText.Get("InheritModel") };
        MainModelBox.Text = local ? settings.OllamaModel :
            string.IsNullOrWhiteSpace(settings.SummaryModel) ? UiText.Get("InheritModel") : settings.SummaryModel;
    }

    private async void MainProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || loadingModels || selectingModel)
            return;
        var candidate = SummaryConfigurationService.Copy(settings);
        candidate.SummaryProvider = (string?)MainProviderBox.SelectedValue ?? settings.SummaryProvider;
        if (candidate.SummaryProvider == settings.SummaryProvider)
            return;
        modelSelectionTask = ChangeSummarySelectionAsync(candidate);
        await modelSelectionTask;
    }

    private Task RefreshMainModelsAsync()
    {
        if (refreshingModels)
            return modelRefreshTask ?? Task.CompletedTask;
        return modelRefreshTask = ReadMainModelsAsync();
    }

    private async Task ReadMainModelsAsync()
    {
        refreshingModels = true;
        UpdateControls();
        try
        {
            var choice = MainModelBox.Text;
            var provider = (string?)MainProviderBox.SelectedValue;
            if (CloudLlmCatalog.IsProvider(provider))
            {
                var definition = CloudLlmCatalog.Get(provider!);
                if (definition.SupportsModelListing)
                {
                    var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
                    snapshot.SummaryProvider = definition.Id;
                    var models = await new CloudLlmService().DiscoverModelsAsync(snapshot, lifetime.Token);
                    MainModelBox.ItemsSource = models;
                    SetStatus(models.Count == 0 ? UiText.Get("CloudNoModels") : UiText.Format("CloudModelsFound", models.Count),
                        models.Count == 0);
                }
                else
                {
                    MainModelBox.ItemsSource = definition.Models;
                    SetStatus(UiText.Get("CloudCatalogModelsOnly"));
                }
            }
            else if (provider == SummaryProviders.Ollama)
            {
                var models = await new OllamaService().DiscoverModelsAsync(settings.OllamaEndpoint, lifetime.Token);
                MainModelBox.ItemsSource = models.Where(model => !model.IsRemote && model.SizeBytes > 0).Select(model => model.Name).ToArray();
                if (MainModelBox.Items.Count == 0)
                    SetStatus(UiText.Get("NoLocalModels"), true);
            }
            else
            {
                var models = await new CopilotService().DiscoverModelsAsync(settings.CopilotExecutable, lifetime.Token);
                MainModelBox.ItemsSource = models.Prepend(UiText.Get("InheritModel")).Distinct().ToArray();
            }
            MainModelBox.Text = choice;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            refreshingModels = false;
            UpdateControls();
        }
    }

    private async void RefreshMainModels_Click(object sender, RoutedEventArgs e) => await RefreshMainModelsAsync();

    private async void ApplyModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!CanCommitModel || selectingModel)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            var next = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
            next.SummaryProvider = (string?)MainProviderBox.SelectedValue
                ?? throw new InvalidOperationException(UiText.Get("InvalidSummaryProvider"));
            if (next.UsesLocalSummaries)
                next.OllamaModel = MainModelBox.Text.Trim();
            else if (next.UsesCloudApi)
                next.GetCloudProfile().Model = MainModelBox.Text.Trim();
            else
                next.SummaryModel = MainModelBox.Text == UiText.Get("InheritModel") ? "" : MainModelBox.Text.Trim();
            modelSelectionTask = ChangeSummarySelectionAsync(next);
            await modelSelectionTask;
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ToggleHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (CompactHistory)
            {
                if (historyPeek.IsOpen)
                    CloseHistoryPreview();
                else
                    RevealHistory(sender, e);
                return;
            }
            var previous = settings.HistoryVisible;
            settings.HistoryVisible = !previous;
            try { storage.SaveSettings(settings); }
            catch { settings.HistoryVisible = previous; throw; }
            UpdateHistoryVisibility();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateHistoryVisibility()
    {
        var pinned = HistoryPinned;
        if (pinned)
            historyPeek.Close();
        var preview = !pinned && historyPeek.IsOpen;
        HistoryPanel.Visibility = pinned || preview ? Visibility.Visible : Visibility.Collapsed;
        HistoryColumn.Width = new GridLength(pinned ? VisibleHistoryWidth : 0);
        HistoryPeekTab.Visibility = pinned ? Visibility.Collapsed : Visibility.Visible;
        NewButton.Margin = new Thickness(pinned ? 0 : 30, 0, 10, 0);
        PinHistoryButton.Visibility = Visibility.Visible;
        PinHistoryButton.Content = pinned || CompactHistory ? "<" : UiText.Get("PinHistory");
        PinHistoryButton.ToolTip = UiText.Get(pinned || CompactHistory ? "HideHistory" : "PinHistoryHint");
        System.Windows.Automation.AutomationProperties.SetName(PinHistoryButton,
            UiText.Get(pinned || CompactHistory ? "HideHistory" : "PinHistory"));
        Grid.SetColumnSpan(HistoryPanel, pinned ? 1 : 2);
        HistoryPanel.Width = pinned ? double.NaN : VisibleHistoryWidth;
        HistoryPanel.HorizontalAlignment = pinned ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        HistoryPanel.Margin = pinned ? new Thickness(0) : new Thickness(30, 0, 0, 0);
        if (preview)
            HistoryPanel.Effect ??= new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.2 };
        else
            HistoryPanel.Effect = null;
        UpdateHistoryTabPosition();
        UpdateApplicationMenu();
    }

    private bool PointerInHistory => HistoryPanel.IsMouseOver || HistoryPeekTab.IsMouseOver ||
        HistoryPanel.IsMouseCaptureWithin || HistoryPeekTab.IsMouseCaptureWithin ||
        (HistoryList.ContextMenu is { IsOpen: true, IsMouseOver: true });

    private void RevealHistory(object sender, RoutedEventArgs e)
    {
        if (HistoryPinned || historyTabDragging)
            return;
        historyPeek.Reveal(historyPeekClock.Elapsed);
        UpdateHistoryVisibility();
        historyPeek.UpdatePointer(PointerInHistory, historyPeekClock.Elapsed);
    }

    private void History_PointerChanged(object sender, MouseEventArgs e) =>
        historyPeek.UpdatePointer(PointerInHistory, historyPeekClock.Elapsed);

    private void History_Activity(object sender, RoutedEventArgs e) => historyPeek.Interact(historyPeekClock.Elapsed);

    private void UpdateHistoryPeek()
    {
        if (HistoryPinned || !historyPeek.IsOpen)
            return;
        var now = historyPeekClock.Elapsed;
        historyPeek.UpdatePointer(PointerInHistory, now);
        if (!historyPeek.Expire(now))
            return;
        CloseHistoryPreview(restoreFocus: true);
    }

    private void CloseHistoryPreview(bool restoreFocus = false)
    {
        if (HistoryPinned)
            return;
        historyPeek.Close();
        if (HistoryList.ContextMenu is { } menu)
            menu.IsOpen = false;
        if (restoreFocus && IsActive && HistoryPanel.IsKeyboardFocusWithin)
            HistoryPeekTab.Focus();
        UpdateHistoryVisibility();
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        AcknowledgeProcessing();
        if (!historyPeek.IsOpen || HistoryPinned)
            return;
        var source = e.OriginalSource as DependencyObject;
        if (IsWithin(source, HistoryPanel) || IsWithin(source, HistoryPeekTab) ||
            IsWithin(source, HistoryList.ContextMenu))
            return;
        CloseHistoryPreview();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        FinishHistoryTabDrag(commit: false);
        if (HistoryResizeGrip.IsDragging)
            HistoryResizeGrip.CancelDrag();
        if (historyPeek.IsOpen)
            CloseHistoryPreview();
    }

    private void HistoryMenu_Closed(object sender, RoutedEventArgs e)
    {
        if (historyPeek.IsOpen && !HistoryPanel.IsMouseOver && !HistoryPeekTab.IsMouseOver)
            CloseHistoryPreview();
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // Title-bar and window-button clicks bypass WPF's mouse-event route.
        if (message is 0x00A1 or 0x00A4 or 0x00A7 or 0x00AB)
        {
            AcknowledgeProcessing();
            if (historyPeek.IsOpen)
                CloseHistoryPreview();
        }
        return 0;
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject? container)
    {
        if (container is null)
            return false;
        while (source is not null)
        {
            if (ReferenceEquals(source, container))
                return true;
            source = source switch
            {
                FrameworkContentElement content => content.Parent,
                ContentElement content => ContentOperations.GetParent(content),
                Visual visual => VisualTreeHelper.GetParent(visual) ?? LogicalTreeHelper.GetParent(visual),
                _ => LogicalTreeHelper.GetParent(source)
            };
        }
        return false;
    }

    private void ReplayRange(PlaybackRange range)
    {
        try
        {
            if (RecordingActive || recordingCommand || exportBusy || closing || selected is null)
                throw new InvalidOperationException(UiText.Get("ReplayAfterStop"));
            EnsureRecovered(selected);
            replay.Play(selected, range);
            UpdateReplayDisplay();
            SetStatus(UiText.Format("ReplayBounds", TranscriptDocument.Timestamp(range.StartSeconds),
                TranscriptDocument.Timestamp(range.EndSeconds)));
        }
        catch (Exception ex) { UpdateReplayDisplay(); ShowError(ex); }
    }

    private void EditSelectedRows(IReadOnlyList<TranscriptRow> rows)
    {
        var ownsBusyState = false;
        try
        {
            var session = RequireIdleSession();
            EnsureRecovered(session);
            SaveNotes();
            StopReplay();
            var transcript = AppStorage.LoadTranscript(session);
            var plan = AudioEditPlan.Create(transcript, session.Frames, rows);
            exportBusy = true;
            ownsBusyState = true;
            UpdateControls();
            var dialog = new EditSessionWindow(session, plan, settings.SaveFolder) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.ResultSession is not { } edited)
                return;
            if (HistorySelectionPath(edited).Equals(HistorySelectionPath(session), StringComparison.OrdinalIgnoreCase))
                ForgetChangedRecordingWork(edited);
            selected = edited;
            pendingTitle = "";
            LoadSessionContent(edited);
            RefreshHistory();
            UpdateRecordingDisplay();
            SetStatus(UiText.Format(edited.Id == session.Id ? "AudioEditUpdated" : "EditedCopyCreated", edited.Title));
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (ownsBusyState)
                exportBusy = false;
            UpdateControls();
        }
    }

    private async void RestoreSilence_Click(object sender, RoutedEventArgs e)
    {
        var ownsBusyState = false;
        try
        {
            var session = RequireIdleSession();
            EnsureRecovered(session);
            SaveNotes();
            StopReplay();
            exportBusy = true;
            ownsBusyState = true;
            UpdateControls();
            SetStatus(UiText.Get("LocatingRestoreSource"));
            var recovery = await new SilenceRecoveryService().PrepareAsync(settings.SaveFolder, session, lifetime.Token);
            var dialog = new EditSessionWindow(recovery, settings.SaveFolder) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.ResultSession is not { } restored)
                return;
            ForgetChangedRecordingWork(restored);
            selected = restored;
            pendingTitle = "";
            LoadSessionContent(restored);
            RefreshHistory();
            UpdateRecordingDisplay();
            SetStatus(UiText.Format("AudioEditUpdated", restored.Title));
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            if (ownsBusyState)
                exportBusy = false;
            UpdateControls();
        }
    }

    private void UpdateReplayDisplay() => TranscriptPanel.SetPlayback(replay.Snapshot);

    private void StopReplay()
    {
        var wasActive = replay.Snapshot is not null;
        replay.Stop();
        UpdateReplayDisplay();
        if (wasActive)
            SetStatus(UiText.Get("ReplayStopped"));
    }

    private RecordingSession RequireIdleSession()
    {
        if (RecordingActive || AiBusy || recordingCommand || exportBusy || selectingModel || closing)
            throw new InvalidOperationException(UiText.Get("SessionBusy"));
        if (MultipleHistorySelection)
            throw new InvalidOperationException(UiText.Get("SelectOneRecording"));
        return selected ?? throw new InvalidOperationException(UiText.Get("SelectSession"));
    }

    private void History_RightClick(object sender, MouseButtonEventArgs e)
    {
        var container = e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(HistoryList, source) as ListBoxItem : null;
        contextSession = container?.DataContext as RecordingSession;
        if (HistoryList.IsEnabled && contextSession is not null && !HistoryList.SelectedItems.Contains(contextSession))
            HistoryList.SelectedItem = contextSession;
    }

    private void History_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        UpdateControls();
        if (HistoryList.SelectedItems.Count == 0 || closing)
        {
            e.Handled = true;
            return;
        }
        if (e.CursorLeft < 0)
            return;
        var target = contextSession;
        if (!HistoryList.IsEnabled)
        {
            var hit = VisualTreeHelper.HitTest(HistoryList, Mouse.GetPosition(HistoryList));
            target = hit is null ? null :
                (ItemsControl.ContainerFromElement(HistoryList, hit.VisualHit) as ListBoxItem)?.DataContext as RecordingSession;
        }
        if (target is null || !SelectedHistorySessions().Any(session =>
            HistorySelectionPath(session).Equals(HistorySelectionPath(target), StringComparison.OrdinalIgnoreCase)))
            e.Handled = true;
    }

    private void RenameSession_Click(object sender, RoutedEventArgs e)
    {
        RenameHistorySelection((session, index, count) =>
        {
            var caption = count == 1 ? UiText.Get("RenameSession") : UiText.Format("RenameRecordingProgress", index, count);
            var dialog = new TextInputWindow(this, caption, session.Title, 160);
            if (dialog.ShowDialog() != true)
                return null;
            return dialog.Value;
        });
    }

    private async void DeleteSession_Click(object sender, RoutedEventArgs e) => await RecycleSelectedHistoryAsync();

    private void CopySessionText_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = RequireIdleSession();
            var contents = TranscriptPresentation.CopyText(TranscriptPresentation.Rows(AppStorage.LoadTranscript(session)));
            if (contents.Length == 0)
                throw new InvalidOperationException(UiText.Get("NoTranscript"));
            Clipboard.SetText(contents);
            SetStatus(UiText.Get("TranscriptCopied"));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ExportTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (MultipleHistorySelection)
        {
            await ChooseHistoryExportAsync(HistoryExportKind.Transcript);
            return;
        }
        try
        {
            var session = RequireIdleSession();
            var transcript = AppStorage.LoadTranscript(session);
            if (transcript.Segments.Count == 0)
                throw new InvalidOperationException(UiText.Get("NoTranscript"));
            var dialog = new SaveFileDialog
            {
                FileName = HistoryExportService.FileNameStem(session.Title) + ".txt",
                Filter = UiText.Get("TranscriptExportFilter"),
                AddExtension = true,
                OverwritePrompt = true
            };
            if (dialog.ShowDialog(this) != true)
                return;
            var destination = Path.GetFullPath(dialog.FileName);
            var protectedPaths = new[] { session.WavePath, session.Mp3Path, session.MetadataPath, session.TranscriptPath,
                session.SummaryPath, session.NotesPath, Path.Combine(session.Folder, "summary.json"),
                Path.Combine(session.Folder, "transcript.refining.json"), PendingProcessingStore.PathFor(session),
                PersonalSummaryStore.PathFor(session) };
            if (protectedPaths.Any(path => string.Equals(Path.GetFullPath(path), destination, StringComparison.OrdinalIgnoreCase)))
                throw new IOException(UiText.Get("ProtectedSessionFile"));
            AtomicFile.WriteText(destination, TranscriptPresentation.CopyText(TranscriptPresentation.Rows(transcript), dialog.FilterIndex == 2));
            SetStatus(UiText.Format("TranscriptExported", destination));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void IdentifySpeakers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = RequireIdleSession();
            EnsureRecovered(session);
            var transcript = AppStorage.LoadTranscript(session);
            if (transcript.IsDraft || transcript.Segments.Count == 0 || transcript.ProcessedFrames != session.Frames)
                throw new InvalidOperationException(UiText.Get("FinalTranscriptRequired"));
            if (transcript.SpeakersIdentified && MessageDialog.ShowMessage(this, UiText.Get("ConfirmSpeakerReanalysis"),
                UiText.Get("IdentifySpeakers"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            StartAiJob(session, "IdentifySpeakers", (snapshot, progress, cancellationToken) =>
                ai.IdentifySpeakersAsync(session, snapshot, progress, cancellationToken));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RecheckUnlabeledSpeakers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = RequireIdleSession();
            EnsureRecovered(session);
            StopReplay();
            StartAiJob(session, "RecheckUnlabeledSpeakers", (snapshot, progress, cancellationToken) =>
                ai.RecheckUnlabeledSpeakersAsync(session, snapshot, progress, cancellationToken));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RenameSpeaker(string id)
    {
        try
        {
            var session = RequireIdleSession();
            var document = AppStorage.LoadTranscript(session);
            var dialog = new TextInputWindow(this, UiText.Get("RenameSpeaker"), document.SpeakerName(id), 80);
            if (dialog.ShowDialog() != true)
                return;
            AppStorage.RenameSpeaker(session, id, dialog.Value);
            currentTranscript = AppStorage.LoadTranscript(session);
            TranscriptPanel.SetDocument(currentTranscript);
            summaryContent = AppStorage.LoadSummaryText(session);
            UpdateModelStatus();
            SetStatus(UiText.Get("SpeakerRenamed"));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CorrectSpeaker(IReadOnlyList<TranscriptRow> rows)
    {
        try
        {
            var session = RequireIdleSession();
            var document = AppStorage.LoadTranscript(session);
            var selection = TranscriptCorrectionPlan.Create(document, session.Frames, rows);
            var dialog = new SpeakerCorrectionWindow(this, document, selection);
            if (dialog.ShowDialog() != true || dialog.TargetSpeakerId is not { } target)
                return;
            currentTranscript = TranscriptCorrectionService.SaveSpeakerAssignment(session, selection, target, dialog.MergeAll, CancellationToken.None);
            TranscriptPanel.SetDocument(currentTranscript);
            UpdateModelStatus();
            SetStatus(UiText.Get("SpeakerAssignmentSaved"));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private bool CanSetSpeakerColors => selected is not null && !MultipleHistorySelection &&
        currentTranscript.Segments.Count > 0 && !recordingCommand && !exportBusy && !closing;

    private void SetSpeakerColor(string? id)
    {
        try
        {
            if (!CanSetSpeakerColors || selected is not { } session)
                throw new InvalidOperationException(UiText.Get("SpeakerColorUnavailable"));
            var document = AppStorage.LoadTranscript(session);
            if (!document.Segments.Any(segment => segment.SpeakerId == id) ||
                id is not null && document.Revision != currentTranscript.Revision)
                throw new InvalidOperationException(UiText.Get("SpeakerColorTargetChanged"));
            var current = document.SpeakerColor(id) ??
                AppAppearance.Rgb(((SolidColorBrush)FindResource("SolidBackgroundFillColorQuarternaryBrush")).Color).ToString();
            var name = id is null ? UiText.Get("UnlabeledSpeakers") : document.SpeakerName(id);
            var dialog = new ColorPickerWindow(this, UiText.Format("SpeakerColorTitle", name), current, allowNeutral: true);
            if (dialog.ShowDialog() != true)
                return;
            ApplySpeakerColor(session, id, dialog.SelectedColor, document.Revision);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ApplySpeakerColor(RecordingSession session, string? id, string? color, string revision)
    {
        if (!CanSetSpeakerColors || selected?.Id != session.Id)
            throw new InvalidOperationException(UiText.Get("SpeakerColorUnavailable"));
        AppStorage.SetSpeakerColor(session, id, color, revision);
        currentTranscript = AppStorage.LoadTranscript(session);
        TranscriptPanel.SetDocument(currentTranscript);
        SetStatus(UiText.Get(id is null ? "UnlabeledSpeakerColorSaved" : "SpeakerColorSaved"));
    }

    private void SetStatus(string text, bool warning = false, [CallerMemberName] string operation = "")
    {
        StatusText.Text = warning ? "" : text;
        StatusText.ToolTip = warning ? null : text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        if (warning)
        {
            warningVersion++;
            warningOperation = null;
            WarningStampText.Text = UiText.Format("WarningRecordedAt", DateTime.Now);
            WarningText.Text = text;
            WarningText.ToolTip = text;
            WarningBanner.Visibility = Visibility.Visible;
        }
        MessageJournal.Record(this, text, MessageReason(operation),
            warning ? text.StartsWith(UiText.Format("ErrorDetails", ""), StringComparison.CurrentCulture)
                ? MessageSeverity.Error : MessageSeverity.Warning : MessageSeverity.Information);
    }

    private void ShowError(Exception ex, [CallerMemberName] string operation = "") =>
        SetStatus(UiText.Format("ErrorDetails", ex.Message), true, operation);
    private void ShowOperationError(Exception ex, string operationKey, string? sessionId)
    {
        SetStatus(UiText.Format("OperationErrorDetails", UiText.Get(operationKey), ex.Message), true, operationKey);
        warningOperation = sessionId is null ? null : (sessionId, operationKey);
    }

    private void DismissWarning_Click(object sender, RoutedEventArgs e)
    {
        WarningBanner.Visibility = Visibility.Collapsed;
        warningOperation = null;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        FinishHistoryTabDrag(commit: false);
        if (HistoryResizeGrip.IsDragging)
            HistoryResizeGrip.CancelDrag();
        if (closeReady)
            return;
        e.Cancel = true;
        if (closing)
            return;
        try
        {
            if (await PrepareCloseAsync())
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                Close();
            }
        }
        catch (Exception ex)
        {
            closing = false;
            ShowError(ex);
            UpdateControls();
        }
    }
}
