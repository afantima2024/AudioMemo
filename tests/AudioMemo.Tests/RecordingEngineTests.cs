using System.Diagnostics;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;

namespace AudioMemo.Tests;

public sealed class RecordingEngineTests
{
    private static SourceSelection Microphone => new("mic", "Test microphone", 1);
    private static SourceSelection Playback => new("playback", "Test playback", 1);

    [Fact]
    public async Task RejectsBothSourcesDisabledWithoutStarting()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        await Assert.ThrowsAsync<RecordingDeviceException>(() =>
            recorder.StartAsync(new() { SaveFolder = folder.Root }, "Test", new(null, null)));
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Empty(factory.Inputs);
        Assert.Null(recorder.Session);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
    }

    [Fact]
    public async Task PauseOmitsWallTimeAndResumeKeepsTheSameFile()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var wall = Stopwatch.StartNew();
        var session = await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Pause", new(Microphone, null));
        factory.Inputs.Last().EmitTone();
        await Task.Delay(160);
        await recorder.PauseAsync();
        var pausedFrames = recorder.Progress.Frames;
        await Task.Delay(400);
        Assert.Equal(pausedFrames, recorder.Progress.Frames);
        Assert.Equal(RecorderState.Paused, recorder.State);
        await recorder.ResumeAsync();
        factory.Inputs.Last().EmitTone();
        await Task.Delay(160);
        await recorder.StopAsync();
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal("Saved", session.Status);
        Assert.True(session.Duration.TotalMilliseconds < wall.Elapsed.TotalMilliseconds - 300);
        Assert.True(session.Frames > 10_000);
        using var file = File.OpenRead(session.WavePath);
        Assert.Equal(session.Frames, DurableWaveFile.ValidateAndGetFrames(file));
        Assert.Contains(File.ReadAllBytes(session.WavePath).Skip(80), value => value != 0);
    }

    [Fact]
    public async Task StartingTwiceCannotDestroyAnActiveRecording()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var original = await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Original", new(Microphone, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recorder.StartAsync(new() { SaveFolder = folder.Root }, "Second", new(Microphone, null)));
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.Same(original, recorder.Session);
        Assert.True(factory.Inputs[0].IsRunning);
    }

    [Fact]
    public async Task FailedSwitchLeavesTheOriginalSourceWorking()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Switch", new(Microphone, null));
        await Assert.ThrowsAsync<IOException>(() => recorder.SwitchSourcesAsync(new(new("broken", "Broken", 1), null)));
        Assert.Equal("mic", recorder.Sources.Microphone?.DeviceId);
        Assert.True(factory.Inputs[0].IsRunning);
        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.SwitchSourcesAsync(new(null, Playback));
        Assert.False(factory.Inputs[0].IsRunning);
        Assert.True(factory.Inputs.Last().IsRunning);
        Assert.NotNull(recorder.Sources.Playback);
        Assert.Null(recorder.Sources.Microphone);
    }

    [Fact]
    public async Task SourceFailureContinuesWithRemainingDeviceThenStopsSafely()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var warnings = new List<string>();
        recorder.Warning += warnings.Add;
        string? failure = null;
        recorder.Failed += message => failure = message;
        var session = await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Disconnect", new(Microphone, Playback));
        factory.Inputs[0].Fail();
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.NotEmpty(warnings);
        await Task.Delay(120);
        factory.Inputs[1].Fail();
        Assert.Equal(RecorderState.Faulted, recorder.State);
        Assert.NotNull(failure);
        await recorder.StopAsync();
        Assert.Equal("Interrupted", session.Status);
        Assert.True(File.Exists(session.WavePath));
    }

    [Fact]
    public async Task AllFailedSourcesLeaveTheEngineReusable()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        await Assert.ThrowsAsync<RecordingDeviceException>(() =>
            recorder.StartAsync(new() { SaveFolder = folder.Root }, "Unavailable", new(new("broken", "Broken", 1), null)));
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Null(recorder.Session);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
        await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Working", new(Microphone, null));
        Assert.Equal(RecorderState.Recording, recorder.State);
    }

    [Fact]
    public async Task StopErrorStillClosesTheWaveForRecovery()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var session = await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Stop error", new(Microphone, null));
        factory.Inputs[0].ThrowOnStop = true;
        await Task.Delay(100);
        await recorder.StopAsync();
        Assert.Equal("Interrupted", session.Status);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.True(DurableWaveFile.Recover(session.WavePath) > 0);
    }

    [Fact]
    public async Task SilenceDetectionIsOptInAndDoesNotDiscardSilentAudio()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var session = await recorder.StartAsync(new() { SaveFolder = folder.Root, SilenceTimeoutSeconds = 1 },
            "Manual stopping", new(Microphone, null));
        await Task.Delay(1500);
        Assert.False(recorder.SilenceTimeoutReached);
        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.StopAsync();
        Assert.True(session.Frames >= PcmFormat.SampleRate);
        using var file = File.OpenRead(session.WavePath);
        Assert.Equal(session.Frames, DurableWaveFile.ValidateAndGetFrames(file));
    }

    [Fact]
    public async Task SourceActivityPauseAndDeviceSwitchResetSilenceDetection()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var settings = new AppSettings { SaveFolder = folder.Root, AutoStopOnSilence = true, SilenceTimeoutSeconds = 1 };
        await recorder.StartAsync(settings, "Silence tracking", new(Microphone, Playback));
        settings.SilenceTimeoutSeconds = 300;
        await WaitForSilenceAsync(recorder);
        factory.Inputs[1].EmitTone();
        Assert.False(recorder.SilenceTimeoutReached);
        await recorder.PauseAsync();
        Assert.False(recorder.SilenceTimeoutReached);
        await recorder.ResumeAsync();
        Assert.False(recorder.SilenceTimeoutReached);
        var oldMicrophone = factory.Inputs[^2];
        await recorder.SwitchSourcesAsync(new(null, Playback));
        Assert.False(recorder.SilenceTimeoutReached);
        await WaitForSilenceAsync(recorder);
        oldMicrophone.EmitTone();
        Assert.True(recorder.SilenceTimeoutReached);
        factory.Inputs[^1].EmitTone();
        Assert.False(recorder.SilenceTimeoutReached);
        await recorder.StopAsync();
        Assert.False(recorder.SilenceTimeoutReached);
    }

    internal static async Task WaitForSilenceAsync(RecordingEngine recorder)
    {
        var clock = Stopwatch.StartNew();
        while (!recorder.SilenceTimeoutReached && clock.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        Assert.True(recorder.SilenceTimeoutReached, "The synthetic recording did not reach its silence timeout.");
    }

    [Fact]
    public async Task OldStartupPacketsDoNotFaultSilenceTrackingOrCountAsNewSound()
    {
        using var folder = new TestFolder();
        var factory = new FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        await recorder.StartAsync(new() { SaveFolder = folder.Root, AutoStopOnSilence = true, SilenceTimeoutSeconds = 1 },
            "Old startup packet", new(Microphone, null));
        factory.Inputs[0].EmitTone(WasapiInput.QpcNow - 100_000_000);
        Assert.Equal(RecorderState.Recording, recorder.State);
        await WaitForSilenceAsync(recorder);
        Assert.Equal(RecorderState.Recording, recorder.State);
    }

    internal sealed class FakeFactory
    {
        public List<FakeInput> Inputs { get; } = [];
        public IAudioInput Create(SourceSelection selection, bool loopback, AudioPacketHandler packet,
            Action<IAudioInput, Exception> error)
        {
            var input = new FakeInput(selection, loopback, packet, error);
            Inputs.Add(input);
            return input;
        }
    }

    internal sealed class FakeInput(SourceSelection selection, bool loopback, AudioPacketHandler onPacket,
        Action<IAudioInput, Exception> onError) : IAudioInput
    {
        public string Kind => loopback ? "Playback" : "Microphone";
        public SourceSelection Selection => selection;
        public bool IsRunning { get; private set; }
        public bool ThrowOnStop { get; set; }
        public Action? OnStart { get; set; }
        public Task StartAsync()
        {
            if (selection.DeviceId == "broken")
                throw new IOException("Test device is unavailable.");
            IsRunning = true;
            OnStart?.Invoke();
            return Task.CompletedTask;
        }
        public void RequestStop() => IsRunning = false;
        public Task StopAsync()
        {
            IsRunning = false;
            return ThrowOnStop ? Task.FromException(new IOException("Test driver failed during stop.")) : Task.CompletedTask;
        }
        public void EmitTone(long? qpc = null, float amplitude = 0.4f) =>
            onPacket(this, qpc ?? WasapiInput.QpcNow, Enumerable.Repeat(amplitude, 4800 * 2).ToArray(), false);
        public void Fail()
        {
            IsRunning = false;
            onError(this, new IOException("Test device disconnected."));
        }
    }
}
