using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.AI;

public sealed record OnlineSpeechModel(string Provider, string Id, long MaximumInputBytes, int? MaximumSeconds,
    bool Base64, string Documentation)
{
    public long EstimatedBytes(long frames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frames);
        var wav = checked(128 + (frames + 2) / 3 * 2);
        return checked((Base64 ? (wav + 2) / 3 * 4 : wav) + 4096);
    }

    public bool Fits(long frames) => frames >= 0 &&
        (MaximumSeconds is null || frames <= (long)MaximumSeconds * PcmFormat.SampleRate) &&
        EstimatedBytes(frames) <= MaximumInputBytes;

    public long MaximumFrames
    {
        get
        {
            var bytes = MaximumInputBytes - 4096;
            if (Base64)
                bytes = bytes / 4 * 3;
            var frames = (bytes - 128) / 2 * 3;
            return MaximumSeconds is { } seconds ? Math.Min(frames, (long)seconds * PcmFormat.SampleRate) : frames;
        }
    }
}

public static class OnlineSpeechCatalog
{
    public const string Qwen = CloudLlmCatalog.Qwen;
    public const string OpenAi = "openai";
    public static IReadOnlyList<OnlineSpeechModel> Models { get; } =
    [
        new(Qwen, "qwen3-asr-flash", 10_000_000, 300, true,
            "https://www.alibabacloud.com/help/en/model-studio/qwen-asr-api-reference"),
        new(OpenAi, "gpt-transcribe", 25_000_000, null, false,
            "https://developers.openai.com/api/docs/guides/speech-to-text")
    ];

    public static IReadOnlyList<LanguageOption> Providers(UiText text) =>
        [new(Qwen, text["ProviderQwen"]), new(OpenAi, "OpenAI")];

    public static OnlineSpeechModel Get(string provider, string? model) =>
        Models.FirstOrDefault(item => item.Provider == provider && item.Id == model)
            ?? throw new InvalidDataException(UiText.Get("InvalidOnlineSpeechModel"));

    public static IReadOnlyList<string> Endpoints(string provider) => provider switch
    {
        Qwen => CloudLlmCatalog.Get(Qwen).Endpoints,
        OpenAi => ["https://api.openai.com/v1/"],
        _ => throw new InvalidDataException(UiText.Get("InvalidOnlineSpeechModel"))
    };

    public static Uri Endpoint(string provider, string? value)
    {
        if (provider == Qwen)
        {
            var uri = CloudLlmCatalog.Endpoint(Qwen, value);
            if (uri.Host is "dashscope.aliyuncs.com" or "dashscope-intl.aliyuncs.com" ||
                uri.Host.EndsWith(".cn-beijing.maas.aliyuncs.com", StringComparison.Ordinal) ||
                uri.Host.EndsWith(".ap-southeast-1.maas.aliyuncs.com", StringComparison.Ordinal))
                return uri;
        }
        else if (provider == OpenAi && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.IdnHost == "api.openai.com" && uri.Port == 443 &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
            uri.AbsolutePath.TrimEnd('/') == "/v1")
            return new("https://api.openai.com/v1/");
        throw new InvalidDataException(UiText.Get("InvalidOnlineSpeechEndpoint"));
    }
}
