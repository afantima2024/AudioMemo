using System.IO;
using System.Runtime.InteropServices;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using NAudio.CoreAudioApi;

namespace AudioMemo.Services;

internal sealed record SetupAudioDevices(IReadOnlyList<AudioDevice> Microphones, IReadOnlyList<AudioDevice> Playback);

internal interface ISetupEnvironment
{
    Task<SetupAudioDevices> ListAudioAsync(CancellationToken cancellationToken);
    Task ValidateAudioAsync(RecordingSources sources, CancellationToken cancellationToken);
    bool SpeechRuntimeAvailable { get; }
    bool IsSpeechModelReady(string model);
    bool SpeakerModelsReady { get; }
    Task DownloadSpeechAsync(string model, IProgress<double> progress, CancellationToken cancellationToken);
    Task DownloadSpeakersAsync(IProgress<double> progress, CancellationToken cancellationToken);
}

internal sealed class SetupEnvironment : ISetupEnvironment
{
    public Task<SetupAudioDevices> ListAudioAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new SetupAudioDevices(AudioDevices.List(DataFlow.Capture), AudioDevices.List(DataFlow.Render));
    }, cancellationToken);

    public Task ValidateAudioAsync(RecordingSources sources, CancellationToken cancellationToken) => Task.Run(() =>
    {
        sources.Validate();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var (selection, flow) in new[]
        {
            (sources.Microphone, DataFlow.Capture), (sources.Playback, DataFlow.Render)
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selection is null)
                continue;
            using var device = enumerator.GetDevice(selection.DeviceId);
            if (device.State != DeviceState.Active || device.DataFlow != flow)
                throw new InvalidOperationException(UiText.Format("SetupDeviceUnavailable", selection.Name));
            // Query access and the mix format without starting a capture or playing sound.
            using var client = device.AudioClient;
            if (client.MixFormat is not { SampleRate: > 0, Channels: > 0 })
                throw new InvalidOperationException(UiText.Format("SetupDeviceUnavailable", selection.Name));
        }
    }, cancellationToken);

    public bool SpeechRuntimeAvailable
    {
        get
        {
            foreach (var library in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" })
            {
                if (!NativeLibrary.TryLoad(library, typeof(SetupEnvironment).Assembly, DllImportSearchPath.System32, out var handle))
                    return false;
                NativeLibrary.Free(handle);
            }
            return true;
        }
    }

    public bool IsSpeechModelReady(string model) => SpeechModels.IsDownloaded(model);
    public bool SpeakerModelsReady => SpeakerModels.IsDownloaded;
    public Task DownloadSpeechAsync(string model, IProgress<double> progress, CancellationToken cancellationToken) =>
        SpeechModels.DownloadAsync(model, progress, cancellationToken);
    public Task DownloadSpeakersAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        SpeakerModels.DownloadAsync(progress, cancellationToken);
}

internal enum SetupResetMode { KeepSettings, ResetPreferences }

internal static class SetupValidation
{
    public static AppSettings Prepare(AppSettings settings, SetupResetMode mode) => mode switch
    {
        SetupResetMode.KeepSettings => System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings)) ?? throw new InvalidDataException("Could not copy setup settings."),
        SetupResetMode.ResetPreferences => new AppSettings(),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static void CheckFolder(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException(UiText.Get("InvalidSavingFolder"));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        var probe = Path.Combine(path, ".audiomemo-write-check-" + Guid.NewGuid().ToString("N"));
        using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose | FileOptions.WriteThrough);
        file.WriteByte(42);
        file.Flush(flushToDisk: true);
        cancellationToken.ThrowIfCancellationRequested();
        file.Position = 0;
        if (file.ReadByte() != 42)
            throw new IOException(UiText.Get("SetupFolderCheckFailed"));
    }
}
