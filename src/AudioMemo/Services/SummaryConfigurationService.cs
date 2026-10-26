using System.IO;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Services;

internal sealed class SummaryConfigurationService(ISummaryService? service = null, TimeSpan? timeout = null)
{
    internal const string SamplePrompt =
        "This is an AudioMemo connection and model-access test using synthetic text only. Reply with exactly READY and nothing else.";
    private readonly ISummaryService summaries = service ?? new SummaryService();

    public async Task ValidateAsync(AppSettings settings, CancellationToken token)
    {
        var snapshot = Copy(settings);
        snapshot.Validate();
        if (!snapshot.CanSummarize)
            throw new InvalidOperationException(UiText.Get("ConsentNotApproved"));
        if (snapshot.UsesLocalSummaries && string.IsNullOrWhiteSpace(snapshot.OllamaModel))
            throw new InvalidOperationException(UiText.Get("ChooseLocalModel"));
        if (snapshot.UsesCloudApi)
        {
            CloudLlmCatalog.ValidateModel(snapshot.ActiveSummaryModel, required: true);
            CloudApiKey.Read(snapshot.SummaryProvider, snapshot.GetCloudProfile());
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        try
        {
            var result = await summaries.SummarizePromptAsync(snapshot, SamplePrompt, null, deadline.Token)
                .ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(result))
                throw new InvalidDataException(UiText.Get("EmptySummary"));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException(UiText.Get("LlmValidationTimedOut"));
        }
    }

    internal static AppSettings Copy(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
            ?? throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));

    internal static bool SelectionChanged(AppSettings previous, AppSettings candidate) =>
        previous.SummaryProvider != candidate.SummaryProvider ||
        previous.ActiveSummaryModel != candidate.ActiveSummaryModel ||
        previous.ActiveSummaryEndpoint != candidate.ActiveSummaryEndpoint ||
        previous.SummaryProvider == SummaryProviders.Copilot && previous.CopilotExecutable != candidate.CopilotExecutable;

    internal static AppSettings Merge(AppSettings current, AppSettings validated)
    {
        var next = Copy(current);
        var selection = Copy(validated);
        next.SummaryProvider = selection.SummaryProvider;
        next.SummaryModel = selection.SummaryModel;
        next.CopilotExecutable = selection.CopilotExecutable;
        next.OllamaEndpoint = selection.OllamaEndpoint;
        next.OllamaModel = selection.OllamaModel;
        next.OllamaContextTokens = selection.OllamaContextTokens;
        next.OllamaTimeoutMinutes = selection.OllamaTimeoutMinutes;
        next.CloudModels = selection.CloudModels;
        next.CloudConsent = selection.CloudConsent;
        next.LiveSummary = selection.LiveSummary;
        next.SummarizeOnStop = selection.SummarizeOnStop;
        next.ReviewUnclearTranscript = selection.ReviewUnclearTranscript;
        next.SummarySetupDeferred = false;
        next.Validate();
        return next;
    }
}
