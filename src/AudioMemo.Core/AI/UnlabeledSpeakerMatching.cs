using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Core.AI;

public sealed record SpeakerReference(string SpeakerId, AudioFrameRange Range);
public sealed record SpeakerMatchTarget(AudioFrameRange Range, int[] SegmentIndices);
public sealed record SpeakerMatchResult(int TargetIndex, string? SpeakerId, double Similarity);
public sealed record SpeakerMatchPlan(string SourceFingerprint, long Frames,
    SpeakerReference[] References, SpeakerMatchTarget[] Targets)
{
    public void Validate()
    {
        if (Frames <= 0 || Frames > (long.MaxValue - DurableWaveFile.HeaderSize) / PcmFormat.BlockAlign ||
            SourceFingerprint is null || SourceFingerprint.Length != 64 ||
            !SourceFingerprint.All(Uri.IsHexDigit) || References is null || References.Length == 0 ||
            Targets is null || Targets.Length == 0 ||
            References.Any(reference => reference is null || string.IsNullOrWhiteSpace(reference.SpeakerId) ||
                !ValidRange(reference.Range) || reference.Range.Length < UnlabeledSpeakerMatching.MinimumFrames ||
                reference.Range.Length > UnlabeledSpeakerMatching.ReferenceFrames) ||
            Targets.Any(target => target is null || !ValidRange(target.Range) ||
                target.SegmentIndices is null || target.SegmentIndices.Length == 0 ||
                target.SegmentIndices.Any(index => index < 0)) ||
            Targets.SelectMany(target => target.SegmentIndices).Distinct().Count() !=
                Targets.Sum(target => target.SegmentIndices.Length))
            throw new InvalidDataException(UiText.Get("InvalidSpeakerMatches"));

        bool ValidRange(AudioFrameRange? range) =>
            range is not null && range.StartFrame >= 0 && range.EndFrame >= range.StartFrame && range.EndFrame <= Frames;
    }
}

public static class UnlabeledSpeakerMatching
{
    public const long MinimumFrames = PcmFormat.SampleRate;
    public const long ReferenceFrames = 8L * PcmFormat.SampleRate;
    public const long WindowFrames = 4L * PcmFormat.SampleRate;
    public const double MinimumSimilarity = 0.70;
    public const double MinimumMargin = 0.12;
    public const int ReferencesPerSpeaker = 3;

    public static SpeakerMatchPlan Create(TranscriptDocument document, long frames, CancellationToken token = default)
    {
        if (document.IsDraft || document.ProcessedFrames != frames || frames <= 0)
            throw new InvalidOperationException(UiText.Get("FinalTranscriptRequired"));
        var groups = TranscriptPresentation.Groups(document).Select(group => (Group: group,
            Range: new AudioFrameRange(AudioEditPlan.FrameAt(group.Row.StartSeconds, frames),
                AudioEditPlan.FrameAt(group.Row.EndSeconds, frames)))).ToArray();
        if (groups.Any(group => group.Range.Length < 0))
            throw new InvalidDataException(UiText.Get("InvalidEditTimeline"));
        var targets = groups.Where(group => group.Group.Row.SpeakerId is null)
            .Select(group => new SpeakerMatchTarget(group.Range, group.Group.SegmentIndices.ToArray())).ToArray();
        if (targets.Length == 0)
            throw new InvalidOperationException(UiText.Get("NoUnlabeledSpeakers"));
        var references = new List<SpeakerReference>();
        foreach (var speaker in groups.Where(group => group.Group.Row.SpeakerId is not null &&
                     group.Range.Length >= MinimumFrames).GroupBy(group => group.Group.Row.SpeakerId!))
        {
            var accepted = 0;
            foreach (var candidate in speaker.OrderByDescending(group => group.Range.Length))
            {
                token.ThrowIfCancellationRequested();
                var first = candidate.Range.StartFrame + Math.Max(0, (candidate.Range.Length - ReferenceFrames) / 2);
                var range = new AudioFrameRange(first, Math.Min(candidate.Range.EndFrame, first + ReferenceFrames));
                if (groups.Any(other => other.Group.Row.SpeakerId != speaker.Key &&
                    other.Range.StartFrame < range.EndFrame && other.Range.EndFrame > range.StartFrame))
                    continue;
                references.Add(new(speaker.Key, range));
                if (++accepted == ReferencesPerSpeaker)
                    break;
            }
        }
        if (references.Count == 0)
            throw new InvalidOperationException(UiText.Get("SpeakerReferencesRequired"));
        var plan = new SpeakerMatchPlan(AudioEditPlan.ContentFingerprint(document), frames, references.ToArray(), targets);
        plan.Validate();
        return plan;
    }

    public static AudioFrameRange[] Windows(AudioFrameRange range)
    {
        if (range.Length < MinimumFrames)
            return [];
        var count = checked((int)((range.Length + WindowFrames - 1) / WindowFrames));
        var length = range.Length / count;
        var extra = range.Length % count;
        var result = new AudioFrameRange[count];
        var cursor = range.StartFrame;
        for (var index = 0; index < count; index++)
        {
            var end = cursor + length + (index < extra ? 1 : 0);
            result[index] = new(cursor, end);
            cursor = end;
        }
        return result;
    }

    public static SpeakerMatchResult[] Match(SpeakerMatchPlan plan, Func<AudioFrameRange, float[]?> extract,
        CancellationToken token = default, Action<int, int>? progress = null)
    {
        plan.Validate();
        var windows = plan.Targets.Select(target => Windows(target.Range)).ToArray();
        var total = checked(plan.References.Length + windows.Sum(ranges => Math.Max(1, ranges.Length)));
        var completed = 0;
        var references = new List<(string Speaker, float[] Values)>();
        progress?.Invoke(completed, total);
        foreach (var reference in plan.References)
        {
            token.ThrowIfCancellationRequested();
            if (Normalize(extract(reference.Range)) is { } values)
                references.Add((reference.SpeakerId, values));
            progress?.Invoke(++completed, total);
        }
        if (references.Count == 0)
            throw new InvalidOperationException(UiText.Get("SpeakerReferencesNoSpeech"));
        var result = new SpeakerMatchResult[plan.Targets.Length];
        for (var index = 0; index < result.Length; index++)
        {
            string? speaker = null;
            var consistent = windows[index].Length > 0;
            var similarity = 1d;
            foreach (var range in windows[index])
            {
                token.ThrowIfCancellationRequested();
                var values = Normalize(extract(range));
                var scores = values is null ? [] : references.GroupBy(reference => reference.Speaker)
                    .Select(group => (Speaker: group.Key, Score: group.Select(reference => Cosine(values, reference.Values))
                        .OrderDescending().Take(2).Average())).OrderByDescending(score => score.Score).ToArray();
                var best = scores.Length == 0 ? (Speaker: (string?)null, Score: -1d) : scores[0];
                similarity = Math.Min(similarity, best.Score);
                consistent &= best.Score >= MinimumSimilarity &&
                    (scores.Length < 2 || best.Score - scores[1].Score >= MinimumMargin) &&
                    (speaker is null || speaker == best.Speaker);
                speaker ??= best.Speaker;
                progress?.Invoke(++completed, total);
            }
            if (windows[index].Length == 0)
                progress?.Invoke(++completed, total);
            result[index] = new(index, consistent ? speaker : null, windows[index].Length == 0 ? 0 : similarity);
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    public static TranscriptDocument Apply(TranscriptDocument source, SpeakerMatchPlan plan,
        IReadOnlyList<SpeakerMatchResult> matches, CancellationToken token = default)
    {
        plan.Validate();
        if (source.ProcessedFrames != plan.Frames || AudioEditPlan.ContentFingerprint(source) != plan.SourceFingerprint)
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        var speakers = plan.References.Select(reference => reference.SpeakerId).ToHashSet();
        if (matches.Count != plan.Targets.Length ||
            matches.Any(match => match is null || match.TargetIndex < 0 || match.TargetIndex >= plan.Targets.Length ||
                !double.IsFinite(match.Similarity) || match.Similarity is < -1 or > 1 ||
                match.SpeakerId is not null && (!speakers.Contains(match.SpeakerId) || match.Similarity < MinimumSimilarity)) ||
            matches.Select(match => match.TargetIndex).Distinct().Count() != matches.Count ||
            plan.Targets.SelectMany(target => target.SegmentIndices).Any(index =>
                index >= source.Segments.Count || source.Segments[index].SpeakerId is not null))
            throw new InvalidDataException(UiText.Get("InvalidSpeakerMatches"));
        var result = source.Copy();
        var changed = false;
        foreach (var match in matches)
        {
            token.ThrowIfCancellationRequested();
            if (match.SpeakerId is not { } id)
                continue;
            foreach (var index in plan.Targets[match.TargetIndex].SegmentIndices)
                result.Segments[index] = result.Segments[index] with { SpeakerId = id };
            changed = true;
        }
        if (changed)
            result.Revision = Guid.NewGuid().ToString("N");
        return result;
    }

    private static float[]? Normalize(float[]? values)
    {
        if (values is null)
            return null;
        if (values.Length == 0 || values.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException(UiText.Get("InvalidSpeakerMatches"));
        var norm = Math.Sqrt(values.Sum(value => (double)value * value));
        if (norm <= 1e-12)
            return null;
        return values.Select(value => (float)(value / norm)).ToArray();
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length != right.Length)
            throw new InvalidDataException(UiText.Get("InvalidSpeakerMatches"));
        var value = 0d;
        for (var index = 0; index < left.Length; index++)
            value += (double)left[index] * right[index];
        return Math.Clamp(value, -1, 1);
    }
}
