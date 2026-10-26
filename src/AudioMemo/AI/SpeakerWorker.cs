using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Services;
using NAudio.Wave;
using SherpaOnnx;

namespace AudioMemo.AI;

public static class SpeakerWorker
{
    public static Task<int> RunAsync(string[] arguments)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            if (arguments.Length is not (5 or 6))
                throw new ArgumentException("Expected --speaker-worker <segmentation> <embedding> <16kHz-WAV> <speaker-count> [clustering-threshold].");
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            if (arguments[1] == "--unlabeled")
            {
                if (arguments.Length != 5)
                    throw new ArgumentException("Expected --speaker-worker --unlabeled <embedding> <safety-WAV> <plan-json>.");
                SpeakerMatchWorker.Run(arguments[2], arguments[3], arguments[4]);
                return Task.FromResult(0);
            }
            using var reader = new WaveFileReader(arguments[3]);
            if (reader.WaveFormat.SampleRate != 16_000 || reader.WaveFormat.Channels != 1)
                throw new InvalidDataException("Speaker analysis requires 16-kHz mono audio.");
            var count = reader.Length / reader.WaveFormat.BlockAlign;
            SpeakerService.CheckMemory(count, ComputerMemory.Read().AvailableBytes);
            var samples = new float[checked((int)count)];
            var source = reader.ToSampleProvider();
            var position = 0;
            int received;
            while (position < samples.Length && (received = source.Read(samples, position, samples.Length - position)) > 0)
                position += received;
            if (position != samples.Length)
                throw new EndOfStreamException("Incomplete speaker-analysis audio.");
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = arguments[1];
            config.Segmentation.Pyannote.WindowShiftRatio = 0.1f;
            config.Segmentation.NumThreads = 2;
            config.Embedding.Model = arguments[2];
            config.Embedding.NumThreads = 2;
            var speakers = int.Parse(arguments[4], CultureInfo.InvariantCulture);
            if (speakers is < 0 or > 20)
                throw new ArgumentOutOfRangeException(nameof(arguments));
            config.Clustering.NumClusters = speakers == 0 ? -1 : speakers;
            var threshold = arguments.Length == 6 ? float.Parse(arguments[5], CultureInfo.InvariantCulture) : 0.7f;
            if (!float.IsFinite(threshold) || threshold is < 0 or > 2)
                throw new ArgumentOutOfRangeException(nameof(arguments));
            config.Clustering.Threshold = threshold;
            using var diarizer = new OfflineSpeakerDiarization(config);
            if (diarizer.SampleRate != 16_000)
                throw new InvalidOperationException("Speaker model initialization failed.");
            var progress = new SpeakerProgress(Console.Error);
            var turns = diarizer.ProcessWithCallback(samples, progress.Report, IntPtr.Zero)
                .Select(turn => new SpeakerTurn(turn.Start, turn.End, turn.Speaker)).ToArray();
            if (progress.Error is { } error)
                throw new IOException("Could not report speaker-analysis progress.", error);
            if (turns.Length == 0)
                throw new InvalidOperationException("No distinct speech was found for speaker identification.");
            Console.WriteLine(JsonSerializer.Serialize(turns));
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return Task.FromResult(1);
        }
    }
}
