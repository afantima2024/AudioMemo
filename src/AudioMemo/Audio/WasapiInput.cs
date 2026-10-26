using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioMemo.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioMemo.Audio;

internal sealed class WasapiInput : IAudioInput
{
    private readonly SourceSelection selection;
    private readonly bool loopback;
    private readonly AudioPacketHandler onPacket;
    private readonly Action<IAudioInput, Exception> onError;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int stopRequested;
    private int running;
    public string Kind => loopback ? "Playback" : "Microphone";
    public SourceSelection Selection => selection;
    public bool IsRunning => Volatile.Read(ref running) != 0;

    public WasapiInput(SourceSelection selection, bool loopback, AudioPacketHandler onPacket,
        Action<IAudioInput, Exception> onError)
    {
        this.selection = selection;
        this.loopback = loopback;
        this.onPacket = onPacket;
        this.onError = onError;
    }

    public async Task StartAsync()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = $"AudioMemo {Kind}", Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch
        {
            RequestStop();
            throw;
        }
    }

    public void RequestStop() => Interlocked.Exchange(ref stopRequested, 1);

    public async Task StopAsync()
    {
        RequestStop();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public static long QpcNow => (long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency));

    private void Run()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(selection.DeviceId);
            using var client = device.AudioClient;
            var flags = AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
            if (loopback)
                flags |= AudioClientStreamFlags.Loopback;
            client.Initialize(AudioClientShareMode.Shared, flags, 1_000_000, 0,
                WaveFormat.CreateIeeeFloatWaveFormat(PcmFormat.SampleRate, PcmFormat.Channels), Guid.Empty);
            var capture = client.AudioCaptureClient;
            client.Start();
            try
            {
                Volatile.Write(ref running, 1);
                ready.TrySetResult();
                while (Volatile.Read(ref stopRequested) == 0)
                {
                    ReadPackets(capture);
                    Thread.Sleep(5);
                }
                ReadPackets(capture);
            }
            finally { client.Stop(); }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref running, 0);
            ready.TrySetException(ex);
            if (Volatile.Read(ref stopRequested) == 0)
                onError(this, ex);
        }
        finally
        {
            Volatile.Write(ref running, 0);
            Interlocked.Exchange(ref stopRequested, 1);
            finished.TrySetResult();
        }
    }

    private void ReadPackets(AudioCaptureClient capture)
    {
        while (capture.GetNextPacketSize() > 0)
        {
            var pointer = capture.GetBuffer(out var frames, out var flags, out _, out var qpc);
            try
            {
                var count = checked(frames * PcmFormat.Channels);
                var samples = ArrayPool<float>.Shared.Rent(count);
                try
                {
                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                        samples.AsSpan(0, count).Clear();
                    else
                        Marshal.Copy(pointer, samples, 0, count);
                    if ((flags & AudioClientBufferFlags.TimestampError) != 0 || qpc == 0)
                        qpc = QpcNow - (long)(frames * 10_000_000d / PcmFormat.SampleRate);
                    onPacket(this, qpc, samples.AsSpan(0, count),
                        (flags & AudioClientBufferFlags.DataDiscontinuity) != 0);
                }
                finally { ArrayPool<float>.Shared.Return(samples); }
            }
            finally { capture.ReleaseBuffer(frames); }
        }
    }
}
