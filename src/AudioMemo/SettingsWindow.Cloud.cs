using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class SettingsWindow
{
    private readonly CloudLlmService cloud = new();
    private CancellationTokenSource? cloudOperation;
    private string? shownCloudProvider;
    private string? credentialEndpoint;
    private bool updatingCloudFields;

    private void ShowCloudFields(string? provider)
    {
        updatingCloudFields = true;
        try
        {
            shownCloudProvider = provider;
            credentialEndpoint = null;
            CloudApiKeyBox.Clear();
            ForgetCloudKeyCheck.IsChecked = false;
            CloudApiConsentCheck.IsChecked = false;
            if (provider is null)
                return;
            var definition = CloudLlmCatalog.Get(provider);
            var profile = Settings.GetCloudProfile(provider);
            CloudEndpointBox.ItemsSource = definition.Endpoints;
            CloudEndpointBox.Text = profile.Endpoint;
            CloudModelBox.ItemsSource = definition.Models;
            CloudModelBox.Text = profile.Model;
            DisableCloudThinkingCheck.IsChecked = profile.DisableThinking;
            CloudModelHint.Text = UiText.Get(provider == CloudLlmCatalog.Doubao ? "DoubaoModelHint" : "CloudPresetHint");
            QwenEndpointHint.Visibility = provider == CloudLlmCatalog.Qwen ? Visibility.Visible : Visibility.Collapsed;
            ReadCloudModelsButton.Visibility = definition.SupportsModelListing ? Visibility.Visible : Visibility.Collapsed;
            CloudModelStatus.Text = UiText.Get("CloudNoAutomaticRequests");
        }
        finally { updatingCloudFields = false; }
        UpdateCloudEndpointState();
    }

    private void CloudEndpoint_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!initialized || updatingCloudFields || shownCloudProvider is null)
            return;
        try { UpdateCloudEndpointState(); }
        catch (Exception ex) { SettingsStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
    }

    private void UpdateCloudEndpointState()
    {
        if (shownCloudProvider is null)
            return;
        var profile = Settings.GetCloudProfile(shownCloudProvider);
        string? endpoint = null;
        try { endpoint = CloudLlmCatalog.Endpoint(shownCloudProvider, CloudEndpointBox.Text).AbsoluteUri; }
        catch (InvalidDataException ex) { CloudKeyStatus.Text = ex.Message; }
        if (endpoint != credentialEndpoint)
        {
            CaptureCloudCredential();
            credentialEndpoint = endpoint;
            CloudApiKeyBox.Clear();
            ForgetCloudKeyCheck.IsChecked = false;
            CloudApiConsentCheck.IsChecked = endpoint is not null && profile.ConsentEndpoint == endpoint;
        }
        if (endpoint is not null)
        {
            CloudKeyStatus.Text = UiText.Get(profile.ProtectedApiKeys.ContainsKey(endpoint) ? "CloudKeyConfigured" : "CloudKeyNotConfigured");
            CloudApiConsentText.Text = UiText.Format("CloudApiConsent",
                SummaryProviders.Label(shownCloudProvider, UiText.Current), endpoint);
        }
        else
            CloudApiConsentText.Text = UiText.Get("InvalidCloudEndpoint");
        UpdateModelOperationControls();
    }

    private void CaptureCloudCredential()
    {
        if (shownCloudProvider is null || credentialEndpoint is null)
            return;
        var profile = Settings.GetCloudProfile(shownCloudProvider);
        var scoped = new CloudSummarySettings { Endpoint = credentialEndpoint, ProtectedApiKeys = profile.ProtectedApiKeys };
        var password = CloudApiKeyBox.Password.Trim();
        if (ForgetCloudKeyCheck.IsChecked == true)
        {
            if (password.Length > 0)
                throw new InvalidOperationException(UiText.Get("CloudKeyEditConflict"));
            CloudApiKey.Forget(shownCloudProvider, scoped);
        }
        else if (password.Length > 0)
            CloudApiKey.Set(shownCloudProvider, scoped, password);
        CloudApiKeyBox.Clear();
        ForgetCloudKeyCheck.IsChecked = false;
        if (CloudApiConsentCheck.IsChecked == true)
            profile.ConsentEndpoint = credentialEndpoint;
        else if (profile.ConsentEndpoint == credentialEndpoint)
            profile.ConsentEndpoint = "";
    }

    private void ReadCloudFields()
    {
        if (shownCloudProvider is null)
            return;
        UpdateCloudEndpointState();
        var profile = Settings.GetCloudProfile(shownCloudProvider);
        profile.Endpoint = CloudLlmCatalog.Endpoint(shownCloudProvider, CloudEndpointBox.Text).AbsoluteUri;
        profile.Model = CloudModelBox.Text.Trim();
        profile.DisableThinking = DisableCloudThinkingCheck.IsChecked == true;
        CloudLlmCatalog.ValidateModel(profile.Model);
        CaptureCloudCredential();
        UpdateCloudEndpointState();
    }

    private AppSettings CloudSnapshot()
    {
        ReadCloudFields();
        var snapshot = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings))
            ?? throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
        snapshot.SummaryProvider = shownCloudProvider
            ?? throw new InvalidOperationException(UiText.Get("InvalidSummaryProvider"));
        snapshot.Validate();
        return snapshot;
    }

    private async Task RunCloudOperationAsync(Func<AppSettings, CancellationToken, Task> operation)
    {
        if (cloudOperation is not null || localOperation is not null)
            return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cloudOperation = cancellation;
        UpdateModelOperationControls();
        try { await operation(CloudSnapshot(), cancellation.Token); }
        catch (OperationCanceledException) { CloudModelStatus.Text = UiText.Get("CloudCheckCancelled"); }
        catch (Exception ex) { CloudModelStatus.Text = UiText.Format("ErrorDetails", ex.Message); }
        finally
        {
            cloudOperation = null;
            UpdateModelOperationControls();
        }
    }

    private async void ReadCloudModels_Click(object sender, RoutedEventArgs e) =>
        await RunCloudOperationAsync(async (snapshot, cancellationToken) =>
        {
            CloudModelStatus.Text = UiText.Get("CloudReadingModels");
            var models = await cloud.DiscoverModelsAsync(snapshot, cancellationToken);
            var selection = CloudModelBox.Text;
            CloudModelBox.ItemsSource = models;
            CloudModelBox.Text = selection;
            CloudModelStatus.Text = models.Count == 0 ? UiText.Get("CloudNoModels") : UiText.Format("CloudModelsFound", models.Count);
        });

    private async void TestCloudModel_Click(object sender, RoutedEventArgs e) =>
        await RunCloudOperationAsync(async (snapshot, cancellationToken) =>
        {
            CloudModelStatus.Text = UiText.Get("CloudTestingModel");
            var result = await cloud.TestAsync(snapshot, cancellationToken);
            CloudModelStatus.Text = UiText.Format("CloudTestPassed", result.Elapsed.TotalSeconds);
        });

    private void CancelCloudTest_Click(object sender, RoutedEventArgs e) => cloudOperation?.Cancel();

    private void OpenCloudConsole_Click(object sender, RoutedEventArgs e)
    {
        if (shownCloudProvider is not null)
            OpenModelLink(CloudLlmCatalog.Get(shownCloudProvider).ConsoleUrl);
    }

    private void OpenCloudModels_Click(object sender, RoutedEventArgs e)
    {
        if (shownCloudProvider is not null)
            OpenModelLink(CloudLlmCatalog.Get(shownCloudProvider).ModelsUrl);
    }
}
