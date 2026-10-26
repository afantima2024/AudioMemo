using System.Buffers.Binary;
using AudioMemo.Core.Audio;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class DurableWaveTests
{
    [Fact]
    public void CheckpointProducesAReadableWaveWhileRecording()
    {
        using var folder = new TestFolder();
        var path = folder.File("recording.wav");
        using var writer = new DurableWaveFile(path);
        writer.Write(new byte[PcmFormat.BytesPerSecond]);
        writer.Checkpoint();
        Assert.Equal(PcmFormat.SampleRate, writer.CommittedFrames);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new WaveFileReader(stream);
        Assert.Equal(48_000, reader.WaveFormat.SampleRate);
        Assert.Equal(2, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(TimeSpan.FromSeconds(1), reader.TotalTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_000_000)]
    [InlineData(4_294_967_220)]
    [InlineData(4_294_967_224)]
    [InlineData(4_294_967_296)]
    [InlineData(20_000_000_000)]
    public void SupportsRiffAndRf64HeaderSizes(long dataBytes)
    {
        using var output = new MemoryStream();
        DurableWaveFile.WriteHeader(output, dataBytes);
        var header = output.ToArray();
        Assert.Equal(80, header.Length);
        if (dataBytes + 72 > uint.MaxValue)
        {
            Assert.Equal("RF64"u8.ToArray(), header[..4]);
            Assert.Equal("ds64"u8.ToArray(), header[12..16]);
            Assert.Equal((ulong)dataBytes + 72, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(20)));
            Assert.Equal((ulong)dataBytes, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(28)));
            Assert.Equal((ulong)dataBytes / 4, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(36)));
            Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76)));
        }
        else
        {
            Assert.Equal("RIFF"u8.ToArray(), header[..4]);
            Assert.Equal((uint)dataBytes, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76)));
        }
    }

    [Fact]
    public void RecoveryUsesAllCompleteFramesEvenWithStaleHeader()
    {
        using var folder = new TestFolder();
        var path = folder.WriteTone();
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            DurableWaveFile.WriteHeader(file, 0);
            file.Position = file.Length;
            file.Write([1, 2, 3]);
        }
        Assert.Equal(PcmFormat.SampleRate, DurableWaveFile.Recover(path));
        Assert.Equal(PcmFormat.BytesPerSecond + 80, new FileInfo(path).Length);
        using var reader = new WaveFileReader(path);
        Assert.Equal(TimeSpan.FromSeconds(1), reader.TotalTime);
    }

    [Fact]
    public void RecoveryRejectsUnrelatedFilesWithoutModifyingThem()
    {
        using var folder = new TestFolder();
        var path = folder.File("other.wav");
        var data = Enumerable.Repeat((byte)42, 100).ToArray();
        File.WriteAllBytes(path, data);
        Assert.Throws<InvalidDataException>(() => DurableWaveFile.Recover(path));
        Assert.Equal(data, File.ReadAllBytes(path));
    }

    [Fact]
    public void RangeStreamIsBoundedAndSeekable()
    {
        using var folder = new TestFolder();
        var path = folder.WriteTone(2);
        using var range = new PcmRangeStream(path, PcmFormat.SampleRate, 100);
        Assert.Equal(400, range.Length);
        var output = new byte[500];
        Assert.Equal(400, range.Read(output));
        Assert.Equal(0, range.Read(output));
        Assert.Equal(0, range.Seek(0, SeekOrigin.Begin));
        Assert.Equal(4, range.Read(output, 0, 4));
        Assert.Throws<IOException>(() => range.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<EndOfStreamException>(() => new PcmRangeStream(path, 96_000, 1));
    }

    [Fact]
    public void DoesNotOverwriteAnExistingRecording()
    {
        using var folder = new TestFolder();
        var path = folder.WriteTone();
        Assert.Throws<IOException>(() => new DurableWaveFile(path));
    }

    [Fact]
    public void AppendingPreservesEveryExistingSampleAndPublishesTheExtendedHeader()
    {
        using var folder = new TestFolder();
        var path = folder.WriteTone();
        var original = File.ReadAllBytes(path);
        var tail = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80 };
        using (var writer = DurableWaveFile.OpenForAppend(path, PcmFormat.SampleRate))
        {
            Assert.Equal(PcmFormat.SampleRate, writer.FramesWritten);
            Assert.Equal(writer.FramesWritten, writer.CommittedFrames);
            writer.Write(tail);
            writer.Checkpoint();
            Assert.Equal(PcmFormat.SampleRate + 2, writer.CommittedFrames);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.Equal(writer.CommittedFrames, DurableWaveFile.ValidateAndGetFrames(input));
        }
        var appended = File.ReadAllBytes(path);
        Assert.Equal(original.AsSpan(DurableWaveFile.HeaderSize).ToArray(),
            appended.AsSpan(DurableWaveFile.HeaderSize, original.Length - DurableWaveFile.HeaderSize).ToArray());
        Assert.Equal(tail, appended.AsSpan(original.Length).ToArray());
        Assert.Equal((uint)(PcmFormat.BytesPerSecond + tail.Length),
            BinaryPrimitives.ReadUInt32LittleEndian(appended.AsSpan(76)));
    }

    [Theory]
    [InlineData("frame-count")]
    [InlineData("stale-header")]
    [InlineData("partial-frame")]
    [InlineData("wrong-format")]
    public void UnsafeAppendIsRejectedWithoutChangingTheSavedFile(string corruption)
    {
        using var folder = new TestFolder();
        var path = folder.WriteTone();
        var frames = (long)PcmFormat.SampleRate;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            switch (corruption)
            {
                case "frame-count": frames--; break;
                case "stale-header": DurableWaveFile.WriteHeader(stream, 0); break;
                case "partial-frame": stream.SetLength(stream.Length + 1); break;
                case "wrong-format": DurableWaveFile.WriteHeader(stream, PcmFormat.BytesPerSecond, 44_100, 1); break;
            }
        }
        var before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => DurableWaveFile.OpenForAppend(path, frames));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }
}
