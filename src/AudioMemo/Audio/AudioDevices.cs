using NAudio.CoreAudioApi;

using AudioMemo.Core.Localization;

namespace AudioMemo.Audio;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record SourceSelection(string DeviceId, string Name, float Gain);
public sealed class RecordingDeviceException(string message) : InvalidOperationException(message);

public sealed record RecordingSources(SourceSelection? Microphone, SourceSelection? Playback)
{
    public void Validate()
    {
        if (Microphone is null && Playback is null)
            throw new RecordingDeviceException(UiText.Get("AtLeastOneDevice"));
        if (Microphone is not null && string.IsNullOrWhiteSpace(Microphone.DeviceId))
            throw new RecordingDeviceException(UiText.Format("ChooseWorkingDevice", UiText.Get("Microphone")));
        if (Playback is not null && string.IsNullOrWhiteSpace(Playback.DeviceId))
            throw new RecordingDeviceException(UiText.Format("ChooseWorkingDevice", UiText.Get("Playback")));
    }
}

public static class AudioDevices
{
    public static IReadOnlyList<AudioDevice> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(flow, Role.Console))
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(flow, Role.Console);
            defaultId = defaultDevice.ID;
        }
        var devices = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
                devices.Add(new(device.ID, device.FriendlyName));
        }
        return devices.OrderByDescending(d => d.Id == defaultId).ThenBy(d => d.Name).ToArray();
    }
}
