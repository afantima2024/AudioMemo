using System.Diagnostics;
using System.IO;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using AudioMemo.Services;

namespace AudioMemo.Audio;

public sealed record PlaybackSnapshot(double StartSeconds, double EndSeconds, double PositionSeconds, bool IsPaused);

public sealed class RecordingPlayback(Func<IWavePlayer>? outputFactory = null) : IDisposable
{
    private readonly object gate = new();
    private Run? active;
    private bool disposed;
    public event Action<Exception?>? Completed;

    public PlaybackSnapshot? Snapshot
    {
        get
        {
            var run = Volatile.Read(ref active);
            return run is null ? null : new(run.Range.StartSeconds, run.Range.EndSeconds,
                Math.Min(run.Range.EndSeconds, run.PositionStartSeconds + run.Clock.Elapsed.TotalSeconds), run.IsPaused);
        }
    }

    public void Play(RecordingSession session, PlaybackRange range)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (session.Status is "Recording" or "Paused" or "Interrupted")
                throw new InvalidOperationException(UiText.Get("ReplayAfterStop"));
            var (start, frames) = range.ToFrames(session.Frames);
            PlayCore((first, end) => new PcmRangeStream(session.WavePath, first, end - first), start, start + frames,
                new((double)start / PcmFormat.SampleRate, (double)(start + frames) / PcmFormat.SampleRate), false);
        }
    }

    internal void PlayRecovery(SilenceRecoveryContext recovery, AudioFrameRange range)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (range.Length <= 0 || AudioRangeSet.Subtract([range], recovery.MutedRanges, recovery.Current.Frames).Count != 0)
                throw new ArgumentOutOfRangeException(nameof(range));
            recovery.Verify(recovery.CompletePlan());
            PlayCore((first, end) => new RecoveryPcmStream(recovery, first, end), range.StartFrame, range.EndFrame,
                new((double)range.StartFrame / PcmFormat.SampleRate, (double)range.EndFrame / PcmFormat.SampleRate), false);
        }
    }

    public void Seek(double positionSeconds)
    {
        var finished = false;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var run = active ?? throw new InvalidOperationException(UiText.Get("ReplayNotActive"));
            if (!double.IsFinite(positionSeconds))
                throw new ArgumentOutOfRangeException(nameof(positionSeconds));
            var position = Math.Clamp(positionSeconds, run.Range.StartSeconds, run.Range.EndSeconds);
            var frame = Math.Min(run.EndFrame, (long)Math.Round(position * PcmFormat.SampleRate));
            if (frame >= run.EndFrame)
            {
                StopCore();
                finished = true;
            }
            else
                PlayCore(run.OpenPcm, frame, run.EndFrame, run.Range, run.IsPaused);
        }
        if (finished)
            Completed?.Invoke(null);
    }

    private void PlayCore(Func<long, long, Stream> openPcm, long startFrame, long endFrame, PlaybackRange range, bool paused)
    {
        StopCore();
        var pcm = openPcm(startFrame, endFrame);
        // Recreate the output on seek so buffered samples from the old position cannot play.
        var source = new RawSourceWaveStream(pcm,
            new WaveFormat(PcmFormat.SampleRate, PcmFormat.BitsPerSample, PcmFormat.Channels));
        IWavePlayer? output = null;
        Run? run = null;
        try
        {
            output = outputFactory is null ? new WasapiOut(AudioClientShareMode.Shared, 120) : outputFactory();
            run = new(output, source, pcm, openPcm, startFrame, endFrame, range) { IsPaused = paused };
            var captured = run;
            var context = SynchronizationContext.Current;
            run.Handler = (_, args) =>
            {
                if (context is not null)
                    context.Post(ignored => Finish(captured, args.Exception), null);
                else
                    ThreadPool.QueueUserWorkItem(ignored => Finish(captured, args.Exception));
            };
            output.PlaybackStopped += run.Handler;
            output.Init(source);
            Volatile.Write(ref active, run);
            if (!paused)
            {
                run.Clock.Start();
                output.Play();
            }
        }
        catch
        {
            Volatile.Write(ref active, null);
            if (output is not null && run is not null)
                output.PlaybackStopped -= run.Handler;
            try { output?.Dispose(); }
            finally { source.Dispose(); pcm.Dispose(); }
            throw;
        }
    }

    public void TogglePause()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var run = active ?? throw new InvalidOperationException(UiText.Get("ReplayNotActive"));
            if (run.IsPaused)
            {
                run.Output.Play();
                run.Clock.Start();
                run.IsPaused = false;
            }
            else
            {
                run.Output.Pause();
                run.Clock.Stop();
                run.IsPaused = true;
            }
        }
    }

    public void Stop()
    {
        lock (gate)
            StopCore();
    }

    private void StopCore()
    {
        if (active is not { } run)
            return;
        run.Output.PlaybackStopped -= run.Handler;
        try { run.Output.Stop(); }
        finally
        {
            try { run.Output.Dispose(); }
            finally
            {
                run.Source.Dispose();
                run.Pcm.Dispose();
                run.Clock.Stop();
                Volatile.Write(ref active, null);
            }
        }
    }

    private void Finish(Run run, Exception? error)
    {
        lock (gate)
        {
            if (!ReferenceEquals(active, run))
                return;
            try { StopCore(); }
            catch (Exception ex) { error = error is null ? ex : new AggregateException(error, ex); }
        }
        Completed?.Invoke(error);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            try { StopCore(); }
            finally { disposed = true; }
        }
    }

    private sealed class Run(IWavePlayer output, WaveStream source, Stream pcm, Func<long, long, Stream> openPcm,
        long startFrame, long endFrame, PlaybackRange range)
    {
        public IWavePlayer Output { get; } = output;
        public WaveStream Source { get; } = source;
        public Stream Pcm { get; } = pcm;
        public PlaybackRange Range { get; } = range;
        public Func<long, long, Stream> OpenPcm { get; } = openPcm;
        public long EndFrame { get; } = endFrame;
        public double PositionStartSeconds { get; } = (double)startFrame / PcmFormat.SampleRate;
        public Stopwatch Clock { get; } = new();
        public volatile bool IsPaused;
        public EventHandler<StoppedEventArgs>? Handler { get; set; }
    }
}
