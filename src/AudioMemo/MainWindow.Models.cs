using System.Windows;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo;

public partial class MainWindow
{
    private readonly Func<AppSettings, CancellationToken, Task> validateSummarySelection;
    private readonly Func<AppSettings, string, AppSettings?> configureSummarySelection;
    private CancellationTokenSource? modelSelectionCancellation;
    private Task? modelSelectionTask;
    private bool selectingModel;

    private bool CanCommitModel => !RecordingActive && !AiBusy && !recordingCommand && !exportBusy && !closing;

    private void RestoreMainModelSelection()
    {
        loadingModels = true;
        try
        {
            MainProviderBox.SelectedValue = settings.SummaryProvider;
            SetMainModelChoices();
        }
        finally { loadingModels = false; }
    }

    private AppSettings? ConfigureSummarySelection(AppSettings candidate, string failure)
    {
        var dialog = new SettingsWindow(candidate, "summary", false, validateSummarySelection, failure, summaryOnly: true)
        { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Settings : null;
    }

    private async Task ChangeSummarySelectionAsync(AppSettings candidate)
    {
        if (selectingModel)
            return;
        selectingModel = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        modelSelectionCancellation = cancellation;
        long? validationWarning = null;
        try
        {
            RestoreMainModelSelection();
            UpdateControls();
            if (!CanCommitModel)
                throw new InvalidOperationException(UiText.Get("SessionBusy"));
            SetStatus(UiText.Get("LlmValidatingSelection"), operation: "LlmLabel");
            try { await validateSummarySelection(candidate, cancellation.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!CanCommitModel || cancellation.IsCancellationRequested)
                    throw new OperationCanceledException(cancellation.Token);
                SetStatus(UiText.Format("LlmSelectionNeedsSetup", ex.Message), true, "LlmLabel");
                validationWarning = warningVersion;
                var configured = configureSummarySelection(candidate, ex.Message);
                if (configured is null)
                {
                    SetStatus(UiText.Get("LlmSelectionKept"), operation: "LlmLabel");
                    return;
                }
                candidate = configured;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!CanCommitModel)
                throw new InvalidOperationException(UiText.Get("LlmSelectionBusy"));
            var next = SummaryConfigurationService.Merge(settings, candidate);
            storage.SaveSettings(next);
            settings = next;
            UpdateModelStatus();
            if (validationWarning == warningVersion)
                DismissWarning_Click(this, new RoutedEventArgs());
            SetStatus(UiText.Get("LlmSelectionValidated"), operation: "LlmLabel");
        }
        catch (OperationCanceledException)
        {
            if (!closing)
                SetStatus(UiText.Get("LlmSelectionKept"), operation: "LlmLabel");
        }
        catch (Exception ex)
        {
            if (!closing)
                ShowError(ex, "LlmLabel");
        }
        finally
        {
            modelSelectionCancellation = null;
            selectingModel = false;
            RestoreMainModelSelection();
            UpdateControls();
        }
    }

    private async Task ValidateChangedSettingsAsync(AppSettings candidate, CancellationToken token)
    {
        if (SummaryConfigurationService.SelectionChanged(settings, candidate))
            await validateSummarySelection(candidate, token);
    }
}
