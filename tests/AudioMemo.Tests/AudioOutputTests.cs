using System.Buffers.Binary;
using System.Text.Json;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class AudioOutputTests
{
    [Theory]
    [InlineData("WAV", 44_100, 1)]
    [InlineData("WAV", 44_100, 2)]
    [InlineData("WAV", 48_000, 1)]
    [InlineData("WAV", 48_000, 2)]
    [InlineData("MP3", 44_100, 1)]
    [InlineData("MP3", 44_100, 2)]
    [InlineData("MP3", 48_000, 1)]
    [InlineData("MP3", 48_000, 2)]
    public void ConfiguredFilesContainTheActualChosenAudioFormat(string format, int rate, int channels)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.Root, Format = format, AudioOutput = new() { SampleRate = rate, Channels = channels } };
        var session = AppStorage.CreateSession(settings, "Synthetic format");
        File.Move(folder.WriteTone(2), session.WavePath);
        session.Frames = 96_000;
        session.Status = "Saved";
        var safety = File.ReadAllBytes(session.WavePath);
        AudioExport.SaveConfigured(session, CancellationToken.None);
        Assert.Equal(safety, File.ReadAllBytes(session.WavePath));
        Assert.NotSame(settings.AudioOutput, session.AudioOutput);
        using WaveStream reader = format == "MP3" ? new MediaFoundationReader(session.OutputPath) : new WaveFileReader(session.OutputPath);
        Assert.Equal(rate, reader.WaveFormat.SampleRate);
        Assert.Equal(channels, reader.WaveFormat.Channels);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.InRange(reader.TotalTime.TotalSeconds, format == "WAV" ? 1.999 : 1.9, format == "WAV" ? 2.001 : 2.2);
        var buffer = new float[4096];
        var provider = reader.ToSampleProvider();
        Assert.True(provider.Read(buffer, 0, buffer.Length) > 0);
        Assert.Contains(buffer, sample => Math.Abs(sample) > 0.1);
        Assert.Equal(format == "WAV" && rate == 48_000 && channels == 2, session.OutputPath == session.WavePath);
        if (session.OutputPath != session.WavePath)
            Assert.Equal(format == "MP3" ? "audio.mp3" : "audio-output.wav", Path.GetFileName(session.OutputPath));
        using var source = File.OpenRead(session.WavePath);
        Assert.Equal(session.Frames, DurableWaveFile.ValidateAndGetFrames(source));
    }

    [Fact]
    public void AudioSettingsRoundTripAndOldRecordingsKeepTheSafetyFormat()
    {
        var old = JsonSerializer.Deserialize<RecordingSession>("""{"RequestedFormat":"WAV"}""")!;
        Assert.True(old.AudioOutput.UsesSafetyFormat);
        Assert.Equal(old.WavePath, old.OutputPath);
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Format":"MP3"}""")!;
        Assert.True(settings.AudioOutput.UsesSafetyFormat);
        settings.AudioOutput = new() { SampleRate = 44_100, Channels = 1 };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.Validate();
        Assert.Equal(44_100, restored.AudioOutput.SampleRate);
        Assert.Equal(1, restored.AudioOutput.Channels);
        using var folder = new TestFolder();
        restored.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(restored, "");
        var (sessions, errors) = AppStorage.LoadSessions(folder.Root);
        Assert.Empty(errors);
        var loaded = Assert.Single(sessions);
        Assert.Equal(session.AudioOutput.SampleRate, loaded.AudioOutput.SampleRate);
        Assert.Equal(session.AudioOutput.Channels, loaded.AudioOutput.Channels);
        restored.AudioOutput.Channels = 2;
        Assert.Equal(1, session.AudioOutput.Channels);
        Assert.Throws<InvalidDataException>(() => new AudioOutputSettings { SampleRate = 16_000 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AudioOutputSettings { Channels = 0 }.Validate());
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<AppSettings>("""{"AudioOutput":null}""")!.Validate());
    }

    [Theory]
    [InlineData(44_100, 1)]
    [InlineData(48_000, 2)]
    public void LongOutputUsesAccurateRf64HeadersWithoutAllocatingLargeFiles(int rate, int channels)
    {
        const long dataBytes = 8L * 1024 * 1024 * 1024;
        using var stream = new MemoryStream();
        DurableWaveFile.WriteHeader(stream, dataBytes, rate, channels);
        var header = stream.ToArray();
        Assert.Equal(DurableWaveFile.HeaderSize, header.Length);
        Assert.Equal("RF64"u8.ToArray(), header[..4]);
        Assert.Equal((ushort)channels, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(58)));
        Assert.Equal(rate, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(60)));
        Assert.Equal(rate * channels * 2, BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(64)));
        Assert.Equal((ulong)(dataBytes / (channels * 2)), BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(36)));
        Assert.Equal((ulong)dataBytes, BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(28)));
    }

    [Fact]
    public void CancellationAndInvalidDestinationsNeverChangeExistingAudio()
    {
        using var folder = new TestFolder();
        var wave = folder.WriteTone(2);
        var before = File.ReadAllBytes(wave);
        var output = folder.File("audio-output.wav");
        File.WriteAllText(output, "Previous output");
        var profile = new AudioOutputSettings { Channels = 1 };
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => AudioExport.ToWave(wave, output, 96_000, profile, cancel.Token));
        Assert.Equal("Previous output", File.ReadAllText(output));
        Assert.DoesNotContain(Directory.GetFiles(folder.Root), file => file.EndsWith(".partial.wav"));
        Assert.Throws<InvalidOperationException>(() => AudioExport.ToWave(wave, wave, 96_000, profile, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => AudioExport.ToMp3(wave, wave, 96_000, profile, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(wave));
    }

    [Fact]
    public void DefaultWaveExportMustNotReportMissingOrIncompleteAudioAsSaved()
    {
        using var folder = new TestFolder();
        var session = AppStorage.CreateSession(new AppSettings { SaveFolder = folder.Root }, "Safety validation");
        Assert.Throws<InvalidOperationException>(() => AudioExport.SaveConfigured(session, CancellationToken.None));
        session.Frames = 96_000;
        Assert.Throws<FileNotFoundException>(() => AudioExport.SaveConfigured(session, CancellationToken.None));
        File.Move(folder.WriteTone(1), session.WavePath);
        var before = File.ReadAllBytes(session.WavePath);
        Assert.Throws<EndOfStreamException>(() => AudioExport.SaveConfigured(session, CancellationToken.None));
        Assert.Equal(before, File.ReadAllBytes(session.WavePath));
    }

    [Theory]
    [InlineData("WAV")]
    [InlineData("MP3")]
    public async Task EditsAndSelectiveRestorationInheritTheCurrentOutputProfile(string format)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        source.AudioOutput = new() { SampleRate = 44_100, Channels = 1 };
        var document = AppStorage.LoadTranscript(source);
        AppStorage.SaveTranscript(source, document);
        var before = EditedSessionTests.Hashes(source.Folder);
        var plan = AudioEditPlan.Create(document, source.Frames, [TranscriptPresentation.Rows(document)[1]]);
        var edited = await new EditedSessionService().CreateAsync(folder.Root, source, plan, "Custom output edit", format,
            new Progress<SessionEditProgress>(), CancellationToken.None);
        Assert.Equal(44_100, edited.AudioOutput.SampleRate);
        Assert.Equal(1, edited.AudioOutput.Channels);
        var service = new SilenceRecoveryService();
        var recovery = await service.PrepareAsync(folder.Root, edited, CancellationToken.None);
        var restored = await service.CreateAsync(folder.Root, recovery, recovery.Plan(recovery.MutedRanges), "Restored custom output", format,
            new Progress<SessionEditProgress>(), CancellationToken.None);
        Assert.Equal(44_100, restored.AudioOutput.SampleRate);
        Assert.Equal(1, restored.AudioOutput.Channels);
        Assert.Equal(File.ReadAllBytes(source.WavePath), File.ReadAllBytes(restored.WavePath));
        Assert.Equal(before, EditedSessionTests.Hashes(source.Folder));
        using WaveStream reader = format == "WAV" ? new WaveFileReader(restored.OutputPath) : new MediaFoundationReader(restored.OutputPath);
        Assert.Equal(44_100, reader.WaveFormat.SampleRate);
        Assert.Equal(1, reader.WaveFormat.Channels);
        Assert.InRange(reader.TotalTime.TotalSeconds, 1.9, 2.2);
    }
}
