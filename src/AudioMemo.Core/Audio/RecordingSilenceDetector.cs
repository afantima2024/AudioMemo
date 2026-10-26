namespace AudioMemo.Core.Audio;

public sealed class RecordingSilenceDetector
{
    public const float AudibleAmplitude = 0.001f; // -60 dBFS, after the selected input gain.
    private readonly long timeoutFrames;
    private long lastAudibleFrame;

    public RecordingSilenceDetector(long timeoutFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutFrames);
        this.timeoutFrames = timeoutFrames;
    }

    public void Reset(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        lastAudibleFrame = frame;
    }

    public void Observe(long firstFrame, ReadOnlySpan<float> stereo, float gain)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstFrame);
        if (stereo.Length % PcmFormat.Channels != 0)
            throw new ArgumentException("Audio must contain complete stereo frames.", nameof(stereo));
        if (!float.IsFinite(gain) || gain is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(gain));

        // Inspect each source before mixing so opposite-phase inputs cannot cancel activity.
        for (var index = stereo.Length - 1; index >= 0; index--)
        {
            if (float.IsFinite(stereo[index]) && Math.Abs(stereo[index] * gain) >= AudibleAmplitude)
            {
                lastAudibleFrame = Math.Max(lastAudibleFrame, firstFrame + index / PcmFormat.Channels + 1);
                break;
            }
        }
    }

    public bool HasTimedOut(long recordedFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recordedFrames);
        return recordedFrames >= lastAudibleFrame && recordedFrames - lastAudibleFrame >= timeoutFrames;
    }
}
