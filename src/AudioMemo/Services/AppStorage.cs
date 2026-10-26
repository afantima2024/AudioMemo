using System.IO;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

public sealed class AppStorage
{
    internal static object TranscriptSync { get; } = new();
    public const string EditStagingPrefix = ".edit-";
    public const string ImportStagingPrefix = ".import-";
    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioMemo");
    public static string ModelsFolder => Path.Combine(DataFolder, "Models");
    public static string WorkFolder => Path.Combine(DataFolder, "Work");
    private readonly string settingsPath;
    internal MessageHistoryStore Messages { get; }

    public AppStorage() : this(DataFolder)
    {
        Directory.CreateDirectory(ModelsFolder);
        Directory.CreateDirectory(WorkFolder);
    }

    internal AppStorage(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        settingsPath = Path.Combine(dataFolder, "settings.json");
        Messages = new MessageHistoryStore(Path.Combine(dataFolder, "Messages"));
    }

    public AppSettings LoadSettings()
    {
        if (!File.Exists(settingsPath))
            return new AppSettings();
        var json = AtomicFile.ReadText(settingsPath);
        var settings = JsonSerializer.Deserialize<AppSettings>(json)
            ?? throw new InvalidDataException("AudioMemo settings are empty.");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(nameof(AppSettings.SetupCompleted), out _))
            settings.SetupCompleted = true;
        settings.Validate();
        return settings;
    }

    public void SaveSettings(AppSettings settings)
    {
        settings.Validate();
        AtomicFile.WriteJson(settingsPath, settings);
    }

    public static RecordingSession CreateSession(AppSettings settings, string title)
    {
        settings.Validate();
        var session = new RecordingSession
        {
            RequestedFormat = settings.Format,
            AudioOutput = settings.AudioOutput.Copy()
        };
        session.Title = string.IsNullOrWhiteSpace(title)
            ? session.CreatedAt.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            : title.Trim();
        session.Folder = Path.Combine(settings.SaveFolder, $"{session.CreatedAt:yyyy-MM-dd_HH-mm-ss}_{session.Id[..8]}");
        Directory.CreateDirectory(session.Folder);
        session.Save();
        AtomicFile.WriteText(session.NotesPath, "");
        return session;
    }

    internal static RecordingSession PrepareContinuation(RecordingSession selected)
    {
        var session = JsonSerializer.Deserialize<RecordingSession>(AtomicFile.ReadText(selected.MetadataPath))
            ?? throw new InvalidDataException(UiText.Get("ContinueSessionChanged"));
        session.Folder = selected.Folder;
        if (session.Id != selected.Id || session.Frames != selected.Frames)
            throw new InvalidOperationException(UiText.Get("ContinueSessionChanged"));
        if (session.Status is not ("Saved" or "Recovered"))
            throw new InvalidOperationException(UiText.Get("RecoverFirst"));
        if (session.AudioOutput is null || session.Continuations is null || session.Devices is null)
            throw new InvalidDataException(UiText.Get("ContinueSessionChanged"));
        if (session.RequestedFormat is not ("WAV" or "MP3"))
            throw new InvalidDataException(UiText.Get("InvalidAudioFormat"));
        session.AudioOutput.Validate();
        long previousStart = 0;
        foreach (var continuation in session.Continuations)
        {
            if (continuation is null || continuation.StartFrame < previousStart || continuation.StartFrame > session.Frames ||
                continuation.TranscriptFrames < 0 || continuation.TranscriptFrames > continuation.StartFrame)
                throw new InvalidDataException(UiText.Get("ContinueSessionChanged"));
            previousStart = continuation.StartFrame;
        }
        var transcript = LoadTranscript(session);
        if (transcript.ProcessedFrames < 0 || transcript.ProcessedFrames > session.Frames)
            throw new InvalidDataException(UiText.Get("ContinueNeedsRecovery"));
        session.Continuations.Add(new(session.Frames, transcript.ProcessedFrames, DateTimeOffset.Now));
        return session;
    }

    public static (List<RecordingSession> Sessions, List<string> Errors) LoadSessions(string root)
    {
        var sessions = new List<RecordingSession>();
        var errors = new List<string>();
        if (!Directory.Exists(root))
            return (sessions, errors);
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(folder);
            if (new[] { EditStagingPrefix, ImportStagingPrefix }.Any(prefix =>
                name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _)))
                continue;
            var metadata = Path.Combine(folder, "session.json");
            try
            {
                AudioEditTransaction.Recover(folder);
                if (!File.Exists(metadata))
                    continue;
                var session = JsonSerializer.Deserialize<RecordingSession>(AtomicFile.ReadText(metadata))
                    ?? throw new InvalidDataException("Empty session metadata.");
                session.Folder = folder;
                if (session.AudioOutput is null)
                    throw new InvalidDataException(UiText.Get("InvalidAudioOutput"));
                session.AudioOutput.Validate();
                sessions.Add(session);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                errors.Add($"{Path.GetFileName(folder)}: {ex.Message}");
            }
        }
        return (sessions.OrderByDescending(s => s.CreatedAt).ToList(), errors);
    }

    public static TranscriptDocument LoadTranscript(RecordingSession session)
    {
        var transcript = File.Exists(session.TranscriptPath)
            ? JsonSerializer.Deserialize<TranscriptDocument>(AtomicFile.ReadText(session.TranscriptPath))
                ?? throw new InvalidDataException("Empty transcript file.")
            : new TranscriptDocument();
        transcript.ValidateSpeakerColors();
        transcript.Me?.Validate();
        if (transcript.Me is { } me && !transcript.Segments.Any(segment => segment.SpeakerId == me.SpeakerId))
            throw new InvalidDataException(UiText.Get("InvalidMeTag"));
        return transcript;
    }

    public static string LoadSummaryText(RecordingSession session)
    {
        string text;
        if (LoadSummaryDocument(session) is { } summary)
        {
            if (summary.Text is null)
                throw new InvalidDataException("Empty summary.");
            text = summary.ToText(UiText.Current);
            var transcript = LoadTranscript(session);
            if (summary.TranscriptRevision == transcript.Revision)
                return TranscriptPresentation.RenderSpeakerReferences(ChineseText.ToSimplified(text), transcript);
            // Preserve old speaker names until a summary for the new transcript succeeds.
            return ChineseText.ToSimplified(File.Exists(session.SummaryPath) ? AtomicFile.ReadText(session.SummaryPath) : text);
        }
        else
            text = File.Exists(session.SummaryPath) ? AtomicFile.ReadText(session.SummaryPath) : "";
        return TranscriptPresentation.RenderSpeakerReferences(ChineseText.ToSimplified(text), LoadTranscript(session));
    }

    public static bool SummaryNeedsRefresh(RecordingSession session, TranscriptDocument transcript)
    {
        var summary = LoadSummaryDocument(session);
        if (summary is null)
            return false;
        if (!summary.MatchesSourcePrefix(transcript))
            return true;
        if (summary.SourceFingerprint.Length > 0)
            return summary.SourceSegments != transcript.Segments.Count || summary.SourceFrames != transcript.ProcessedFrames;
        if (session.Continuations.Count == 0)
            return false;
        return summary.SummarizedSegments < transcript.Segments.Count || transcript.ProcessedFrames < session.Frames;
    }

    public static SummaryDocument? LoadSummaryDocument(RecordingSession session)
    {
        var path = Path.Combine(session.Folder, "summary.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SummaryDocument>(AtomicFile.ReadText(path))
            ?? throw new InvalidDataException("Empty summary state.") : null;
    }

    public static bool HasCurrentSummary(RecordingSession session, TranscriptDocument transcript) =>
        LoadSummaryDocument(session) is { HasOverallSummary: true, SegmentOffset: 0 } summary &&
        !string.IsNullOrWhiteSpace(summary.Text) && !transcript.IsDraft &&
        transcript.ProcessedFrames == session.Frames && summary.SummarizedSegments == transcript.Segments.Count &&
        !SummaryNeedsRefresh(session, transcript);

    public static void RenameSpeaker(RecordingSession session, string id, string name)
    {
        var transcript = LoadTranscript(session);
        var summary = LoadSummaryDocument(session);
        var legacySource = summary is { SourceFingerprint.Length: 0 } && summary.TranscriptRevision == transcript.Revision
            ? SummaryInput.Fingerprint(transcript) : null;
        transcript.RenameSpeaker(id, name);
        if (summary is not null && legacySource is not null && legacySource != SummaryInput.Fingerprint(transcript))
        {
            // Preserve the pre-edit source for legacy summaries without changing their generation time.
            summary.SourceFingerprint = legacySource;
            summary.SourceSegments = transcript.Segments.Count;
            summary.SourceFrames = transcript.ProcessedFrames;
            AtomicFile.WriteJson(Path.Combine(session.Folder, "summary.json"), summary);
        }
        SaveTranscript(session, transcript);
        if (File.Exists(Path.Combine(session.Folder, "summary.json")))
            AtomicFile.WriteText(session.SummaryPath, LoadSummaryText(session));
    }

    public static void SetSpeakerColor(RecordingSession session, string? id, string? color, string? expectedRevision = null)
    {
        lock (TranscriptSync)
        {
            var transcript = LoadTranscript(session);
            if (id is not null && expectedRevision is not null && transcript.Revision != expectedRevision)
                throw new InvalidOperationException(UiText.Get("SpeakerColorTargetChanged"));
            transcript.SetSpeakerColor(id, color);
            AtomicFile.WriteJson(session.TranscriptPath, transcript);
        }
    }

    internal static void SaveAiTranscript(RecordingSession session, TranscriptDocument transcript, bool newSpeakerSet = false)
    {
        lock (TranscriptSync)
        {
            if (File.Exists(session.TranscriptPath))
            {
                var current = LoadTranscript(session);
                if (!newSpeakerSet)
                    transcript.CopySpeakerColorsFrom(current);
                else
                    // Reanalysis can reuse an ID for a different voice.
                    transcript.UnlabeledSpeakerColor = current.UnlabeledSpeakerColor;
            }
            SaveTranscript(session, transcript);
        }
    }

    internal static void RefreshTranscriptColors(RecordingSession session, TranscriptDocument transcript)
    {
        var current = LoadTranscript(session);
        if (transcript.Revision == current.Revision)
            transcript.CopySpeakerColorsFrom(current);
        else
            transcript.UnlabeledSpeakerColor = current.UnlabeledSpeakerColor;
    }

    public static void SaveTranscript(RecordingSession session, TranscriptDocument transcript)
    {
        lock (TranscriptSync)
        {
            AtomicFile.WriteJson(session.TranscriptPath, transcript);
            AtomicFile.WriteText(Path.Combine(session.Folder, "transcript.txt"), transcript.ToText());
            session.TranscribedFrames = transcript.ProcessedFrames;
            session.Save();
        }
    }
}
