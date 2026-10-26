using System.Buffers.Binary;
using System.Text;

namespace AudioMemo.Core.Audio;

public sealed class DurableWaveFile : IDisposable
{
    // Reserve ds64 space from the start so recordings can exceed the RIFF 4 GiB limit.
    public const int HeaderSize = 80;
    private readonly FileStream stream;
    private readonly int sampleRate;
    private readonly int channels;
    private int BlockAlign => channels * (PcmFormat.BitsPerSample / 8);
    private bool disposed;
    public long FramesWritten { get; private set; }
    public long CommittedFrames { get; private set; }

    public DurableWaveFile(string path, int sampleRate = PcmFormat.SampleRate, int channels = PcmFormat.Channels)
    {
        ValidateFormat(sampleRate, channels);
        this.sampleRate = sampleRate;
        this.channels = channels;
        stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        try
        {
            WriteHeader(stream, 0, sampleRate, channels);
            stream.Flush(true);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private DurableWaveFile(string path, long expectedFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedFrames);
        sampleRate = PcmFormat.SampleRate;
        channels = PcmFormat.Channels;
        stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        try
        {
            var frames = ValidateAndGetFrames(stream);
            using var expected = new MemoryStream();
            WriteHeader(expected, checked(expectedFrames * PcmFormat.BlockAlign));
            Span<byte> header = stackalloc byte[HeaderSize];
            stream.Position = 0;
            stream.ReadExactly(header);
            if (frames != expectedFrames || stream.Length != HeaderSize + expectedFrames * PcmFormat.BlockAlign ||
                !header.SequenceEqual(expected.GetBuffer().AsSpan(0, HeaderSize)))
                throw new InvalidDataException(Localization.UiText.Get("ContinueNeedsRecovery"));
            FramesWritten = frames;
            CommittedFrames = frames;
            stream.Position = stream.Length;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static DurableWaveFile OpenForAppend(string path, long expectedFrames) => new(path, expectedFrames);

    public void Write(ReadOnlySpan<byte> pcm)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (pcm.Length % BlockAlign != 0)
            throw new ArgumentException("PCM data is not frame-aligned.", nameof(pcm));
        stream.Write(pcm);
        FramesWritten += pcm.Length / BlockAlign;
    }

    public void Checkpoint()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var position = stream.Position;
        WriteHeader(stream, FramesWritten * BlockAlign, sampleRate, channels);
        stream.Position = position;
        stream.Flush(true);
        CommittedFrames = FramesWritten;
    }

    public static void WriteHeader(Stream output, long dataBytes,
        int sampleRate = PcmFormat.SampleRate, int channels = PcmFormat.Channels)
    {
        ValidateFormat(sampleRate, channels);
        var blockAlign = channels * (PcmFormat.BitsPerSample / 8);
        ArgumentOutOfRangeException.ThrowIfNegative(dataBytes);
        if (dataBytes % blockAlign != 0)
            throw new ArgumentException("PCM length is not frame-aligned.", nameof(dataBytes));
        var riffSize = checked(dataBytes + HeaderSize - 8);
        var rf64 = riffSize > uint.MaxValue;
        output.Position = 0;
        using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes(rf64 ? "RF64" : "RIFF"));
        writer.Write(rf64 ? uint.MaxValue : (uint)riffSize);
        writer.Write("WAVE"u8);
        writer.Write(Encoding.ASCII.GetBytes(rf64 ? "ds64" : "JUNK"));
        writer.Write(28u);
        writer.Write(rf64 ? (ulong)riffSize : 0ul);
        writer.Write(rf64 ? (ulong)dataBytes : 0ul);
        writer.Write(rf64 ? (ulong)(dataBytes / blockAlign) : 0ul);
        writer.Write(0u);
        writer.Write("fmt "u8);
        writer.Write(16u);
        writer.Write((ushort)1);
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(checked(sampleRate * blockAlign));
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)PcmFormat.BitsPerSample);
        writer.Write("data"u8);
        writer.Write(rf64 ? uint.MaxValue : (uint)dataBytes);
    }

    private static void ValidateFormat(int sampleRate, int channels)
    {
        if (sampleRate is not (44_100 or 48_000) || channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Unsupported PCM output format.");
    }

    public static long ValidateAndGetFrames(Stream input)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        input.Position = 0;
        input.ReadExactly(header);
        if ((!header[..4].SequenceEqual("RIFF"u8) && !header[..4].SequenceEqual("RF64"u8)) ||
            !header[8..12].SequenceEqual("WAVE"u8) ||
            (!header[12..16].SequenceEqual("JUNK"u8) && !header[12..16].SequenceEqual("ds64"u8)) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) != 28 ||
            !header[48..52].SequenceEqual("fmt "u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[56..]) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[58..]) != PcmFormat.Channels ||
            BinaryPrimitives.ReadInt32LittleEndian(header[60..]) != PcmFormat.SampleRate ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[70..]) != PcmFormat.BitsPerSample ||
            !header[72..76].SequenceEqual("data"u8))
            throw new InvalidDataException("This is not an AudioMemo safety WAV.");
        return (input.Length - HeaderSize) / PcmFormat.BlockAlign;
    }

    public static long Recover(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var frames = ValidateAndGetFrames(file);
        file.SetLength(HeaderSize + frames * PcmFormat.BlockAlign);
        WriteHeader(file, frames * PcmFormat.BlockAlign);
        file.Flush(true);
        return frames;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        try { Checkpoint(); }
        finally
        {
            disposed = true;
            stream.Dispose();
        }
    }
}
