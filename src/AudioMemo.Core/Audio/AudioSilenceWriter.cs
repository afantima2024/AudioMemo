using AudioMemo.Core.Localization;
using System.Diagnostics;

namespace AudioMemo.Core.Audio;

public sealed record AudioRestoreSource(Stream Audio, long Frames, AudioFrameRange Range);

public static class AudioSilenceWriter
{
    public static void Write(Stream source, string destination, AudioEditPlan plan,
        IProgress<double>? progress, CancellationToken cancellationToken) =>
        WriteRanges(source, null, destination, plan.SourceFrames, plan.MutedRanges, progress, cancellationToken);

    public static void Restore(Stream current, Stream original, string destination, AudioRestorePlan plan,
        IProgress<double>? progress, CancellationToken cancellationToken) =>
        Restore(current, [new(original, plan.SourceFrames, new(0, plan.SourceFrames))],
            destination, plan, progress, cancellationToken);

    public static void Restore(Stream current, IReadOnlyList<AudioRestoreSource> originals, string destination,
        AudioRestorePlan plan, IProgress<double>? progress, CancellationToken cancellationToken) =>
        WriteRanges(current, originals, destination, plan.SourceFrames, plan.RestoredRanges, progress, cancellationToken);

    private static void WriteRanges(Stream source, IReadOnlyList<AudioRestoreSource>? replacements, string destination, long frames,
        IReadOnlyList<AudioFrameRange> ranges, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DurableWaveFile.ValidateAndGetFrames(source) != frames ||
            source.Length != DurableWaveFile.HeaderSize + frames * PcmFormat.BlockAlign)
            throw new InvalidDataException(UiText.Get("EditSourceChanged"));
        if (replacements is not null)
        {
            long covered = 0;
            foreach (var replacement in replacements)
            {
                if (replacement.Range.StartFrame != covered || replacement.Range.EndFrame <= covered ||
                    replacement.Range.EndFrame > replacement.Frames ||
                    DurableWaveFile.ValidateAndGetFrames(replacement.Audio) != replacement.Frames ||
                    replacement.Audio.Length != DurableWaveFile.HeaderSize + replacement.Frames * PcmFormat.BlockAlign)
                    throw new InvalidDataException(UiText.Get("EditSourceChanged"));
                covered = replacement.Range.EndFrame;
            }
            if (covered != frames)
                throw new InvalidDataException(UiText.Get("EditSourceChanged"));
        }
        using var output = new DurableWaveFile(destination);
        var buffer = new byte[1024 * 1024];
        var clock = Stopwatch.StartNew();
        long written = 0;
        var replacementIndex = 0;
        foreach (var range in ranges)
        {
            WriteUntil(range.StartFrame, source);
            if (replacements is null)
                WriteUntil(range.EndFrame, null);
            else
                while (written < range.EndFrame)
                {
                    while (replacements[replacementIndex].Range.EndFrame <= written)
                        replacementIndex++;
                    var replacement = replacements[replacementIndex];
                    WriteUntil(Math.Min(range.EndFrame, replacement.Range.EndFrame), replacement.Audio);
                }
        }
        WriteUntil(frames, source);
        cancellationToken.ThrowIfCancellationRequested();

        void WriteUntil(long endFrame, Stream? input)
        {
            var remaining = (endFrame - written) * PcmFormat.BlockAlign;
            if (input is null)
                Array.Clear(buffer);
            else
                input.Position = DurableWaveFile.HeaderSize + written * PcmFormat.BlockAlign;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(remaining, buffer.Length);
                if (input is not null)
                    input.ReadExactly(buffer.AsSpan(0, count));
                output.Write(buffer.AsSpan(0, count));
                remaining -= count;
                written += count / PcmFormat.BlockAlign;
                if (clock.ElapsedMilliseconds >= 100 || written == frames)
                {
                    progress?.Report(100d * written / frames);
                    clock.Restart();
                }
            }
        }
    }
}
