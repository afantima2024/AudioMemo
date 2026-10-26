using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class SummaryFreshnessTests
{
    [Theory]
    [InlineData("text")]
    [InlineData("speaker-name")]
    [InlineData("speaker-id")]
    [InlineData("time")]
    [InlineData("revision")]
    public async Task MeaningfulChangesAreDetectedEvenWhenOldRevisionWasNotUpdated(string change)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = OllamaTests.LocalSettings();
        var service = new Capture();
        var pipeline = new AiPipeline(summaryService: service);
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        var transcript = AppStorage.LoadTranscript(session);
        Assert.True(AppStorage.HasCurrentSummary(session, transcript));
        var originalAudio = File.ReadAllBytes(session.WavePath);
        var originalNotes = File.ReadAllBytes(session.NotesPath);
        transcript = Mutate(transcript, change);
        AppStorage.SaveTranscript(session, transcript);
        Assert.True(AppStorage.SummaryNeedsRefresh(session, transcript));
        Assert.False(AppStorage.HasCurrentSummary(session, transcript));

        service.Batches.Clear();
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);

        Assert.Empty(service.Batches[0].Previous);
        Assert.True(AppStorage.HasCurrentSummary(session, transcript));
        Assert.Equal(SummaryInput.Fingerprint(transcript), AppStorage.LoadSummaryDocument(session)!.SourceFingerprint);
        Assert.Equal(originalAudio, File.ReadAllBytes(session.WavePath));
        Assert.Equal(originalNotes, File.ReadAllBytes(session.NotesPath));
        Assert.DoesNotContain(service.Batches, batch => batch.Text.Contains("PRIVATE NOTE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisplayColorsAndPersonalNotesDoNotInvalidateASummary()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = OllamaTests.LocalSettings();
        var service = new Capture();
        var pipeline = new AiPipeline(summaryService: service);
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        var state = File.ReadAllBytes(Path.Combine(session.Folder, "summary.json"));
        AppStorage.SetSpeakerColor(session, "spkr1", "#112233");
        AppStorage.SetSpeakerColor(session, null, "#445566");
        File.WriteAllText(session.NotesPath, "PRIVATE UPDATED NOTE");
        var transcript = AppStorage.LoadTranscript(session);
        transcript.Me = SelfSpeaker.Create("spkr1", "PRIVATE OPTIONAL ALIAS");
        AppStorage.SaveTranscript(session, transcript);
        Assert.True(AppStorage.HasCurrentSummary(session, transcript));
        Assert.False(AppStorage.SummaryNeedsRefresh(session, transcript));
        var calls = service.Batches.Count;
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        Assert.Equal(calls, service.Batches.Count);
        Assert.Equal(state, File.ReadAllBytes(Path.Combine(session.Folder, "summary.json")));
    }

    [Fact]
    public async Task RenamingASpeakerInvalidatesLegacySummariesAndStillUpdatesTheirDisplayedReferences()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = OllamaTests.LocalSettings();
        var transcript = AppStorage.LoadTranscript(session);
        var legacy = new SummaryDocument
        {
            Text = "[spkr1] owns this action.", OverallText = "Discuss the next steps.",
            OverallTextPolicyVersion = SummaryPrompt.OverallTextPolicyVersion,
            TextPolicyVersion = SummaryPrompt.TextPolicyVersion, TranscriptRevision = transcript.Revision,
            SummarizedSegments = transcript.Segments.Count, Model = settings.ActiveSummaryModel,
            Provider = settings.SummaryProvider, Endpoint = settings.ActiveSummaryEndpoint
        };
        AtomicFile.WriteJson(Path.Combine(session.Folder, "summary.json"), legacy);
        AtomicFile.WriteText(session.SummaryPath, AppStorage.LoadSummaryText(session));
        Assert.True(AppStorage.HasCurrentSummary(session, transcript));

        AppStorage.RenameSpeaker(session, "spkr1", "Corrected legacy name");

        var renamed = AppStorage.LoadTranscript(session);
        Assert.Equal(transcript.Revision, renamed.Revision);
        Assert.True(AppStorage.SummaryNeedsRefresh(session, renamed));
        Assert.False(AppStorage.HasCurrentSummary(session, renamed));
        Assert.Contains("Corrected legacy name", AppStorage.LoadSummaryText(session));
        Assert.Contains("Corrected legacy name", File.ReadAllText(session.SummaryPath));
        Assert.Null(AppStorage.LoadSummaryDocument(session)!.GeneratedAt);
        var service = new Capture();
        await new AiPipeline(summaryService: service).SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        Assert.Single(service.Batches);
        Assert.Empty(service.Batches[0].Previous);
        Assert.True(AppStorage.HasCurrentSummary(session, renamed));
    }

    [Fact]
    public async Task AppendingLiveTextKeepsTheExistingPrefixAndOnlySendsNewText()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic incremental summary");
        var transcript = new TranscriptDocument { Revision = "stable", Segments = [new(0, 1, "EARLIER TEXT")] };
        AppStorage.SaveTranscript(session, transcript);
        var service = new Capture();
        var pipeline = new AiPipeline(summaryService: service);
        await pipeline.SummarizeAsync(session, settings, new Updates(), true, CancellationToken.None);
        transcript.Segments.Add(new(1, 2, "APPENDED TEXT"));
        AppStorage.SaveTranscript(session, transcript);
        await pipeline.SummarizeAsync(session, settings, new Updates(), true, CancellationToken.None);
        Assert.Equal(2, service.Batches.Count);
        Assert.DoesNotContain("EARLIER TEXT", service.Batches[1].Text);
        Assert.Contains("APPENDED TEXT", service.Batches[1].Text);
        Assert.Equal("Synthetic main points 1.", service.Batches[1].Previous);
    }

    [Fact]
    public async Task ConfirmedRepeatCreatesAFreshGenerationAndCanBeRetriedIdempotently()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = OllamaTests.LocalSettings();
        var service = new Capture();
        var pipeline = new AiPipeline(summaryService: service);
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        Assert.True(AppStorage.HasCurrentSummary(session, AppStorage.LoadTranscript(session)));
        var generation = Guid.NewGuid().ToString("N");
        settings.OllamaModel = "new-selected-model";
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None, generation);
        Assert.Equal(2, service.Batches.Count);
        Assert.Empty(service.Batches[1].Previous);
        Assert.Equal(2, service.OverallCalls);
        Assert.Equal(generation, AppStorage.LoadSummaryDocument(session)!.GenerationId);
        Assert.Equal("new-selected-model", AppStorage.LoadSummaryDocument(session)!.Model);
        var hashes = EditedSessionTests.Hashes(session.Folder);
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None, generation);
        Assert.Equal(2, service.Batches.Count);
        Assert.Equal(hashes, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task AFailedFreshRequestPreservesThePreviousSummaryAndItsModel()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = OllamaTests.LocalSettings();
        await new AiPipeline(summaryService: new Capture()).SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        var hashes = EditedSessionTests.Hashes(session.Folder);
        settings.OllamaModel = "different-model";
        await Assert.ThrowsAsync<IOException>(() => new AiPipeline(summaryService: new Capture { Fail = true })
            .SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None, Guid.NewGuid().ToString("N")));
        Assert.Equal(hashes, EditedSessionTests.Hashes(session.Folder));
        Assert.NotEqual(settings.OllamaModel, AppStorage.LoadSummaryDocument(session)!.Model);
    }

    [Fact]
    public async Task ARepeatCancelledBeforeItsFirstCheckpointStillRunsWhenTheSavedPlanResumes()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = OllamaTests.LocalSettings();
        await new AiPipeline(summaryService: new Capture()).SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        var generation = Guid.NewGuid().ToString("N");
        PendingProcessingStore.Defer(session, [ProcessingWork.ForAi("UpdateMainPoints", settings, generation)]);
        var before = EditedSessionTests.Hashes(session.Folder);
        using var cancellation = new CancellationTokenSource();
        var cancelled = new Capture { StartingBatch = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiPipeline(summaryService: cancelled)
            .SummarizeAsync(session, settings, new Updates(), false, cancellation.Token, generation));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        Assert.Empty(cancelled.Batches);
        var work = Assert.Single(PendingProcessingStore.Load(session)!.Work);
        var resumed = new Capture();

        await new AiPipeline(summaryService: resumed).SummarizeAsync(session, settings, new Updates(), false,
            CancellationToken.None, work.SummaryGenerationId);

        Assert.Single(resumed.Batches);
        Assert.Empty(resumed.Batches[0].Previous);
        Assert.Equal(generation, AppStorage.LoadSummaryDocument(session)!.GenerationId);
    }

    [Fact]
    public async Task APartiallySavedRepeatResumesWithoutRepeatingItsSuccessfulBatches()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = OllamaTests.LocalSettings();
        var transcript = AppStorage.LoadTranscript(session);
        transcript.Segments[0] = transcript.Segments[0] with { Text = new string('x', 9500) };
        AppStorage.SaveTranscript(session, transcript);
        await new AiPipeline(summaryService: new Capture()).SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        var generation = Guid.NewGuid().ToString("N");
        PendingProcessingStore.Defer(session, [ProcessingWork.ForAi("UpdateMainPoints", settings, generation)]);
        var interrupted = new Capture { FailOnAttempt = 2 };
        await Assert.ThrowsAsync<IOException>(() => new AiPipeline(summaryService: interrupted)
            .SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None, generation));
        var checkpoint = AppStorage.LoadSummaryDocument(session)!;
        Assert.True(checkpoint.Provisional);
        Assert.Equal(generation, checkpoint.GenerationId);
        Assert.Single(interrupted.Batches);
        var remaining = SummaryPrompt.ResumableBatches(transcript.Segments,
            checkpoint.SummarizedSegments, checkpoint.SegmentOffset, 4000).Select(batch => batch.Text).ToArray();
        var resumed = new Capture();

        await new AiPipeline(summaryService: resumed).SummarizeAsync(session, settings, new Updates(), false,
            CancellationToken.None, Assert.Single(PendingProcessingStore.Load(session)!.Work).SummaryGenerationId);

        Assert.Equal(remaining, resumed.Batches.Select(batch => batch.Text).ToArray());
        Assert.Equal(checkpoint.Text, resumed.Batches[0].Previous);
        Assert.Equal(generation, AppStorage.LoadSummaryDocument(session)!.GenerationId);
        Assert.True(AppStorage.HasCurrentSummary(session, transcript));
    }

    [Fact]
    public void ForcedSummaryIntentSurvivesCloseAndRejectsInvalidQueueIdentifiers()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var generation = Guid.NewGuid().ToString("N");
        var item = ProcessingWork.ForAi("UpdateMainPoints", OllamaTests.LocalSettings(), generation);
        PendingProcessingStore.Defer(session, [item]);
        var plan = PendingProcessingStore.Load(session)!;
        Assert.Equal(generation, Assert.Single(plan.Work).SummaryGenerationId);
        Assert.Throws<InvalidDataException>(() => PendingProcessingStore.Save(session, plan with
        {
            Work = [item with { SummaryGenerationId = "invalid" }]
        }));
        Assert.Throws<InvalidDataException>(() => PendingProcessingStore.Save(session, plan with
        {
            Work = [item with { Kind = ProcessingWorkKind.Speakers }]
        }));
        Assert.Equal(generation, Assert.Single(PendingProcessingStore.Load(session)!.Work).SummaryGenerationId);
    }

    [Fact]
    public async Task InheritedCliSelectionIsResolvedOnceAndSavedWithoutChangingPreferences()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = new AppSettings { CloudConsent = true, SummaryModel = "" };
        var service = new Capture { ResolvedModel = "resolved-test-model" };
        await new AiPipeline(summaryService: service).SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        Assert.Equal("resolved-test-model", AppStorage.LoadSummaryDocument(session)!.Model);
        Assert.All(service.Models, model => Assert.Equal("resolved-test-model", model));
        Assert.Empty(settings.SummaryModel);
    }

    [Fact]
    public void FingerprintsIgnoreDictionaryOrderAndUnusedNamesButIncludeSpokenNames()
    {
        var first = new TranscriptDocument
        {
            Segments = [new(0, 1, "Text") { SpeakerId = "spkr1" }],
            SpeakerNames = new() { ["unused"] = "Not spoken", ["spkr1"] = "Alex" }
        };
        var second = first.Copy();
        second.SpeakerNames = new() { ["spkr1"] = "Alex", ["unused"] = "Different unused name" };
        Assert.Equal(SummaryInput.Fingerprint(first), SummaryInput.Fingerprint(second));
        second.SpeakerNames["spkr1"] = "Jamie";
        Assert.NotEqual(SummaryInput.Fingerprint(first), SummaryInput.Fingerprint(second));
        Assert.Throws<InvalidDataException>(() => SummaryInput.Fingerprint(first, 2));
    }

    private static TranscriptDocument Mutate(TranscriptDocument transcript, string change)
    {
        switch (change)
        {
            case "text": transcript.Segments[0] = transcript.Segments[0] with { Text = "CORRECTED CONTENT" }; break;
            case "speaker-name": transcript.RenameSpeaker("spkr1", "Corrected name"); break;
            case "speaker-id": transcript.Segments[0] = transcript.Segments[0] with { SpeakerId = "spkr2" }; break;
            case "time": transcript.Segments[0] = transcript.Segments[0] with { StartSeconds = 0.1 }; break;
            case "revision": transcript.Revision = "re-transcribed"; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        return transcript;
    }

    internal sealed class Capture : ISummaryService
    {
        public string? ResolvedModel { get; init; }
        public bool Fail { get; init; }
        public int FailOnAttempt { get; init; }
        public Action? StartingBatch { get; init; }
        private int attempts;
        public List<(string Text, string Previous)> Batches { get; } = [];
        public List<string> Models { get; } = [];
        public int OverallCalls { get; private set; }
        public string ResolveModel(AppSettings settings) => ResolvedModel ?? settings.ActiveSummaryModel;
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            StartingBatch?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Fail || ++attempts == FailOnAttempt)
                throw new IOException("Synthetic summary failure.");
            Batches.Add((transcript, previous));
            Models.Add(settings.ActiveSummaryModel);
            return Task.FromResult($"Synthetic main points {Batches.Count}.");
        }
        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints, Action<string>? warning,
            CancellationToken cancellationToken)
        {
            OverallCalls++;
            Models.Add(settings.ActiveSummaryModel);
            return Task.FromResult("Overall synthetic notes.");
        }
    }

    private sealed class Updates : IProgress<AiUpdate>
    {
        public void Report(AiUpdate value) { }
    }
}
