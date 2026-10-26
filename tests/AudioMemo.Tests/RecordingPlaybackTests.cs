using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class RecordingPlaybackTests
{
    [Fact]
    public void ReplayReadsExactlyTheRequestedPcmAndStopsBeforeReplacingIt()
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder);
        var original = File.ReadAllBytes(session.WavePath);
        var outputs = new List<FakePlayer>();
        using var playback = new RecordingPlayback(() =>
        {
            var output = new FakePlayer();
            outputs.Add(output);
            return output;
        });
        playback.Play(session, new(0.25, 0.75));
        var first = Assert.Single(outputs);
        var bytes = ReadAll(first.Provider!);
        Assert.Equal(PcmFormat.BytesPerSecond / 2, bytes.Length);
        Assert.Equal(original.AsSpan(DurableWaveFile.HeaderSize + PcmFormat.BytesPerSecond / 4, bytes.Length).ToArray(), bytes);
        playback.TogglePause();
        Assert.True(playback.Snapshot!.IsPaused);
        Assert.Equal(PlaybackState.Paused, first.PlaybackState);
        playback.TogglePause();
        Assert.False(playback.Snapshot!.IsPaused);
        playback.Play(session, new(1, 1.5));
        Assert.True(first.Disposed);
        Assert.True(first.StopCalls > 0);
        Assert.Equal(1, playback.Snapshot!.StartSeconds);
        playback.Stop();
        Assert.Null(playback.Snapshot);
        Assert.All(outputs, output => Assert.True(output.Disposed));
        using var exclusive = File.Open(session.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(original.Length, exclusive.Length);
    }

    [Theory]
    [InlineData("Recording")]
    [InlineData("Paused")]
    [InlineData("Interrupted")]
    public void ReplayCannotOpenAnActiveOrUnrecoveredRecording(string status)
    {
        var createdOutput = false;
        using var playback = new RecordingPlayback(() => { createdOutput = true; return new FakePlayer(); });
        Assert.Throws<InvalidOperationException>(() => playback.Play(new() { Status = status }, new(0, 1)));
        Assert.False(createdOutput);
    }

    [Fact]
    public async Task NaturalCompletionAndDeviceFailureReleaseFilesAndSurfaceErrors()
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder);
        var output = new FakePlayer();
        using var playback = new RecordingPlayback(() => output);
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.Completed += error => completed.TrySetResult(error);
        playback.Play(session, new(0, 0.2));
        var error = new IOException("Synthetic output-device failure.");
        output.Complete(error);
        Assert.Same(error, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(playback.Snapshot);
        Assert.True(output.Disposed);
        using var exclusive = File.Open(session.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void FailedOutputInitializationDoesNotLeakTheWaveOrPretendToPlay()
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder);
        var output = new FakePlayer { FailInit = true };
        using var playback = new RecordingPlayback(() => output);
        Assert.Throws<IOException>(() => playback.Play(session, new(0, 1)));
        Assert.Null(playback.Snapshot);
        Assert.True(output.Disposed);
        using var exclusive = File.Open(session.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [ReplayDeviceFact]
    [Trait("Category", "ReplayDevice")]
    public async Task RealDefaultOutputFinishesAShortSilentRangeWithoutRecordingAnything()
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder, silent: true);
        using var playback = new RecordingPlayback();
        var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        playback.Completed += error => completed.TrySetResult(error);
        playback.Play(session, new(0.1, 0.4));
        Assert.Null(await completed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Null(playback.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeekingFlushesOldSamplesPreservesTheRangeAndKeepsPauseState(bool paused)
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder);
        var original = File.ReadAllBytes(session.WavePath);
        var outputs = new List<FakePlayer>();
        using var playback = new RecordingPlayback(() =>
        {
            var output = new FakePlayer();
            outputs.Add(output);
            return output;
        });
        playback.Play(session, new(0.25, 1.75));
        if (paused)
            playback.TogglePause();
        const double requested = 1.123456789;
        var firstFrame = (long)Math.Round(requested * PcmFormat.SampleRate);
        playback.Seek(requested);
        Assert.Equal(2, outputs.Count);
        Assert.True(outputs[0].Disposed);
        Assert.Equal(paused ? PlaybackState.Stopped : PlaybackState.Playing, outputs[1].PlaybackState);
        var snapshot = playback.Snapshot!;
        Assert.Equal(0.25, snapshot.StartSeconds);
        Assert.Equal(1.75, snapshot.EndSeconds);
        Assert.Equal(paused, snapshot.IsPaused);
        Assert.InRange(snapshot.PositionSeconds, (double)firstFrame / PcmFormat.SampleRate,
            (double)firstFrame / PcmFormat.SampleRate + (paused ? 0 : 0.1));
        var bytes = ReadAll(outputs[1].Provider!);
        Assert.Equal(original.AsSpan(DurableWaveFile.HeaderSize + (int)firstFrame * PcmFormat.BlockAlign,
            (int)(1.75 * PcmFormat.SampleRate - firstFrame) * PcmFormat.BlockAlign).ToArray(), bytes);
        if (paused)
        {
            playback.TogglePause();
            Assert.Equal(PlaybackState.Playing, outputs[1].PlaybackState);
            Assert.False(playback.Snapshot!.IsPaused);
        }
        playback.Seek(-100);
        Assert.Equal(3, outputs.Count);
        Assert.Equal(original.AsSpan(DurableWaveFile.HeaderSize + PcmFormat.BytesPerSecond / 4,
            PcmFormat.BytesPerSecond * 3 / 2).ToArray(), ReadAll(outputs[2].Provider!));
        var completions = 0;
        playback.Completed += error => { Assert.Null(error); completions++; };
        playback.Seek(100);
        Assert.Null(playback.Snapshot);
        Assert.Equal(1, completions);
        Assert.All(outputs, output => Assert.True(output.Disposed));
        using var exclusive = File.Open(session.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(original.Length, exclusive.Length);
    }

    [Fact]
    public void InvalidSeekCannotDisturbReplayAndFailedSeekReleasesTheFile()
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder);
        var outputs = new List<FakePlayer>();
        using var playback = new RecordingPlayback(() =>
        {
            var output = new FakePlayer { FailInit = outputs.Count > 0 };
            outputs.Add(output);
            return output;
        });
        Assert.Throws<InvalidOperationException>(() => playback.Seek(0));
        playback.Play(session, new(0.25, 1.75));
        foreach (var position in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => playback.Seek(position));
        Assert.False(Assert.Single(outputs).Disposed);
        Assert.NotNull(playback.Snapshot);
        Assert.Throws<IOException>(() => playback.Seek(1));
        Assert.Null(playback.Snapshot);
        Assert.All(outputs, output => Assert.True(output.Disposed));
        using var exclusive = File.Open(session.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void AQueuedCompletionFromBeforeSeekingCannotStopTheNewPosition()
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder);
        var outputs = new List<FakePlayer>();
        using var playback = new RecordingPlayback(() =>
        {
            var output = new FakePlayer();
            outputs.Add(output);
            return output;
        });
        var previous = SynchronizationContext.Current;
        var context = new QueuedContext();
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            var completions = 0;
            playback.Completed += _ => completions++;
            playback.Play(session, new(0, 2));
            outputs[0].Complete(new IOException("Stale output failure."));
            playback.Seek(1);
            context.Drain();
            Assert.NotNull(playback.Snapshot);
            Assert.False(outputs[1].Disposed);
            Assert.Equal(0, completions);
            outputs[1].Complete();
            context.Drain();
            Assert.Null(playback.Snapshot);
            Assert.Equal(1, completions);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> callbacks = [];
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Enqueue(() => callback(state));
        public void Drain()
        {
            while (callbacks.TryDequeue(out var callback))
                callback();
        }
    }

    private static RecordingSession MakeSession(TestFolder folder, bool silent = false)
    {
        var session = AppStorage.CreateSession(new() { SaveFolder = folder.Root }, "Synthetic replay check");
        File.Copy(folder.WriteTone(2, silent ? 0 : 0.25), session.WavePath, true);
        session.Frames = 2 * PcmFormat.SampleRate;
        session.Status = "Saved";
        session.Save();
        return session;
    }

    internal static byte[] ReadAll(IWaveProvider provider)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = provider.Read(buffer, 0, buffer.Length)) > 0)
            memory.Write(buffer, 0, count);
        Assert.Equal(0, provider.Read(buffer, 0, buffer.Length));
        return memory.ToArray();
    }

    internal sealed class FakePlayer : IWavePlayer
    {
        public IWaveProvider? Provider { get; private set; }
        public bool FailInit { get; init; }
        public bool Disposed { get; private set; }
        public int StopCalls { get; private set; }
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public float Volume { get; set; } = 1;
        public PlaybackState PlaybackState { get; private set; }
        public WaveFormat OutputWaveFormat => Provider?.WaveFormat ?? new WaveFormat(48_000, 16, 2);
        public void Init(IWaveProvider waveProvider)
        {
            Provider = waveProvider;
            if (FailInit)
                throw new IOException("Synthetic output initialization failure.");
        }
        public void Play() => PlaybackState = PlaybackState.Playing;
        public void Pause() => PlaybackState = PlaybackState.Paused;
        public void Stop() { StopCalls++; Complete(); }
        public void Complete(Exception? error = null)
        {
            PlaybackState = PlaybackState.Stopped;
            PlaybackStopped?.Invoke(this, new(error));
        }
        public void Dispose() { Disposed = true; PlaybackState = PlaybackState.Stopped; }
    }
}

public sealed class ReplayDeviceFactAttribute : FactAttribute
{
    public ReplayDeviceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_REPLAY") != "1")
            Skip = "Set AUDIOMEMO_TEST_REPLAY=1 to send a short generated SILENT clip to the default Windows output. No microphone is used.";
    }
}
