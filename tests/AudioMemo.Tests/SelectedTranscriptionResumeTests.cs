using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class SelectedTranscriptionResumeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopKeepsCompletedRowsAndContinueReadsOnlyTheRemainingSelection(bool missingFirstRow)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0, 1, 2);
        var settings = new AppSettings { SaveFolder = folder.Root };
        var checkpoint = new SelectedTranscriptionCheckpoint(plan, settings);
        var first = new TranscriptCorrectionTests.FakeTranscriber();
        if (missingFirstRow)
            first.Worker.Response = (_, _, _) => Task.FromResult<IReadOnlyList<TranscriptSegment>>([]);
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TranscriptCorrectionTests.Service(first).GenerateAsync(session, plan, settings, new Updates(update =>
            {
                if (update.Row?.Index == 0)
                    stop.Cancel();
            }), stop.Token, checkpoint));
        var row = Assert.Single(checkpoint.CompletedRows);
        Assert.Equal(missingFirstRow, row.Unrecognized);
        Assert.Equal(missingFirstRow ? plan.Segments[0].Text : "Recognized selection 1.", row.Text);
        Assert.False(checkpoint.IsComplete);
        Assert.True(first.Worker.Disposed);
        Assert.Single(first.Worker.Chunks);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));

        var remaining = new TranscriptCorrectionTests.FakeTranscriber();
        remaining.Worker.Response = (_, number, _) =>
            Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 0.4, $"Remaining row {number}.")]);
        var result = await TranscriptCorrectionTests.Service(remaining).GenerateAsync(
            session, plan, settings, new Updates(), CancellationToken.None, checkpoint);
        Assert.Equal([row.Text, "Remaining row 1.", "Remaining row 2."], result.Texts);
        Assert.Equal(missingFirstRow ? [0] : Array.Empty<int>(), result.Unrecognized);
        Assert.True(checkpoint.IsComplete);
        Assert.Equal(1, checkpoint.Fraction);
        Assert.Equal(2, remaining.Worker.Chunks.Count);
        Assert.Equal(checkpoint.AudioVersion, result.AudioVersion);
        for (var index = 0; index < remaining.Worker.Chunks.Count; index++)
        {
            var segment = plan.Segments[index + 1];
            var expected = folder.File($"remaining-{index}.wav");
            var start = AudioEditPlan.FrameAt(segment.StartSeconds, plan.SourceFrames);
            var end = AudioEditPlan.FrameAt(segment.EndSeconds, plan.SourceFrames);
            AudioExport.WriteSpeechChunk(session.WavePath, expected, start, end - start, CancellationToken.None);
            Assert.Equal(File.ReadAllBytes(expected), remaining.Worker.Chunks[index]);
        }
        Assert.All(first.Worker.Paths.Concat(remaining.Worker.Paths), path => Assert.False(File.Exists(path)));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task StopWithinOneLongRowPreservesTheExcerptWithoutReplacingItsUnfinishedText()
    {
        using var folder = new TestFolder();
        var session = MakeLongSession(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0);
        var settings = new AppSettings();
        var checkpoint = new SelectedTranscriptionCheckpoint(plan, settings);
        var first = new TranscriptCorrectionTests.FakeTranscriber();
        first.Worker.Response = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 5, "Finished first chunk.")]);
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TranscriptCorrectionTests.Service(first).GenerateAsync(session, plan, settings, new Updates(update =>
            {
                if (!string.IsNullOrEmpty(update.PartialText))
                    stop.Cancel();
            }), stop.Token, checkpoint));
        Assert.Empty(checkpoint.CompletedRows);
        Assert.Equal("Finished first chunk.", checkpoint.PartialText);
        Assert.Equal(30d / 65, checkpoint.Fraction, 8);
        Assert.Equal("Original complete row.", plan.Apply([plan.Segments[0].Text]).Segments[0].Text);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));

        var remaining = new TranscriptCorrectionTests.FakeTranscriber();
        remaining.Worker.Response = (_, number, _) =>
            Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(2, 5, $"Remaining chunk {number}.")]);
        var result = await TranscriptCorrectionTests.Service(remaining).GenerateAsync(
            session, plan, settings, new Updates(), CancellationToken.None, checkpoint);
        Assert.Equal(2, remaining.Worker.Chunks.Count);
        var expected = folder.File("remaining-chunk.wav");
        AudioExport.WriteSpeechChunk(session.WavePath, expected, 28L * PcmFormat.SampleRate,
            32L * PcmFormat.SampleRate, CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(expected), remaining.Worker.Chunks[0]);
        var text = Assert.Single(result.Texts);
        Assert.Contains("Finished first chunk.", text);
        Assert.Contains("Remaining chunk 1.", text);
        Assert.Contains("Remaining chunk 2.", text);
        Assert.Equal(2, text.Split("Finished first chunk.", StringSplitOptions.None).Length);
        Assert.Empty(checkpoint.PartialText);
        Assert.True(checkpoint.IsComplete);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Theory]
    [InlineData("audio")]
    [InlineData("selection")]
    [InlineData("model")]
    [InlineData("language")]
    [InlineData("vocabulary")]
    [InlineData("transcript")]
    public async Task AStaleCheckpointCannotContinueAgainstChangedInput(string change)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0, 1);
        var settings = new AppSettings();
        var checkpoint = new SelectedTranscriptionCheckpoint(plan, settings);
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TranscriptCorrectionTests.Service(new()).GenerateAsync(session, plan, settings, new Updates(update =>
            {
                if (update.Row is not null)
                    stop.Cancel();
            }), stop.Token, checkpoint));
        switch (change)
        {
            case "audio": File.SetLastWriteTimeUtc(session.WavePath, checkpoint.AudioVersion!.LastWriteUtc.AddSeconds(2)); break;
            case "selection": plan = TranscriptCorrectionTests.Plan(session, 1); break;
            case "model": settings.SpeechModel = "base"; break;
            case "language": settings.Language = "en"; break;
            case "vocabulary": settings.SpeechVocabularyTerms = ["ChangedHint"]; break;
            case "transcript":
                var changed = AppStorage.LoadTranscript(session);
                changed.Segments[1] = changed.Segments[1] with { Text = "External change." };
                AppStorage.SaveTranscript(session, changed);
                break;
        }
        var before = EditedSessionTests.Hashes(session.Folder);
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        await Assert.ThrowsAsync<InvalidOperationException>(() => TranscriptCorrectionTests.Service(fake)
            .GenerateAsync(session, plan, settings, new Updates(), CancellationToken.None, checkpoint));
        Assert.Equal(0, fake.OpenCount);
        Assert.Single(checkpoint.CompletedRows);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task WorkerFailureRetainsCompletedRowsButDoesNotPublishThem()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0, 1);
        var before = EditedSessionTests.Hashes(session.Folder);
        var checkpoint = new SelectedTranscriptionCheckpoint(plan, new());
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        fake.Worker.Response = (_, number, _) => number == 2
            ? throw new IOException("Synthetic resumable worker failure.")
            : Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 0.4, "Finished row.")]);
        await Assert.ThrowsAsync<IOException>(() => TranscriptCorrectionTests.Service(fake)
            .GenerateAsync(session, plan, new(), new Updates(), CancellationToken.None, checkpoint));
        Assert.Equal("Finished row.", Assert.Single(checkpoint.CompletedRows).Text);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        var remaining = new TranscriptCorrectionTests.FakeTranscriber();
        var result = await TranscriptCorrectionTests.Service(remaining)
            .GenerateAsync(session, plan, new(), new Updates(), CancellationToken.None, checkpoint);
        Assert.Single(remaining.Worker.Chunks);
        Assert.Equal("Finished row.", result.Texts[0]);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task SavingReviewedRowsAfterStopPreservesUnfinishedTextAudioNotesAndTimestamps()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var original = AppStorage.LoadTranscript(session);
        var before = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        var plan = TranscriptCorrectionTests.Plan(session, 0, 1, 2);
        var settings = new AppSettings { SaveFolder = folder.Root };
        var checkpoint = new SelectedTranscriptionCheckpoint(plan, settings);
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TranscriptCorrectionTests.Service(new()).GenerateAsync(session, plan, settings, new Updates(update =>
            {
                if (update.Row?.Index == 0)
                    stop.Cancel();
            }), stop.Token, checkpoint));
        Assert.Single(checkpoint.CompletedRows);
        Assert.False(checkpoint.IsComplete);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value));
        using (File.Open(session.WavePath, FileMode.Open, FileAccess.Read, FileShare.None)) { }

        var reviewed = plan.Segments.Select(segment => segment.Text).ToArray();
        reviewed[0] = "Reviewed completed transcription.";
        var version = Assert.IsType<AudioSourceVersion>(checkpoint.AudioVersion);
        var saved = TranscriptCorrectionService.Save(session, plan, reviewed, CancellationToken.None, version);
        var persisted = AppStorage.LoadTranscript(session);
        Assert.Equal(saved.Segments, persisted.Segments);
        Assert.Equal(reviewed, plan.Indices.Select(index => persisted.Segments[index].Text));
        Assert.Equal(original.Segments.Skip(1), saved.Segments.Skip(1));
        Assert.Equal(original.Segments.Select(segment => (segment.StartSeconds, segment.EndSeconds, segment.SpeakerId)),
            saved.Segments.Select(segment => (segment.StartSeconds, segment.EndSeconds, segment.SpeakerId)));
        Assert.True(saved.Segments[0].IsUserEdited);
        Assert.Contains(reviewed[0], File.ReadAllText(Path.Combine(session.Folder, "transcript.txt")));
        Assert.True(AppStorage.SummaryNeedsRefresh(session, saved));
        var after = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in before.Where(pair => pair.Key is not ("transcript.json" or "transcript.txt")))
            Assert.Equal(pair.Value, after[pair.Key]);
    }

    [Fact]
    public async Task ConcurrentCheckpointUseCannotReleaseTheActiveRunOrStartAnotherWorker()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0, 1);
        var settings = new AppSettings();
        var checkpoint = new SelectedTranscriptionCheckpoint(plan, settings);
        var first = new TranscriptCorrectionTests.FakeTranscriber();
        first.Worker.Response = async (_, _, token) =>
        {
            first.Worker.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        };
        using var stop = new CancellationTokenSource();
        var pending = TranscriptCorrectionTests.Service(first).GenerateAsync(
            session, plan, settings, new Updates(), stop.Token, checkpoint);
        try
        {
            await first.Worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var other = new TranscriptCorrectionTests.FakeTranscriber();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    TranscriptCorrectionTests.Service(other).GenerateAsync(
                        session, plan, settings, new Updates(), CancellationToken.None, checkpoint));
            }
            Assert.Equal(0, other.OpenCount);
            Assert.False(pending.IsCompleted);
            Assert.Empty(checkpoint.CompletedRows);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        Assert.True(first.Worker.Disposed);
        Assert.All(first.Worker.Paths, path => Assert.False(File.Exists(path)));
        var remaining = new TranscriptCorrectionTests.FakeTranscriber();
        var result = await TranscriptCorrectionTests.Service(remaining).GenerateAsync(
            session, plan, settings, new Updates(), CancellationToken.None, checkpoint);
        Assert.Equal(2, result.Texts.Count);
        Assert.Equal(2, remaining.Worker.Chunks.Count);
        Assert.True(checkpoint.IsComplete);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    internal static RecordingSession MakeLongSession(TestFolder folder)
    {
        var session = AppStorage.CreateSession(new() { SaveFolder = folder.Root }, "Synthetic resumable selection");
        File.Copy(folder.WriteTone(65), session.WavePath, true);
        session.Frames = 65L * PcmFormat.SampleRate;
        session.Status = "Saved";
        AppStorage.SaveTranscript(session, AudioEditPlanTests.Document(65, new TranscriptSegment(0, 65, "Original complete row.")));
        return session;
    }

    private sealed class Updates(Action<SelectedTranscriptionProgress>? report = null) : IProgress<SelectedTranscriptionProgress>
    {
        public void Report(SelectedTranscriptionProgress value) => report?.Invoke(value);
    }
}
