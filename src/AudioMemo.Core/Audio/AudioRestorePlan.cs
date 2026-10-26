using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Core.Audio;

public sealed class AudioRestorePlan
{
    private readonly string currentFingerprint;
    private readonly string originalFingerprint;
    private readonly TranscriptDocument transcript;
    public long SourceFrames { get; }
    public long RestoredFrames { get; }
    public IReadOnlyList<AudioFrameRange> RestoredRanges { get; }
    public IReadOnlyList<AudioFrameRange> RemainingMutedRanges { get; }
    public bool RebuildsTranscript { get; }

    public AudioRestorePlan(TranscriptDocument current, TranscriptDocument original, long frames,
        IReadOnlyList<AudioFrameRange> mutedRanges, IReadOnlyList<AudioFrameRange> selection, bool allowEmptySelection = false)
    {
        if (current.IsDraft || original.IsDraft || current.ProcessedFrames != frames || original.ProcessedFrames != frames)
            throw new InvalidOperationException(UiText.Get("EditNeedsFinalTranscript"));
        if (selection.Count == 0 && !allowEmptySelection)
            throw new InvalidOperationException(UiText.Get("SelectRestoreRanges"));
        var available = AudioRangeSet.Normalize(mutedRanges, frames);
        if (selection.Any(range => AudioRangeSet.Subtract([range], available, frames).Count != 0))
            throw new InvalidOperationException(UiText.Get("RestoreHistoryChanged"));
        SourceFrames = frames;
        currentFingerprint = AudioEditPlan.Fingerprint(current);
        originalFingerprint = AudioEditPlan.Fingerprint(original);
        RestoredRanges = AudioRangeSet.Normalize(selection, frames);
        RestoredFrames = RestoredRanges.Sum(range => range.Length);
        RemainingMutedRanges = AudioRangeSet.Subtract(available, RestoredRanges, frames);
        if (available.Count == 0)
        {
            transcript = AudioEditPlan.ForMutedRanges(current, frames, []).CreateTranscript();
            return;
        }
        var originalResult = AudioEditPlan.ForMutedRanges(original, frames, RemainingMutedRanges, "RestoreOverlapSelection").CreateTranscript();
        var originalAnchors = original.Segments.Select(segment =>
            (segment.StartSeconds, segment.EndSeconds, segment.SpeakerId)).ToHashSet();
        var inserted = new List<TranscriptSegment>();
        var canMerge = current.Model == original.Model && current.Language == original.Language;
        foreach (var segment in current.Segments)
        {
            var (first, last) = Frames(segment);
            if (AudioRangeSet.Overlaps(available, first, last) ||
                (segment.SpeakerId is not null &&
                 !originalAnchors.Contains((segment.StartSeconds, segment.EndSeconds, segment.SpeakerId))))
                canMerge = false;
        }
        foreach (var segment in original.Segments)
        {
            var (first, last) = Frames(segment);
            if (!AudioRangeSet.Overlaps(RestoredRanges, first, last))
                continue;
            var range = RestoredRanges[AudioRangeSet.FirstEndingAfter(RestoredRanges, first)];
            if (first < range.StartFrame || last > range.EndFrame)
                canMerge = false;
            inserted.Add(segment);
        }
        RebuildsTranscript = !canMerge;
        transcript = canMerge ? new()
        {
            Model = current.Model,
            Language = current.Language,
            ProcessedFrames = frames,
            Revision = Guid.NewGuid().ToString("N"),
            SpeakersIdentified = current.SpeakersIdentified && original.SpeakersIdentified,
            Segments = current.Segments.Concat(inserted).OrderBy(segment => segment.StartSeconds)
                .ThenBy(segment => segment.EndSeconds).ToList()
        } : originalResult;
        var ids = transcript.Segments.Select(segment => segment.SpeakerId).OfType<string>().ToHashSet();
        transcript.Me = current.Me is { } me && ids.Contains(me.SpeakerId) &&
            (canMerge || current.Segments.Where(segment => segment.SpeakerId == me.SpeakerId).All(segment =>
                originalAnchors.Contains((segment.StartSeconds, segment.EndSeconds, segment.SpeakerId))))
            ? me.Copy() : null;
        transcript.SpeakerNames = original.SpeakerNames.Where(pair => ids.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        transcript.SpeakerColors = original.SpeakerColors.Where(pair => ids.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        transcript.UnlabeledSpeakerColor = current.UnlabeledSpeakerColor;
        foreach (var id in current.Segments.Select(segment => segment.SpeakerId).OfType<string>().Distinct().Where(ids.Contains))
        {
            var segments = current.Segments.Where(segment => segment.SpeakerId == id).ToArray();
            if (canMerge || (segments.Length > 0 && segments.All(segment =>
                originalAnchors.Contains((segment.StartSeconds, segment.EndSeconds, segment.SpeakerId)))))
            {
                if (current.SpeakerNames.TryGetValue(id, out var name))
                    transcript.SpeakerNames[id] = name;
                if (current.SpeakerColors.TryGetValue(id, out var color))
                    transcript.SpeakerColors[id] = color;
                else
                    transcript.SpeakerColors.Remove(id);
            }
        }

        (long First, long Last) Frames(TranscriptSegment segment)
        {
            var first = AudioEditPlan.FrameAt(segment.StartSeconds, frames);
            var last = AudioEditPlan.FrameAt(segment.EndSeconds, frames);
            if (last < first)
                throw new InvalidDataException(UiText.Get("InvalidEditTimeline"));
            return (first, last);
        }
    }

    public bool MatchesCurrent(TranscriptDocument value) => currentFingerprint == AudioEditPlan.Fingerprint(value);
    public bool MatchesOriginal(TranscriptDocument value) => originalFingerprint == AudioEditPlan.Fingerprint(value);
    public TranscriptDocument CreateTranscript() => transcript.Copy();
}
