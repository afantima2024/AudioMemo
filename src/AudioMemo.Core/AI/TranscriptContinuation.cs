using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Core.AI;

public static class TranscriptContinuation
{
    public static long PreservedFrames(TranscriptDocument transcript, long requested)
    {
        if (requested < 0 || requested > transcript.ProcessedFrames)
            throw new InvalidDataException(UiText.Get("ContinueTranscriptChanged"));
        if (requested == 0)
            return 0;
        var boundary = (double)requested / PcmFormat.SampleRate;
        var crossingEnd = transcript.Segments.Where(segment => segment.StartSeconds < boundary)
            .Select(segment => segment.EndSeconds).DefaultIfEmpty(boundary).Max();
        if (!double.IsFinite(crossingEnd) || crossingEnd * PcmFormat.SampleRate > transcript.ProcessedFrames + 1)
            throw new InvalidDataException(UiText.Get("ContinueTranscriptChanged"));
        return Math.Max(requested, Math.Min(transcript.ProcessedFrames, (long)Math.Ceiling(crossingEnd * PcmFormat.SampleRate)));
    }

    public static TranscriptDocument CreateRefinement(TranscriptDocument source, long preservedFrames, string model, string language)
    {
        var result = source.Copy();
        result.Segments = Prefix(source, preservedFrames).ToList();
        result.ProcessedFrames = preservedFrames;
        result.IsDraft = false;
        result.Model = model;
        result.Language = language;
        result.Revision = Guid.NewGuid().ToString("N");
        result.SpeakersIdentified = preservedFrames > 0 && source.SpeakersIdentified;
        if (preservedFrames == 0)
        {
            result.SpeakerNames.Clear();
            result.SpeakerColors.Clear();
        }
        if (result.Me is { } me && !result.Segments.Any(segment => segment.SpeakerId == me.SpeakerId))
            result.Me = null;
        return result;
    }

    public static bool MatchesPrefix(TranscriptDocument checkpoint, TranscriptDocument source, long preservedFrames) =>
        checkpoint.ProcessedFrames >= preservedFrames &&
        Prefix(checkpoint, preservedFrames).SequenceEqual(Prefix(source, preservedFrames)) &&
        (preservedFrames == 0 ||
            source.SpeakerNames.All(pair => checkpoint.SpeakerNames.GetValueOrDefault(pair.Key) == pair.Value) &&
            (source.Me is null || !Prefix(source, preservedFrames).Any(segment => segment.SpeakerId == source.Me.SpeakerId)
                ? checkpoint.Me is null : source.Me.Matches(checkpoint.Me)));

    private static IEnumerable<TranscriptSegment> Prefix(TranscriptDocument source, long frames) =>
        source.Segments.Where(segment => segment.StartSeconds < (double)frames / PcmFormat.SampleRate);

    public static void ApplySpeakers(TranscriptDocument transcript, TranscriptDocument analyzed, long preservedFrames)
    {
        if (transcript.Segments.Count != analyzed.Segments.Count ||
            transcript.Segments.Where((segment, index) =>
                segment with { SpeakerId = null } != analyzed.Segments[index] with { SpeakerId = null }).Any())
            throw new InvalidDataException(UiText.Get("ContinueTranscriptChanged"));
        var boundary = (double)preservedFrames / PcmFormat.SampleRate;
        var evidence = transcript.Segments.Select((segment, index) => (Old: segment, New: analyzed.Segments[index]))
            .Where(pair => pair.Old.StartSeconds < boundary && pair.Old.SpeakerId is not null && pair.New.SpeakerId is not null)
            .GroupBy(pair => pair.New.SpeakerId!)
            .Select(group => new
            {
                Id = group.Key,
                Matches = group.GroupBy(pair => pair.Old.SpeakerId!)
                    .Select(match => (Id: match.Key, Weight: match.Sum(pair => pair.Old.EndSeconds - pair.Old.StartSeconds)))
                    .OrderByDescending(match => match.Weight).ToArray()
            }).OrderByDescending(group => group.Matches[0].Weight);
        var mapped = new Dictionary<string, string>();
        var usedOld = new HashSet<string>();
        foreach (var group in evidence)
        {
            var best = group.Matches[0];
            if (best.Weight > 0 && best.Weight >= group.Matches.Sum(match => match.Weight) * 0.6 &&
                (group.Matches.Length == 1 || best.Weight > group.Matches[1].Weight * 1.2) && usedOld.Add(best.Id))
                mapped[group.Id] = best.Id;
        }
        var used = transcript.Segments.Select(segment => segment.SpeakerId).OfType<string>()
            .Concat(transcript.SpeakerNames.Keys).Concat(transcript.SpeakerColors.Keys).ToHashSet();
        var next = 1;
        foreach (var id in analyzed.Segments.Select(segment => segment.SpeakerId).OfType<string>().Distinct())
        {
            if (mapped.ContainsKey(id))
                continue;
            while (used.Contains("spkr" + next))
                next++;
            mapped[id] = "spkr" + next++;
            used.Add(mapped[id]);
        }
        transcript.Segments = transcript.Segments.Select((segment, index) => segment.StartSeconds < boundary
            ? segment
            : segment with { SpeakerId = analyzed.Segments[index].SpeakerId is { } id ? mapped[id] : null }).ToList();
        if (transcript.Me is { } me && !transcript.Segments.Any(segment => segment.SpeakerId == me.SpeakerId))
            transcript.Me = null;
        transcript.SpeakersIdentified = analyzed.SpeakersIdentified;
        transcript.Revision = Guid.NewGuid().ToString("N");
    }
}
