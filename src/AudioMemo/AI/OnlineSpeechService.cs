using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.AI;

public sealed class OnlineSpeechService(HttpClient? httpClient = null, TimeSpan? requestTimeout = null)
{
    private readonly CloudApiTransport transport = new(httpClient);
    private readonly TimeSpan timeout = requestTimeout ?? TimeSpan.FromMinutes(10);

    public async Task<string> TranscribeAsync(AppSettings settings, string wavePath, long frames,
        string authorizedEndpoint, CancellationToken cancellationToken)
    {
        settings.Validate();
        var selection = settings.OnlineSpeech;
        var profile = selection.GetProfile();
        var model = OnlineSpeechCatalog.Get(selection.Provider, profile.Model);
        var endpoint = OnlineSpeechCatalog.Endpoint(selection.Provider, profile.Endpoint);
        if (authorizedEndpoint != endpoint.AbsoluteUri)
            throw new InvalidOperationException(UiText.Get("OnlineAudioConsentRequired"));
        if (!model.Fits(frames))
            throw new InvalidOperationException(UiText.Get("OnlineSelectionTooLarge"));
        using var source = new FileStream(wavePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = source.Length;
        var inputSize = model.Base64 ? checked((length + 2) / 3 * 4 + 4096) : length + 4096;
        if (length == 0 || inputSize > model.MaximumInputBytes)
            throw new InvalidOperationException(UiText.Get("OnlineSelectionTooLarge"));
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint,
            selection.Provider == OnlineSpeechCatalog.Qwen ? "chat/completions" : "audio/transcriptions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudApiKey.ReadOnline(settings));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (selection.Provider == OnlineSpeechCatalog.Qwen)
        {
            var data = new byte[checked((int)length)];
            await source.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
            var options = new JsonObject { ["enable_itn"] = false };
            if (settings.Language != "auto")
                options["language"] = settings.Language;
            var body = new JsonObject
            {
                ["model"] = model.Id, ["stream"] = false, ["asr_options"] = options,
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "input_audio",
                        ["input_audio"] = new JsonObject { ["data"] = "data:audio/wav;base64," + Convert.ToBase64String(data) }
                    })
                })
            };
            var json = JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (json.LongLength > model.MaximumInputBytes)
                throw new InvalidOperationException(UiText.Get("OnlineSelectionTooLarge"));
            request.Content = new ByteArrayContent(json);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        else
        {
            var content = new MultipartFormDataContent();
            request.Content = content;
            content.Add(new StringContent(model.Id), "model");
            if (settings.Language != "auto")
                content.Add(new StringContent(settings.Language), "languages[]");
            var audio = new StreamContent(source);
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audio, "file", "selection.wav");
        }
        if (request.Content.Headers.ContentLength is not { } actualBytes || actualBytes > model.MaximumInputBytes)
            throw new InvalidOperationException(UiText.Get("OnlineSelectionTooLarge"));
        using var response = await transport.SendAsync(request, selection.Provider == OnlineSpeechCatalog.Qwen
            ? new UiText(settings.UiLanguage)["ProviderQwen"] : "OpenAI", new(settings.UiLanguage),
            timeout, cancellationToken, requestRejectedHint: "OnlineSpeechRequestRejected").ConfigureAwait(false);
        var root = response.RootElement;
        string text;
        if (selection.Provider == OnlineSpeechCatalog.Qwen)
        {
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() != 1 || choices[0].ValueKind != JsonValueKind.Object ||
                !choices[0].TryGetProperty("finish_reason", out var finish) ||
                finish.ValueKind != JsonValueKind.String || finish.GetString() != "stop" ||
                !choices[0].TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
                message.TryGetProperty("tool_calls", out _) ||
                message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind is not JsonValueKind.Null)
                throw new InvalidDataException(UiText.Get("CloudOutputIncomplete"));
            text = ReadText(message, "content");
        }
        else
            text = ReadText(root, "text");
        text = ChineseText.ToSimplified(text.Trim());
        if (text.Length > TranscriptCorrectionPlan.MaximumTextLength ||
            text.Any(character => char.IsControl(character) && character is not ('\t' or '\n' or '\r')))
            throw new InvalidDataException(UiText.Get("InvalidCorrectionText"));
        return text;
    }

    private static string ReadText(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidDataException(UiText.Get("CloudInvalidResponse"));
}
