using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Core.Audio;

public sealed record AudioFrameRange(long StartFrame, long EndFrame)
{
    [JsonIgnore] public long Length => EndFrame - StartFrame;
    [JsonIgnore] public string Display => $"{AudioEditPlan.FormatTime(StartFrame)} - {AudioEditPlan.FormatTime(EndFrame)}";
}

public sealed class AudioEditPlan
{
    private readonly AudioFrameRange[] mutedRanges;
    private readonly string sourceFingerprint;
    private readonly TranscriptDocument outputTranscript;
    public long SourceFrames { get; }
    public long EditedFrames => SourceFrames;
    public long MutedFrames { get; }
    public int SelectedRowCount { get; }
    public int RemovedSegmentCount { get; }
    public int RemainingSegmentCount => outputTranscript.Segments.Count;
    public IReadOnlyList<AudioFrameRange> MutedRanges { get; }

    private AudioEditPlan(TranscriptDocument source, long sourceFrames, IReadOnlyList<TranscriptRow> selection)
        : this(source, sourceFrames, SelectedRanges(source, sourceFrames, selection), selection.Distinct().Count(), true, "EditOverlappingRow")
    {
    }

    private AudioEditPlan(TranscriptDocument source, long sourceFrames, IReadOnlyList<AudioFrameRange> ranges,
        int selectedRows, bool requireRemoval, string overlapErrorKey)
    {
        SourceFrames = sourceFrames;
        if (sourceFrames <= 0 || sourceFrames > (long.MaxValue - DurableWaveFile.HeaderSize) / PcmFormat.BlockAlign ||
            source.IsDraft || source.ProcessedFrames != sourceFrames || (requireRemoval && source.Segments.Count == 0))
            throw new InvalidOperationException(UiText.Get("EditNeedsFinalTranscript"));
        SelectedRowCount = selectedRows;
        sourceFingerprint = Fingerprint(source);
        mutedRanges = AudioRangeSet.Normalize(ranges, sourceFrames).ToArray();
        MutedRanges = Array.AsReadOnly(mutedRanges);
        MutedFrames = mutedRanges.Sum(range => range.Length);
        var segments = new List<TranscriptSegment>();
        var removed = 0;
        foreach (var segment in source.Segments)
        {
            var first = ToFrame(segment.StartSeconds);
            var last = ToFrame(segment.EndSeconds);
            if (last < first)
                throw new InvalidDataException(UiText.Get("InvalidEditTimeline"));
            var rangeIndex = AudioRangeSet.FirstEndingAfter(mutedRanges, first);
            var overlaps = rangeIndex < mutedRanges.Length && (last == first
                ? first >= mutedRanges[rangeIndex].StartFrame : mutedRanges[rangeIndex].StartFrame < last);
            if (overlaps)
            {
                if (first < mutedRanges[rangeIndex].StartFrame || last > mutedRanges[rangeIndex].EndFrame)
                    throw new InvalidOperationException(UiText.Get(overlapErrorKey));
                removed++;
                continue;
            }
            segments.Add(segment);
        }
        if (requireRemoval && removed == 0)
            throw new InvalidOperationException(UiText.Get("SelectEditRows"));
        RemovedSegmentCount = removed;
        var speakers = segments.Select(segment => segment.SpeakerId).OfType<string>().ToHashSet();
        outputTranscript = new()
        {
            ProcessedFrames = EditedFrames,
            Model = source.Model,
            Language = source.Language,
            Revision = Guid.NewGuid().ToString("N"),
            SpeakersIdentified = source.SpeakersIdentified,
            SpeakerNames = source.SpeakerNames.Where(pair => speakers.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            SpeakerColors = source.SpeakerColors.Where(pair => speakers.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value),
            UnlabeledSpeakerColor = source.UnlabeledSpeakerColor,
            Me = source.Me is { } me && speakers.Contains(me.SpeakerId) ? me.Copy() : null,
            Segments = segments
        };
    }

    public static AudioEditPlan Create(TranscriptDocument source, long sourceFrames, IReadOnlyList<TranscriptRow> selection) =>
        new(source, sourceFrames, selection);

    public static AudioEditPlan ForMutedRanges(TranscriptDocument source, long sourceFrames, IReadOnlyList<AudioFrameRange> ranges,
        string overlapErrorKey = "EditOverlappingRow") => new(source, sourceFrames, ranges, 0, false, overlapErrorKey);

    public bool Matches(TranscriptDocument source) => sourceFingerprint == Fingerprint(source);

    public TranscriptDocument CreateTranscript() => outputTranscript.Copy();

    public long ToFrame(double seconds) => FrameAt(seconds, SourceFrames);

    public static long FrameAt(double seconds, long sourceFrames)
    {
        var frame = Math.Round(seconds * PcmFormat.SampleRate, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(frame) || frame < 0 || frame > sourceFrames)
            throw new InvalidDataException(UiText.Get("InvalidEditTimeline"));
        return (long)frame;
    }

    private static IReadOnlyList<AudioFrameRange> SelectedRanges(TranscriptDocument source, long sourceFrames,
        IReadOnlyList<TranscriptRow> selection)
    {
        if (selection.Count == 0)
            throw new InvalidOperationException(UiText.Get("SelectEditRows"));
        var available = TranscriptPresentation.Rows(source).ToHashSet();
        if (selection.Any(row => !available.Contains(row)))
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        return selection.Select(row => new AudioFrameRange(FrameAt(row.StartSeconds, sourceFrames),
            FrameAt(row.EndSeconds, sourceFrames))).ToArray();
    }

    public static string FormatTime(long frames)
    {
        var time = PcmFormat.Duration(frames);
        return $"{(long)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    public static string Fingerprint(TranscriptDocument source) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(source)));

    public static string ContentFingerprint(TranscriptDocument source)
    {
        var content = source.Copy();
        content.SpeakerColors.Clear();
        content.UnlabeledSpeakerColor = null;
        return Fingerprint(content);
    }
}
