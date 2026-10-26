namespace AudioMemo.Core.Audio;

public sealed class PcmRangeStream : Stream
{
    private readonly FileStream file;
    private readonly long start;
    private readonly long length;
    private long position;

    public PcmRangeStream(string path, long startFrame, long frameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(frameCount);
        file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.SequentialScan);
        try
        {
            var available = DurableWaveFile.ValidateAndGetFrames(file);
            if (startFrame > available || frameCount > available - startFrame)
                throw new EndOfStreamException("The requested audio has not been saved yet.");
            start = checked(DurableWaveFile.HeaderSize + startFrame * PcmFormat.BlockAlign);
            length = checked(frameCount * PcmFormat.BlockAlign);
            file.Position = start;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var count = (int)Math.Min(buffer.Length, length - position);
        var read = file.Read(buffer[..count]);
        position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > length)
            throw new IOException("Cannot seek outside this audio range.");
        file.Position = start + target;
        return position = target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            file.Dispose();
        base.Dispose(disposing);
    }
}
