using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed record PlaybackRange(double StartSeconds, double EndSeconds)
{
    public static PlaybackRange FromRows(IEnumerable<TranscriptRow> rows)
    {
        var selected = rows.ToArray();
        if (selected.Length == 0)
            throw new InvalidOperationException(UiText.Get("SelectReplayRange"));
        if (selected.Any(row => !double.IsFinite(row.StartSeconds) || !double.IsFinite(row.EndSeconds) ||
            row.StartSeconds < 0 || row.EndSeconds < row.StartSeconds))
            throw new InvalidDataException(UiText.Get("InvalidReplayRange"));
        var range = new PlaybackRange(selected.Min(row => row.StartSeconds), selected.Max(row => row.EndSeconds));
        range.Validate();
        return range;
    }

    public (long StartFrame, long FrameCount) ToFrames(long availableFrames)
    {
        Validate();
        if (availableFrames <= 0 || StartSeconds >= (double)availableFrames / PcmFormat.SampleRate)
            throw new InvalidOperationException(UiText.Get("ReplayOutsideRecording"));
        var first = (long)Math.Floor(StartSeconds * PcmFormat.SampleRate);
        var last = (long)Math.Ceiling(Math.Min(EndSeconds, (double)availableFrames / PcmFormat.SampleRate) * PcmFormat.SampleRate);
        last = Math.Min(last, availableFrames);
        if (first < 0 || last <= first)
            throw new InvalidOperationException(UiText.Get("ReplayOutsideRecording"));
        return (first, last - first);
    }

    private void Validate()
    {
        if (!double.IsFinite(StartSeconds) || !double.IsFinite(EndSeconds) || StartSeconds < 0 || EndSeconds <= StartSeconds)
            throw new InvalidDataException(UiText.Get("InvalidReplayRange"));
    }
}
