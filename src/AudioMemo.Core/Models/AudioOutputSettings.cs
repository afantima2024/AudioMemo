using System.Text.Json.Serialization;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed class AudioOutputSettings
{
    public int SampleRate { get; set; } = 48_000;
    public int Channels { get; set; } = 2;
    [JsonIgnore] public bool UsesSafetyFormat => SampleRate == 48_000 && Channels == 2;

    public AudioOutputSettings Copy() => new() { SampleRate = SampleRate, Channels = Channels };

    public void Validate()
    {
        if (SampleRate is not (44_100 or 48_000) || Channels is not (1 or 2))
            throw new InvalidDataException(UiText.Get("InvalidAudioOutput"));
    }
}
