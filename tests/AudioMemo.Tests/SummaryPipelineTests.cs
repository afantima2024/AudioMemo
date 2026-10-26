using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class SummaryPipelineTests
{
    [Fact]
    public void BatchCheckpointsPreserveEveryCharacterIncludingSurrogatePairs()
    {
        var segments = new[]
        {
            new TranscriptSegment(0, 10, new string('x', 3990) + "\ud83d\ude00" + new string('y', 12000)),
            new TranscriptSegment(10, 20, "Final decision.")
        };
        var all = SummaryPrompt.ResumableBatches(segments, maximumCharacters: 4000).ToArray();
        foreach (var (batch, index) in all.Select((batch, index) => (batch, index)))
        {
            var resumed = SummaryPrompt.ResumableBatches(segments, batch.NextSegment, batch.NextOffset, 4000);
            Assert.Equal(string.Concat(all.Skip(index + 1).Select(part => part.Text)), string.Concat(resumed.Select(part => part.Text)));
            Assert.False(char.IsHighSurrogate(batch.Text[^1]));
        }
        Assert.Equal(string.Concat(SummaryPrompt.Batches(segments)), string.Concat(all.Select(batch => batch.Text)));
    }

    [Fact]
    public void AWordSplitAcrossBatchesIsNotReconvertedWithoutItsPhraseContext()
    {
        const int maximumCharacters = 4000;
        var padding = new string('x', maximumCharacters - "[00:00:00 - 00:00:10] ".Length - 1);
        var transcript = new[] { new TranscriptSegment(0, 10, padding + "\u4e7e\u9686") };
        var batches = SummaryPrompt.ResumableBatches(transcript, maximumCharacters: maximumCharacters).ToArray();
        Assert.EndsWith("\u4e7e", batches[0].Text);
        Assert.StartsWith("\u9686", batches[1].Text);
        foreach (var batch in batches)
        {
            var prompt = SummaryPrompt.Create(batch.Text, "", false);
            using var payload = JsonDocument.Parse(prompt[prompt.IndexOf("{\"previousSummary\"", StringComparison.Ordinal)..]);
            Assert.Equal(batch.Text, payload.RootElement.GetProperty("transcript").GetString());
        }
    }

    [Fact]
    public async Task FailedSummaryResumesFromTheLastSavedBatchWithoutResendingIt()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic checkpoint test");
        var transcript = new TranscriptDocument
        {
            Segments = [new(0, 90, new string('x', 9500) + "\ud83d\ude00")]
        };
        AppStorage.SaveTranscript(session, transcript);
        File.WriteAllText(session.NotesPath, "Private test note, never sent.");
        var service = new FakeSummaryService { FailOnCall = 2 };
        var pipeline = new AiPipeline(summaryService: service);

        await Assert.ThrowsAsync<IOException>(() => pipeline.SummarizeAsync(
            session, settings, new Updates(), false, CancellationToken.None));
        var checkpoint = ReadSummary(session);
        Assert.Equal(0, checkpoint.SummarizedSegments);
        Assert.Equal(4000, checkpoint.SegmentOffset);
        Assert.True(checkpoint.Provisional);
        Assert.Equal(SummaryPrompt.TextPolicyVersion, checkpoint.TextPolicyVersion);
        Assert.Equal("Notes 1", File.ReadAllText(session.SummaryPath));

        var resumedService = new FakeSummaryService();
        await new AiPipeline(summaryService: resumedService).SummarizeAsync(
            session, settings, new Updates(), false, CancellationToken.None);
        var completed = ReadSummary(session);
        Assert.Equal(1, completed.SummarizedSegments);
        Assert.Equal(0, completed.SegmentOffset);
        Assert.False(completed.Provisional);
        Assert.Equal(SummaryProviders.Ollama, completed.Provider);
        Assert.Equal("http://127.0.0.1:11434/", completed.Endpoint);
        Assert.Equal("Notes 1", resumedService.Calls[0].Previous);
        Assert.Equal(completed.Text, Assert.Single(resumedService.OverallCalls));
        Assert.True(completed.HasOverallSummary);
        Assert.Equal(completed.ToText(new UiText(settings.UiLanguage)), File.ReadAllText(session.SummaryPath));
        Assert.Equal(string.Concat(SummaryPrompt.Batches(transcript.Segments)),
            service.Calls[0].Transcript + string.Concat(resumedService.Calls.Select(call => call.Transcript)));
        Assert.DoesNotContain(resumedService.Calls, call => call.Transcript.Contains("Private test note", StringComparison.Ordinal));
        Assert.Equal("Private test note, never sent.", File.ReadAllText(session.NotesPath));
    }

    [Fact]
    public async Task SwitchingProvidersDoesNotReuseTheOtherProvidersSummaryState()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic provider change");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 10, "A meeting on Friday.")] });
        AtomicFile.WriteJson(Path.Combine(session.Folder, "summary.json"), new SummaryDocument
        {
            Model = settings.OllamaModel,
            Provider = SummaryProviders.Copilot,
            SummarizedSegments = 1,
            Text = "Old provider notes."
        });
        var service = new FakeSummaryService();
        await new AiPipeline(summaryService: service).SummarizeAsync(
            session, settings, new Updates(), false, CancellationToken.None);
        Assert.Empty(Assert.Single(service.Calls).Previous);
        Assert.Contains("Friday", service.Calls[0].Transcript);
        Assert.Equal(SummaryProviders.Ollama, ReadSummary(session).Provider);
    }

    [Fact]
    public async Task LocalFailureDoesNotOverwriteAnExistingSavedSummary()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic failure");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 10, "Synthetic content")] });
        File.WriteAllText(session.SummaryPath, "Existing saved notes.");
        var service = new FakeSummaryService { FailOnCall = 1 };
        await Assert.ThrowsAsync<IOException>(() => new AiPipeline(summaryService: service).SummarizeAsync(
            session, settings, new Updates(), false, CancellationToken.None));
        Assert.Equal("Existing saved notes.", File.ReadAllText(session.SummaryPath));
    }

    [Fact]
    public async Task LiveSummaryCanBeFinalizedWithoutAddingNewTranscriptText()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic live summary");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 10, "The decision is final.")] });
        var service = new FakeSummaryService();
        var pipeline = new AiPipeline(summaryService: service);
        await pipeline.SummarizeAsync(session, settings, new Updates(), true, CancellationToken.None);
        Assert.True(ReadSummary(session).Provisional);
        Assert.Empty(service.OverallCalls);
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        Assert.Equal(2, service.Calls.Count);
        Assert.Empty(service.Calls[1].Transcript);
        Assert.False(service.Calls[1].Live);
        Assert.False(ReadSummary(session).Provisional);
        Assert.Single(service.OverallCalls);
    }

    [Fact]
    public async Task AnUnchangedCompletedSummaryDoesNotRewriteFilesOrRequestAiAgain()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic unchanged summary");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 10, "A synthetic decision.")] });
        var service = new FakeSummaryService();
        var pipeline = new AiPipeline(summaryService: service);
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        var before = EditedSessionTests.Hashes(session.Folder);
        using var stateReader = new FileStream(Path.Combine(session.Folder, "summary.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var exportReader = new FileStream(session.SummaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);

        Assert.Single(service.Calls);
        Assert.Single(service.OverallCalls);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task ACompletedBatchIsNotSavedAgainAfterReportingItsCheckpoint()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic completed checkpoint");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 10, "A synthetic decision.")] });
        FileStream? reader = null;
        var updates = new Updates
        {
            Reported = update =>
            {
                if (update.Summary is not null && reader is null)
                    reader = new FileStream(Path.Combine(session.Folder, "summary.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        };
        try
        {
            var service = new FakeSummaryService
            {
                StartingOverall = () =>
                {
                    reader?.Dispose();
                    reader = null;
                }
            };
            await new AiPipeline(summaryService: service).SummarizeAsync(
                session, settings, updates, false, CancellationToken.None);
            Assert.False(ReadSummary(session).Provisional);
            Assert.Equal(ReadSummary(session).ToText(new UiText(settings.UiLanguage)), updates.Values[^1].Summary);
            Assert.Single(service.Calls);
            Assert.Single(service.OverallCalls);
        }
        finally { reader?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrStaleMarkdownIsRepairedWithoutRewritingTheCheckpointOrCallingAi(bool missing)
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic export repair");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 10, "A synthetic decision.")] });
        var service = new FakeSummaryService();
        var pipeline = new AiPipeline(summaryService: service);
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        if (missing)
            File.Delete(session.SummaryPath);
        else
            File.WriteAllText(session.SummaryPath, "Stale export");
        using var reader = new FileStream(Path.Combine(session.Folder, "summary.json"), FileMode.Open, FileAccess.Read, FileShare.Read);

        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);

        Assert.Single(service.Calls);
        Assert.Single(service.OverallCalls);
        Assert.Equal(ReadSummary(session).ToText(new UiText(settings.UiLanguage)), AtomicFile.ReadText(session.SummaryPath));
    }

    [Fact]
    public async Task AnExportFailureKeepsTheCheckpointAndRetryDoesNotRepeatTheAiRequest()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic locked export");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 10, "A synthetic decision.")] });
        File.WriteAllText(session.SummaryPath, "Existing markdown");
        var service = new FakeSummaryService();
        var pipeline = new AiPipeline(summaryService: service);
        using (var reader = new FileStream(session.SummaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<IOException>(() =>
                pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None));
            Assert.Contains(session.SummaryPath, error.Message);
            Assert.Equal("Existing markdown", AtomicFile.ReadText(session.SummaryPath));
            Assert.Equal(1, ReadSummary(session).SummarizedSegments);
            Assert.False(ReadSummary(session).Provisional);
        }
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        Assert.Single(service.Calls);
        Assert.Single(service.OverallCalls);
        Assert.Equal(ReadSummary(session).ToText(new UiText(settings.UiLanguage)), AtomicFile.ReadText(session.SummaryPath));
    }

    [Fact]
    public async Task CopilotStillRequiresConsentBeforeAnySummaryWork()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.Root };
        var session = AppStorage.CreateSession(settings, "Synthetic consent check");
        var service = new FakeSummaryService();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AiPipeline(summaryService: service).SummarizeAsync(
            session, settings, new Updates(), false, CancellationToken.None));
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task AnAlreadyCancelledRequestDoesNotChangeSavedNotes()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic cancellation");
        File.WriteAllText(session.SummaryPath, "Previously saved notes.");
        var service = new FakeSummaryService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AiPipeline(summaryService: service).SummarizeAsync(session, settings, new Updates(), false, cancellation.Token));
        Assert.Empty(service.Calls);
        Assert.Equal("Previously saved notes.", File.ReadAllText(session.SummaryPath));
    }

    [Theory]
    [InlineData(SummaryProviders.Copilot, "en")]
    [InlineData(SummaryProviders.Ollama, "zh-CN")]
    public async Task BothProvidersPersistAndDisplaySimplifiedNotesWithoutChangingPersonalNotes(string provider, string language)
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        settings.SummaryProvider = provider;
        settings.UiLanguage = language;
        settings.CloudConsent = provider == SummaryProviders.Copilot;
        var session = AppStorage.CreateSession(settings, "Synthetic Chinese notes");
        const string traditional = "## \u6703\u8b70\u8a18\u9304\nAlex \u5728\u9031\u4e94\u6e96\u5099 slides\u3002";
        const string simplified = "## \u4f1a\u8bae\u8bb0\u5f55\nAlex \u5728\u5468\u4e94\u51c6\u5907 slides\u3002";
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 10, traditional)] });
        File.WriteAllText(session.NotesPath, traditional);
        var updates = new Updates();
        var service = new FakeSummaryService { ResultText = traditional, OverallResultText = traditional };

        await new AiPipeline(summaryService: service).SummarizeAsync(
            session, settings, updates, true, CancellationToken.None);
        Assert.Equal(simplified, File.ReadAllText(session.SummaryPath));
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.Folder, "summary.json")));
        Assert.Equal(simplified, state.RootElement.GetProperty("Text").GetString());
        Assert.Equal(SummaryPrompt.TextPolicyVersion, state.RootElement.GetProperty("TextPolicyVersion").GetInt32());
        Assert.All(updates.Values.Where(update => update.Summary is not null), update => Assert.Equal(simplified, update.Summary));
        Assert.Equal(simplified, updates.Values[^1].Summary);
        Assert.Equal(traditional, File.ReadAllText(session.NotesPath));

        await new AiPipeline(summaryService: service).SummarizeAsync(
            session, settings, updates, false, CancellationToken.None);
        Assert.Equal(simplified, service.Calls[^1].Previous);
        Assert.Equal(simplified, Assert.Single(service.OverallCalls));
        Assert.Equal(simplified.Replace("\n", Environment.NewLine), ReadSummary(session).OverallText);
        Assert.Equal(ReadSummary(session).ToText(new UiText(language)), File.ReadAllText(session.SummaryPath));
        Assert.Equal(File.ReadAllText(session.SummaryPath), updates.Values[^1].Summary);
        Assert.Equal(traditional, File.ReadAllText(session.NotesPath));
        Assert.False(ReadSummary(session).Provisional);
    }

    [Fact]
    public async Task LegacyCharacterOffsetsAreNotReusedAfterChineseNormalization()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic old Chinese checkpoint");
        var original = new string('\u346e', 4200) + " Friday";
        var simplified = string.Concat(Enumerable.Repeat("\ud86d\udf48", 4200)) + " Friday";
        Assert.Equal(simplified, ChineseText.ToSimplified(original));
        Assert.NotEqual(original.Length, simplified.Length);
        AtomicFile.WriteJson(session.TranscriptPath, new
        {
            Segments = new[] { new { StartSeconds = 0, EndSeconds = 90, Text = original } }
        });
        AtomicFile.WriteJson(Path.Combine(session.Folder, "summary.json"), new
        {
            Provider = settings.SummaryProvider,
            Model = settings.OllamaModel,
            Endpoint = SummaryProviders.LocalEndpoint(settings.OllamaEndpoint).AbsoluteUri,
            SummarizedSegments = 0,
            SegmentOffset = 4000,
            Text = "\u820a\u6458\u8981"
        });
        var service = new FakeSummaryService();
        await new AiPipeline(summaryService: service).SummarizeAsync(
            session, settings, new Updates(), false, CancellationToken.None);
        Assert.Empty(service.Calls[0].Previous);
        Assert.Equal("[00:00:00 - 00:01:30] " + simplified + "\n",
            string.Concat(service.Calls.Select(call => call.Transcript)));
        Assert.Equal(SummaryPrompt.TextPolicyVersion, ReadSummary(session).TextPolicyVersion);
        Assert.Equal(1, ReadSummary(session).SummarizedSegments);
    }

    [Fact]
    public async Task RefinementOrSpeakerChangesInvalidateOldSummaryOffsets()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic revision change");
        AppStorage.SaveTranscript(session, new() { Revision = "draft", Segments = [new(0, 2, "Draft words.")] });
        await new AiPipeline(summaryService: new FakeSummaryService()).SummarizeAsync(
            session, settings, new Updates(), true, CancellationToken.None);
        AppStorage.SaveTranscript(session, new()
        {
            Revision = "final-speakers",
            Segments = [new(0, 2, "Refined words.") { SpeakerId = "spkr1" }]
        });
        var service = new FakeSummaryService
        {
            ResultText = "[spkr1] owns the action.",
            OverallResultText = "## Action items\n  - [spkr1] owns the action overall."
        };
        var updates = new Updates();
        await new AiPipeline(summaryService: service).SummarizeAsync(session, settings, updates, false, CancellationToken.None);
        Assert.Empty(Assert.Single(service.Calls).Previous);
        Assert.Contains("[spkr1] Refined words.", service.Calls[0].Transcript);
        Assert.Equal("final-speakers", ReadSummary(session).TranscriptRevision);
        AppStorage.RenameSpeaker(session, "spkr1", "Alex");
        Assert.True(AppStorage.SummaryNeedsRefresh(session, AppStorage.LoadTranscript(session)));
        Assert.StartsWith("Alex owns the action.", AppStorage.LoadSummaryText(session));
        Assert.Contains("Alex owns the action overall.", AppStorage.LoadSummaryText(session));
        Assert.DoesNotContain("[spkr1]", AppStorage.LoadSummaryText(session));
        await new AiPipeline(summaryService: service).SummarizeAsync(session, settings, updates, false, CancellationToken.None);
        Assert.Equal(2, service.Calls.Count);
        Assert.Empty(service.Calls[1].Previous);
        Assert.Equal(AppStorage.LoadSummaryText(session), updates.Values[^1].Summary);
        Assert.Equal(2, service.OverallCalls.Count);
    }

    [Fact]
    public void ReanalysisDoesNotRelabelAnOldSummaryAsADifferentPerson()
    {
        using var folder = new TestFolder();
        var session = AppStorage.CreateSession(new() { SaveFolder = folder.Root }, "Synthetic old labels");
        AppStorage.SaveTranscript(session, new()
        {
            Revision = "new-voices",
            Segments = [new(0, 1, "New transcript.") { SpeakerId = "spkr1" }],
            SpeakerNames = new() { ["spkr1"] = "Alex" }
        });
        AtomicFile.WriteJson(Path.Combine(session.Folder, "summary.json"),
            new SummaryDocument { TranscriptRevision = "old-voices", Text = "[spkr1] owns the slides." });
        File.WriteAllText(session.SummaryPath, "Jamie owns the slides.");
        Assert.True(AppStorage.SummaryNeedsRefresh(session, AppStorage.LoadTranscript(session)));
        Assert.Equal("Jamie owns the slides.", AppStorage.LoadSummaryText(session));
        AppStorage.RenameSpeaker(session, "spkr1", "New name");
        Assert.Equal("Jamie owns the slides.", File.ReadAllText(session.SummaryPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledOverallWorkKeepsMainPointsAndRetriesOnlyTheMissingStep(bool cancelled)
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic overall retry");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 1, "Meeting on Friday.")] });
        File.WriteAllText(session.NotesPath, "Private personal note.");
        var original = File.ReadAllBytes(session.TranscriptPath);
        var service = new FakeSummaryService
        {
            OverallError = cancelled ? new OperationCanceledException() : new IOException("Synthetic overall failure")
        };
        var updates = new Updates();
        var work = new AiPipeline(summaryService: service).SummarizeAsync(session, settings, updates, false, CancellationToken.None);
        if (cancelled)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => work);
            Assert.Contains("Synthetic overall failure", error.Message);
            Assert.Contains("main points are saved", error.Message);
        }
        Assert.Single(service.Calls);
        Assert.Equal("Notes 1", Assert.Single(service.OverallCalls));
        Assert.Equal("Notes 1", File.ReadAllText(session.SummaryPath));
        Assert.False(ReadSummary(session).HasOverallSummary);
        Assert.DoesNotContain(updates.Values, update => update.Status == new UiText(settings.UiLanguage)["SummarySaved"]);

        var retry = new FakeSummaryService();
        await new AiPipeline(summaryService: retry).SummarizeAsync(session, settings, updates, false, CancellationToken.None);
        Assert.Empty(retry.Calls);
        Assert.Equal("Notes 1", Assert.Single(retry.OverallCalls));
        Assert.True(ReadSummary(session).HasOverallSummary);
        Assert.Equal(ReadSummary(session).ToText(new UiText(settings.UiLanguage)), File.ReadAllText(session.SummaryPath));
        Assert.Equal(original, File.ReadAllBytes(session.TranscriptPath));
        Assert.Equal("Private personal note.", File.ReadAllText(session.NotesPath));
    }

    [Fact]
    public async Task CancellationBetweenMainPointsAndOverallDoesNotStartAnotherModelRequest()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic inter-step cancellation");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 1, "A decision.")] });
        using var cancellation = new CancellationTokenSource();
        var updates = new Updates { Reported = update => { if (update.Summary is not null) cancellation.Cancel(); } };
        var service = new FakeSummaryService();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AiPipeline(summaryService: service).SummarizeAsync(session, settings, updates, false, cancellation.Token));
        Assert.Single(service.Calls);
        Assert.Empty(service.OverallCalls);
        Assert.Equal("Notes 1", File.ReadAllText(session.SummaryPath));
    }

    [Fact]
    public async Task ACompletedOverallCheckpointRepairsItsExportWithoutAnotherRequestOrCheckpointWrite()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic overall export repair");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 1, "A decision.")] });
        FileStream? lockedExport = null;
        var service = new FakeSummaryService
        {
            StartingOverall = () =>
                lockedExport = new FileStream(session.SummaryPath, FileMode.Open, FileAccess.Read, FileShare.Read)
        };
        var pipeline = new AiPipeline(summaryService: service);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None));
            Assert.Contains(session.SummaryPath, error.Message);
            Assert.True(ReadSummary(session).HasOverallSummary);
            Assert.Equal("Notes 1", File.ReadAllText(session.SummaryPath));
            Assert.Contains("Overall notes.", AppStorage.LoadSummaryText(session));
        }
        finally { lockedExport?.Dispose(); }
        using var checkpoint = new FileStream(Path.Combine(session.Folder, "summary.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        Assert.Single(service.Calls);
        Assert.Single(service.OverallCalls);
        Assert.Equal(ReadSummary(session).ToText(new UiText(settings.UiLanguage)), File.ReadAllText(session.SummaryPath));
    }

    [Fact]
    public async Task LegacyCompletedNotesGetOnlyAnOverallStepWithoutResendingTheTranscript()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic legacy summary");
        AppStorage.SaveTranscript(session, new() { Revision = "stable", Segments = [new(0, 1, "A decision.")] });
        const string mainPoints = "## Decisions\n[00:00:00 - 00:00:01]\n  Keep the original notes.";
        AtomicFile.WriteJson(Path.Combine(session.Folder, "summary.json"), new
        {
            Text = mainPoints,
            TranscriptRevision = "stable",
            SummarizedSegments = 1,
            TextPolicyVersion = SummaryPrompt.TextPolicyVersion,
            Provider = settings.SummaryProvider,
            Model = settings.ActiveSummaryModel,
            Endpoint = settings.ActiveSummaryEndpoint
        });
        Assert.Equal(mainPoints, AppStorage.LoadSummaryText(session));
        var service = new FakeSummaryService();
        await new AiPipeline(summaryService: service).SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        Assert.Empty(service.Calls);
        Assert.Equal(mainPoints, Assert.Single(service.OverallCalls));
        Assert.Equal(mainPoints, ReadSummary(session).Text);
        Assert.True(ReadSummary(session).HasOverallSummary);
    }

    [Fact]
    public async Task UpdatingMainPointsClearsTheOldOverallSectionUntilTheNextFinalSummary()
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic appended conversation");
        var transcript = new TranscriptDocument { Segments = [new(0, 1, "Original discussion.")] };
        AppStorage.SaveTranscript(session, transcript);
        await new AiPipeline(summaryService: new FakeSummaryService { OverallResultText = "OLD OVERALL" })
            .SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        transcript.Segments.Add(new(1, 2, "An updated decision."));
        AppStorage.SaveTranscript(session, transcript);
        var service = new FakeSummaryService { ResultText = "Updated main points.", OverallResultText = "NEW OVERALL" };
        var pipeline = new AiPipeline(summaryService: service);
        await pipeline.SummarizeAsync(session, settings, new Updates(), true, CancellationToken.None);
        Assert.Empty(service.OverallCalls);
        Assert.False(ReadSummary(session).HasOverallSummary);
        Assert.Empty(ReadSummary(session).OverallText);
        Assert.Equal("Updated main points.", AppStorage.LoadSummaryText(session));
        await pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None);
        Assert.Equal("Updated main points.", Assert.Single(service.OverallCalls));
        Assert.Contains("NEW OVERALL", AppStorage.LoadSummaryText(session));
        Assert.DoesNotContain("OLD OVERALL", File.ReadAllText(session.SummaryPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[00:00:02]")]
    [InlineData("oversized")]
    public async Task EmptyTimestampOnlyOrOversizedOverallResultsAreNotSavedAsSuccess(string output)
    {
        using var folder = new TestFolder();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic invalid overall result");
        AppStorage.SaveTranscript(session, new() { Segments = [new(0, 1, "A decision.")] });
        var service = new FakeSummaryService
        {
            OverallResultText = output == "oversized" ? new string('x', SummaryPrompt.MaximumPreviousSummaryCharacters + 1) : output
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AiPipeline(summaryService: service).SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None));
        Assert.False(ReadSummary(session).HasOverallSummary);
        Assert.Equal("Notes 1", File.ReadAllText(session.SummaryPath));
    }

    private static SummaryDocument ReadSummary(RecordingSession session) =>
        JsonSerializer.Deserialize<SummaryDocument>(File.ReadAllText(Path.Combine(session.Folder, "summary.json")))!;

    private sealed class FakeSummaryService : ISummaryService
    {
        public int FailOnCall { get; init; }
        public string? ResultText { get; init; }
        public string OverallResultText { get; init; } = "## Main points\n  - Overall notes.";
        public Exception? OverallError { get; init; }
        public Action? StartingOverall { get; init; }
        public List<(string Transcript, string Previous, bool Live)> Calls { get; } = [];
        public List<string> OverallCalls { get; } = [];
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            Calls.Add((transcript, previous, live));
            if (Calls.Count == FailOnCall)
                throw new IOException("Synthetic local service failure.");
            return Task.FromResult(ResultText ?? "Notes " + Calls.Count);
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            OverallCalls.Add(mainPoints);
            StartingOverall?.Invoke();
            if (OverallError is not null)
                return Task.FromException<string>(OverallError);
            return Task.FromResult(OverallResultText);
        }
    }

    private sealed class Updates : IProgress<AiUpdate>
    {
        public List<AiUpdate> Values { get; } = [];
        public Action<AiUpdate>? Reported { get; init; }
        public void Report(AiUpdate value)
        {
            Values.Add(value);
            Reported?.Invoke(value);
        }
    }
}
