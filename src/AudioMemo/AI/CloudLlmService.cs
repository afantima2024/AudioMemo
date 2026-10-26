using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.AI;

public sealed record CloudLlmResult(string Text, TimeSpan Elapsed);

public sealed class CloudLlmService(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
{
    private readonly CloudApiTransport transport = new(httpClient);
    private readonly TimeSpan timeout = requestTimeout ?? TimeSpan.FromMinutes(5);

    public async Task<IReadOnlyList<string>> DiscoverModelsAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        var provider = CloudLlmCatalog.Get(settings.SummaryProvider);
        if (!provider.SupportsModelListing)
            throw new InvalidOperationException(UiText.Get("CloudCatalogModelsOnly"));
        using var document = await SendAsync(settings, HttpMethod.Get, "models", null,
            TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in models.EnumerateArray())
        {
            var id = RequiredString(model, "id");
            CloudLlmCatalog.ValidateModel(id, required: true);
            result.Add(id);
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<CloudLlmResult> TestAsync(AppSettings settings, CancellationToken cancellationToken) =>
        GenerateAsync(settings,
            SummaryPrompt.Create("[00:00:00] This is a synthetic test. The demonstration is Friday. Alex will prepare the slides.", "", false),
            512, cancellationToken);

    public Task<CloudLlmResult> SummarizeAsync(AppSettings settings, string transcript, string previous,
        bool live, CancellationToken cancellationToken) =>
        SummarizePromptAsync(settings, SummaryPrompt.Create(transcript, previous, live), cancellationToken);

    internal Task<CloudLlmResult> SummarizePromptAsync(AppSettings settings, string prompt, CancellationToken cancellationToken,
        bool normalizeText = true)
    {
        settings.Validate();
        if (!settings.UsesCloudApi || !settings.CanSummarize)
            throw new InvalidOperationException(UiText.Get("ConsentNotApproved"));
        return GenerateAsync(settings, prompt, 4096, cancellationToken, normalizeText);
    }

    private async Task<CloudLlmResult> GenerateAsync(AppSettings settings, string prompt, int maximumTokens,
        CancellationToken cancellationToken, bool normalizeText = true)
    {
        settings.Validate();
        var profile = settings.GetCloudProfile();
        CloudLlmCatalog.ValidateModel(profile.Model, required: true);
        var request = new JsonObject
        {
            ["model"] = profile.Model,
            ["stream"] = false,
            ["max_tokens"] = maximumTokens,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt })
        };
        if (profile.DisableThinking)
        {
            if (settings.SummaryProvider == CloudLlmCatalog.Qwen)
                request["enable_thinking"] = false;
            else
                request["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
        var watch = Stopwatch.StartNew();
        using var response = await SendAsync(settings, HttpMethod.Post, "chat/completions", request,
            timeout, cancellationToken).ConfigureAwait(false);
        var root = response.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() != 1)
            throw InvalidResponse();
        var choice = choices[0];
        var finish = RequiredString(choice, "finish_reason");
        if (finish == "length")
            throw new InvalidDataException(UiText.Get("CloudOutputTruncated"));
        if (finish != "stop" || !choice.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(UiText.Get("CloudOutputIncomplete"));
        if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null &&
            (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 0))
            throw new InvalidDataException(UiText.Get("CloudUnexpectedTools"));
        if (message.TryGetProperty("function_call", out var function) && function.ValueKind != JsonValueKind.Null)
            throw new InvalidDataException(UiText.Get("CloudUnexpectedTools"));
        if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null &&
            (refusal.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(refusal.GetString())))
            throw new InvalidDataException(UiText.Get("CloudOutputIncomplete"));
        var summary = RequiredString(message, "content").Trim();
        if (summary.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            var end = summary.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                throw new InvalidDataException(UiText.Get("CloudOutputIncomplete"));
            summary = summary[(end + "</think>".Length)..].Trim();
        }
        if (summary.Length == 0 || summary.Length > SummaryPrompt.MaximumPreviousSummaryCharacters)
            throw new InvalidDataException(UiText.Get("CloudOutputIncomplete"));
        if (normalizeText)
            summary = ChineseText.ToSimplified(summary);
        if (summary.Length > SummaryPrompt.MaximumPreviousSummaryCharacters)
            throw new InvalidDataException(UiText.Get("CloudOutputIncomplete"));
        return new(summary, watch.Elapsed);
    }

    private async Task<JsonDocument> SendAsync(AppSettings settings, HttpMethod method, string path, JsonObject? body,
        TimeSpan deadlineTime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = settings.GetCloudProfile();
        var endpoint = CloudLlmCatalog.Endpoint(settings.SummaryProvider, profile.Endpoint);
        var text = new UiText(settings.UiLanguage);
        var provider = SummaryProviders.Label(settings.SummaryProvider, text);
        using var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudApiKey.Read(settings.SummaryProvider, profile));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return await transport.SendAsync(request, provider, text, deadlineTime, cancellationToken).ConfigureAwait(false);
    }

    private static string RequiredString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw InvalidResponse();

    private static InvalidDataException InvalidResponse() => new(UiText.Get("CloudInvalidResponse"));
}
