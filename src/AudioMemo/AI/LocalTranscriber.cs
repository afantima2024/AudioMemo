using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.AI;

public interface ISpeechWorker : IAsyncDisposable
{
    string Runtime { get; }
    string? StartupWarning { get; }
    Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string wavePath, CancellationToken cancellationToken);
}

public interface ILocalTranscriber
{
    string WorkerExecutable { get; }
    Task<ISpeechWorker> OpenAsync(SpeechModel model, string language, string acceleration, bool draft,
        CancellationToken cancellationToken, IReadOnlyList<string>? vocabulary = null);
}

public sealed class LocalTranscriber(string? workerExecutable = null) : ILocalTranscriber
{
    public string WorkerExecutable => workerExecutable ?? Path.Combine(AppContext.BaseDirectory, "AudioMemo.exe");

    public async Task<ISpeechWorker> OpenAsync(SpeechModel model, string language, string acceleration,
        bool draft, CancellationToken cancellationToken, IReadOnlyList<string>? vocabulary = null) =>
        await SpeechWorkerConnection.StartAsync(WorkerExecutable,
            model, language, acceleration, draft, cancellationToken, vocabulary).ConfigureAwait(false);

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string wavePath, SpeechModel model,
        string language, CancellationToken cancellationToken)
    {
        var executable = WorkerExecutable;
        if (!File.Exists(executable))
            throw new FileNotFoundException("The AudioMemo speech worker executable was not found.", executable);
        var start = new ProcessStartInfo(executable) { WorkingDirectory = AppContext.BaseDirectory };
        foreach (var argument in new[] { "--speech-worker", model.Path, wavePath, language })
            start.ArgumentList.Add(argument);
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(UiText.Format("TranscriptionFailed", result.ExitCode, result.Error.Trim()));
        var segments = new List<TranscriptSegment>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segment = JsonSerializer.Deserialize<TranscriptSegment>(line)
                ?? throw new InvalidDataException("The speech worker returned an empty segment.");
            if (!double.IsFinite(segment.StartSeconds) || !double.IsFinite(segment.EndSeconds) ||
                segment.StartSeconds < 0 || segment.EndSeconds < segment.StartSeconds)
                throw new InvalidDataException("The speech worker returned invalid timestamps.");
            segments.Add(segment);
        }
        return segments;
    }
}
