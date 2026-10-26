using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;

namespace AudioMemo.AI;

public sealed class SpeechWorkerConnection : ISpeechWorker
{
    private readonly Process process;
    private readonly Task<string> errors;
    public string Runtime { get; private set; } = "";
    public string? StartupWarning { get; private set; }

    private SpeechWorkerConnection(Process process)
    {
        this.process = process;
        errors = DrainErrorsAsync();
    }

    public static async Task<SpeechWorkerConnection> StartAsync(string executable, SpeechModel model,
        string language, string acceleration, bool draft, CancellationToken cancellationToken, IReadOnlyList<string>? vocabulary = null)
    {
        var prompt = SpeechVocabulary.CreatePrompt(vocabulary ?? []).Text;
        if (acceleration != "auto")
            return await StartSingleAsync(executable, model, language, acceleration, draft, prompt, cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartSingleAsync(executable, model, language, "vulkan", draft, prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            var cpu = await StartSingleAsync(executable, model, language, "cpu", draft, prompt, cancellationToken).ConfigureAwait(false);
            cpu.StartupWarning = ex.Message;
            return cpu;
        }
    }

    private static async Task<SpeechWorkerConnection> StartSingleAsync(string executable, SpeechModel model,
        string language, string acceleration, bool draft, string prompt, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "--speech-server", model.Path, language, acceleration, draft ? "draft" : "final" })
            start.ArgumentList.Add(argument);
        if (prompt.Length > 0)
            start.ArgumentList.Add("--with-hints");
        var process = new Process { StartInfo = start };
        if (!process.Start())
        {
            process.Dispose();
            throw new IOException("Could not start the isolated speech worker.");
        }
        var connection = new SpeechWorkerConnection(process);
        try
        {
            if (prompt.Length > 0)
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(prompt).AsMemory(), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var ready = await connection.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (ready.Kind != "ready" || string.IsNullOrWhiteSpace(ready.Runtime))
                throw new InvalidDataException("The speech worker did not report readiness.");
            connection.Runtime = ready.Runtime;
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string wavePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(wavePath).AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        var response = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (response.Kind != "result" || response.Segments is null ||
            response.Segments.Any(segment => !double.IsFinite(segment.StartSeconds) || !double.IsFinite(segment.EndSeconds) ||
                segment.StartSeconds < 0 || segment.EndSeconds < segment.StartSeconds))
            throw new InvalidDataException("Invalid speech-worker result.");
        return response.Segments;
    }

    private async Task<SpeechWorkerMessage> ReadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(deadline.Token).ConfigureAwait(false);
            if (line is null)
                throw new IOException("The speech worker stopped: " + await errors.WaitAsync(deadline.Token).ConfigureAwait(false));
            if (line.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("The speech worker returned too much text.");
            var result = JsonSerializer.Deserialize<SpeechWorkerMessage>(line) ?? throw new InvalidDataException("Empty speech response.");
            if (result.Kind == "error")
                throw new InvalidOperationException(result.Error ?? "The speech worker failed.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The speech worker timed out; saved audio is unaffected.");
        }
    }

    private async Task<string> DrainErrorsAsync()
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await process.StandardError.ReadAsync(buffer)) > 0)
        {
            if (text.Length + count > 64 * 1024)
                text.Remove(0, Math.Min(text.Length, count));
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                // Only this owned worker is terminated, never another recording or shared model server.
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await errors.ConfigureAwait(false);
        }
        finally { process.Dispose(); }
    }
}
