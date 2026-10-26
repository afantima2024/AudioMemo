using System.Buffers.Binary;
using System.IO;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioMemo.Services;

public sealed class AudioImportService(VideoAudioService? videoService = null)
{
    public Task<RecordingSession> ImportAsync(string path, string library, IProgress<double> progress,
        CancellationToken cancellationToken, VideoImportSelection? video = null) => Task.Run(async () =>
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException(UiText.Get("ImportFileMissing"));
        if (!Path.IsPathFullyQualified(library))
            throw new InvalidDataException(UiText.Get("InvalidSavingFolder"));
        cancellationToken.ThrowIfCancellationRequested();
        using var sourceLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateRiff(sourceLock);
        var isVideo = VideoAudioService.IsVideo(path);
        if (video is not null && !isVideo)
            throw new InvalidDataException(UiText.Get("VideoChooseTrack"));
        var videos = videoService ?? new VideoAudioService();
        if (isVideo && video is null)
        {
            var media = await videos.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
            if (media.AudioTracks.Count != 1)
                throw new InvalidDataException(UiText.Get("VideoChooseTrack"));
            video = new(media, media.AudioTracks[0].StreamIndex);
        }
        if (video is not null)
            VideoAudioService.ValidateSelection(path, video);
        using var reader = isVideo ? null : new AudioFileReader(path);
        SampleToWaveProvider16? pcm = null;
        if (reader is not null)
        {
            ISampleProvider samples = reader;
            if (samples.WaveFormat.Channels == 1)
                samples = new MonoToStereoSampleProvider(samples);
            else if (samples.WaveFormat.Channels != 2)
                throw new InvalidDataException(UiText.Get("ImportChannelsUnsupported"));
            if (samples.WaveFormat.SampleRate != PcmFormat.SampleRate)
                samples = new WdlResamplingSampleProvider(samples, PcmFormat.SampleRate);
            pcm = new SampleToWaveProvider16(samples);
        }
        var title = Path.GetFileNameWithoutExtension(path);
        title = new string(title.Where(character => !char.IsControl(character)).Take(160).ToArray()).Trim();
        var session = new RecordingSession
        {
            Title = title.Length == 0 ? UiText.Get("ImportedRecording") : title,
            ImportedFileName = Path.GetFileName(path),
            Status = "Saved"
        };
        var staging = Path.Combine(library, AppStorage.ImportStagingPrefix + session.Id);
        var destination = Path.Combine(library, $"{session.CreatedAt:yyyy-MM-dd_HH-mm-ss}_{session.Id[..8]}");
        if (Directory.Exists(staging) || Directory.Exists(destination))
            throw new IOException(UiText.Get("EditDestinationExists"));
        Directory.CreateDirectory(staging);
        session.Folder = staging;
        var published = false;
        try
        {
            progress.Report(0);
            using (var writer = new DurableWaveFile(session.WavePath))
            {
                if (video is not null)
                    await videos.ExtractAsync(path, video, writer, progress, cancellationToken).ConfigureAwait(false);
                else if (pcm is not null && reader is not null)
                {
                    var buffer = new byte[64 * 1024];
                    var lastProgress = System.Diagnostics.Stopwatch.StartNew();
                    int count;
                    while ((count = pcm.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.Write(buffer.AsSpan(0, count));
                        if (lastProgress.ElapsedMilliseconds >= 100)
                        {
                            progress.Report(reader.Length <= 0 ? 0 : Math.Clamp(100d * reader.Position / reader.Length, 0, 99));
                            lastProgress.Restart();
                        }
                    }
                }
                session.Frames = writer.FramesWritten;
            }
            if (session.Frames == 0)
                throw new InvalidDataException(UiText.Get("NoSavedAudio"));
            cancellationToken.ThrowIfCancellationRequested();
            session.Save();
            AtomicFile.WriteText(session.NotesPath, "");
            progress.Report(100);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            published = true;
            session.Folder = destination;
            return session;
        }
        finally
        {
            if (!published && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }, cancellationToken);

    private static void ValidateRiff(Stream source)
    {
        if (source.Length < 12)
            return;
        Span<byte> header = stackalloc byte[12];
        source.ReadExactly(header);
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
            return;
        var end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (end > source.Length || end < 12)
            throw new InvalidDataException(UiText.Get("ImportTruncatedWave"));
        // Windows decoders can otherwise report successful EOF for truncated WAV data.
        Span<byte> chunk = stackalloc byte[8];
        while (source.Position <= end - chunk.Length)
        {
            source.ReadExactly(chunk);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var next = checked(source.Position + length);
            if (next > end)
                throw new InvalidDataException(UiText.Get("ImportTruncatedWave"));
            source.Position = next + (length & 1);
        }
    }
}
