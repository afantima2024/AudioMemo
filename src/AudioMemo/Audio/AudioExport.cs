using System.IO;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

using AudioMemo.Core.Localization;

namespace AudioMemo.Audio;

public static class AudioExport
{
    public static void ToMp3(string wavePath, string destination, long frames, CancellationToken cancellationToken)
        => ToMp3(wavePath, destination, frames, new(), cancellationToken);

    public static void ToMp3(string wavePath, string destination, long frames,
        AudioOutputSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        if (frames <= 0)
            throw new InvalidOperationException(UiText.Get("NoSavedAudio"));
        if (Path.GetFullPath(wavePath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("OutputCannotReplaceSafety"));
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileNameWithoutExtension(destination)}-{Guid.NewGuid():N}.partial.mp3");
        try
        {
            using (var range = new PcmRangeStream(wavePath, 0, frames))
            using (var source = new RawSourceWaveStream(range,
                new WaveFormat(PcmFormat.SampleRate, PcmFormat.BitsPerSample, PcmFormat.Channels)))
            {
                MediaFoundationEncoder.EncodeToMp3(new CancellableWaveProvider(OutputProvider(source, settings), cancellationToken),
                    temporary, 192_000);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.PublishTemporary(temporary, destination, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void SaveConfigured(RecordingSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        session.AudioOutput.Validate();
        if (session.RequestedFormat == "MP3")
            ToMp3(session.WavePath, session.Mp3Path, session.Frames, session.AudioOutput, cancellationToken);
        else if (session.RequestedFormat != "WAV")
            throw new InvalidDataException(UiText.Get("InvalidAudioFormat"));
        else if (!session.AudioOutput.UsesSafetyFormat)
            ToWave(session.WavePath, session.OutputPath, session.Frames, session.AudioOutput, cancellationToken);
        else
        {
            if (session.Frames <= 0)
                throw new InvalidOperationException(UiText.Get("NoSavedAudio"));
            using var safety = new PcmRangeStream(session.WavePath, 0, session.Frames);
        }
    }

    public static void ToWave(string wavePath, string destination, long frames,
        AudioOutputSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        if (frames <= 0)
            throw new InvalidOperationException(UiText.Get("NoSavedAudio"));
        if (Path.GetFullPath(wavePath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("OutputCannotReplaceSafety"));
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileNameWithoutExtension(destination)}-{Guid.NewGuid():N}.partial.wav");
        try
        {
            using (var range = new PcmRangeStream(wavePath, 0, frames))
            using (var source = new RawSourceWaveStream(range,
                new WaveFormat(PcmFormat.SampleRate, PcmFormat.BitsPerSample, PcmFormat.Channels)))
            using (var output = new DurableWaveFile(temporary, settings.SampleRate, settings.Channels))
            {
                var provider = OutputProvider(source, settings);
                var buffer = new byte[64 * 1024];
                int count;
                while ((count = provider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer.AsSpan(0, count));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.PublishTemporary(temporary, destination, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static IWaveProvider OutputProvider(WaveStream source, AudioOutputSettings settings)
    {
        ISampleProvider samples = source.ToSampleProvider();
        if (settings.Channels == 1)
            samples = new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (settings.SampleRate != PcmFormat.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, settings.SampleRate);
        return new SampleToWaveProvider16(samples);
    }

    public static double WriteSpeechChunk(string sourcePath, string destination, long firstFrame,
        long frameCount, CancellationToken cancellationToken)
    {
        using var range = new PcmRangeStream(sourcePath, firstFrame, frameCount);
        using var source = new RawSourceWaveStream(range,
            new WaveFormat(PcmFormat.SampleRate, PcmFormat.BitsPerSample, PcmFormat.Channels));
        var mono = new StereoToMonoSampleProvider(source.ToSampleProvider()) { LeftVolume = 0.5f, RightVolume = 0.5f };
        var resampled = new WdlResamplingSampleProvider(mono, 16_000);
        using var output = new WaveFileWriter(destination, new WaveFormat(16_000, 16, 1));
        var buffer = new float[4096];
        double energy = 0;
        long samples = 0;
        int count;
        while ((count = resampled.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteSamples(buffer, 0, count);
            for (var index = 0; index < count; index++)
                energy += buffer[index] * buffer[index];
            samples += count;
        }
        return samples == 0 ? 0 : Math.Sqrt(energy / samples);
    }

    private sealed class CancellableWaveProvider(IWaveProvider source, CancellationToken cancellationToken) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return source.Read(buffer, offset, count);
        }
    }
}
