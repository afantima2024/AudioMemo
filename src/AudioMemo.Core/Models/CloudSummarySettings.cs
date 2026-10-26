using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed class CloudSummarySettings
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public bool DisableThinking { get; set; } = true;
    public string ConsentEndpoint { get; set; } = "";
    public Dictionary<string, string> ProtectedApiKeys { get; set; } = [];

    public bool HasConsent(string provider) =>
        ConsentEndpoint == CloudLlmCatalog.Endpoint(provider, Endpoint).AbsoluteUri;

    public void Validate(string provider)
    {
        CloudLlmCatalog.Endpoint(provider, Endpoint);
        CloudLlmCatalog.ValidateModel(Model);
        if (ConsentEndpoint is null || ProtectedApiKeys is null)
            throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
        if (ConsentEndpoint.Length > 0)
            CloudLlmCatalog.Endpoint(provider, ConsentEndpoint);
        foreach (var (endpoint, key) in ProtectedApiKeys)
        {
            if (CloudLlmCatalog.Endpoint(provider, endpoint).AbsoluteUri != endpoint ||
                string.IsNullOrWhiteSpace(key) || key.Length > 32768)
                throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
        }
    }
}
