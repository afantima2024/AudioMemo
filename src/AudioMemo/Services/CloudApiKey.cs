using System.IO;
using System.Security.Cryptography;
using System.Text;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Services;

public static class CloudApiKey
{
    public static bool IsConfigured(string provider, CloudSummarySettings profile) =>
        profile.ProtectedApiKeys.ContainsKey(CloudLlmCatalog.Endpoint(provider, profile.Endpoint).AbsoluteUri);

    public static void Set(string provider, CloudSummarySettings profile, string value)
    {
        var endpoint = CloudLlmCatalog.Endpoint(provider, profile.Endpoint).AbsoluteUri;
        SetScoped(provider, endpoint, profile.ProtectedApiKeys, value);
    }

    internal static void SetScoped(string provider, string endpoint, IDictionary<string, string> keys, string value)
    {
        Validate(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            keys[endpoint] = Convert.ToBase64String(
                ProtectedData.Protect(bytes, Entropy(provider, endpoint), DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex) { throw new InvalidOperationException(UiText.Get("CloudKeyProtectFailed"), ex); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static string Read(string provider, CloudSummarySettings profile)
    {
        var endpoint = CloudLlmCatalog.Endpoint(provider, profile.Endpoint).AbsoluteUri;
        return ReadScoped(provider, endpoint, profile.ProtectedApiKeys);
    }

    internal static string ReadScoped(string provider, string endpoint, IReadOnlyDictionary<string, string> keys)
    {
        if (!keys.TryGetValue(endpoint, out var encrypted))
            throw new InvalidOperationException(UiText.Get("CloudKeyRequired"));
        byte[]? bytes = null;
        try
        {
            bytes = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy(provider, endpoint),
                DataProtectionScope.CurrentUser);
            var key = Encoding.UTF8.GetString(bytes);
            Validate(key);
            return key;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(UiText.Get("CloudKeyUnlockFailed"), ex);
        }
        finally
        {
            if (bytes is not null)
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static void Forget(string provider, CloudSummarySettings profile) =>
        profile.ProtectedApiKeys.Remove(CloudLlmCatalog.Endpoint(provider, profile.Endpoint).AbsoluteUri);

    public static void SetOnline(OnlineSpeechSettings settings, string value)
    {
        var profile = settings.GetProfile();
        SetScoped(settings.Provider, OnlineSpeechCatalog.Endpoint(settings.Provider, profile.Endpoint).AbsoluteUri,
            profile.ProtectedApiKeys, value);
    }

    public static string ReadOnline(AppSettings settings)
    {
        var selection = settings.OnlineSpeech;
        var profile = selection.GetProfile();
        var endpoint = OnlineSpeechCatalog.Endpoint(selection.Provider, profile.Endpoint).AbsoluteUri;
        if (profile.ProtectedApiKeys.ContainsKey(endpoint))
            return ReadScoped(selection.Provider, endpoint, profile.ProtectedApiKeys);
        if (selection.Provider == OnlineSpeechCatalog.Qwen &&
            settings.CloudModels.TryGetValue(CloudLlmCatalog.Qwen, out var summary) && summary.ProtectedApiKeys.ContainsKey(endpoint))
            return ReadScoped(selection.Provider, endpoint, summary.ProtectedApiKeys);
        throw new InvalidOperationException(UiText.Get("OnlineSpeechKeyRequired"));
    }

    private static byte[] Entropy(string provider, string endpoint) =>
        SHA256.HashData(Encoding.UTF8.GetBytes("AudioMemo/cloud-api-key/v1\n" + provider + "\n" + endpoint));

    private static void Validate(string value)
    {
        if (value.Length is < 1 or > 8192 || value.Any(character => character is < '!' or > '~'))
            throw new InvalidDataException(UiText.Get("InvalidCloudKey"));
    }
}
