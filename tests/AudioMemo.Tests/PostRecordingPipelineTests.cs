using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class PostRecordingPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpeakerAnalysisIsOptionalAndSummariesStillRun(bool identify)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            IdentifySpeakersOnStop = identify,
            SummarizeOnStop = true,
            SummaryProvider = SummaryProviders.Ollama,
            OllamaModel = "synthetic-model"
        };
        var session = AppStorage.CreateSession(settings, "Synthetic post-recording work");
        session.Frames = PcmFormat.SampleRate;
        var document = new TranscriptDocument
        {
            Revision = "synthetic-revision",
            ProcessedFrames = session.Frames,
            Segments = [new(0, 1, "A synthetic decision.") { SpeakerId = "spkr2" }],
            SpeakerNames = new() { ["spkr2"] = "Existing name" }
        };
        AppStorage.SaveTranscript(session, document);
        var before = File.ReadAllBytes(session.TranscriptPath);
        var speaker = new FakeSpeakers();
        var summary = new FakeSummary();
        var updates = new Updates();
        await new AiPipeline(summaryService: summary, speakerService: speaker)
            .CompleteTranscriptionAsync(session, settings, document, updates, CancellationToken.None);
        Assert.Equal(identify ? 1 : 0, speaker.Calls);
        Assert.Equal(1, summary.Calls);
        Assert.Equal(["main-points", "overall"], summary.Steps);
        Assert.True(File.Exists(session.SummaryPath));
        Assert.Contains("A synthetic overall summary.", AppStorage.LoadSummaryText(session));
        Assert.Equal(updates.Values[^1].Summary, AppStorage.LoadSummaryText(session));
        Assert.Equal(identify, AppStorage.LoadTranscript(session).SpeakersIdentified);
        Assert.Equal("A synthetic decision.", document.Segments[0].Text);
        Assert.Equal(0, document.Segments[0].StartSeconds);
        Assert.Equal(1, document.Segments[0].EndSeconds);
        Assert.DoesNotContain(updates.Values, update => update.IsWarning);
        if (!identify)
        {
            Assert.Equal(before, File.ReadAllBytes(session.TranscriptPath));
            Assert.Equal("spkr2", document.Segments[0].SpeakerId);
            Assert.Equal("Existing name", document.SpeakerNames["spkr2"]);
        }
    }

    [Fact]
    public async Task SpeakerFailuresAreReportedWithoutLosingTheTranscriptOrSkippingTheSummary()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            SummarizeOnStop = true,
            SummaryProvider = SummaryProviders.Ollama,
            OllamaModel = "synthetic-model"
        };
        var session = AppStorage.CreateSession(settings, "Synthetic speaker failure");
        var document = new TranscriptDocument { Segments = [new(0, 1, "Saved words.")] };
        AppStorage.SaveTranscript(session, document);
        var before = File.ReadAllBytes(session.TranscriptPath);
        var summary = new FakeSummary();
        var updates = new Updates();
        await new AiPipeline(summaryService: summary, speakerService: new FakeSpeakers { Error = new IOException("Synthetic failure") })
            .CompleteTranscriptionAsync(session, settings, document, updates, CancellationToken.None);
        Assert.Equal(before, File.ReadAllBytes(session.TranscriptPath));
        Assert.Contains(updates.Values, update => update.IsWarning && update.Status.Contains("Synthetic failure"));
        Assert.Equal(1, summary.Calls);
        Assert.Equal(["main-points", "overall"], summary.Steps);
    }

    [Fact]
    public async Task ExistingSpeakerLabelsAreNotReanalyzedAutomatically()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.Root };
        var session = AppStorage.CreateSession(settings, "Synthetic existing speakers");
        var document = new TranscriptDocument { SpeakersIdentified = true, Segments = [new(0, 1, "Saved words.")] };
        var speaker = new FakeSpeakers();
        await new AiPipeline(speakerService: speaker).CompleteTranscriptionAsync(session, settings, document, new Updates(), CancellationToken.None);
        Assert.Equal(0, speaker.Calls);
    }

    [Fact]
    public async Task CancellingSpeakerAnalysisDoesNotStartASummary()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.Root, SummarizeOnStop = true };
        var session = AppStorage.CreateSession(settings, "Synthetic speaker cancellation");
        var document = new TranscriptDocument { Segments = [new(0, 1, "Saved words.")] };
        var summary = new FakeSummary();
        var updates = new Updates();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AiPipeline(summaryService: summary, speakerService: new FakeSpeakers { Error = new OperationCanceledException() })
                .CompleteTranscriptionAsync(session, settings, document, updates, CancellationToken.None));
        Assert.Equal(0, summary.Calls);
        Assert.Empty(summary.Steps);
        Assert.DoesNotContain(updates.Values, update => update.IsWarning);
    }

    [Fact]
    public async Task DisablingAutomaticSummariesSkipsBothMainPointsAndTheOverallSummary()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        settings.SummarizeOnStop = false;
        settings.IdentifySpeakersOnStop = false;
        var session = AppStorage.CreateSession(settings, "Synthetic manual summaries");
        var document = new TranscriptDocument { Segments = [new(0, 1, "Saved words.")] };
        AppStorage.SaveTranscript(session, document);
        var summary = new FakeSummary();
        await new AiPipeline(summaryService: summary)
            .CompleteTranscriptionAsync(session, settings, document, new Updates(), CancellationToken.None);
        Assert.Empty(summary.Steps);
        Assert.False(File.Exists(session.SummaryPath));
    }

    private sealed class FakeSpeakers : ISpeakerService
    {
        public int Calls { get; private set; }
        public Exception? Error { get; init; }
        public Task IdentifyAsync(RecordingSession session, TranscriptDocument transcript, int count, CancellationToken cancellationToken,
            Action<AiWorkProgress>? progress = null)
        {
            Calls++;
            if (Error is not null)
                return Task.FromException(Error);
            SpeakerAssignment.Apply(transcript, [new SpeakerTurn(0, 1, 0)], cancellationToken);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSummary : ISummaryService
    {
        public int Calls { get; private set; }
        public List<string> Steps { get; } = [];
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previousSummary,
            bool live, Action<string>? warning, CancellationToken cancellationToken)
        {
            Calls++;
            Steps.Add("main-points");
            return Task.FromResult("A synthetic summary.");
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            Assert.Equal("A synthetic summary.", mainPoints);
            Steps.Add("overall");
            return Task.FromResult("A synthetic overall summary.");
        }
    }

    private sealed class Updates : IProgress<AiUpdate>
    {
        public List<AiUpdate> Values { get; } = [];
        public void Report(AiUpdate value) => Values.Add(value);
    }
}
