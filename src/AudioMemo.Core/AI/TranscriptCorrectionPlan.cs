using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;

namespace AudioMemo.Core.AI;

public sealed class TranscriptCorrectionPlan
{
    public const int MaximumTextLength = 16000;
    private readonly TranscriptDocument source;
    private readonly string fingerprint;
    public long SourceFrames { get; }
    public IReadOnlyList<int> Indices { get; }
    public IReadOnlyList<TranscriptSegment> Segments { get; }
    public string OriginalText { get; }

    private TranscriptCorrectionPlan(TranscriptDocument document, long frames, IReadOnlyList<TranscriptRow> selection,
        IReadOnlyList<int>? sourceIndices = null)
    {
        if (frames <= 0 || frames > (long.MaxValue - DurableWaveFile.HeaderSize) / PcmFormat.BlockAlign ||
            document.IsDraft || document.ProcessedFrames != frames)
            throw new InvalidOperationException(UiText.Get("CorrectionNeedsFinalTranscript"));
        if (selection.Count == 0 && sourceIndices is not { Count: > 0 })
            throw new InvalidOperationException(UiText.Get("SelectCorrectionRows"));
        var indices = new SortedSet<int>();
        if (sourceIndices is not null)
        {
            if (sourceIndices.Any(index => index < 0 || index >= document.Segments.Count))
                throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
            indices.UnionWith(sourceIndices);
        }
        else
        {
            var groups = TranscriptPresentation.Groups(document).ToLookup(group => group.Row);
            foreach (var row in selection.Distinct())
            {
                var matches = groups[row].ToArray();
                if (matches.Length != 1)
                    throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
                indices.UnionWith(matches[0].SegmentIndices);
            }
        }
        foreach (var index in indices)
        {
            var segment = document.Segments[index];
            if (AudioEditPlan.FrameAt(segment.EndSeconds, frames) < AudioEditPlan.FrameAt(segment.StartSeconds, frames))
                throw new InvalidDataException(UiText.Get("InvalidEditTimeline"));
        }
        SourceFrames = frames;
        source = document.Copy();
        fingerprint = AudioEditPlan.ContentFingerprint(source);
        Indices = Array.AsReadOnly(indices.ToArray());
        Segments = Array.AsReadOnly(indices.Select(index => source.Segments[index]).ToArray());
        OriginalText = sourceIndices is null
            ? TranscriptPresentation.CopyText(selection.Distinct().OrderBy(row => row.StartSeconds), true)
            : string.Join(Environment.NewLine, Segments.Select(segment =>
                $"[{TranscriptDocument.Timestamp(segment.StartSeconds)}] {SpeakerName(segment.SpeakerId)}: {segment.Text}"));
    }

    public static TranscriptCorrectionPlan Create(TranscriptDocument source, long frames, IReadOnlyList<TranscriptRow> selection) =>
        new(source, frames, selection);

    public static TranscriptCorrectionPlan CreateForSegments(TranscriptDocument source, long frames, IReadOnlyList<int> indices) =>
        new(source, frames, [], indices);

    public bool Matches(TranscriptDocument current) => fingerprint == AudioEditPlan.ContentFingerprint(current);
    public string SpeakerName(string? id) => id is null ? "" : source.SpeakerName(id);

    public TranscriptDocument Apply(IReadOnlyList<string> texts)
    {
        if (texts.Count != Indices.Count)
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        var result = source.Copy();
        var changed = false;
        for (var index = 0; index < texts.Count; index++)
        {
            var text = texts[index];
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumTextLength ||
                text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
                throw new InvalidDataException(UiText.Get("InvalidCorrectionText"));
            var original = source.Segments[Indices[index]];
            var updated = original with { Text = text.Trim() };
            if (updated.Text == original.Text)
                continue;
            result.Segments[Indices[index]] = updated with { IsUserEdited = true };
            changed = true;
        }
        if (changed)
            result.Revision = Guid.NewGuid().ToString("N");
        return result;
    }
}
