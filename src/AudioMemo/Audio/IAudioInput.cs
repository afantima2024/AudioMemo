namespace AudioMemo.Audio;

internal delegate void AudioPacketHandler(IAudioInput source, long qpcTime,
    ReadOnlySpan<float> stereo, bool discontinuity);

internal interface IAudioInput
{
    string Kind { get; }
    SourceSelection Selection { get; }
    bool IsRunning { get; }
    Task StartAsync();
    void RequestStop();
    Task StopAsync();
}
