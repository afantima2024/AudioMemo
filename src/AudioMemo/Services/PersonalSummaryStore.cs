using System.IO;
using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

internal sealed class PersonalSummaryDocument
{
    public int PolicyVersion { get; set; } = PersonalSummaryPrompt.PolicyVersion;
    public string GenerationId { get; set; } = "";
    public string TagId { get; set; } = "";
    public string SourceFingerprint { get; set; } = "";
    public int SourceSegments { get; set; }
    public int SummarizedSegments { get; set; }
    public int SegmentOffset { get; set; }
    public string Text { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public bool Completed { get; set; }
    public DateTimeOffset? GeneratedAt { get; set; }
    public string NotesSection { get; set; } = "";
    public string AppliedSection { get; set; } = "";
    public string AppliedGenerationId { get; set; } = "";
    public string AppliedProvider { get; set; } = "";
    public string AppliedModel { get; set; } = "";

    public bool Matches(TranscriptDocument transcript, AppSettings settings) =>
        PolicyVersion == PersonalSummaryPrompt.PolicyVersion && TagId == transcript.Me?.TagId &&
        SourceSegments == transcript.Segments.Count && SourceFingerprint == PersonalSummaryPrompt.Fingerprint(transcript) &&
        Provider == settings.SummaryProvider && Model == settings.ActiveSummaryModel &&
        Endpoint == settings.ActiveSummaryEndpoint;

    public void Validate()
    {
        if (PolicyVersion < 1 || Text is null || NotesSection is null || AppliedSection is null ||
            Provider is null || Model is null || AppliedProvider is null || AppliedModel is null || Endpoint is null || SourceFingerprint is null ||
            Text.Length > SummaryPrompt.MaximumPreviousSummaryCharacters || NotesSection.Length > 100_000 || AppliedSection.Length > 100_000 ||
            SourceSegments < 0 || SummarizedSegments < 0 || SummarizedSegments > SourceSegments || SegmentOffset < 0 ||
            AppliedGenerationId is null || AppliedGenerationId.Length > 0 && !Guid.TryParseExact(AppliedGenerationId, "N", out _) ||
            GenerationId is null || GenerationId.Length > 0 && !Guid.TryParseExact(GenerationId, "N", out _) ||
            TagId is null || TagId.Length > 0 && !Guid.TryParseExact(TagId, "N", out _) ||
            (Text.Length > 0 || Completed) && (GenerationId.Length == 0 || TagId.Length == 0 || SourceFingerprint.Length == 0) ||
            Completed && (Text.Length == 0 || NotesSection.Length == 0 || GeneratedAt is null ||
                SummarizedSegments != SourceSegments || SegmentOffset != 0))
            throw new InvalidDataException(UiText.Get("InvalidAboutMeSummary"));
    }
}

internal static class PersonalSummaryStore
{
    public const string FileName = "about-me.json";
    public static string PathFor(RecordingSession session) => Path.Combine(session.Folder, FileName);

    public static PersonalSummaryDocument? Load(RecordingSession session)
    {
        var path = PathFor(session);
        if (!File.Exists(path))
            return null;
        var result = JsonSerializer.Deserialize<PersonalSummaryDocument>(AtomicFile.ReadText(path))
            ?? throw new InvalidDataException(UiText.Get("InvalidAboutMeSummary"));
        result.Validate();
        return result;
    }

    public static void Save(RecordingSession session, PersonalSummaryDocument state, CancellationToken token = default)
    {
        state.Validate();
        AtomicFile.WriteJson(PathFor(session), state, token);
    }

    public static (string Provider, string Model) AppliedAttribution(PersonalSummaryDocument? state, string notes)
    {
        if (state is null)
            return ("", "");
        if (state.Completed && (state.AppliedGenerationId == state.GenerationId ||
            PersonalNotesSection.ContainsOnce(notes, state.NotesSection)))
            return (state.Provider, state.Model);
        return (state.AppliedProvider, state.AppliedModel);
    }

    public static (string Section, string Provider, string Model) NotesTracking(RecordingSession session, PersonalSummaryDocument? state)
    {
        if (state is null)
            return ("", "", "");
        var notes = File.Exists(session.NotesPath) ? AtomicFile.ReadText(session.NotesPath) : "";
        var section = state.NotesSection.Length > 0 && PersonalNotesSection.ContainsOnce(notes, state.NotesSection)
            ? state.NotesSection : state.AppliedSection.Length > 0 ? state.AppliedSection : state.NotesSection;
        var (provider, model) = AppliedAttribution(state, notes);
        return (section, provider, model);
    }

    public static void CopyNotesTracking(RecordingSession original, RecordingSession copy)
    {
        var (section, provider, model) = NotesTracking(original, Load(original));
        if (section.Length > 0)
            Save(copy, new() { AppliedSection = section, AppliedProvider = provider, AppliedModel = model });
    }
}
