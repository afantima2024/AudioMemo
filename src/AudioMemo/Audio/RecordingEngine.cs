using System.IO;
using System.Runtime.InteropServices;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Services;

using AudioMemo.Core.Localization;

namespace AudioMemo.Audio;

public enum RecorderState { Idle, Starting, Recording, Paused, Stopping, Faulted }
public sealed record RecordingProgress(RecorderState State, long Frames, long CommittedFrames,
    float Peak, long LateFrames, long ClippedSamples, long Discontinuities);

public sealed class RecordingEngine : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly Dictionary<string, IAudioInput> inputs = [];
    private readonly Func<SourceSelection, bool, AudioPacketHandler, Action<IAudioInput, Exception>, IAudioInput> inputFactory;
    private readonly Dictionary<string, long> nextFrames = [];
    private TimelineMixer mixer = new();
    private RecordingSilenceDetector? silenceDetector;
    private DurableWaveFile? writer;
    private ManualResetEventSlim? pumpStop;
    private Task? pump;
    private long anchor;
    private long baseFrame;
    private long boundary = long.MaxValue;
    private long committed;
    private long discontinuities;
    private float peak;
    private bool accepting;
    private bool expectedStop;
    private string? failure;
    private RecorderState state;
    public RecordingSession? Session { get; private set; }
    public RecordingSources Sources { get; private set; } = new(null, null);
    public RecorderState State { get { lock (sync) return state; } }
    public bool SilenceTimeoutReached
    {
        get
        {
            lock (sync)
                return state == RecorderState.Recording && silenceDetector?.HasTimedOut(mixer.Position) == true;
        }
    }
    public event Action<string>? Warning;
    public event Action<string>? Failed;

    public RecordingEngine() : this((selection, loopback, packet, error) => new WasapiInput(selection, loopback, packet, error)) { }

    internal RecordingEngine(Func<SourceSelection, bool, AudioPacketHandler, Action<IAudioInput, Exception>, IAudioInput> inputFactory)
    {
        this.inputFactory = inputFactory;
    }

    public RecordingProgress Progress
    {
        get
        {
            lock (sync)
                return new(state, mixer.Position, Interlocked.Read(ref committed), peak,
                    mixer.LateFrames, mixer.ClippedSamples, discontinuities);
        }
    }

    public Task<RecordingSession> StartAsync(AppSettings settings, string title, RecordingSources sources) =>
        StartSessionAsync(settings, title, sources, null);

    public Task<RecordingSession> ContinueAsync(AppSettings settings, RecordingSession session, RecordingSources sources) =>
        StartSessionAsync(settings, "", sources, session);

    private async Task<RecordingSession> StartSessionAsync(AppSettings settings, string title, RecordingSources sources,
        RecordingSession? existing)
    {
        await commands.WaitAsync();
        var initializing = false;
        var published = false;
        var previousSession = Session;
        var previousSources = Sources;
        try
        {
            if (State != RecorderState.Idle)
                throw new InvalidOperationException(UiText.Get("StopCurrentRecording"));
            sources.Validate();
            settings.Validate();
            var continuing = existing is null ? null : AppStorage.PrepareContinuation(existing);
            var startingFrame = continuing?.Frames ?? 0;
            initializing = true;
            lock (sync)
            {
                state = RecorderState.Starting;
                failure = null;
                committed = startingFrame;
                discontinuities = 0;
                peak = 0;
                mixer = new(position: startingFrame);
                silenceDetector = settings.AutoStopOnSilence
                    ? new RecordingSilenceDetector((long)settings.SilenceTimeoutSeconds * PcmFormat.SampleRate) : null;
                nextFrames.Clear();
            }
            Session = null;
            if (continuing is not null)
                writer = DurableWaveFile.OpenForAppend(continuing.WavePath, startingFrame);
            Sources = sources;
            await StartInputsAsync(sources);
            lock (sync)
            {
                if (!inputs.Values.Any(input => input.IsRunning))
                    throw new RecordingDeviceException(UiText.Format("NoWorkingDevice", UiText.Get("DeviceStoppedDuringStart")));
                // Publish only after capture starts; device-loss callbacks wait until the interval is active.
                Session = continuing ?? AppStorage.CreateSession(settings, title);
                if (continuing is null)
                    writer = new DurableWaveFile(Session.WavePath);
                if (continuing is not null)
                {
                    Session.Status = "Recording";
                    Session.LastError = null;
                    Session.Save();
                }
                published = true;
                BeginInterval();
            }
            return Session;
        }
        catch
        {
            if (!initializing)
                throw;
            try { await StopInputsAsync(); }
            finally
            {
                try { writer?.Dispose(); }
                finally
                {
                    writer = null;
                    lock (sync) state = RecorderState.Idle;
                }
                if (Session is not null && (existing is null || published))
                {
                    Session.Status = "Interrupted";
                    Session.Save();
                }
                else
                {
                    Session = previousSession;
                    Sources = previousSources;
                }
            }
            throw;
        }
        finally { commands.Release(); }
    }

    public async Task PauseAsync()
    {
        await commands.WaitAsync();
        try
        {
            if (State != RecorderState.Recording)
                throw new InvalidOperationException(UiText.Get("NoRecordingToPause"));
            await EndIntervalAsync();
            if (State == RecorderState.Faulted)
                throw new IOException(failure ?? UiText.Get("RecordingFailedWhilePausing"));
            lock (sync) state = RecorderState.Paused;
            SaveSession("Paused");
        }
        finally { commands.Release(); }
    }

    public async Task ResumeAsync()
    {
        await commands.WaitAsync();
        try
        {
            if (State != RecorderState.Paused)
                throw new InvalidOperationException(UiText.Get("RecordingNotPaused"));
            Sources.Validate();
            await StartInputsAsync(Sources);
            BeginInterval();
            SaveSession("Recording");
        }
        finally { commands.Release(); }
    }

    public async Task<RecordingSession?> StopAsync()
    {
        await commands.WaitAsync();
        try
        {
            if (State == RecorderState.Idle)
                return Session;
            lock (sync) state = RecorderState.Stopping;
            try
            {
                await EndIntervalAsync();
            }
            catch (Exception ex)
            {
                failure = UiText.Format("SourceStopFailed", ex.Message);
                Warning?.Invoke(failure);
            }
            try
            {
                if (writer is not null)
                {
                    writer.Dispose();
                    Interlocked.Exchange(ref committed, writer.CommittedFrames);
                }
            }
            catch (Exception ex)
            {
                failure = UiText.Format("WaveFinalizeFailed", ex.Message);
                Warning?.Invoke(failure);
            }
            finally
            {
                writer = null;
            }
            SaveSession(failure is null ? "Saved" : "Interrupted");
            return Session;
        }
        finally
        {
            // Live transcription must not see Idle before the final frame count is published.
            lock (sync) state = RecorderState.Idle;
            commands.Release();
        }
    }

    public async Task SwitchSourcesAsync(RecordingSources sources)
    {
        sources.Validate();
        await commands.WaitAsync();
        var replacements = new Dictionary<string, IAudioInput>();
        try
        {
            if (State == RecorderState.Paused)
            {
                Sources = sources;
                return;
            }
            if (State != RecorderState.Recording)
                throw new InvalidOperationException(UiText.Get("ApplyDevicesWhileActive"));
            if (sources == Sources)
                return;

            foreach (var (kind, selection) in Selections(sources))
            {
                IAudioInput? current;
                lock (sync) inputs.TryGetValue(kind, out current);
                if (selection is not null && current?.Selection.DeviceId != selection.DeviceId)
                {
                    var replacement = CreateInput(kind, selection);
                    replacements.Add(kind, replacement);
                    await replacement.StartAsync();
                }
            }

            var oldInputs = new List<IAudioInput>();
            lock (sync)
            {
                if (state != RecorderState.Recording)
                    throw new InvalidOperationException(UiText.Get("ApplyDevicesWhileActive"));
                if (replacements.Values.Any(input => !input.IsRunning))
                    throw new IOException(UiText.Get("ReplacementDeviceStopped"));
                foreach (var (kind, selection) in Selections(sources))
                {
                    if (selection is null || replacements.ContainsKey(kind))
                    {
                        if (inputs.Remove(kind, out var old))
                            oldInputs.Add(old);
                        nextFrames.Remove(kind);
                    }
                    if (replacements.TryGetValue(kind, out var replacement))
                        inputs[kind] = replacement;
                }
                Sources = sources;
                silenceDetector?.Reset(mixer.Position);
                RecordDevices();
            }
            replacements.Clear();
            foreach (var old in oldInputs)
                await old.StopAsync();
            Session?.Save();
        }
        finally
        {
            try
            {
                foreach (var input in replacements.Values)
                    await input.StopAsync();
            }
            finally { commands.Release(); }
        }
    }

    private static IEnumerable<(string Kind, SourceSelection? Selection)> Selections(RecordingSources sources)
    {
        yield return ("Microphone", sources.Microphone);
        yield return ("Playback", sources.Playback);
    }

    private IAudioInput CreateInput(string kind, SourceSelection selection) =>
        inputFactory(selection, kind == "Playback", OnPacket, OnInputError);

    private async Task StartInputsAsync(RecordingSources sources)
    {
        var errors = new List<string>();
        expectedStop = false;
        foreach (var (kind, selection) in Selections(sources))
        {
            if (selection is null)
                continue;
            IAudioInput? input = null;
            try
            {
                input = CreateInput(kind, selection);
                lock (sync) inputs[kind] = input;
                await input.StartAsync();
                lock (sync)
                    if (!input.IsRunning || !inputs.TryGetValue(kind, out var current) || current != input)
                        throw new IOException(UiText.Get("DeviceStoppedDuringStart"));
            }
            catch (Exception ex) when (ex is IOException or COMException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException or TimeoutException)
            {
                lock (sync) inputs.Remove(kind);
                errors.Add($"{UiText.Get(kind)} ({selection.Name}): {ex.Message}");
                if (input is not null)
                {
                    input.RequestStop();
                    try { await input.StopAsync(); }
                    catch (Exception stopError) when (stopError is IOException or COMException or UnauthorizedAccessException or
                        InvalidOperationException or ArgumentException or TimeoutException)
                    {
                        errors.Add(UiText.Format("SourceStopFailed", stopError.Message));
                    }
                }
            }
        }
        lock (sync)
        {
            if (!inputs.Values.Any(input => input.IsRunning))
                throw new RecordingDeviceException(UiText.Format("NoWorkingDevice",
                    errors.Count > 0 ? string.Join(" ", errors) : UiText.Get("DeviceStoppedDuringStart")));
            Sources = new(inputs.TryGetValue("Microphone", out var microphone) && microphone.IsRunning ? sources.Microphone : null,
                inputs.TryGetValue("Playback", out var playback) && playback.IsRunning ? sources.Playback : null);
        }
        if (errors.Count > 0)
            Warning?.Invoke(UiText.Format("RemainingDevice", string.Join(" ", errors)));
    }

    private void BeginInterval()
    {
        lock (sync)
        {
            if (inputs.Count == 0)
                throw new RecordingDeviceException(UiText.Format("NoWorkingDevice", UiText.Get("DeviceStoppedDuringStart")));
            baseFrame = mixer.Position;
            anchor = WasapiInput.QpcNow;
            boundary = long.MaxValue;
            nextFrames.Clear();
            silenceDetector?.Reset(baseFrame);
            accepting = true;
            expectedStop = false;
            state = RecorderState.Recording;
            RecordDevices();
        }
        pumpStop?.Dispose();
        pumpStop = new ManualResetEventSlim();
        pump = Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private async Task EndIntervalAsync()
    {
        lock (sync)
        {
            if (accepting)
                boundary = Math.Max(mixer.Position, FrameAt(WasapiInput.QpcNow));
            expectedStop = true;
        }
        try { await StopInputsAsync(); }
        finally
        {
            lock (sync) accepting = false;
            pumpStop?.Set();
            if (pump is not null)
                await pump;
            pump = null;
        }
    }

    private async Task StopInputsAsync()
    {
        IAudioInput[] stopping;
        lock (sync)
        {
            expectedStop = true;
            stopping = inputs.Values.ToArray();
        }
        foreach (var input in stopping)
            input.RequestStop();
        List<Exception> errors = [];
        foreach (var input in stopping)
        {
            try { await input.StopAsync(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        lock (sync) inputs.Clear();
        if (errors.Count > 0)
            throw new AggregateException(UiText.Get("DriverStopFailed"), errors);
    }

    private long FrameAt(long qpc) =>
        baseFrame + (long)Math.Round((qpc - anchor) * (double)PcmFormat.SampleRate / 10_000_000);

    private void OnPacket(IAudioInput input, long qpc, ReadOnlySpan<float> samples, bool discontinuity)
    {
        try
        {
            lock (sync)
            {
                if (!accepting || !inputs.TryGetValue(input.Kind, out var current) || current != input)
                    return;
                if (discontinuity)
                    discontinuities++;
                var frame = FrameAt(qpc);
                if (frame < baseFrame)
                {
                    var skip = (int)Math.Min(samples.Length / 2, baseFrame - frame);
                    samples = samples[(skip * 2)..];
                    frame += skip;
                }
                if (nextFrames.TryGetValue(input.Kind, out var next))
                {
                    var drift = frame - next;
                    if (Math.Abs(drift) <= PcmFormat.SampleRate / 500 && !discontinuity)
                        frame = next;
                    else if (frame < next)
                    {
                        var skip = (int)Math.Min(samples.Length / 2, next - frame);
                        samples = samples[(skip * 2)..];
                        frame += skip;
                    }
                }
                if (samples.IsEmpty)
                    return;
                var frames = samples.Length / PcmFormat.Channels;
                nextFrames[input.Kind] = frame + frames;
                if (frame >= boundary)
                    return;
                if (frames > boundary - frame)
                    samples = samples[..checked((int)(boundary - frame) * PcmFormat.Channels)];
                var gain = input.Kind == "Microphone" ? Sources.Microphone?.Gain : Sources.Playback?.Gain;
                mixer.Add(frame, samples, gain ?? 0);
                silenceDetector?.Observe(frame, samples, gain ?? 0);
            }
        }
        catch (Exception ex) { Fail(ex.Message, false); }
    }

    private void OnInputError(IAudioInput input, Exception error)
    {
        var noInputs = false;
        lock (sync)
        {
            if (expectedStop || !inputs.TryGetValue(input.Kind, out var current) || current != input)
                return;
            inputs.Remove(input.Kind);
            if (accepting)
            {
                Sources = input.Kind == "Microphone" ? Sources with { Microphone = null } : Sources with { Playback = null };
                silenceDetector?.Reset(mixer.Position);
                RecordDevices();
                noInputs = inputs.Count == 0;
            }
        }
        var message = UiText.Format("DeviceDisconnected", UiText.Get(input.Kind), error.Message);
        if (noInputs)
            Fail(UiText.Format("AllDevicesLost", message));
        else
            Warning?.Invoke(message);
    }

    private void Pump()
    {
        try
        {
            var block = new byte[960 * PcmFormat.BlockAlign];
            if (SetThreadExecutionState(0x80000001) == 0)
                Warning?.Invoke(UiText.Get("StayAwakeFailed"));
            var lastCheckpoint = WasapiInput.QpcNow;
            while (pumpStop is not null && !pumpStop.Wait(20))
            {
                long target;
                lock (sync)
                {
                    target = Math.Min(boundary, FrameAt(WasapiInput.QpcNow) - PcmFormat.SampleRate / 4);
                    if (target - mixer.Position > mixer.CapacityFrames)
                        throw new IOException(UiText.Get("RecordingStalled"));
                }
                RenderUntil(target, block);
                if (WasapiInput.QpcNow - lastCheckpoint >= 10_000_000)
                {
                    Checkpoint();
                    lastCheckpoint = WasapiInput.QpcNow;
                }
            }
            long end;
            lock (sync) end = boundary == long.MaxValue ? mixer.Position : boundary;
            RenderUntil(end, block);
            Checkpoint();
        }
        catch (Exception ex) { Fail(UiText.Format("RecordingStoppedSafely", ex.Message), false); }
        finally { SetThreadExecutionState(0x80000000); }
    }

    private void RenderUntil(long target, byte[] block)
    {
        while (true)
        {
            int bytes;
            lock (sync)
            {
                var frames = (int)Math.Min(960, target - mixer.Position);
                if (frames <= 0)
                    return;
                bytes = frames * PcmFormat.BlockAlign;
                peak = mixer.Read(block.AsSpan(0, bytes));
            }
            (writer ?? throw new InvalidOperationException("Recording file is closed.")).Write(block.AsSpan(0, bytes));
        }
    }

    private void Checkpoint()
    {
        writer?.Checkpoint();
        Interlocked.Exchange(ref committed, writer?.CommittedFrames ?? committed);
    }

    private void Fail(string message, bool finishBuffered = true)
    {
        lock (sync)
        {
            if (failure is not null)
                return;
            failure = message;
            boundary = finishBuffered
                ? Math.Max(mixer.Position, Math.Min(FrameAt(WasapiInput.QpcNow), mixer.Position + PcmFormat.SampleRate / 4))
                : mixer.Position;
            accepting = false;
            expectedStop = true;
            state = RecorderState.Faulted;
            foreach (var input in inputs.Values)
                input.RequestStop();
            pumpStop?.Set();
        }
        Failed?.Invoke(message);
    }

    private void RecordDevices()
    {
        if (Session is null)
            return;
        lock (Session)
        {
            foreach (var (kind, selected) in Selections(Sources))
                Session.Devices.Add(new(PcmFormat.Duration(mixer.Position).TotalSeconds,
                    kind, selected?.Name ?? "Disabled", selected is not null));
        }
    }

    private void SaveSession(string status)
    {
        if (Session is null)
            return;
        Session.Frames = Interlocked.Read(ref committed);
        Session.Status = status;
        Session.LastError = failure;
        Session.Save();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        pumpStop?.Dispose();
        commands.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint flags);
}
