using System.Buffers.Binary;
using AudioMemo.Core.Audio;

namespace AudioMemo.Tests;

internal sealed class TestFolder : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "AudioMemo.Tests", Guid.NewGuid().ToString("N"));
    public TestFolder() => Directory.CreateDirectory(Root);
    public string File(string name) => Path.Combine(Root, name);

    public string WriteTone(double seconds = 1, double amplitude = 0.4)
    {
        var path = File(Guid.NewGuid().ToString("N") + ".wav");
        using var writer = new DurableWaveFile(path);
        var frames = (int)(PcmFormat.SampleRate * seconds);
        var bytes = new byte[frames * PcmFormat.BlockAlign];
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = (short)(Math.Sin(frame * 2 * Math.PI * 440 / PcmFormat.SampleRate) * amplitude * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(frame * 4), sample);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(frame * 4 + 2), sample);
        }
        writer.Write(bytes);
        return path;
    }

    public void Dispose() => Directory.Delete(Root, true);
}
