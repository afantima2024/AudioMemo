using System.Text.Json.Serialization;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Storage;

namespace AudioMemo.Core.Models;

public sealed class RecordingSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled recording";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string RequestedFormat { get; set; } = "WAV";
    public AudioOutputSettings AudioOutput { get; set; } = new();
    public string Status { get; set; } = "Recording";
    public long Frames { get; set; }
    public long TranscribedFrames { get; set; }
    public string? LastError { get; set; }
    public string? ImportedFileName { get; set; }
    public string? TranscriptionModel { get; set; }
    public string? TranscriptionLanguage { get; set; }
    public List<DeviceChange> Devices { get; set; } = [];
    public List<RecordingContinuation> Continuations { get; set; } = [];
    public RecordingEdit? Edit { get; set; }
    [JsonIgnore] public string Folder { get; set; } = "";
    [JsonIgnore] public string WavePath => Path.Combine(Folder, "audio.wav");
    [JsonIgnore] public string Mp3Path => Path.Combine(Folder, "audio.mp3");
    [JsonIgnore] public string OutputPath => RequestedFormat == "MP3" ? Mp3Path :
        AudioOutput.UsesSafetyFormat ? WavePath : Path.Combine(Folder, "audio-output.wav");
    [JsonIgnore] public string MetadataPath => Path.Combine(Folder, "session.json");
    [JsonIgnore] public string TranscriptPath => Path.Combine(Folder, "transcript.json");
    [JsonIgnore] public string SummaryPath => Path.Combine(Folder, "summary.md");
    [JsonIgnore] public string NotesPath => Path.Combine(Folder, "notes.md");
    [JsonIgnore] public string Display => $"{CreatedAt:MMM d, HH:mm}  {Title}";
    [JsonIgnore] public TimeSpan Duration => PcmFormat.Duration(Frames);
    [JsonIgnore] public long PreservedTranscriptFrames => Continuations.LastOrDefault()?.TranscriptFrames ?? 0;

    public bool HasAudioPrefix(long frames) => frames >= 0 && frames <= Frames &&
        (frames == Frames || Continuations.Any(continuation => continuation.StartFrame == frames));

    public void Save()
    {
        lock (this)
            AtomicFile.WriteJson(MetadataPath, this);
    }
}

public sealed record DeviceChange(double AtSeconds, string Source, string Device, bool Enabled);
public sealed record RecordingContinuation(long StartFrame, long TranscriptFrames, DateTimeOffset StartedAt);
public sealed record RecordingEdit(string SourceSessionId, string SourceTitle, DateTimeOffset SourceCreatedAt,
    long SourceFrames, List<AudioFrameRange> RemovedRanges)
{
    // Older edited sessions removed time and did not persist a mode.
    public string Mode { get; init; } = "Cut";
    public List<AudioFrameRange> RestoredRanges { get; init; } = [];
}
public sealed record TranscriptSegment(double StartSeconds, double EndSeconds, string Text)
{
    private string text = ChineseText.ToSimplified(Text);

    public string Text
    {
        get => text;
        init => text = ChineseText.ToSimplified(value);
    }
    public string? SpeakerId { get; init; }
    public bool IsUserEdited { get; init; }
}

public sealed class TranscriptDocument
{
    public long ProcessedFrames { get; set; }
    public bool IsDraft { get; set; }
    public string Model { get; set; } = "";
    public string Language { get; set; } = "";
    public string Revision { get; set; } = "";
    public Dictionary<string, string> SpeakerNames { get; set; } = [];
    public Dictionary<string, string> SpeakerColors { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnlabeledSpeakerColor { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SelfSpeaker? Me { get; set; }
    public bool SpeakersIdentified { get; set; }
    public List<TranscriptSegment> Segments { get; set; } = [];

    public TranscriptDocument Copy() => new()
    {
        ProcessedFrames = ProcessedFrames,
        IsDraft = IsDraft,
        Model = Model,
        Language = Language,
        Revision = Revision,
        SpeakerNames = new(SpeakerNames),
        SpeakerColors = new(SpeakerColors),
        UnlabeledSpeakerColor = UnlabeledSpeakerColor,
        Me = Me?.Copy(),
        SpeakersIdentified = SpeakersIdentified,
        Segments = [.. Segments]
    };

    public string ToText() => string.Join(Environment.NewLine, Segments.Select(s =>
        $"[{Timestamp(s.StartSeconds)}] " +
        (s.SpeakerId is null ? "" : SpeakerName(s.SpeakerId) + ": ") + s.Text.Trim()));

    public string SpeakerName(string id) => SpeakerNames.GetValueOrDefault(id, id);

    public void RenameSpeaker(string id, string name)
    {
        name = name.Trim();
        if (!Segments.Any(segment => segment.SpeakerId == id) || name.Length is < 1 or > 80 || name.Any(char.IsControl))
            throw new ArgumentException(UiText.Get("InvalidSpeakerName"));
        SpeakerNames[id] = ChineseText.ToSimplified(name);
    }

    public string? SpeakerColor(string? id) =>
        id is null ? UnlabeledSpeakerColor : SpeakerColors.GetValueOrDefault(id);

    public void SetSpeakerColor(string? id, string? color)
    {
        if (id is not null && (string.IsNullOrWhiteSpace(id) || !Segments.Any(segment => segment.SpeakerId == id)))
            throw new ArgumentException(UiText.Get("ChooseLabeledSpeaker"));
        var normalized = color is null ? null : RgbColor.Parse(color).ToString();
        if (id is null)
            UnlabeledSpeakerColor = normalized;
        else if (normalized is null)
            SpeakerColors.Remove(id);
        else
            SpeakerColors[id] = normalized;
    }

    public void CopySpeakerColorsFrom(TranscriptDocument source)
    {
        source.ValidateSpeakerColors();
        var ids = Segments.Select(segment => segment.SpeakerId).OfType<string>().ToHashSet();
        SpeakerColors = source.SpeakerColors.Where(pair => ids.Contains(pair.Key)).ToDictionary();
        UnlabeledSpeakerColor = source.UnlabeledSpeakerColor;
    }

    public void ValidateSpeakerColors()
    {
        if (SpeakerColors is null)
            throw new InvalidDataException(UiText.Get("InvalidAppearanceColors"));
        foreach (var color in SpeakerColors.Values)
            RgbColor.Parse(color);
        if (UnlabeledSpeakerColor is not null)
            RgbColor.Parse(UnlabeledSpeakerColor);
    }

    public static string Timestamp(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }
}
