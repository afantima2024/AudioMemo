using System.Text.Json.Serialization;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed class AppSettings
{
    public bool SetupCompleted { get; set; }
    public bool SummarySetupDeferred { get; set; }
    public StatusBarSettings StatusBar { get; set; } = new();
    public string SaveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AudioMemo");
    public string Format { get; set; } = "WAV";
    public AudioOutputSettings AudioOutput { get; set; } = new();
    public string? MicrophoneId { get; set; }
    public string? PlaybackId { get; set; }
    public bool MicrophoneEnabled { get; set; } = true;
    public bool PlaybackEnabled { get; set; } = true;
    public double MicrophoneGain { get; set; } = 0.8;
    public double PlaybackGain { get; set; } = 0.7;
    public bool AutoStopOnSilence { get; set; }
    public int SilenceTimeoutSeconds { get; set; } = 300;
    public string SpeechModel { get; set; } = "small";
    public bool FastLiveDrafts { get; set; } = true;
    public bool SpeechVocabularyEnabled { get; set; } = true;
    public List<string> SpeechVocabularyTerms { get; set; } = [];
    public string SpeechAcceleration { get; set; } = "auto";
    public bool IdentifySpeakersOnStop { get; set; } = true;
    public int SpeakerCount { get; set; }
    public bool HistoryVisible { get; set; } = true;
    public bool MainPointsVisible { get; set; } = true;
    public bool MyNotesVisible { get; set; } = true;
    public double HistoryWidth { get; set; } = 210;
    public double HistoryTabPosition { get; set; }
    public double TranscriptNavigationX { get; set; } = 1;
    public double TranscriptNavigationY { get; set; } = 1;
    public string UiLanguage { get; set; } = "en";
    public string Theme { get; set; } = AppThemes.Light;
    public AppearanceSettings Appearance { get; set; } = new();
    public ShortcutSettings Shortcuts { get; set; } = new();
    public string Language { get; set; } = "auto";
    public string CopilotExecutable { get; set; } = "";
    public string SummaryModel { get; set; } = "";
    public string SummaryProvider { get; set; } = SummaryProviders.Copilot;
    public string OllamaEndpoint { get; set; } = SummaryProviders.DefaultOllamaEndpoint;
    public string OllamaModel { get; set; } = "";
    public int OllamaContextTokens { get; set; } = 32768;
    public int OllamaTimeoutMinutes { get; set; } = 15;
    public bool LiveTranscription { get; set; }
    public bool LiveSummary { get; set; }
    public bool TranscribeOnStop { get; set; } = true;
    public bool ReviewUnclearTranscript { get; set; } = true;
    public bool SummarizeOnStop { get; set; }
    public bool CloudConsent { get; set; }
    public Dictionary<string, CloudSummarySettings> CloudModels { get; set; } = [];
    public OnlineSpeechSettings OnlineSpeech { get; set; } = new();
    [JsonIgnore] public bool UsesLocalSummaries => SummaryProvider == SummaryProviders.Ollama;
    [JsonIgnore] public bool UsesCloudApi => CloudLlmCatalog.IsProvider(SummaryProvider);
    [JsonIgnore]
    public bool CanSummarize => UsesLocalSummaries ||
        (UsesCloudApi ? GetCloudProfile().HasConsent(SummaryProvider) : SummaryProvider == SummaryProviders.Copilot && CloudConsent);
    [JsonIgnore]
    public string ActiveSummaryModel => UsesLocalSummaries ? OllamaModel :
        UsesCloudApi ? GetCloudProfile().Model : SummaryModel;
    [JsonIgnore]
    public string ActiveSummaryEndpoint => UsesLocalSummaries
        ? SummaryProviders.LocalEndpoint(OllamaEndpoint).AbsoluteUri
        : UsesCloudApi ? CloudLlmCatalog.Endpoint(SummaryProvider, GetCloudProfile().Endpoint).AbsoluteUri : "";

    public CloudSummarySettings GetCloudProfile(string? provider = null)
    {
        var definition = CloudLlmCatalog.Get(provider ?? SummaryProvider);
        if (CloudModels is null)
            throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
        if (CloudModels.TryGetValue(definition.Id, out var profile))
            return profile ?? throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
        profile = new() { Endpoint = definition.DefaultEndpoint, Model = definition.DefaultModel };
        CloudModels.Add(definition.Id, profile);
        return profile;
    }

    public void Validate()
    {
        if (StatusBar is null)
            throw new InvalidDataException(UiText.Get("InvalidStatusBarSettings"));
        if (OnlineSpeech is null)
            throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
        OnlineSpeech.Validate();
        if (!UiText.IsSupportedLanguage(UiLanguage))
            throw new InvalidDataException(UiText.Get("InvalidInterfaceLanguage"));
        if (!AppThemes.IsSupported(Theme))
            throw new InvalidDataException(UiText.Get("InvalidTheme"));
        if (Appearance is null)
            throw new InvalidDataException(UiText.Get("InvalidAppearanceColors"));
        Appearance.Validate();
        if (Shortcuts is null)
            throw new InvalidDataException(UiText.Get("InvalidShortcuts"));
        Shortcuts.Validate();
        if (!double.IsFinite(HistoryTabPosition) || HistoryTabPosition is < 0 or > 1)
            throw new InvalidDataException(UiText.Get("InvalidHistoryTabPosition"));
        if (!double.IsFinite(TranscriptNavigationX) || !double.IsFinite(TranscriptNavigationY) ||
            TranscriptNavigationX is < 0 or > 1 || TranscriptNavigationY is < 0 or > 1)
            throw new InvalidDataException(UiText.Get("InvalidTranscriptNavigationPosition"));
        if (!double.IsFinite(HistoryWidth) || HistoryWidth is < 180 or > 640)
            throw new InvalidDataException(UiText.Get("InvalidHistoryWidth"));
        if (string.IsNullOrWhiteSpace(SaveFolder) || !Path.IsPathFullyQualified(SaveFolder))
            throw new InvalidDataException(UiText.Get("InvalidSavingFolder"));
        if (Format is not ("WAV" or "MP3"))
            throw new InvalidDataException(UiText.Get("InvalidAudioFormat"));
        if (AudioOutput is null)
            throw new InvalidDataException(UiText.Get("InvalidAudioOutput"));
        AudioOutput.Validate();
        if (SilenceTimeoutSeconds is < 1 or > 86400)
            throw new InvalidDataException(UiText.Get("InvalidSilenceTimeout"));
        if (SpeechModel is not ("base" or "small" or "large-v3-turbo-q5_0"))
            throw new InvalidDataException(UiText.Get("InvalidSpeechModel"));
        if (SpeechAcceleration is not ("auto" or "cpu" or "vulkan"))
            throw new InvalidDataException(UiText.Get("InvalidSpeechAcceleration"));
        if (SpeechVocabularyTerms is null)
            throw new InvalidDataException(UiText.Get("InvalidSpeechVocabulary"));
        SpeechVocabulary.Normalize(SpeechVocabularyTerms);
        if (SpeakerCount is < 0 or > 20)
            throw new InvalidDataException(UiText.Get("InvalidSpeakerCount"));
        if (MicrophoneGain is < 0 or > 2 || PlaybackGain is < 0 or > 2 ||
            !double.IsFinite(MicrophoneGain) || !double.IsFinite(PlaybackGain))
            throw new InvalidDataException(UiText.Get("InvalidAudioGain"));
        if (Language is null || Language.Length is < 2 or > 12 ||
            Language.Any(c => !char.IsAsciiLetter(c) && c != '-'))
            throw new InvalidDataException(UiText.Get("InvalidSpeechLanguage"));
        if (SummaryProvider is not (SummaryProviders.Copilot or SummaryProviders.Ollama) && !UsesCloudApi)
            throw new InvalidDataException(UiText.Get("InvalidSummaryProvider"));
        if (CloudModels is null)
            throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
        foreach (var (provider, profile) in CloudModels)
        {
            CloudLlmCatalog.Get(provider);
            if (profile is null)
                throw new InvalidDataException(UiText.Get("InvalidCloudSettings"));
            profile.Validate(provider);
        }
        if (OllamaModel is null || OllamaModel.Length > 200 || OllamaModel.Any(char.IsControl))
            throw new InvalidDataException(UiText.Get("InvalidLocalModel"));
        if (OllamaContextTokens is < 8192 or > 1048576 || OllamaTimeoutMinutes is < 1 or > 120)
            throw new InvalidDataException(UiText.Get("InvalidLocalLimits"));
        if (UsesLocalSummaries)
            SummaryProviders.LocalEndpoint(OllamaEndpoint);
    }
}
