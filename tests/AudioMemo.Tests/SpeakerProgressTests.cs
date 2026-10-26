using AudioMemo.AI;

namespace AudioMemo.Tests;

public sealed class SpeakerProgressTests
{
    [Fact]
    public void NativeProgressIsThrottledAndDoesNotClaimThatClusteringHasFinished()
    {
        using var output = new StringWriter();
        var progress = new SpeakerProgress(output);
        for (var completed = 0; completed <= 10_000; completed++)
            Assert.Equal(0, progress.Report(completed, 10_000, IntPtr.Zero));
        Assert.Null(progress.Error);
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(101, lines.Length);
        var updates = lines.Select(line => Assert.IsType<AiWorkProgress>(SpeakerProgress.Parse(line))).ToArray();
        Assert.Equal(new AiWorkProgress("speaker-features", 0), updates[0]);
        Assert.Equal(new AiWorkProgress("speaker-features", 0.5), updates[50]);
        Assert.Equal(new AiWorkProgress("speaker-finalizing"), updates[^1]);
    }

    [Fact]
    public void UnrelatedNativeDiagnosticsRemainDiagnostics()
    {
        Assert.Null(SpeakerProgress.Parse("Synthetic native warning"));
        Assert.Null(SpeakerProgress.Parse(""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("-1/4")]
    [InlineData("1/0")]
    [InlineData("4/3")]
    [InlineData("1.0/4")]
    [InlineData("1/2/3")]
    [InlineData("1/9999999999999")]
    public void MalformedProgressIsReportedAsAnError(string counts) =>
        Assert.Throws<InvalidDataException>(() => SpeakerProgress.Parse(SpeakerProgress.Prefix + counts));

    [Fact]
    public void CallbackFailuresAreRetainedWithoutEscapingAcrossNativeFrames()
    {
        var writer = new FailingWriter();
        var progress = new SpeakerProgress(writer);
        Assert.Equal(0, progress.Report(1, 4, IntPtr.Zero));
        Assert.Equal("Synthetic pipe failure", Assert.IsType<IOException>(progress.Error).Message);
        Assert.Equal(0, progress.Report(2, 4, IntPtr.Zero));
        Assert.Equal(1, writer.Calls);
        var invalid = new SpeakerProgress(new StringWriter());
        Assert.Equal(0, invalid.Report(5, 4, IntPtr.Zero));
        Assert.IsType<InvalidDataException>(invalid.Error);
    }

    private sealed class FailingWriter : StringWriter
    {
        public int Calls { get; private set; }
        public override void WriteLine(string? value)
        {
            Calls++;
            throw new IOException("Synthetic pipe failure");
        }
    }
}
