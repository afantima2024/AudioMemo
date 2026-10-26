using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class TranscriptEditWindow
{
    private readonly SelectedOnlineTranscriptionService onlineRecognizer;
    private readonly Action<OnlineSpeechSettings>? saveOnlineSettings;
    private bool updatingOnline = true;
    private bool comparingOnline;
    private string? shownEndpoint;
    private OnlineComparison? onlineComparison;

    private void InitializeOnline(bool show)
    {
        OnlineProviderBox.ItemsSource = OnlineSpeechCatalog.Providers(UiText.Current);
        OnlineProviderBox.SelectedValue = settings.OnlineSpeech.Provider;
        ShowOnlineProfile();
        if (show)
        {
            OnlineOptionsPanel.Visibility = Visibility.Visible;
            OnlineOptionsPanel.IsExpanded = true;
        }
        try
        {
            onlineComparison = OnlineComparisonStore.Load(session, plan);
            if (onlineComparison is not null)
            {
                ShowOnlineResult(onlineComparison);
                CorrectionStatus.Text = UiText.Get("OnlineComparisonLoaded");
            }
        }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void ShowOnlineProfile()
    {
        updatingOnline = true;
        try
        {
            var selection = settings.OnlineSpeech;
            selection.Provider = (string)OnlineProviderBox.SelectedValue;
            var profile = selection.GetProfile();
            OnlineModelBox.ItemsSource = OnlineSpeechCatalog.Models.Where(model => model.Provider == selection.Provider).ToArray();
            OnlineModelBox.SelectedValue = profile.Model;
            OnlineEndpointBox.ItemsSource = OnlineSpeechCatalog.Endpoints(selection.Provider);
            OnlineEndpointBox.Text = profile.Endpoint;
            OnlineKeyBox.Clear();
            shownEndpoint = profile.Endpoint;
        }
        finally { updatingOnline = false; }
        RefreshOnlineSelection();
    }

    private void OnlineProvider_Changed(object sender, RoutedEventArgs e)
    {
        if (!updatingOnline && OnlineProviderBox.SelectedValue is string)
            ShowOnlineProfile();
    }

    private void OnlineEndpoint_Changed(object sender, RoutedEventArgs e)
    {
        if (updatingOnline)
            return;
        if (shownEndpoint != OnlineEndpointBox.Text)
        {
            OnlineKeyBox.Clear();
            shownEndpoint = OnlineEndpointBox.Text;
        }
        RefreshOnlineSelection();
    }

    private void OnlineConfig_Changed(object sender, RoutedEventArgs e)
    {
        if (!updatingOnline)
            RefreshOnlineSelection();
    }

    private AppSettings ReadOnlineSettings(bool captureKey)
    {
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        snapshot.OnlineSpeech.Provider = (string?)OnlineProviderBox.SelectedValue
            ?? throw new InvalidOperationException(UiText.Get("InvalidOnlineSpeechModel"));
        var profile = snapshot.OnlineSpeech.GetProfile();
        profile.Endpoint = OnlineSpeechCatalog.Endpoint(snapshot.OnlineSpeech.Provider, OnlineEndpointBox.Text).AbsoluteUri;
        profile.Model = (string?)OnlineModelBox.SelectedValue
            ?? throw new InvalidOperationException(UiText.Get("InvalidOnlineSpeechModel"));
        snapshot.Language = (string?)CorrectionLanguageBox.SelectedValue
            ?? throw new InvalidOperationException(UiText.Get("InvalidSpeechLanguage"));
        if (captureKey && OnlineKeyBox.Password.Length > 0)
            CloudApiKey.SetOnline(snapshot.OnlineSpeech, OnlineKeyBox.Password.Trim());
        snapshot.Validate();
        return snapshot;
    }

    private void RefreshOnlineSelection()
    {
        OnlineConsentCheck.IsChecked = false;
        try
        {
            var snapshot = ReadOnlineSettings(false);
            var profile = snapshot.OnlineSpeech.GetProfile();
            var requests = CreateOnlinePlan(snapshot);
            var model = requests.Model;
            var duration = model.MaximumSeconds is { } seconds ? UiText.Format("OnlineDurationLimit", seconds) : UiText.Get("OnlineNoDurationLimit");
            OnlineLimitsText.Text = UiText.Format("OnlineSelectionLimits", model.Id,
                (double)requests.SelectedFrames / PcmFormat.SampleRate, requests.EstimatedBytes / 1_000_000d,
                duration, model.MaximumInputBytes / 1_000_000d, requests.Chunks.Count) +
                Environment.NewLine + UiText.Get(model.Base64 ? "OnlineBase64LimitHint" : "OnlineFileLimitHint") +
                (requests.Allowed ? "" : Environment.NewLine + UiText.Get("OnlineSelectionTooLarge"));
            OnlineConsentText.Text = UiText.Format("OnlineAudioConsent", model.Id, profile.Endpoint,
                requests.Chunks.Count, (double)requests.SelectedFrames / PcmFormat.SampleRate);
            var configured = profile.ProtectedApiKeys.ContainsKey(profile.Endpoint) ||
                snapshot.OnlineSpeech.Provider == OnlineSpeechCatalog.Qwen &&
                snapshot.CloudModels.TryGetValue(OnlineSpeechCatalog.Qwen, out var summary) &&
                summary.ProtectedApiKeys.ContainsKey(profile.Endpoint);
            OnlineKeyStatus.Text = UiText.Get(configured ? "OnlineKeyAvailable" : "OnlineSpeechKeyRequired");
        }
        catch (Exception ex)
        {
            OnlineLimitsText.Text = UiText.Format("ErrorDetails", ex.Message);
            OnlineConsentText.Text = UiText.Get("InvalidOnlineSpeechEndpoint");
        }
    }

    private OnlineTranscriptionPlan CreateOnlinePlan(AppSettings snapshot)
    {
        if (new FileInfo(session.WavePath).Length != DurableWaveFile.HeaderSize + plan.SourceFrames * PcmFormat.BlockAlign)
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        return new(plan, OnlineSpeechCatalog.Get(snapshot.OnlineSpeech.Provider, snapshot.OnlineSpeech.GetProfile().Model),
            SplitOnlineCheck.IsChecked == true);
    }

    private void OnlineOptions_Click(object sender, RoutedEventArgs e)
    {
        OnlineOptionsPanel.Visibility = Visibility.Visible;
        OnlineOptionsPanel.IsExpanded = !OnlineOptionsPanel.IsExpanded;
    }

    private void SaveOnlineConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = ReadOnlineSettings(true);
            if (saveOnlineSettings is null)
                throw new InvalidOperationException(UiText.Get("OnlineSettingsUnavailable"));
            saveOnlineSettings(snapshot.OnlineSpeech);
            settings.OnlineSpeech = snapshot.OnlineSpeech;
            OnlineKeyBox.Clear();
            RefreshOnlineSelection();
            CorrectionStatus.Text = UiText.Get("OnlineSettingsSaved");
        }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private async void UploadOnline_Click(object sender, RoutedEventArgs e) => await TranscribeOnlineAsync();

    private async Task TranscribeOnlineAsync()
    {
        if (operation is not null)
            return;
        using var cancellation = new CancellationTokenSource();
        try
        {
            CommitCell();
            var snapshot = ReadOnlineSettings(true);
            if (OnlineConsentCheck.IsChecked != true)
                throw new InvalidOperationException(UiText.Get("OnlineAudioConsentRequired"));
            var profile = snapshot.OnlineSpeech.GetProfile();
            var requestPlan = CreateOnlinePlan(snapshot);
            requestPlan.EnsureAllowed();
            operation = cancellation;
            StartRecognitionTiming();
            UpdateControls();
            CorrectionStatus.Text = UiText.Get("OnlineTranscribing");
            var progress = new Progress<SelectedTranscriptionProgress>(update =>
            {
                if (operation != cancellation)
                    return;
                RecognitionProgress.Value = 100d * update.Completed / update.Total;
                recognitionTiming.Report("online-transcription", (double)update.Completed / update.Total);
                UpdateRecognitionTiming();
                CorrectionStatus.Text = UiText.Format("OnlineTranscriptionProgress", update.Completed, update.Total, update.Model);
            });
            var result = await onlineRecognizer.GenerateAsync(session, plan, snapshot, SplitOnlineCheck.IsChecked == true,
                profile.Endpoint, progress, cancellation.Token);
            audioVersion = result.AudioVersion;
            onlineComparison = new(session.Id, plan.SourceFrames, result.AudioVersion, snapshot.OnlineSpeech.Provider,
                profile.Model, profile.Endpoint, DateTimeOffset.Now, rows.Select((row, index) => new OnlineComparisonRow(
                    plan.Indices[index], AudioEditPlan.FrameAt(plan.Segments[index].StartSeconds, plan.SourceFrames),
                    AudioEditPlan.FrameAt(plan.Segments[index].EndSeconds, plan.SourceFrames), row.LocalText, result.Texts[index])).ToList());
            ShowOnlineResult(onlineComparison);
            await Task.Run(() => OnlineComparisonStore.Save(session, plan, onlineComparison, cancellation.Token), cancellation.Token);
            CorrectionStatus.Text = UiText.Format("OnlineComparisonReady", rows.Count(row => row.OnlineDifferent), result.Unrecognized.Count);
        }
        catch (OperationCanceledException) { CorrectionStatus.Text = UiText.Get("SelectionRecognitionCancelled"); }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
        finally
        {
            StopRecognitionTiming();
            operation = null;
            OnlineConsentCheck.IsChecked = false;
            UpdateControls();
            if (closeRequested)
                Close();
        }
    }

    private void ShowOnlineResult(OnlineComparison comparison)
    {
        audioVersion = comparison.AudioVersion;
        for (var index = 0; index < rows.Count; index++)
        {
            // Older caches can contain an unsaved local preview and its missing-result flag.
            rows[index].OnlineText = comparison.Rows[index].OnlineText;
        }
        OnlineResultCaption.Text = UiText.Format("OnlineResultSource", comparison.Model, comparison.CreatedAt.LocalDateTime,
            new Uri(comparison.Endpoint).Host);
        CorrectionRows.Items.Refresh();
    }

    private void CompareResults_Click(object sender, RoutedEventArgs e)
    {
        try { ToggleComparison(); }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void ToggleComparison()
    {
        CommitCell();
        comparingOnline = !comparingOnline;
        if (comparingOnline)
            OnlineOptionsPanel.IsExpanded = false;
        foreach (var row in rows)
            row.ComparingOnline = comparingOnline;
        OnlineTextColumn.Visibility = comparingOnline ? Visibility.Visible : Visibility.Collapsed;
        CorrectedTextColumn.Visibility = comparingOnline ? Visibility.Collapsed : Visibility.Visible;
        UpdateComparisonActions();
        if (!comparingOnline)
            OnlyDifferencesCheck.IsChecked = false;
        FilterComparison();
    }

    private void OnlyDifferences_Click(object sender, RoutedEventArgs e)
    {
        try { FilterComparison(); }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void FilterComparison()
    {
        CommitCell();
        CollectionViewSource.GetDefaultView(rows).Filter = OnlyDifferencesCheck.IsChecked == true
            ? value => ((CorrectionRow)value).OnlineDifferent : null;
    }

    private void UseOnlineResults_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CommitCell();
            if (onlineComparison is null || onlineComparison.AudioVersion != AudioSourceVersion.Read(session.WavePath))
                throw new InvalidOperationException(UiText.Get("OnlineComparisonStale"));
            TranscriptCorrectionService.VerifySource(session, plan);
            foreach (var row in rows.Where(row => !string.IsNullOrWhiteSpace(row.OnlineText)))
                row.SetCorrection(row.OnlineText!);
            CorrectionRows.Items.Refresh();
            if (comparingOnline)
                ToggleComparison();
            CorrectionStatus.Text = UiText.Get("OnlineResultsNotSaved");
        }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void ReduceOnlineSelection_Click(object sender, RoutedEventArgs e)
    {
        if (operation is not null)
            return;
        try
        {
            CommitCell();
            if (rows.Where((row, index) => row.Text != plan.Segments[index].Text).Any() &&
                MessageDialog.ShowMessage(this, UiText.Get("ConfirmReturnToSelection"), UiText.Get("ReduceOnlineSelection"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            Close();
        }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void OnlineDocs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var snapshot = ReadOnlineSettings(false);
            var url = OnlineSpeechCatalog.Get(snapshot.OnlineSpeech.Provider, snapshot.OnlineSpeech.GetProfile().Model).Documentation;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { CorrectionStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void UpdateOnlineControls(bool ready)
    {
        OnlineOptionsButton.IsEnabled = ready;
        OnlineProviderBox.IsEnabled = OnlineModelBox.IsEnabled = OnlineEndpointBox.IsEnabled = OnlineKeyBox.IsEnabled = ready;
        SplitOnlineCheck.IsEnabled = OnlineConsentCheck.IsEnabled = SaveOnlineConnectionButton.IsEnabled = ready;
        UploadOnlineButton.IsEnabled = ready;
        CompareResultsButton.IsEnabled = UseOnlineResultsButton.IsEnabled = ready && onlineComparison is not null;
        OnlyDifferencesCheck.IsEnabled = ready;
        UpdateComparisonActions();
    }

    private void UpdateComparisonActions()
    {
        ComparisonActions.Visibility = comparingOnline || rows.Any(row => row.HasCorrection) ? Visibility.Visible : Visibility.Collapsed;
        OnlyDifferencesCheck.Visibility = UseOnlineResultsButton.Visibility = OnlineResultCaption.Visibility =
            comparingOnline ? Visibility.Visible : Visibility.Collapsed;
    }
}
