using System.Text.RegularExpressions;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.AI;

public sealed record CloudLlmProvider(string Id, string LabelKey, IReadOnlyList<string> Endpoints,
    IReadOnlyList<string> Models, string ConsoleUrl, string ModelsUrl, bool SupportsModelListing = false)
{
    public string DefaultEndpoint => Endpoints[0];
    public string DefaultModel => Models.FirstOrDefault() ?? "";
}

public static class CloudLlmCatalog
{
    public const string Doubao = "doubao";
    public const string Qwen = "qwen";
    public const string DeepSeek = "deepseek";

    public static IReadOnlyList<CloudLlmProvider> Providers { get; } = Array.AsReadOnly<CloudLlmProvider>(
    [
        new(Doubao, "ProviderDoubao", ["https://ark.cn-beijing.volces.com/api/v3/"], [],
            "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey",
            "https://docs.volcengine.com/docs/ark/model-list?lang=zh"),
        new(Qwen, "ProviderQwen",
            ["https://dashscope.aliyuncs.com/compatible-mode/v1/", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/"],
            ["qwen3.7-plus", "qwen3.8-flash", "qwen3.8-max", "qwen-plus", "qwen-flash"],
            "https://bailian.console.aliyun.com/",
            "https://www.alibabacloud.com/help/en/model-studio/text-generation-model"),
        new(DeepSeek, "ProviderDeepSeek", ["https://api.deepseek.com/"],
            ["deepseek-flash", "deepseek-v4-pro"],
            "https://platform.deepseek.com/api_keys",
            "https://api-docs.deepseek.com/quick_start/pricing", true)
    ]);

    private static readonly Regex QwenWorkspaceHost = new(
        @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.(?:cn-beijing|ap-southeast-1|us-east-1|cn-hongkong|ap-northeast-1)\.maas\.aliyuncs\.com$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsProvider(string? id) => Providers.Any(provider => provider.Id == id);

    public static CloudLlmProvider Get(string id) => Providers.FirstOrDefault(provider => provider.Id == id)
        ?? throw new InvalidDataException(UiText.Get("InvalidSummaryProvider"));

    public static Uri Endpoint(string provider, string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException(UiText.Get("InvalidCloudEndpoint"));
        var host = uri.IdnHost.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');
        var valid = provider switch
        {
            Doubao => host == "ark.cn-beijing.volces.com" && path == "/api/v3",
            Qwen => (host is "dashscope.aliyuncs.com" or "dashscope-intl.aliyuncs.com" ||
                QwenWorkspaceHost.IsMatch(host)) && path == "/compatible-mode/v1",
            DeepSeek => host == "api.deepseek.com" && path is "" or "/v1",
            _ => false
        };
        if (!valid)
            throw new InvalidDataException(UiText.Get("InvalidCloudEndpoint"));
        return new UriBuilder(uri) { Port = -1, Path = path + "/" }.Uri;
    }

    public static void ValidateModel(string? model, bool required = false)
    {
        if (model is null || model.Length > 200 || model.Any(character => character is < '!' or > '~'))
            throw new InvalidDataException(UiText.Get("InvalidCloudModel"));
        if (required && model.Length == 0)
            throw new InvalidOperationException(UiText.Get("ChooseCloudModel"));
    }
}
