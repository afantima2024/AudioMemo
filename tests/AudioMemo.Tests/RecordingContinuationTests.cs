using System.Buffers.Binary;
using System.Text.Json.Nodes;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class RecordingContinuationTests
{
    private static RecordingSources Sources => new(new("mic", "New microphone", 1), null);

    [Theory]
    [InlineData("Saved")]
    [InlineData("Recovered")]
    public async Task ContinuationAppendsWithoutChangingEarlierAudioTextNotesOrOutputProfile(string status)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        source.CreatedAt = DateTimeOffset.Now.AddDays(-4);
        source.Status = status;
        source.RequestedFormat = "MP3";
        source.AudioOutput = new() { SampleRate = 44_100, Channels = 1 };
        source.Save();
        if (status == "Saved")
        {
            var legacy = JsonNode.Parse(File.ReadAllText(source.MetadataPath))!.AsObject();
            legacy.Remove("Continuations");
            File.WriteAllText(source.MetadataPath, legacy.ToJsonString());
        }
        var originalAudio = File.ReadAllBytes(source.WavePath);
        var originalDevices = source.Devices.ToArray();
        var kept = new[] { source.TranscriptPath, source.NotesPath, source.SummaryPath,
            source.Mp3Path, Path.Combine(source.Folder, "summary.json") }.ToDictionary(path => path, File.ReadAllBytes);
        var factory = new RecordingEngineTests.FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var settings = new AppSettings { SaveFolder = folder.File("another-library"), Format = "WAV" };
        var continued = await recorder.ContinueAsync(settings, source, Sources);
        Assert.Equal(source.Id, continued.Id);
        Assert.Equal(source.Folder, continued.Folder);
        Assert.Equal(source.Title, continued.Title);
        Assert.Equal(source.CreatedAt, continued.CreatedAt);
        Assert.Equal(source.Frames, continued.PreservedTranscriptFrames);
        Assert.Equal(source.Frames, Assert.Single(continued.Continuations).StartFrame);
        Assert.True(recorder.Progress.CommittedFrames >= source.Frames);
        factory.Inputs[^1].EmitTone();
        await Task.Delay(160);
        await recorder.PauseAsync();
        var paused = recorder.Progress.Frames;
        await Task.Delay(150);
        Assert.Equal(paused, recorder.Progress.Frames);
        await recorder.ResumeAsync();
        factory.Inputs[^1].EmitTone();
        await Task.Delay(160);
        await recorder.StopAsync();
        Assert.Equal("Saved", continued.Status);
        Assert.InRange(continued.Frames, source.Frames + 1, source.Frames + 30L * PcmFormat.SampleRate);
        Assert.Equal("MP3", continued.RequestedFormat);
        Assert.Equal(44_100, continued.AudioOutput.SampleRate);
        Assert.Equal(1, continued.AudioOutput.Channels);
        Assert.Equal(originalDevices, continued.Devices.Take(originalDevices.Length));
        Assert.All(continued.Devices.Skip(originalDevices.Length), change =>
            Assert.True(change.AtSeconds >= source.Duration.TotalSeconds));
        var audio = File.ReadAllBytes(continued.WavePath);
        Assert.Equal(originalAudio.AsSpan(DurableWaveFile.HeaderSize).ToArray(),
            audio.AsSpan(DurableWaveFile.HeaderSize, originalAudio.Length - DurableWaveFile.HeaderSize).ToArray());
        Assert.Contains(audio.Skip(originalAudio.Length), value => value != 0);
        Assert.Equal(DurableWaveFile.HeaderSize + continued.Frames * PcmFormat.BlockAlign, audio.LongLength);
        Assert.Equal(source.TranscribedFrames, continued.TranscribedFrames);
        foreach (var (path, contents) in kept)
            Assert.Equal(contents, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(settings.SaveFolder));
        Assert.Equal(continued.Id, Assert.Single(AppStorage.LoadSessions(folder.Root).Sessions).Id);
    }

    [Theory]
    [InlineData("broken-source", typeof(RecordingDeviceException))]
    [InlineData("stale-selection", typeof(InvalidOperationException))]
    [InlineData("interrupted", typeof(InvalidOperationException))]
    [InlineData("wrong-audio-length", typeof(InvalidDataException))]
    [InlineData("locked-metadata", typeof(IOException))]
    [InlineData("invalid-continuation", typeof(InvalidDataException))]
    [InlineData("invalid-format", typeof(InvalidDataException))]
    public async Task FailedContinuationLeavesAllExistingFilesIntactAndClosesNewInputs(string reason, Type expected)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var sources = Sources;
        FileStream? locked = null;
        switch (reason)
        {
            case "broken-source": sources = new(new("broken", "Unavailable microphone", 1), null); break;
            case "stale-selection": source.Frames--; break;
            case "interrupted": source.Status = "Interrupted"; source.Save(); break;
            case "wrong-audio-length":
                using (var audio = new FileStream(source.WavePath, FileMode.Open, FileAccess.Write))
                    audio.SetLength(audio.Length + PcmFormat.BlockAlign);
                break;
            case "locked-metadata":
                locked = new FileStream(source.MetadataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                break;
            case "invalid-continuation":
                source.Continuations.Add(new(source.Frames + 1, source.Frames, DateTimeOffset.Now));
                source.Save();
                break;
            case "invalid-format": source.RequestedFormat = "unknown"; source.Save(); break;
        }
        try
        {
            var before = EditedSessionTests.Hashes(source.Folder);
            var factory = new RecordingEngineTests.FakeFactory();
            await using var recorder = new RecordingEngine(factory.Create);
            var error = await Record.ExceptionAsync(() =>
                recorder.ContinueAsync(new() { SaveFolder = folder.Root }, source, sources));
            Assert.NotNull(error);
            Assert.Equal(expected, error.GetType());
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Null(recorder.Session);
            Assert.All(factory.Inputs, input => Assert.False(input.IsRunning));
            Assert.Equal(before, EditedSessionTests.Hashes(source.Folder));
            using var exclusive = new FileStream(source.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public async Task ContinuingCannotInterruptAnAlreadyActiveSession()
    {
        using var folder = new TestFolder();
        var history = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(history.Folder);
        var factory = new RecordingEngineTests.FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var active = await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Active recording", Sources);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recorder.ContinueAsync(new() { SaveFolder = folder.Root }, history, Sources));
        Assert.Same(active, recorder.Session);
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.True(Assert.Single(factory.Inputs).IsRunning);
        Assert.Equal(before, EditedSessionTests.Hashes(history.Folder));
    }

    [Fact]
    public async Task ADeviceHandoffRecordsTheNewInputAndIgnoresPacketsFromTheReplacedDevice()
    {
        using var folder = new TestFolder();
        var factory = new RecordingEngineTests.FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var session = await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Handoff", Sources);
        var first = factory.Inputs[0];
        first.EmitTone(amplitude: 0.2f);
        await Task.Delay(150);
        await recorder.SwitchSourcesAsync(new(new("replacement", "Replacement microphone", 1), null));
        var at = WasapiInput.QpcNow;
        factory.Inputs[^1].EmitTone(at, -0.6f);
        first.EmitTone(at, 0.9f);
        await Task.Delay(150);
        await recorder.StopAsync();
        var audio = File.ReadAllBytes(session.WavePath);
        var samples = Enumerable.Range(0, (audio.Length - DurableWaveFile.HeaderSize) / 2)
            .Select(index => BinaryPrimitives.ReadInt16LittleEndian(audio.AsSpan(DurableWaveFile.HeaderSize + index * 2))).ToArray();
        Assert.Contains(samples, sample => sample is > 6_000 and < 7_000);
        Assert.Contains(samples, sample => sample is < -19_000 and > -20_000);
        Assert.DoesNotContain(samples, sample => sample > 20_000);
        Assert.False(first.IsRunning);
    }

    internal static RecordingSession AppendAudio(TestFolder folder, RecordingSession source, double seconds = 1,
        double amplitude = 0.4)
    {
        var continued = AppStorage.PrepareContinuation(source);
        var tail = File.ReadAllBytes(folder.WriteTone(seconds, amplitude));
        using (var writer = DurableWaveFile.OpenForAppend(continued.WavePath, continued.Frames))
        {
            writer.Write(tail.AsSpan(DurableWaveFile.HeaderSize));
            continued.Frames = writer.FramesWritten;
        }
        continued.Status = "Saved";
        continued.Save();
        return continued;
    }
}
