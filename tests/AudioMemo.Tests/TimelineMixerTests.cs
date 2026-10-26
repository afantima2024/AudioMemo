using System.Buffers.Binary;
using AudioMemo.Core.Audio;

namespace AudioMemo.Tests;

public sealed class TimelineMixerTests
{
    [Fact]
    public void MixesStereoSourcesAtTheSameAbsoluteTime()
    {
        var mixer = new TimelineMixer(100);
        mixer.Add(3, [0.2f, 0.4f, 0.3f, 0.1f], 1);
        mixer.Add(3, [0.4f, 0.2f, 0.1f, 0.3f], 0.5f);
        var result = new byte[7 * 4];
        mixer.Read(result);
        Assert.All(result.Take(12), value => Assert.Equal(0, value));
        Assert.InRange(Sample(result, 3, 0), 13_105, 13_108);
        Assert.InRange(Sample(result, 3, 1), 16_382, 16_385);
        Assert.All(result.Skip(20), value => Assert.Equal(0, value));
    }

    [Fact]
    public void LoopbackSilenceDoesNotCompressTheTimeline()
    {
        var mixer = new TimelineMixer(100);
        mixer.Add(0, [0.5f, 0.5f], 1);
        mixer.Add(90, [0.25f, 0.25f], 1);
        var result = new byte[100 * 4];
        mixer.Read(result);
        Assert.Equal(100, mixer.Position);
        Assert.All(result.Skip(4).Take(89 * 4), value => Assert.Equal(0, value));
        Assert.InRange(Sample(result, 90, 0), 8190, 8193);
    }

    [Fact]
    public void RingWrapClearsOldDataAndHasBoundedStorage()
    {
        var mixer = new TimelineMixer(32);
        var buffer = new byte[32 * 4];
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            mixer.Add(mixer.Position + 4, [0.2f, 0.2f], 1);
            mixer.Read(buffer);
            Assert.InRange(Sample(buffer, 4, 0), 6552, 6555);
            Assert.Equal(0, Sample(buffer, 3, 0));
            Assert.Equal(0, Sample(buffer, 5, 0));
        }
        Assert.Equal(320_000, mixer.Position);
        Assert.Equal(32, mixer.CapacityFrames);
    }

    [Fact]
    public void LateFramesAreReportedAndCannotCorruptNewAudio()
    {
        var mixer = new TimelineMixer(32);
        mixer.Read(new byte[10 * 4]);
        mixer.Add(8, [1, 1, 1, 1, 0.5f, 0.5f], 1);
        var output = new byte[4];
        mixer.Read(output);
        Assert.Equal(2, mixer.LateFrames);
        Assert.InRange(Sample(output, 0, 0), 16382, 16385);
    }

    [Fact]
    public void OverflowIsExplicitRatherThanDiscardingNewAudio()
    {
        var mixer = new TimelineMixer(32);
        Assert.Throws<IOException>(() => mixer.Add(32, [1, 1], 1));
    }

    [Fact]
    public void FramePositionsRemainCorrectBeyondTwentyFourHours()
    {
        var day = 24L * 3600 * PcmFormat.SampleRate;
        var mixer = new TimelineMixer(32, day);
        mixer.Add(day + 3, [0.5f, 0.25f], 1);
        var output = new byte[32 * 4];
        mixer.Read(output);
        Assert.Equal(day + 32, mixer.Position);
        Assert.InRange(Sample(output, 3, 0), 16382, 16385);
        Assert.InRange(Sample(output, 3, 1), 8190, 8193);
        Assert.Equal(0, mixer.LateFrames);
    }

    [Fact]
    public void ClipsSafelyAndCountsClippedSamples()
    {
        var mixer = new TimelineMixer(32);
        mixer.Add(0, [1, -1], 2);
        var output = new byte[4];
        mixer.Read(output);
        Assert.Equal(short.MaxValue, Sample(output, 0, 0));
        Assert.Equal(-short.MaxValue, Sample(output, 0, 1));
        Assert.Equal(2, mixer.ClippedSamples);
    }

    [Fact]
    public void RejectsInvalidGainAndUnalignedFrames()
    {
        var mixer = new TimelineMixer(32);
        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.Add(0, [1, 1], float.NaN));
        Assert.Throws<ArgumentException>(() => mixer.Add(0, [1], 1));
        Assert.Throws<ArgumentException>(() => mixer.Read(new byte[3]));
    }

    private static short Sample(byte[] data, int frame, int channel) =>
        BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(frame * 4 + channel * 2));
}
