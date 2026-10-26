using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.AI;

public interface ISummaryService
{
    string ResolveModel(AppSettings settings) => settings.ActiveSummaryModel;
    Task<string> SummarizePromptAsync(AppSettings settings, string prompt, Action<string>? warning,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This summary service does not support explicit prompts.");

    Task<string> GenerateStructuredPromptAsync(AppSettings settings, string prompt, Action<string>? warning,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This summary service does not support structured prompts.");

    Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
        Action<string>? warning, CancellationToken cancellationToken);
    Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
        Action<string>? warning, CancellationToken cancellationToken);
}

public sealed class SummaryService(OllamaService? ollama = null, CloudLlmService? cloud = null) : ISummaryService
{
    private readonly OllamaService local = ollama ?? new();
    private readonly CloudLlmService api = cloud ?? new();
    private readonly CopilotService copilot = new();

    public string ResolveModel(AppSettings settings) =>
        settings.SummaryProvider == SummaryProviders.Copilot && string.IsNullOrWhiteSpace(settings.SummaryModel)
            ? CopilotService.ReadConfiguration().Model ?? ""
            : settings.ActiveSummaryModel;

    public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
        Action<string>? warning, CancellationToken cancellationToken) =>
        SummarizePromptAsync(settings, SummaryPrompt.Create(transcript, previous, live), warning, cancellationToken);

    public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
        Action<string>? warning, CancellationToken cancellationToken) =>
        SummarizePromptAsync(settings, SummaryPrompt.CreateOverall(mainPoints), warning, cancellationToken);

    public Task<string> SummarizePromptAsync(AppSettings settings, string prompt,
        Action<string>? warning, CancellationToken cancellationToken) =>
        GeneratePromptAsync(settings, prompt, warning, cancellationToken, normalizeText: true);

    public Task<string> GenerateStructuredPromptAsync(AppSettings settings, string prompt,
        Action<string>? warning, CancellationToken cancellationToken) =>
        GeneratePromptAsync(settings, prompt, warning, cancellationToken, normalizeText: false);

    private async Task<string> GeneratePromptAsync(AppSettings settings, string prompt,
        Action<string>? warning, CancellationToken cancellationToken, bool normalizeText)
    {
        if (settings.SummaryProvider == SummaryProviders.Ollama)
            return (await local.SummarizePromptAsync(settings, prompt, warning, cancellationToken, normalizeText)
                .ConfigureAwait(false)).Text;
        if (settings.UsesCloudApi)
            return (await api.SummarizePromptAsync(settings, prompt, cancellationToken, normalizeText)
                .ConfigureAwait(false)).Text;
        if (settings.SummaryProvider != SummaryProviders.Copilot)
            throw new InvalidOperationException(UiText.Get("InvalidSummaryProvider"));
        return await copilot.SummarizePromptAsync(settings, prompt, cancellationToken, normalizeText).ConfigureAwait(false);
    }
}
