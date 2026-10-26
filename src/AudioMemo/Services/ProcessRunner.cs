using System.Diagnostics;
using System.IO;
using System.Text;

namespace AudioMemo.Services;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

public static class ProcessRunner
{
    public static Task<ProcessResult> RunAsync(ProcessStartInfo start, TimeSpan timeout,
        CancellationToken cancellationToken, Action<string>? errorLine = null) =>
        RunCoreAsync(start, timeout, (process, deadline) => ReadBoundedAsync(process.StandardOutput, deadline),
            cancellationToken, errorLine);

    public static Task<ProcessResult> RunStreamingAsync(ProcessStartInfo start, TimeSpan timeout,
        Func<Stream, CancellationToken, Task> consumeOutput, CancellationToken cancellationToken) =>
        RunCoreAsync(start, timeout, async (process, deadline) =>
        {
            try { await consumeOutput(process.StandardOutput.BaseStream, deadline.Token).ConfigureAwait(false); }
            catch { deadline.Cancel(); throw; }
            return "";
        }, cancellationToken);

    private static async Task<ProcessResult> RunCoreAsync(ProcessStartInfo start, TimeSpan timeout,
        Func<Process, CancellationTokenSource, Task<string>> readOutput, CancellationToken cancellationToken,
        Action<string>? errorLine = null)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new IOException($"Could not start {Path.GetFileName(start.FileName)}.");
        process.StandardInput.Close();
        try
        {
            var output = readOutput(process, deadline);
            var error = ReadBoundedAsync(process.StandardError, deadline, errorLine);
            await Task.WhenAll(output, error, process.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
            return new(process.ExitCode, await output, await error);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            if (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
                throw new TimeoutException($"{Path.GetFileName(start.FileName)} timed out. The saved recording is unaffected.");
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource deadline,
        Action<string>? onLine = null)
    {
        var result = new StringBuilder();
        var line = onLine is null ? null : new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (result.Length + count > 4 * 1024 * 1024)
                    throw new IOException("The external process returned too much output.");
                result.Append(buffer, 0, count);
                if (line is null)
                    continue;
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        onLine!(line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    else
                        line.Append(buffer[index]);
                }
            }
            if (line is { Length: > 0 })
                onLine!(line.ToString().TrimEnd('\r'));
            return result.ToString();
        }
        catch
        {
            deadline.Cancel();
            throw;
        }
    }
}
