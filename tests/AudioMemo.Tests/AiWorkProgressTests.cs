using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class AiWorkProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedAndRefinementTranscriptionReportRealAudioProgressIncludingResume(bool refinement)
    {
        using var folder = new TestFolder();
        var session = SelectedTranscriptionResumeTests.MakeLongSession(folder);
        var audioBefore = File.ReadAllBytes(session.WavePath);
        var settings = new AppSettings
        {
            SaveFolder = folder.Root, SpeechModel = "base", Language = "en",
            IdentifySpeakersOnStop = false, SummarizeOnStop = false, LiveSummary = false
        };
        var document = new TranscriptDocument
        {
            Model = "base", Language = "en", Revision = "synthetic-timing",
            ProcessedFrames = 30L * PcmFormat.SampleRate,
            Segments = [new(0, 30, "Previously saved transcript.")]
        };
        if (refinement)
        {
            var checkpoint = TranscriptContinuation.CreateRefinement(document, 0, settings.SpeechModel, settings.Language);
            checkpoint.ProcessedFrames = document.ProcessedFrames;
            checkpoint.Segments = document.Copy().Segments;
            AtomicFile.WriteJson(Path.Combine(session.Folder, "transcript.refining.json"), checkpoint);
            document.IsDraft = true;
        }
        AppStorage.SaveTranscript(session, document);
        var updates = new Updates();
        var transcriber = new TranscriptCorrectionTests.FakeTranscriber();
        await new AiPipeline(transcriber, null, null, (_, _) => Task.CompletedTask)
            .TranscribeAsync(session, settings, () => (session.Frames, false), updates, CancellationToken.None);
        var phase = refinement ? "refinement" : "transcription";
        var fractions = updates.Values.Where(update => update.WorkProgress?.Phase == phase)
            .Select(update => update.WorkProgress!.Fraction).Distinct().ToArray();
        Assert.Equal(new double?[] { 30d / 65, 60d / 65, 1 }, fractions);
        Assert.Contains(updates.Values, update => update.WorkProgress == new AiWorkProgress("speech-model"));
        Assert.Equal(new AiWorkProgress("saving"), updates.Values[^1].WorkProgress);
        Assert.Equal(2, transcriber.Worker.Chunks.Count);
        Assert.Equal(session.Frames, AppStorage.LoadTranscript(session).ProcessedFrames);
        Assert.Equal(audioBefore, File.ReadAllBytes(session.WavePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryBatchesReportStepsButLiveSummariesDoNotReplaceTranscriptionProgress(bool live)
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic summary timing");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 90, new string('x', 9500))] });
        var service = new Summaries();
        var updates = new Updates();
        await new AiPipeline(summaryService: service).SummarizeAsync(session, settings, updates, live, CancellationToken.None);
        Assert.Equal(3, service.Batches);
        if (live)
        {
            Assert.All(updates.Values, update => Assert.Null(update.WorkProgress));
            Assert.Equal(0, service.Overall);
        }
        else
        {
            var fractions = updates.Values.Where(update => update.WorkProgress?.Phase == "main-points")
                .Select(update => update.WorkProgress!.Fraction).ToArray();
            Assert.Equal(new double?[] { 0, 1d / 3, 1d / 3, 2d / 3, 2d / 3, 1 }, fractions);
            Assert.Contains(updates.Values, update => update.WorkProgress == new AiWorkProgress("overall-summary"));
            Assert.Equal(new AiWorkProgress("saving"), updates.Values[^1].WorkProgress);
            Assert.Equal(1, service.Overall);
        }
    }

    [Fact]
    public async Task SpeakerFeatureProgressReachesTheUiWithoutClaimingThatGroupingIsMeasured()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.Root, IdentifySpeakersOnStop = true, SummarizeOnStop = false };
        var session = AppStorage.CreateSession(settings, "Synthetic speaker timing");
        var document = new TranscriptDocument { Segments = [new(0, 1, "Saved transcript.")] };
        var updates = new Updates();
        await new AiPipeline(speakerService: new Speakers())
            .CompleteTranscriptionAsync(session, settings, document, updates, CancellationToken.None);
        Assert.Equal(new AiWorkProgress?[]
        {
            new("speakers"), new("speaker-features", 0.25), new("speaker-features", 0.75),
            new("speaker-finalizing"), new("saving")
        }, updates.Values.Select(update => update.WorkProgress));
        Assert.True(AppStorage.LoadTranscript(session).SpeakersIdentified);
    }

    private sealed class Updates : IProgress<AiUpdate>
    {
        public List<AiUpdate> Values { get; } = [];
        public void Report(AiUpdate value) => Values.Add(value);
    }

    private sealed class Speakers : ISpeakerService
    {
        public Task IdentifyAsync(RecordingSession session, TranscriptDocument transcript, int count, CancellationToken token,
            Action<AiWorkProgress>? progress = null)
        {
            progress?.Invoke(new("speaker-features", 0.25));
            progress?.Invoke(new("speaker-features", 0.75));
            progress?.Invoke(new("speaker-finalizing"));
            transcript.SpeakersIdentified = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Summaries : ISummaryService
    {
        public int Batches { get; private set; }
        public int Overall { get; private set; }
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previousSummary,
            bool live, Action<string>? warning, CancellationToken token)
        {
            Batches++;
            return Task.FromResult("Synthetic main points.");
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints, Action<string>? warning,
            CancellationToken token)
        {
            Overall++;
            return Task.FromResult("Synthetic overall summary.");
        }
    }
}
