using AudioMemo.Core.Models;

namespace AudioMemo.Core.AI;

public sealed record SpeakerTurn(double Start, double End, int Speaker);

public static class SpeakerAssignment
{
    public static void Apply(TranscriptDocument transcript, IReadOnlyList<SpeakerTurn> turns, CancellationToken cancellationToken = default)
    {
        if (turns.Count == 0 || turns.Any(turn => !double.IsFinite(turn.Start) || !double.IsFinite(turn.End) ||
            turn.Start < 0 || turn.End <= turn.Start || turn.Speaker < 0))
            throw new InvalidDataException("Invalid speaker-analysis timestamps.");
        var ids = turns.OrderBy(turn => turn.Start).Select(turn => turn.Speaker).Distinct()
            .Select((speaker, index) => (speaker, id: "spkr" + (index + 1)))
            .ToDictionary(item => item.speaker, item => item.id);
        var ordered = turns.OrderBy(turn => turn.Start).ToArray();
        var assigned = new List<TranscriptSegment>(transcript.Segments.Count);
        var first = 0;
        foreach (var segment in transcript.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (first < ordered.Length && ordered[first].End <= segment.StartSeconds)
                first++;
            var overlaps = ordered.Skip(first).TakeWhile(turn => turn.Start < segment.EndSeconds).GroupBy(turn => turn.Speaker)
                .Select(group => (Speaker: group.Key, Duration: group.Sum(turn =>
                    Math.Max(0, Math.Min(segment.EndSeconds, turn.End) - Math.Max(segment.StartSeconds, turn.Start)))))
                .OrderByDescending(item => item.Duration).ToArray();
            // A short or overlapping turn is not enough evidence to invent a speaker.
            var best = overlaps.FirstOrDefault();
            var duration = segment.EndSeconds - segment.StartSeconds;
            var reliable = best.Duration > 0 && best.Duration >= duration * 0.55 &&
                (overlaps.Length < 2 || best.Duration > overlaps[1].Duration * 1.2);
            assigned.Add(segment with { SpeakerId = reliable ? ids[best.Speaker] : null });
        }
        if (!assigned.Any(segment => segment.SpeakerId is not null))
            throw new InvalidOperationException("No confident speaker-to-text alignment was found. The transcript is unchanged.");
        transcript.Segments = assigned;
        transcript.SpeakerNames.Clear();
        transcript.SpeakerColors.Clear();
        transcript.Me = null;
        transcript.SpeakersIdentified = true;
        transcript.Revision = Guid.NewGuid().ToString("N");
    }
}
