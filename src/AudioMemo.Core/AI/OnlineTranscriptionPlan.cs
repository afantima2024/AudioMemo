using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.AI;

public sealed record OnlineAudioChunk(int Row, long StartFrame, long Frames);

public sealed class OnlineTranscriptionPlan
{
    public OnlineSpeechModel Model { get; }
    public long SelectedFrames { get; }
    public long EstimatedBytes { get; }
    public bool RequiresSplitting { get; }
    public bool Allowed { get; }
    public IReadOnlyList<OnlineAudioChunk> Chunks { get; }

    public OnlineTranscriptionPlan(TranscriptCorrectionPlan selection, OnlineSpeechModel model, bool allowSplitting)
    {
        Model = model;
        if (model.MaximumFrames <= 0)
            throw new InvalidDataException(UiText.Get("InvalidOnlineSpeechModel"));
        var chunks = new List<OnlineAudioChunk>();
        for (var index = 0; index < selection.Segments.Count; index++)
        {
            var row = selection.Segments[index];
            var first = AudioEditPlan.FrameAt(row.StartSeconds, selection.SourceFrames);
            var last = AudioEditPlan.FrameAt(row.EndSeconds, selection.SourceFrames);
            if (last <= first)
                throw new InvalidDataException(UiText.Get("InvalidEditTimeline"));
            SelectedFrames = checked(SelectedFrames + last - first);
            for (var position = first; position < last;)
            {
                var count = Math.Min(last - position, model.MaximumFrames);
                chunks.Add(new(index, position, count));
                position += count;
            }
        }
        EstimatedBytes = chunks.Sum(chunk => model.EstimatedBytes(chunk.Frames));
        RequiresSplitting = !model.Fits(SelectedFrames);
        Allowed = !RequiresSplitting || allowSplitting;
        Chunks = chunks.AsReadOnly();
    }

    public void EnsureAllowed()
    {
        if (!Allowed)
            throw new InvalidOperationException(UiText.Get("OnlineSelectionTooLarge"));
    }
}
