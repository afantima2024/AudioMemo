using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.AI;

public sealed record OllamaModel(string Name, long SizeBytes, bool IsRemote)
{
    public override string ToString() => Name;
}
public sealed record OllamaResult(string Text, TimeSpan Elapsed, double? TokensPerSecond);

public sealed class OllamaService(HttpClient? httpClient = null)
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client = httpClient ?? SharedClient;

    public async Task<IReadOnlyList<OllamaModel>> DiscoverModelsAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var document = await SendAsync(endpoint, HttpMethod.Get, "api/tags", null,
            TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw InvalidResponse();
        var result = new List<OllamaModel>();
        foreach (var model in models.EnumerateArray())
        {
            var name = RequiredString(model, "name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl) ||
                !model.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
                !size.TryGetInt64(out var bytes) || bytes < 0)
                throw InvalidResponse();
            result.Add(new(name, bytes, IsRemote(model) || SummaryProviders.HasCloudTag(name)));
        }
        return result.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Task<OllamaResult> TestAsync(AppSettings settings, Action<string>? warning, CancellationToken cancellationToken) =>
        GenerateAsync(settings,
            SummaryPrompt.Create("[00:00:00] The demonstration is Friday. Alex will prepare the slides. The budget was approved.", "", false),
            512, warning, cancellationToken);

    public Task<OllamaResult> SummarizeAsync(AppSettings settings, string transcript, string previous,
        bool live, Action<string>? warning, CancellationToken cancellationToken) =>
        SummarizePromptAsync(settings, SummaryPrompt.Create(transcript, previous, live), warning, cancellationToken);

    internal Task<OllamaResult> SummarizePromptAsync(AppSettings settings, string prompt,
        Action<string>? warning, CancellationToken cancellationToken, bool normalizeText = true) =>
        GenerateAsync(settings, prompt, 4096, warning, cancellationToken, normalizeText);

    private async Task<OllamaResult> GenerateAsync(AppSettings settings, string prompt, int outputTokens,
        Action<string>? warning, CancellationToken cancellationToken, bool normalizeText = true)
    {
        settings.Validate();
        var text = new UiText(settings.UiLanguage);
        if (!settings.UsesLocalSummaries || string.IsNullOrWhiteSpace(settings.OllamaModel))
            throw new InvalidOperationException(text["ChooseLocalModel"]);
        if (SummaryProviders.HasCloudTag(settings.OllamaModel))
            throw new InvalidOperationException(text["LocalCloudModelBlocked"]);
        if (Encoding.UTF8.GetByteCount(prompt) + outputTokens + 512 > settings.OllamaContextTokens)
            throw new InvalidOperationException(text["LocalContextTooSmall"]);

        var models = await DiscoverModelsAsync(settings.OllamaEndpoint, cancellationToken).ConfigureAwait(false);
        var selected = models.FirstOrDefault(model => SameModel(model.Name, settings.OllamaModel))
            ?? throw new InvalidOperationException(text.FormatValue("LocalModelNotInstalled", settings.OllamaModel));
        if (selected.IsRemote || selected.SizeBytes == 0)
            throw new InvalidOperationException(text["LocalCloudModelBlocked"]);
        using var details = await SendAsync(settings.OllamaEndpoint, HttpMethod.Post, "api/show",
            new { model = selected.Name, verbose = false }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var info = details.RootElement;
        if (IsRemote(info))
            throw new InvalidOperationException(text["LocalCloudModelBlocked"]);
        if (!info.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array ||
            !capabilities.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "completion"))
            throw new InvalidOperationException(text["LocalCompletionRequired"]);

        try
        {
            var memoryWarning = LocalModelDiagnostics.MemoryWarning(selected.SizeBytes, ComputerMemory.Read(), text);
            if (memoryWarning is not null)
                warning?.Invoke(memoryWarning);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            warning?.Invoke(text.FormatValue("LocalMemoryUnavailable", ex.Message));
        }

        var request = new JsonObject
        {
            ["model"] = selected.Name,
            ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["options"] = new JsonObject
            {
                ["temperature"] = 0.2,
                ["num_ctx"] = settings.OllamaContextTokens,
                ["num_predict"] = outputTokens
            }
        };
        if (info.TryGetProperty("thinking", out var thinking) && thinking.ValueKind == JsonValueKind.Object &&
            thinking.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            if (values.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.False))
                request["think"] = false;
            else if (values.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "low"))
                request["think"] = "low";
        }

        var watch = Stopwatch.StartNew();
        using var slowNotice = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var responseTask = SendAsync(settings.OllamaEndpoint, HttpMethod.Post, "api/chat", request,
            TimeSpan.FromMinutes(settings.OllamaTimeoutMinutes), cancellationToken);
        var delay = Task.Delay(LocalModelDiagnostics.SlowRequestThreshold, slowNotice.Token);
        try
        {
            if (await Task.WhenAny(responseTask, delay).ConfigureAwait(false) == delay)
            {
                if (!cancellationToken.IsCancellationRequested)
                    warning?.Invoke(text["LocalSlowRequest"]);
            }
            using var response = await responseTask.ConfigureAwait(false);
            var root = response.RootElement;
            if (!root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                throw InvalidResponse();
            if (OptionalString(root, "done_reason") == "length")
                throw new InvalidDataException(text["LocalOutputTruncated"]);
            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind != JsonValueKind.Null)
            {
                if (calls.ValueKind != JsonValueKind.Array)
                    throw InvalidResponse();
                if (calls.GetArrayLength() > 0)
                    throw new InvalidDataException(text["LocalUnexpectedTools"]);
            }
            var summary = RequiredString(message, "content").Trim();
            if (summary.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
            {
                var end = summary.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (end < 0)
                    throw new InvalidDataException(text["LocalEmptySummary"]);
                summary = summary[(end + "</think>".Length)..].Trim();
            }
            if (summary.Length == 0)
                throw new InvalidDataException(text["LocalEmptySummary"]);
            if (summary.Length > SummaryPrompt.MaximumPreviousSummaryCharacters)
                throw new InvalidDataException(text["LocalOversizedSummary"]);
            if (normalizeText)
                summary = ChineseText.ToSimplified(summary);
            if (summary.Length > SummaryPrompt.MaximumPreviousSummaryCharacters)
                throw new InvalidDataException(text["LocalOversizedSummary"]);

            double? tokensPerSecond = null;
            if (root.TryGetProperty("eval_count", out var count) && count.ValueKind == JsonValueKind.Number &&
                count.TryGetInt64(out var tokens) && tokens >= 20 &&
                root.TryGetProperty("eval_duration", out var duration) && duration.ValueKind == JsonValueKind.Number &&
                duration.TryGetInt64(out var nanoseconds) && nanoseconds > 0)
                tokensPerSecond = tokens * 1_000_000_000d / nanoseconds;
            var speedWarning = LocalModelDiagnostics.SpeedWarning(watch.Elapsed, tokensPerSecond, text);
            if (speedWarning is not null)
                warning?.Invoke(speedWarning);
            return new(summary, watch.Elapsed, tokensPerSecond);
        }
        finally
        {
            await slowNotice.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task<JsonDocument> SendAsync(string endpoint, HttpMethod method, string path, object? body,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var baseUri = SummaryProviders.LocalEndpoint(endpoint);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
        if (body is not null)
            request.Content = JsonContent.Create(body);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidDataException(UiText.Get("LocalResponseTooLarge"));
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    throw new InvalidDataException(UiText.Get("LocalResponseTooLarge"));
                buffer.Write(chunk, 0, count);
            }
            if (!response.IsSuccessStatusCode)
            {
                var error = Encoding.UTF8.GetString(buffer.ToArray());
                throw new InvalidOperationException(UiText.Format("LocalHttpFailed", (int)response.StatusCode,
                    error.Length > 500 ? error[..500] : error));
            }
            var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw InvalidResponse();
            }
            if (document.RootElement.TryGetProperty("error", out var failure))
            {
                var message = failure.ToString();
                document.Dispose();
                throw new InvalidOperationException(UiText.Format("LocalServerError", message.Length > 500 ? message[..500] : message));
            }
            return document;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(UiText.Format("LocalConnectionFailed", baseUri.Authority, ex.Message), ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(UiText.Get("LocalRequestTimeout"), ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(UiText.Get("LocalInvalidResponse"), ex);
        }
    }

    private static bool SameModel(string left, string right)
    {
        static string Normalize(string name) => name[(name.LastIndexOf('/') + 1)..].Contains(':') ? name : name + ":latest";
        return Normalize(left).Equals(Normalize(right.Trim()), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRemote(JsonElement model) =>
        !string.IsNullOrWhiteSpace(OptionalString(model, "remote_model")) ||
        !string.IsNullOrWhiteSpace(OptionalString(model, "remote_host"));

    private static string RequiredString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString()! : throw InvalidResponse();

    private static string? OptionalString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var item) && item.ValueKind != JsonValueKind.Null
            ? item.ValueKind == JsonValueKind.String ? item.GetString() : throw InvalidResponse()
            : null;

    private static InvalidDataException InvalidResponse() => new(UiText.Get("LocalInvalidResponse"));
}
