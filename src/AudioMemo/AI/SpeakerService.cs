using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using AudioMemo.Core.Audio;
using AudioMemo.Services;

namespace AudioMemo.AI;

public interface ISpeakerService
{
    Task IdentifyAsync(RecordingSession session, TranscriptDocument transcript, int count, CancellationToken cancellationToken,
        Action<AiWorkProgress>? progress = null);
    Task<SpeakerMatchResult[]> MatchUnlabeledAsync(RecordingSession session, SpeakerMatchPlan plan,
        CancellationToken cancellationToken, Action<AiWorkProgress>? progress = null) =>
        throw new NotSupportedException("Unlabeled speaker matching is not supported by this speaker service.");
}

public sealed class SpeakerService(string? workerExecutable = null) : ISpeakerService
{
    public async Task<SpeakerMatchResult[]> MatchUnlabeledAsync(RecordingSession session, SpeakerMatchPlan plan,
        CancellationToken cancellationToken, Action<AiWorkProgress>? progress = null)
    {
        plan.Validate();
        await SpeakerModels.VerifyAsync(cancellationToken);
        Directory.CreateDirectory(AppStorage.WorkFolder);
        var temporary = Path.Combine(AppStorage.WorkFolder, $"speaker-matches-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(plan), cancellationToken);
            var start = new ProcessStartInfo(workerExecutable ?? Path.Combine(AppContext.BaseDirectory, "AudioMemo.exe"));
            foreach (var argument in new[] { "--speaker-worker", "--unlabeled", SpeakerModels.Embedding, session.WavePath, temporary })
                start.ArgumentList.Add(argument);
            var result = await ProcessRunner.RunAsync(start, TimeSpan.FromHours(4), cancellationToken, line =>
            {
                if (SpeakerProgress.Parse(line) is { } update)
                    progress?.Invoke(update);
            });
            if (result.ExitCode != 0)
            {
                var error = string.Join(Environment.NewLine, result.Error.Split('\n')
                    .Where(line => !line.StartsWith(SpeakerProgress.Prefix, StringComparison.Ordinal))).Trim();
                throw new InvalidOperationException(error.Length > 0 ? error : $"Speaker matching exited with code {result.ExitCode}.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return JsonSerializer.Deserialize<SpeakerMatchResult[]>(result.Output)
                ?? throw new InvalidDataException("Empty speaker-matching output.");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public async Task IdentifyAsync(RecordingSession session, TranscriptDocument transcript, int count,
        CancellationToken cancellationToken, Action<AiWorkProgress>? progress = null)
    {
        progress?.Invoke(new("speakers"));
        CheckMemory(checked((session.Frames + 2) / (PcmFormat.SampleRate / 16_000)), ComputerMemory.Read().AvailableBytes);
        await SpeakerModels.VerifyAsync(cancellationToken);
        Directory.CreateDirectory(AppStorage.WorkFolder);
        var temporary = Path.Combine(AppStorage.WorkFolder, $"speakers-{Guid.NewGuid():N}.wav");
        try
        {
            await Task.Run(() => AudioExport.WriteSpeechChunk(session.WavePath, temporary, 0, session.Frames, cancellationToken), cancellationToken);
            var start = new ProcessStartInfo(workerExecutable ?? Path.Combine(AppContext.BaseDirectory, "AudioMemo.exe"));
            foreach (var argument in new[] { "--speaker-worker", SpeakerModels.Segmentation, SpeakerModels.Embedding,
                temporary, count.ToString(CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            var result = await ProcessRunner.RunAsync(start, TimeSpan.FromHours(4), cancellationToken, line =>
            {
                if (SpeakerProgress.Parse(line) is { } update)
                    progress?.Invoke(update);
            });
            if (result.ExitCode != 0)
            {
                var error = string.Join(Environment.NewLine, result.Error.Split('\n')
                    .Where(line => !line.StartsWith(SpeakerProgress.Prefix, StringComparison.Ordinal))).Trim();
                throw new InvalidOperationException(error.Length > 0 ? error : $"Speaker analysis exited with code {result.ExitCode}.");
            }
            var turns = JsonSerializer.Deserialize<SpeakerTurn[]>(result.Output)
                ?? throw new InvalidDataException("Empty speaker-analysis output.");
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(new("speaker-finalizing"));
            SpeakerAssignment.Apply(transcript, turns, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void CheckMemory(long samples, ulong availableBytes)
    {
        var embeddingRows = Math.Ceiling(samples / 16_000d + 10) * 3;
        // Complete-link clustering uses a quadratic distance matrix and 32-bit native indexing.
        var required = samples * 12d + embeddingRows * (embeddingRows - 1) * 4 + 512d * 1024 * 1024;
        if (samples <= 0 || samples > Array.MaxLength || samples * 2 + 44 > uint.MaxValue ||
            embeddingRows > 46_000 || required > availableBytes / 2d)
            throw new InvalidOperationException("This recording exceeds the safe memory/size limit for speaker analysis. Transcription and saved audio are unaffected.");
    }
}
