using System.Buffers.Binary;

namespace AudioMemo.Core.Audio;

// Absolute frame positions preserve silence and alignment when loopback emits no packets.
public sealed class TimelineMixer
{
    private readonly float[] samples;
    public int CapacityFrames { get; }
    public long Position { get; private set; }
    public long LateFrames { get; private set; }
    public long ClippedSamples { get; private set; }

    public TimelineMixer(int capacityFrames = PcmFormat.SampleRate * 8, long position = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        CapacityFrames = capacityFrames;
        Position = position;
        samples = new float[checked(capacityFrames * PcmFormat.Channels)];
    }

    public void Add(long firstFrame, ReadOnlySpan<float> stereo, float gain)
    {
        if (stereo.Length % PcmFormat.Channels != 0)
            throw new ArgumentException("Audio must contain complete stereo frames.", nameof(stereo));
        if (!float.IsFinite(gain) || gain < 0 || gain > 2)
            throw new ArgumentOutOfRangeException(nameof(gain));

        var frameCount = stereo.Length / PcmFormat.Channels;
        var skipped = (int)Math.Min(frameCount, Math.Max(0, Position - firstFrame));
        LateFrames += skipped;
        firstFrame += skipped;
        stereo = stereo[(skipped * PcmFormat.Channels)..];
        if (stereo.IsEmpty)
            return;
        if (firstFrame + stereo.Length / PcmFormat.Channels > Position + CapacityFrames)
            throw new IOException("The recording disk cannot keep up with audio. The safety WAV is preserved.");

        var slot = (int)((firstFrame * PcmFormat.Channels) % samples.Length);
        for (var index = 0; index < stereo.Length; index++)
        {
            samples[slot] += float.IsFinite(stereo[index]) ? stereo[index] * gain : 0;
            if (++slot == samples.Length)
                slot = 0;
        }
    }

    public float Read(Span<byte> pcm16)
    {
        if (pcm16.Length % PcmFormat.BlockAlign != 0 || pcm16.Length / PcmFormat.BlockAlign > CapacityFrames)
            throw new ArgumentException("Invalid PCM block size.", nameof(pcm16));

        float peak = 0;
        var slot = (int)((Position * PcmFormat.Channels) % samples.Length);
        for (var index = 0; index < pcm16.Length / 2; index++)
        {
            var value = samples[slot];
            samples[slot] = 0;
            if (++slot == samples.Length)
                slot = 0;
            peak = Math.Max(peak, Math.Abs(value));
            if (value is > 1 or < -1)
                ClippedSamples++;
            var sample = (short)Math.Round(Math.Clamp(value, -1, 1) * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm16[(index * 2)..], sample);
        }
        Position += pcm16.Length / PcmFormat.BlockAlign;
        return Math.Min(1, peak);
    }
}
