namespace AudioMemo.Core.Audio;

public static class PcmFormat
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int BlockAlign = Channels * BitsPerSample / 8;
    public const int BytesPerSecond = SampleRate * BlockAlign;

    public static TimeSpan Duration(long frames) => TimeSpan.FromSeconds((double)frames / SampleRate);
}
