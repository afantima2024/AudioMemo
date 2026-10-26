using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

[JsonConverter(typeof(JsonStringEnumConverter<ProcessingWorkKind>))]
internal enum ProcessingWorkKind { Transcribe, MainPoints, Speakers, ExportAudio, ExportMp3, AboutMe, UnlabeledSpeakers, TranscriptReview }

internal sealed record ProcessingOptions(
    string SpeechModel, string Language, string SpeechAcceleration, bool SpeechVocabularyEnabled,
    List<string> SpeechVocabularyTerms, bool IdentifySpeakersOnStop, int SpeakerCount, bool SummarizeOnStop,
    string SummaryProvider, string SummaryModel, string SummaryEndpoint)
{
    public bool ReviewUnclearTranscript { get; init; } = true;

    public static ProcessingOptions Capture(AppSettings settings) => new(
        settings.SpeechModel, settings.Language, settings.SpeechAcceleration, settings.SpeechVocabularyEnabled,
        [.. settings.SpeechVocabularyTerms], settings.IdentifySpeakersOnStop, settings.SpeakerCount,
        settings.SummarizeOnStop && settings.CanSummarize, settings.SummaryProvider,
        settings.ActiveSummaryModel, settings.ActiveSummaryEndpoint)
    { ReviewUnclearTranscript = settings.ReviewUnclearTranscript };

    public AppSettings Restore(AppSettings current)
    {
        if (SpeechVocabularyTerms is null)
            throw new InvalidDataException(UiText.Get("InvalidPendingProcessing"));
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current))
            ?? throw new InvalidDataException(UiText.Get("InvalidPendingProcessing"));
        settings.SpeechModel = SpeechModel;
        settings.Language = Language;
        settings.SpeechAcceleration = SpeechAcceleration;
        settings.SpeechVocabularyEnabled = SpeechVocabularyEnabled;
        settings.SpeechVocabularyTerms = [.. SpeechVocabularyTerms];
        settings.IdentifySpeakersOnStop = IdentifySpeakersOnStop;
        settings.SpeakerCount = SpeakerCount;
        settings.SummarizeOnStop = SummarizeOnStop;
        settings.ReviewUnclearTranscript = ReviewUnclearTranscript;
        settings.SummaryProvider = SummaryProvider;
        if (settings.UsesLocalSummaries)
        {
            settings.OllamaModel = SummaryModel;
            settings.OllamaEndpoint = SummaryEndpoint;
        }
        else if (settings.UsesCloudApi)
        {
            settings.GetCloudProfile().Model = SummaryModel;
            settings.GetCloudProfile().Endpoint = SummaryEndpoint;
        }
        else
            settings.SummaryModel = SummaryModel;
        // Credentials and sharing consent always come from current settings, never the queue.
        settings.Validate();
        return settings;
    }
}

internal sealed record ProcessingWork(ProcessingWorkKind Kind, ProcessingOptions? Options = null,
    string? SummaryGenerationId = null, string? MeTagId = null)
{
    [JsonIgnore]
    public string LabelKey => Kind switch
    {
        ProcessingWorkKind.Transcribe => "TranscribeResume",
        ProcessingWorkKind.MainPoints => "UpdateMainPoints",
        ProcessingWorkKind.Speakers => "IdentifySpeakers",
        ProcessingWorkKind.UnlabeledSpeakers => "RecheckUnlabeledSpeakers",
        ProcessingWorkKind.TranscriptReview => "ReviewUnclearTranscript",
        ProcessingWorkKind.ExportAudio => "ExportConfiguredAudio",
        ProcessingWorkKind.ExportMp3 => "ExportMp3",
        ProcessingWorkKind.AboutMe => "SummarizeAboutMe",
        _ => throw new InvalidDataException(UiText.Get("InvalidPendingProcessing"))
    };

    public static ProcessingWork ForAi(string operationKey, AppSettings settings, string? summaryGenerationId = null,
        string? meTagId = null) => new(operationKey switch
    {
        "TranscribeResume" => ProcessingWorkKind.Transcribe,
        "UpdateMainPoints" => ProcessingWorkKind.MainPoints,
        "IdentifySpeakers" => ProcessingWorkKind.Speakers,
        "RecheckUnlabeledSpeakers" => ProcessingWorkKind.UnlabeledSpeakers,
        "ReviewUnclearTranscript" => ProcessingWorkKind.TranscriptReview,
        "SummarizeAboutMe" => ProcessingWorkKind.AboutMe,
        _ => throw new InvalidOperationException(UiText.Get("InvalidPendingProcessing"))
    }, ProcessingOptions.Capture(settings), summaryGenerationId, meTagId);
}

internal sealed record PendingProcessingPlan(string SessionId, long Frames, AudioSourceVersion AudioVersion,
    List<ProcessingWork> Work, bool PromptOnStartup = true);

internal static class PendingProcessingStore
{
    public const string FileName = "pending-processing.json";
    public static string PathFor(RecordingSession session) => Path.Combine(session.Folder, FileName);

    public static PendingProcessingPlan? Load(RecordingSession session)
    {
        var path = PathFor(session);
        if (!File.Exists(path))
            return null;
        var plan = JsonSerializer.Deserialize<PendingProcessingPlan>(AtomicFile.ReadText(path))
            ?? throw new InvalidDataException(UiText.Get("InvalidPendingProcessing"));
        Validate(session, plan);
        return plan;
    }

    public static void VerifyAudio(RecordingSession session, PendingProcessingPlan plan)
    {
        Validate(session, plan);
        if (plan.Frames != session.Frames || plan.AudioVersion != AudioSourceVersion.Read(session.WavePath))
            throw new InvalidOperationException(UiText.Get("PendingProcessingAudioChanged"));
    }

    public static void Defer(RecordingSession session, IEnumerable<ProcessingWork> work)
    {
        var existing = Load(session);
        if (existing is not null)
            VerifyAudio(session, existing);
        var items = (existing?.Work ?? []).Concat(work).GroupBy(item => item.Kind)
            .Select(group => group.Last()).OrderBy(item => item.Kind switch
            {
                ProcessingWorkKind.ExportAudio or ProcessingWorkKind.ExportMp3 => 0,
                ProcessingWorkKind.Transcribe => 1,
                ProcessingWorkKind.Speakers or ProcessingWorkKind.UnlabeledSpeakers => 2,
                ProcessingWorkKind.TranscriptReview => 3,
                _ => 4
            })
            .ToList();
        var plan = new PendingProcessingPlan(session.Id, session.Frames, AudioSourceVersion.Read(session.WavePath), items);
        Save(session, plan);
    }

    public static void Save(RecordingSession session, PendingProcessingPlan plan)
    {
        VerifyAudio(session, plan);
        AtomicFile.WriteJson(PathFor(session), plan);
    }

    public static void SetPromptOnStartup(RecordingSession session, PendingProcessingPlan plan, bool prompt)
    {
        Validate(session, plan);
        AtomicFile.WriteJson(PathFor(session), plan with { PromptOnStartup = prompt });
    }

    public static void Complete(RecordingSession session, ProcessingWorkKind kind)
    {
        var plan = Load(session);
        if (plan is null)
            return;
        VerifyAudio(session, plan);
        var remaining = plan.Work.Where(item => item.Kind != kind).ToList();
        if (remaining.Count == plan.Work.Count)
            return;
        if (remaining.Count == 0)
            Clear(session);
        else
            Save(session, plan with { Work = remaining });
    }

    public static void Clear(RecordingSession session) => File.Delete(PathFor(session));

    private static void Validate(RecordingSession session, PendingProcessingPlan plan)
    {
        if (plan.SessionId != session.Id || plan.Frames <= 0 ||
            plan.Frames > (long.MaxValue - DurableWaveFile.HeaderSize) / PcmFormat.BlockAlign ||
            plan.AudioVersion is null ||
            plan.AudioVersion.Length != DurableWaveFile.HeaderSize + plan.Frames * PcmFormat.BlockAlign ||
            plan.Work is null || plan.Work.Count is < 1 or > 8 ||
            plan.Work.Any(item => item is null || !Enum.IsDefined(item.Kind)) ||
            plan.Work.Select(item => item.Kind).Distinct().Count() != plan.Work.Count)
            throw new InvalidDataException(UiText.Get("InvalidPendingProcessing"));
        foreach (var item in plan.Work)
        {
            if (item.SummaryGenerationId is not null &&
                (item.Kind is not (ProcessingWorkKind.MainPoints or ProcessingWorkKind.AboutMe) ||
                    !Guid.TryParseExact(item.SummaryGenerationId, "N", out _)))
                throw new InvalidDataException(UiText.Get("InvalidPendingProcessing"));
            if (item.Kind == ProcessingWorkKind.AboutMe
                ? item.SummaryGenerationId is null || !Guid.TryParseExact(item.MeTagId, "N", out _)
                : item.MeTagId is not null)
                throw new InvalidDataException(UiText.Get("InvalidPendingProcessing"));
            if (item.Kind is ProcessingWorkKind.ExportAudio or ProcessingWorkKind.ExportMp3)
            {
                if (item.Options is not null)
                    throw new InvalidDataException(UiText.Get("InvalidPendingProcessing"));
            }
            else
            {
                if (item.Options is null)
                    throw new InvalidDataException(UiText.Get("InvalidPendingProcessing"));
                item.Options.Restore(new AppSettings());
            }
        }
    }
}
