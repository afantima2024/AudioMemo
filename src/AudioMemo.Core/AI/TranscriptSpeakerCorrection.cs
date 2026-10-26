using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Core.AI;

public static class TranscriptSpeakerCorrection
{
    public static TranscriptDocument Apply(TranscriptDocument document, TranscriptCorrectionPlan selection,
        string targetId, bool wholeSpeaker)
    {
        if (!selection.Matches(document))
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        if (!document.Segments.Any(segment => segment.SpeakerId == targetId) || string.IsNullOrWhiteSpace(targetId))
            throw new InvalidOperationException(UiText.Get("ChooseLabeledSpeaker"));
        var sourceId = selection.Segments[0].SpeakerId;
        var indices = wholeSpeaker
            ? document.Segments.Select((segment, index) => (segment, index)).Where(item => item.segment.SpeakerId == sourceId)
                .Select(item => item.index).ToArray()
            : selection.Indices;
        var result = document.Copy();
        var changed = false;
        foreach (var index in indices)
        {
            if (result.Segments[index].SpeakerId == targetId)
                continue;
            result.Segments[index] = result.Segments[index] with { SpeakerId = targetId };
            changed = true;
        }
        if (!changed)
            throw new InvalidOperationException(UiText.Get("SpeakerAlreadyAssigned"));
        var remaining = result.Segments.Select(segment => segment.SpeakerId).OfType<string>().ToHashSet();
        result.SpeakerNames = result.SpeakerNames.Where(pair => remaining.Contains(pair.Key)).ToDictionary();
        result.SpeakerColors = result.SpeakerColors.Where(pair => remaining.Contains(pair.Key)).ToDictionary();
        if (result.Me is { } me && !remaining.Contains(me.SpeakerId))
            result.Me = me with { SpeakerId = targetId, TagId = Guid.NewGuid().ToString("N") };
        result.Revision = Guid.NewGuid().ToString("N");
        return result;
    }
}
