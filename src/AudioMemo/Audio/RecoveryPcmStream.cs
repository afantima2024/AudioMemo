using System.IO;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo.Audio;

internal sealed class RecoveryPcmStream : Stream
{
    private readonly List<(PcmRangeStream Stream, long Offset)> parts = [];
    private long position;
    private readonly long length;
    private bool disposed;

    internal RecoveryPcmStream(SilenceRecoveryContext recovery, long first, long end)
    {
        if (first < 0 || end <= first || end > recovery.Current.Frames)
            throw new ArgumentOutOfRangeException(nameof(first));
        length = checked((end - first) * PcmFormat.BlockAlign);
        var cursor = first;
        try
        {
            foreach (var source in recovery.AudioSources)
            {
                var start = Math.Max(first, source.Range.StartFrame);
                var stop = Math.Min(end, source.Range.EndFrame);
                if (stop <= start)
                    continue;
                if (start != cursor)
                    throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
                parts.Add((new PcmRangeStream(source.Session.WavePath, start, stop - start),
                    checked((start - first) * PcmFormat.BlockAlign)));
                cursor = stop;
            }
            if (cursor != end)
                throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
        }
        catch
        {
            foreach (var part in parts)
                part.Stream.Dispose();
            throw;
        }
    }

    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var total = 0;
        foreach (var (stream, offset) in parts)
        {
            if (position >= offset + stream.Length || position < offset)
                continue;
            stream.Position = position - offset;
            var count = (int)Math.Min(buffer.Length - total, stream.Length - stream.Position);
            var received = stream.Read(buffer.Slice(total, count));
            if (received == 0 && count > 0)
                throw new EndOfStreamException(UiText.Get("RestoreHistoryChanged"));
            total += received;
            position += received;
            if (total == buffer.Length || received < count)
                break;
        }
        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0 || target > length)
            throw new IOException(UiText.Get("ReplayOutsideRecording"));
        return position = target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            foreach (var part in parts)
                part.Stream.Dispose();
        }
        base.Dispose(disposing);
    }
}
