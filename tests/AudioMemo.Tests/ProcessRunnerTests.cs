using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class ProcessRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotReviewOutputPreservesExactUnicodeAndEscapesAcrossTheProcessBoundary(bool rawWhitespace)
    {
        const string quote = "\u4e7e";
        const string reason = "\u8acb\u78ba\u8a8d \"clickpilot\" \\n C:\\audio first\nsecond";
        var response = JsonSerializer.Serialize(new { items = new[] { new { segment = 0, quote, reason } } },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (rawWhitespace)
            response = response.Replace("first\\nsecond", "first\nsecond", StringComparison.Ordinal);
        var output = "\u001b[32m" + response + "\u001b[0m\r\n";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(output));
        var result = await ProcessRunner.RunAsync(Shell($"""
            [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
            [Console]::Out.Write([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{base64}')))
            """), TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(output, result.Output);
        Assert.Equal(response, CopilotService.ReadResponse(result, normalizeText: false));
        var item = Assert.Single(TranscriptReview.Parse(CopilotService.ReadResponse(result, normalizeText: false),
            new([new(0, 0, "\u4e7e\u5764")])));
        Assert.Equal(quote, item.Quote);
        Assert.Equal(ChineseText.ToSimplified(reason), item.Reason);
        Assert.Equal(ChineseText.ToSimplified(response), CopilotService.ReadResponse(result));
        Assert.NotEqual(response, CopilotService.ReadResponse(result));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopilotStructuredAndProseResponsesStillRejectFailureEmptyAndOversizedOutput(bool normalizeText)
    {
        Assert.Throws<InvalidOperationException>(() => CopilotService.ReadResponse(
            new(1, "", "Synthetic CLI failure."), normalizeText));
        Assert.Throws<InvalidDataException>(() => CopilotService.ReadResponse(new(0, " \r\n", ""), normalizeText));
        Assert.Throws<InvalidDataException>(() => CopilotService.ReadResponse(new(0, new string('x', 8001), ""), normalizeText));
    }

    [Fact]
    public async Task ErrorLinesAreStreamedWithoutChangingCapturedOutput()
    {
        var start = Shell("""
            [Console]::Error.Write('first')
            [Console]::Error.Flush()
            Start-Sleep -Milliseconds 50
            [Console]::Error.Write(" line`r`nsecond`nlast")
            [Console]::Out.Write('synthetic result')
            """);
        var lines = new List<string>();
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(20), CancellationToken.None, lines.Add);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["first line", "second", "last"], lines);
        Assert.Equal("first line\r\nsecond\nlast", result.Error);
        Assert.Equal("synthetic result", result.Output);
    }

    [Fact]
    public async Task ProgressArrivesBeforeTheChildExitsAndCancellationStopsIt()
    {
        using var cancellation = new CancellationTokenSource();
        var update = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ProcessRunner.RunAsync(Shell("""
            [Console]::Error.WriteLine('synthetic progress')
            [Console]::Error.Flush()
            Start-Sleep -Seconds 30
            """), TimeSpan.FromSeconds(40), cancellation.Token, line => update.TrySetResult(line));
        try
        {
            Assert.Equal("synthetic progress", await update.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(run.IsCompleted);
        }
        finally { cancellation.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task AProgressConsumerFailureStopsTheChildWithoutWaitingForTimeout()
    {
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ProcessRunner.RunAsync(Shell("""
            [Console]::Error.WriteLine('synthetic progress')
            [Console]::Error.Flush()
            Start-Sleep -Seconds 30
            """), TimeSpan.FromSeconds(40), CancellationToken.None,
            _ => throw new InvalidDataException("Synthetic progress failure")));
        Assert.Equal("Synthetic progress failure", error.Message);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task StreamingDiagnosticsStillEnforceTheOutputLimit()
    {
        var error = await Assert.ThrowsAsync<IOException>(() => ProcessRunner.RunAsync(Shell("""
            [Console]::Error.Write('x' * (4 * 1024 * 1024 + 1))
            [Console]::Error.Flush()
            Start-Sleep -Seconds 30
            """), TimeSpan.FromSeconds(40), CancellationToken.None, _ => { }));
        Assert.Contains("too much output", error.Message);
    }

    private static ProcessStartInfo Shell(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"));
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-OutputFormat", "Text", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'\n" + script)) })
            start.ArgumentList.Add(argument);
        return start;
    }
}
