using System.Net;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.AI;

public static class SummaryProviders
{
    public const string Copilot = "copilot";
    public const string Ollama = "ollama";
    public const string DefaultOllamaEndpoint = "http://127.0.0.1:11434";

    public static IReadOnlyList<LanguageOption> GetOptions(UiText text) =>
    [
        new(Copilot, text["ProviderCopilot"]),
        new(Ollama, text["ProviderOllama"]),
        .. CloudLlmCatalog.Providers.Select(provider => new LanguageOption(provider.Id, text[provider.LabelKey]))
    ];

    public static string Label(string provider, UiText text) => text[provider switch
    {
        Copilot => "ProviderCopilot",
        Ollama => "ProviderOllama",
        _ => CloudLlmCatalog.Get(provider).LabelKey
    }];

    public static Uri LocalEndpoint(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !uri.IsLoopback ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
            throw new InvalidDataException(UiText.Get("InvalidLocalEndpoint"));

        // Avoid DNS/proxy configuration turning "localhost" into a remote destination.
        var builder = new UriBuilder(uri);
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            builder.Host = IPAddress.Loopback.ToString();
        else if (!IPAddress.TryParse(uri.IdnHost, out var address) || !IPAddress.IsLoopback(address))
            throw new InvalidDataException(UiText.Get("InvalidLocalEndpoint"));
        return builder.Uri;
    }

    public static bool HasCloudTag(string name) =>
        name.Split(':', '/').Any(part =>
            part.Equals("cloud", StringComparison.OrdinalIgnoreCase) ||
            part.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase));
}
