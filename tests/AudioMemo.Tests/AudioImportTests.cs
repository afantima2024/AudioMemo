using System.Buffers.Binary;
using System.Security.Cryptography;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class AudioImportTests
{
    [Theory]
    [InlineData(16000, 1)]
    [InlineData(44100, 2)]
    [InlineData(48000, 1)]
    [InlineData(48000, 2)]
    public async Task ImportStreamsAnIndependentSafetyWaveWithoutAnalyzingOrChangingTheSource(int sampleRate, int channels)
    {
        using var folder = new TestFolder();
        var source = folder.File("Synthetic imported meeting.wav");
        using (var writer = new WaveFileWriter(source, new WaveFormat(sampleRate, 16, channels)))
        {
            var samples = new byte[sampleRate * channels * 2];
            for (var index = 0; index < sampleRate; index++)
            {
                var value = (short)(Math.Sin(2 * Math.PI * index * 440 / sampleRate) * 12000);
                BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(index * channels * 2), value);
                if (channels == 2)
                    BinaryPrimitives.WriteInt16LittleEndian(samples.AsSpan(index * channels * 2 + 2), (short)-value);
            }
            writer.Write(samples, 0, samples.Length);
        }
        var hash = SHA256.HashData(File.ReadAllBytes(source));
        var library = folder.File("library");
        var updates = new Updates();
        var result = await new AudioImportService().ImportAsync(source, library, updates, CancellationToken.None);
        Assert.Equal("Synthetic imported meeting", result.Title);
        Assert.Equal(Path.GetFileName(source), result.ImportedFileName);
        Assert.Equal("Saved", result.Status);
        Assert.Equal("WAV", result.RequestedFormat);
        Assert.True(result.AudioOutput.UsesSafetyFormat);
        Assert.InRange(result.Frames, PcmFormat.SampleRate - 3, PcmFormat.SampleRate + 3);
        Assert.Equal(DurableWaveFile.HeaderSize + result.Frames * PcmFormat.BlockAlign, new FileInfo(result.WavePath).Length);
        using var wave = new WaveFileReader(result.WavePath);
        Assert.Equal(48000, wave.WaveFormat.SampleRate);
        Assert.Equal(2, wave.WaveFormat.Channels);
        Assert.Equal(16, wave.WaveFormat.BitsPerSample);
        var pcm = new byte[wave.Length];
        Assert.Equal(pcm.Length, wave.Read(pcm, 0, pcm.Length));
        var energy = Enumerable.Range(0, pcm.Length / 4).Sum(index =>
        {
            var left = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 4));
            var right = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 4 + 2));
            Assert.InRange(Math.Abs((channels == 1 ? left : -left) - right), 0, 2);
            return (double)left * left;
        });
        Assert.InRange(Math.Sqrt(energy / result.Frames), 8300, 8600);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        Assert.False(File.Exists(result.TranscriptPath));
        Assert.False(File.Exists(result.SummaryPath));
        Assert.Empty(File.ReadAllText(result.NotesPath));
        Assert.Empty(result.Continuations);
        Assert.Equal(result.Id, Assert.Single(AppStorage.LoadSessions(library).Sessions).Id);
        Assert.Empty(AppStorage.LoadSessions(library).Errors);
        Assert.Equal(0, updates.Values[0]);
        Assert.Equal(100, updates.Values[^1]);
        Assert.Equal(updates.Values.Order(), updates.Values);
        Assert.Empty(Directory.GetDirectories(library, ".import-*"));
    }

    [Fact]
    public async Task ExistingMp3CanBeImportedAndThenContinuedAsANormalWaveSession()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var path = folder.File("Existing mp3.mp3");
        AudioExport.ToMp3(source.WavePath, path, source.Frames, CancellationToken.None);
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var result = await new AudioImportService().ImportAsync(path, folder.File("imported"), new Updates(), CancellationToken.None);
        Assert.InRange((double)result.Frames / PcmFormat.SampleRate, 1.9, 2.2);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        var continuation = AppStorage.PrepareContinuation(result);
        Assert.Equal(result.Id, continuation.Id);
        Assert.Equal(result.Frames, Assert.Single(continuation.Continuations).StartFrame);
        using var writer = DurableWaveFile.OpenForAppend(result.WavePath, result.Frames);
        Assert.Equal(result.Frames, writer.FramesWritten);
    }

    [Fact]
    public async Task CancellationCleansStagingAndKeepsTheSourceLockedAndUnchanged()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(source.Folder);
        var library = folder.File("imports");
        using var cancellation = new CancellationTokenSource();
        var updates = new Updates(_ =>
        {
            Assert.Throws<IOException>(() => File.Open(source.WavePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AudioImportService().ImportAsync(source.WavePath, library, updates, cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(library));
        Assert.Equal(before, EditedSessionTests.Hashes(source.Folder));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AudioImportService().ImportAsync(source.WavePath, library, new Updates(), new CancellationToken(true)));
        Assert.Empty(Directory.GetFileSystemEntries(library));
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("empty")]
    [InlineData("surround")]
    [InlineData("truncated")]
    [InlineData("oversized-data-chunk")]
    public async Task InvalidEmptyOrMultichannelAudioNeverPublishesASession(string kind)
    {
        using var folder = new TestFolder();
        var path = folder.File("source.wav");
        if (kind == "corrupt")
            File.WriteAllText(path, "Not an audio file");
        else
        {
            using var writer = new WaveFileWriter(path, new WaveFormat(48000, 16, kind == "surround" ? 6 : 1));
            if (kind is "surround" or "truncated" or "oversized-data-chunk")
                writer.Write(new byte[4800 * 12], 0, 4800 * 12);
        }
        if (kind == "truncated")
        {
            using var truncated = File.OpenWrite(path);
            truncated.SetLength(truncated.Length - 3000);
        }
        else if (kind == "oversized-data-chunk")
        {
            var malformed = File.ReadAllBytes(path);
            var data = malformed.AsSpan().IndexOf("data"u8);
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(data + 4), (uint)malformed.Length);
            File.WriteAllBytes(path, malformed);
        }
        var bytes = File.ReadAllBytes(path);
        var library = folder.File("imports");
        Assert.NotNull(await Record.ExceptionAsync(() =>
            new AudioImportService().ImportAsync(path, library, new Updates(), CancellationToken.None)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.True(!Directory.Exists(library) || Directory.GetFileSystemEntries(library).Length == 0);
    }

    [Fact]
    public void CrashedImportsAreHiddenFromTheRecordingList()
    {
        using var folder = new TestFolder();
        var path = folder.File(AppStorage.ImportStagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        AtomicFile.WriteJson(Path.Combine(path, "session.json"), new RecordingSession { Status = "Saved" });
        Assert.Empty(AppStorage.LoadSessions(folder.Root).Sessions);
        Assert.Empty(AppStorage.LoadSessions(folder.Root).Errors);
    }

    [Theory]
    [InlineData("m4a")]
    [InlineData("wma")]
    public async Task WindowsCompressedFormatsDecodeWithoutAffectingTheOriginal(string extension)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var file = folder.File("compressed." + extension);
        using (var wave = new WaveFileReader(source.WavePath))
        {
            if (extension == "m4a")
                MediaFoundationEncoder.EncodeToAac(wave, file, 128000);
            else
                MediaFoundationEncoder.EncodeToWma(wave, file, 128000);
        }
        var original = File.ReadAllBytes(file);
        var imported = await new AudioImportService().ImportAsync(file, folder.File("library"), new Updates(), CancellationToken.None);
        Assert.InRange((double)imported.Frames / PcmFormat.SampleRate, 1.9, 2.3);
        Assert.Equal(original, File.ReadAllBytes(file));
        using var result = new WaveFileReader(imported.WavePath);
        Assert.Equal(48000, result.WaveFormat.SampleRate);
        Assert.Equal(2, result.WaveFormat.Channels);
        Assert.Equal(16, result.WaveFormat.BitsPerSample);
    }

    private sealed class Updates(Action<double>? onUpdate = null) : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) { Values.Add(value); onUpdate?.Invoke(value); }
    }
}
