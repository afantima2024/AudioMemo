using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class UnlabeledSpeakerTests
{
    internal static TranscriptDocument Document()
    {
        var document = AudioEditPlanTests.Document(18,
            new(0, 3, "Known first speaker.") { SpeakerId = "spkr1", IsUserEdited = true },
            new(3, 6, "Known second speaker.") { SpeakerId = "spkr2" },
            new(6, 8, "Unlabeled first voice.") { IsUserEdited = true },
            new(8, 10, "Unlabeled second voice."),
            new(10, 11, "Ambiguous voice."),
            new(11, 11.5, "Too brief."),
            new(12, 18, "Different voices within one row."));
        document.SpeakerNames = new() { ["spkr1"] = "Alex", ["spkr2"] = "Sam" };
        document.SpeakerColors = new() { ["spkr1"] = "#123456", ["spkr2"] = "#ABCDEF" };
        document.Me = SelfSpeaker.Create("spkr1", "Alex\nA. Example");
        return document;
    }

    [Fact]
    public void OnlyUnlabeledRowsChangeAndAmbiguousShortOrMixedVoicesStayUnlabeled()
    {
        var document = Document();
        var original = JsonSerializer.Serialize(document);
        var plan = UnlabeledSpeakerMatching.Create(document, document.ProcessedFrames);
        var analyzed = new List<AudioFrameRange>();
        var progress = new List<(int Completed, int Total)>();
        var matches = UnlabeledSpeakerMatching.Match(plan, range =>
        {
            analyzed.Add(range);
            return (range.StartFrame / PcmFormat.SampleRate) switch
            {
                0 or 6 or 12 => [1, 0, 0],
                3 or 8 or 15 => [0, 1, 0],
                10 => [0.71f, 0.70f, 0],
                _ => throw new InvalidOperationException("Audio outside a reference or eligible unlabeled row was read.")
            };
        }, progress: (completed, total) => progress.Add((completed, total)));
        Assert.Equal(new string?[] { "spkr1", "spkr2", null, null, null }, matches.Select(match => match.SpeakerId));
        Assert.DoesNotContain(analyzed, range => range.StartFrame == 11L * PcmFormat.SampleRate);
        Assert.Equal(progress[^1].Total, progress[^1].Completed);
        var updated = UnlabeledSpeakerMatching.Apply(document, plan, matches);
        Assert.NotEqual(document.Revision, updated.Revision);
        Assert.Equal(document.Segments[0], updated.Segments[0]);
        Assert.Equal(document.Segments[1], updated.Segments[1]);
        Assert.Equal(document.Segments[2] with { SpeakerId = "spkr1" }, updated.Segments[2]);
        Assert.Equal(document.Segments[3] with { SpeakerId = "spkr2" }, updated.Segments[3]);
        Assert.Equal(document.Segments.Skip(4), updated.Segments.Skip(4));
        Assert.True(document.Me!.Matches(updated.Me));
        Assert.Equal(document.SpeakerNames, updated.SpeakerNames);
        Assert.Equal(document.SpeakerColors, updated.SpeakerColors);
        Assert.Equal(document.Model, updated.Model);
        Assert.Equal(document.Language, updated.Language);
        Assert.Equal(document.ProcessedFrames, updated.ProcessedFrames);
        Assert.Equal(original, JsonSerializer.Serialize(document));
        Assert.DoesNotContain("Known first speaker", JsonSerializer.Serialize(plan));
        Assert.DoesNotContain("Alex", JsonSerializer.Serialize(plan));
    }

    [Fact]
    public void ReferencesAreBoundedAndLongRowsAreCheckedInEntireContiguousWindows()
    {
        var document = AudioEditPlanTests.Document(100,
            new(0, 10, "Reference 1.") { SpeakerId = "spkr1" },
            new(11, 20, "Reference 2.") { SpeakerId = "spkr1" },
            new(21, 30, "Reference 3.") { SpeakerId = "spkr1" },
            new(31, 40, "Reference 4.") { SpeakerId = "spkr1" },
            new(50, 99, "Long unlabeled row."));
        var plan = UnlabeledSpeakerMatching.Create(document, document.ProcessedFrames);
        Assert.Equal(UnlabeledSpeakerMatching.ReferencesPerSpeaker, plan.References.Length);
        Assert.All(plan.References, reference => Assert.InRange(reference.Range.Length,
            UnlabeledSpeakerMatching.MinimumFrames, UnlabeledSpeakerMatching.ReferenceFrames));
        var target = Assert.Single(plan.Targets);
        var windows = UnlabeledSpeakerMatching.Windows(target.Range);
        Assert.Equal(target.Range.StartFrame, windows[0].StartFrame);
        Assert.Equal(target.Range.EndFrame, windows[^1].EndFrame);
        Assert.Equal(target.Range.Length, windows.Sum(window => window.Length));
        Assert.All(windows, window => Assert.InRange(window.Length,
            UnlabeledSpeakerMatching.MinimumFrames, UnlabeledSpeakerMatching.WindowFrames));
        for (var index = 1; index < windows.Length; index++)
            Assert.Equal(windows[index - 1].EndFrame, windows[index].StartFrame);
    }

    [Theory]
    [InlineData(0.699, false)]
    [InlineData(0.701, true)]
    public void SimilarityThresholdIsEnforcedRatherThanAlwaysChoosingTheClosestVoice(double similarity, bool assigned)
    {
        var document = AudioEditPlanTests.Document(4,
            new(0, 2, "Reference.") { SpeakerId = "spkr1" }, new(2, 4, "Unlabeled."));
        var plan = UnlabeledSpeakerMatching.Create(document, document.ProcessedFrames);
        var result = UnlabeledSpeakerMatching.Match(plan, range => range.StartFrame == 0 ? [1, 0] :
            [(float)similarity, (float)Math.Sqrt(1 - similarity * similarity)]);
        Assert.Equal(assigned, Assert.Single(result).SpeakerId is not null);
    }

    [Fact]
    public void NoMatchesLeaveRevisionUnchangedAndInvalidOrStaleResultsCannotRelabelKnownRows()
    {
        var document = Document();
        var plan = UnlabeledSpeakerMatching.Create(document, document.ProcessedFrames);
        var empty = plan.Targets.Select((_, index) => new SpeakerMatchResult(index, null, 0)).ToArray();
        Assert.Equal(JsonSerializer.Serialize(document), JsonSerializer.Serialize(UnlabeledSpeakerMatching.Apply(document, plan, empty)));
        var original = JsonSerializer.Serialize(document);
        foreach (var invalid in new[]
        {
            empty[..^1],
            empty.Select((item, index) => index == 0 ? item with { SpeakerId = "unknown-id", Similarity = 1 } : item).ToArray(),
            empty.Select((item, index) => index == 0 ? item with { SpeakerId = "spkr1", Similarity = 0.69 } : item).ToArray(),
            empty.Select((item, index) => index == 0 ? item with { Similarity = double.NaN } : item).ToArray(),
            empty.Select(item => item with { TargetIndex = 0 }).ToArray()
        })
            Assert.Throws<InvalidDataException>(() => UnlabeledSpeakerMatching.Apply(document, plan, invalid));
        var tampered = plan with { Targets = [new(plan.Targets[0].Range, [0]), .. plan.Targets.Skip(1)] };
        Assert.Throws<InvalidDataException>(() => UnlabeledSpeakerMatching.Apply(document, tampered, empty));
        Assert.Equal(original, JsonSerializer.Serialize(document));
        document.Segments[2] = document.Segments[2] with { Text = "Concurrent correction." };
        Assert.Throws<InvalidOperationException>(() => UnlabeledSpeakerMatching.Apply(document, plan, empty));
    }

    [Fact]
    public void MissingReferencesOverlapsInvalidEmbeddingsAndCancellationAreExplicit()
    {
        var document = AudioEditPlanTests.Document(3, new TranscriptSegment(0, 3, "Unlabeled."));
        Assert.Throws<InvalidOperationException>(() => UnlabeledSpeakerMatching.Create(document, document.ProcessedFrames));
        document.Segments.Insert(0, new(0, 3, "Overlapping reference.") { SpeakerId = "spkr1" });
        Assert.Throws<InvalidOperationException>(() => UnlabeledSpeakerMatching.Create(document, document.ProcessedFrames));
        var valid = Document();
        var plan = UnlabeledSpeakerMatching.Create(valid, valid.ProcessedFrames);
        Assert.Throws<InvalidOperationException>(() => UnlabeledSpeakerMatching.Match(plan, _ => null));
        Assert.Throws<InvalidDataException>(() => UnlabeledSpeakerMatching.Match(plan, _ => [float.NaN]));
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => UnlabeledSpeakerMatching.Match(plan, _ =>
        {
            cancellation.Cancel();
            return [1, 0];
        }, cancellation.Token));
    }

    [Fact]
    public void BoundedMonoResamplingDoesNotReadAudioBeforeOrAfterTheRequestedRange()
    {
        using var folder = new TestFolder();
        var path = folder.WriteTone(3);
        using (var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            output.Position = DurableWaveFile.HeaderSize + PcmFormat.BytesPerSecond;
            output.Write(new byte[PcmFormat.BytesPerSecond]);
        }
        var before = File.ReadAllBytes(path);
        var samples = SpeakerMatchWorker.ReadSamples(path, new(PcmFormat.SampleRate, 2L * PcmFormat.SampleRate));
        Assert.InRange(samples.Length, 15990, 16010);
        Assert.All(samples, sample => Assert.Equal(0f, sample));
        Assert.Equal(before, File.ReadAllBytes(path));
        using var exclusive = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task PipelineUsesExistingLabelsAndPreservesAudioNotesNamesAndMe()
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder);
        var original = AppStorage.LoadTranscript(session);
        var summary = new SummaryDocument { Text = "Synthetic summary.", Model = "fake-model" };
        summary.RecordSource(original);
        AudioMemo.Core.Storage.AtomicFile.WriteJson(Path.Combine(session.Folder, "summary.json"), summary);
        Assert.False(AppStorage.SummaryNeedsRefresh(session, original));
        var before = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        var fake = new FakeSpeakers();
        var updates = new Updates();
        await new AiPipeline(speakerService: fake).RecheckUnlabeledSpeakersAsync(session,
            new AppSettings { SaveFolder = folder.Root }, updates, CancellationToken.None);
        Assert.Equal(1, fake.MatchCalls);
        var result = AppStorage.LoadTranscript(session);
        Assert.Equal("spkr1", result.Segments[2].SpeakerId);
        Assert.True(original.Me!.Matches(result.Me));
        var after = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var name in before.Keys.Except(["transcript.json", "transcript.txt"]))
            Assert.Equal(before[name], after[name]);
        Assert.NotNull(updates.Values.Last().Document);
        Assert.True(AppStorage.SummaryNeedsRefresh(session, result));
        var work = ProcessingWork.ForAi("RecheckUnlabeledSpeakers", new AppSettings());
        PendingProcessingStore.Defer(session, [work]);
        Assert.Equal(ProcessingWorkKind.UnlabeledSpeakers, Assert.Single(PendingProcessingStore.Load(session)!.Work).Kind);
    }

    [Fact]
    public async Task CancelledAndFailedPublicationKeepBothTranscriptFilesUnchanged()
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeSpeakers { BeforeReturn = () => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AiPipeline(speakerService: fake)
            .RecheckUnlabeledSpeakersAsync(session, new AppSettings(), new Updates(), cancellation.Token));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        using (var locked = new FileStream(session.TranscriptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => new AiPipeline(speakerService: new FakeSpeakers())
                .RecheckUnlabeledSpeakersAsync(session, new AppSettings(), new Updates(), CancellationToken.None));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task ConcurrentCorrectionIsPreservedInsteadOfPublishingStaleAssignments()
    {
        using var folder = new TestFolder();
        var session = MakeSession(folder);
        var fake = new FakeSpeakers
        {
            BeforeReturn = () =>
            {
                var latest = AppStorage.LoadTranscript(session);
                latest.Segments[2] = latest.Segments[2] with { Text = "Newly corrected text.", IsUserEdited = true };
                AppStorage.SaveTranscript(session, latest);
            }
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AiPipeline(speakerService: fake)
            .RecheckUnlabeledSpeakersAsync(session, new AppSettings(), new Updates(), CancellationToken.None));
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal("Newly corrected text.", saved.Segments[2].Text);
        Assert.Null(saved.Segments[2].SpeakerId);
    }

    internal static RecordingSession MakeSession(TestFolder folder)
    {
        var session = AppStorage.CreateSession(new AppSettings { SaveFolder = folder.Root }, "Synthetic voice matching");
        File.Copy(folder.WriteTone(18), session.WavePath, true);
        session.Frames = 18L * PcmFormat.SampleRate;
        session.Status = "Saved";
        AppStorage.SaveTranscript(session, Document());
        File.WriteAllText(session.NotesPath, "PRIVATE NOTES NOT FOR MATCHING");
        return session;
    }

    internal sealed class FakeSpeakers : ISpeakerService
    {
        public Action? BeforeReturn { get; init; }
        public int MatchCalls { get; private set; }
        public Task IdentifyAsync(RecordingSession session, TranscriptDocument transcript, int count, CancellationToken token,
            Action<AiWorkProgress>? progress = null) => throw new InvalidOperationException("Full speaker reanalysis must not run.");
        public Task<SpeakerMatchResult[]> MatchUnlabeledAsync(RecordingSession session, SpeakerMatchPlan plan,
            CancellationToken token, Action<AiWorkProgress>? progress = null)
        {
            MatchCalls++;
            BeforeReturn?.Invoke();
            return Task.FromResult(plan.Targets.Select((_, index) =>
                new SpeakerMatchResult(index, index == 0 ? "spkr1" : null, 0.95)).ToArray());
        }
    }

    private sealed class Updates : IProgress<AiUpdate>
    {
        public List<AiUpdate> Values { get; } = [];
        public void Report(AiUpdate value) => Values.Add(value);
    }
}
