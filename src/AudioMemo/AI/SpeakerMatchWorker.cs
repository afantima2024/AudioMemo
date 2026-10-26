using System.IO;
using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

namespace AudioMemo.AI;

internal static class SpeakerMatchWorker
{
    internal static void Run(string embedding, string audio, string planPath)
    {
        var plan = JsonSerializer.Deserialize<SpeakerMatchPlan>(File.ReadAllText(planPath))
            ?? throw new InvalidDataException(UiText.Get("InvalidSpeakerMatches"));
        plan.Validate();
        using var input = new FileStream(audio, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (DurableWaveFile.ValidateAndGetFrames(input) != plan.Frames ||
            input.Length != DurableWaveFile.HeaderSize + plan.Frames * PcmFormat.BlockAlign)
            throw new InvalidDataException(UiText.Get("EditSourceChanged"));
        using var extractor = new SpeakerEmbeddingExtractor(new()
        {
            Model = embedding, NumThreads = 2, Provider = "cpu", Debug = 0
        });
        var progress = new SpeakerProgress(Console.Error);
        var matches = UnlabeledSpeakerMatching.Match(plan, range =>
        {
            var samples = ReadSamples(audio, range);
            if (samples.Length == 0 || samples.All(sample => Math.Abs(sample) < 1e-7))
                return null;
            using var stream = extractor.CreateStream();
            stream.AcceptWaveform(16_000, samples);
            stream.InputFinished();
            return extractor.IsReady(stream) ? extractor.Compute(stream) : null;
        }, progress: (completed, total) =>
        {
            progress.Report(completed, total, IntPtr.Zero);
            if (progress.Error is { } error)
                throw new IOException("Could not report speaker-matching progress.", error);
        });
        Console.WriteLine(JsonSerializer.Serialize(matches));
    }

    internal static float[] ReadSamples(string audio, AudioFrameRange range)
    {
        if (range.Length <= 0 || range.Length > UnlabeledSpeakerMatching.ReferenceFrames)
            throw new InvalidDataException(UiText.Get("InvalidSpeakerMatches"));
        using var pcm = new PcmRangeStream(audio, range.StartFrame, range.Length);
        using var wave = new RawSourceWaveStream(pcm, new WaveFormat(PcmFormat.SampleRate, 16, PcmFormat.Channels));
        var mono = new StereoToMonoSampleProvider(wave.ToSampleProvider()) { LeftVolume = 0.5f, RightVolume = 0.5f };
        var resampled = new WdlResamplingSampleProvider(mono, 16_000);
        var samples = new List<float>();
        var buffer = new float[4096];
        int received;
        while ((received = resampled.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, received).ToArray());
        return samples.ToArray();
    }
}
