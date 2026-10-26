using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed class OnlineSpeechSettings
{
    public string Provider { get; set; } = OnlineSpeechCatalog.Qwen;
    public Dictionary<string, OnlineSpeechProfile> Profiles { get; set; } = [];

    public OnlineSpeechProfile GetProfile()
    {
        var endpoint = OnlineSpeechCatalog.Endpoints(Provider)[0];
        if (!Profiles.TryGetValue(Provider, out var profile))
        {
            profile = new() { Endpoint = endpoint, Model = OnlineSpeechCatalog.Models.First(model => model.Provider == Provider).Id };
            Profiles.Add(Provider, profile);
        }
        return profile ?? throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
    }

    public void Validate()
    {
        OnlineSpeechCatalog.Endpoints(Provider);
        if (Profiles is null)
            throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
        foreach (var (provider, profile) in Profiles)
        {
            if (profile is null || profile.ProtectedApiKeys is null)
                throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
            OnlineSpeechCatalog.Get(provider, profile.Model);
            OnlineSpeechCatalog.Endpoint(provider, profile.Endpoint);
            foreach (var (endpoint, key) in profile.ProtectedApiKeys)
            {
                OnlineSpeechCatalog.Endpoint(provider, endpoint);
                if (string.IsNullOrEmpty(key) || key.Length > 20_000)
                    throw new InvalidDataException(UiText.Get("InvalidCloudKey"));
            }
        }
    }
}

public sealed class OnlineSpeechProfile
{
    public string Endpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public Dictionary<string, string> ProtectedApiKeys { get; set; } = [];
}
