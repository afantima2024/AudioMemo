using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class TranscriptContinuationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RecognitionAndLiveRefinementOnlyProcessTheTailAndPreserveEarlierCorrections(bool live, bool refine)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var previous = AppStorage.LoadTranscript(source);
        previous.Segments[0] = previous.Segments[0] with { Text = "Corrected original words.", IsUserEdited = true };
        previous.SpeakerColors["spkr1"] = "#123456";
        AppStorage.SaveTranscript(source, previous);
        var session = RecordingContinuationTests.AppendAudio(folder, source, 2);
        var audio = File.ReadAllBytes(session.WavePath);
        var notes = File.ReadAllBytes(session.NotesPath);
        var summary = File.ReadAllBytes(session.SummaryPath);
        var settings = Settings(folder);
        settings.SpeechModel = refine ? "small" : "base";
        settings.Language = refine ? "zh" : "en";
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        var isRecording = live;
        fake.Worker.Response = (_, number, _) =>
        {
            isRecording = false;
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 2, $"New continuation words {number}.")]);
        };
        await Pipeline(fake).TranscribeAsync(session, settings, () => (session.Frames, isRecording),
            new Updates(), CancellationToken.None);
        var result = AppStorage.LoadTranscript(session);
        Assert.Equal(session.Frames, result.ProcessedFrames);
        Assert.False(result.IsDraft);
        Assert.Equal(previous.Segments, result.Segments.Take(previous.Segments.Count));
        Assert.Equal(previous.SpeakerNames, result.SpeakerNames);
        Assert.Equal(previous.SpeakerColors, result.SpeakerColors);
        var added = Assert.Single(result.Segments.Skip(previous.Segments.Count));
        Assert.Equal(2, added.StartSeconds);
        Assert.Equal(4, added.EndSeconds);
        Assert.Equal(live ? 2 : 1, fake.OpenCount);
        Assert.All(fake.Worker.Chunks, chunk =>
        {
            using var reader = new WaveFileReader(new MemoryStream(chunk));
            Assert.InRange(reader.TotalTime.TotalSeconds, 1.99, 2.01);
        });
        Assert.Equal(settings.SpeechModel, result.Model);
        Assert.Equal(settings.Language, result.Language);
        Assert.Equal(audio, File.ReadAllBytes(session.WavePath));
        Assert.Equal(notes, File.ReadAllBytes(session.NotesPath));
        Assert.Equal(summary, File.ReadAllBytes(session.SummaryPath));
        Assert.True(AppStorage.SummaryNeedsRefresh(session, result));
        Assert.False(File.Exists(Path.Combine(session.Folder, "transcript.refining.json")));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    public async Task ColorsChangedDuringRecognitionSurviveLiveFinalAndDeferredPublication(bool live, bool refine, bool reset)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        AppStorage.SetSpeakerColor(source, "spkr1", "#AABBCC");
        AppStorage.SetSpeakerColor(source, null, "#DDEEFF");
        var previous = AppStorage.LoadTranscript(source);
        var session = RecordingContinuationTests.AppendAudio(folder, source, 2);
        var audio = File.ReadAllBytes(session.WavePath);
        var notes = File.ReadAllBytes(session.NotesPath);
        var settings = Settings(folder);
        settings.SpeechModel = refine ? "small" : "base";
        settings.TranscribeOnStop = !live || refine;
        var isRecording = live;
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        fake.Worker.Response = (_, number, _) =>
        {
            AppStorage.SetSpeakerColor(session, "spkr1", reset ? null : "#112233");
            AppStorage.SetSpeakerColor(session, null, reset ? null : "#445566");
            isRecording = false;
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 2, $"New words {number}.")]);
        };
        await Pipeline(fake).TranscribeAsync(session, settings, () => (session.Frames, isRecording),
            new Updates(), CancellationToken.None);
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal(previous.Segments, saved.Segments.Take(previous.Segments.Count));
        Assert.Equal(reset ? null : "#112233", saved.SpeakerColor("spkr1"));
        Assert.Equal(reset ? null : "#445566", saved.UnlabeledSpeakerColor);
        Assert.Equal(live && !refine, saved.IsDraft);
        Assert.Equal(session.Frames, saved.ProcessedFrames);
        Assert.Equal(live && refine ? 2 : 1, fake.OpenCount);
        Assert.Equal(audio, File.ReadAllBytes(session.WavePath));
        Assert.Equal(notes, File.ReadAllBytes(session.NotesPath));
    }

    [Fact]
    public async Task ColorChangesDoNotRestartAnAlreadyCompletedRefinementCheckpoint()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        AppStorage.SetSpeakerColor(source, "spkr1", "#AABBCC");
        AppStorage.SetSpeakerColor(source, null, "#DDEEFF");
        var previous = AppStorage.LoadTranscript(source);
        var session = RecordingContinuationTests.AppendAudio(folder, source);
        var checkpoint = TranscriptContinuation.CreateRefinement(previous, previous.ProcessedFrames, "small", "en");
        checkpoint.Segments.Add(new(2, 3, "Final text already recognized."));
        checkpoint.ProcessedFrames = session.Frames;
        AtomicFile.WriteJson(Path.Combine(session.Folder, "transcript.refining.json"), checkpoint);
        AppStorage.SetSpeakerColor(session, "spkr1", null);
        AppStorage.SetSpeakerColor(session, null, "#112233");
        var settings = Settings(folder);
        settings.SpeechModel = "small";
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        await Pipeline(fake).TranscribeAsync(session, settings, () => (session.Frames, false),
            new Updates(), CancellationToken.None);
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal(0, fake.OpenCount);
        Assert.Equal("Final text already recognized.", saved.Segments[^1].Text);
        Assert.Equal(previous.Segments, saved.Segments.Take(previous.Segments.Count));
        Assert.Null(saved.SpeakerColor("spkr1"));
        Assert.Equal("#112233", saved.UnlabeledSpeakerColor);
        Assert.False(File.Exists(Path.Combine(session.Folder, "transcript.refining.json")));
    }

    [Fact]
    public async Task UntranscribedAudioBeforeTheContinuationIsStillRecognized()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var previous = AppStorage.LoadTranscript(source);
        previous.ProcessedFrames = PcmFormat.SampleRate;
        previous.Segments = previous.Segments.Where(segment => segment.EndSeconds <= 1).ToList();
        AppStorage.SaveTranscript(source, previous);
        var session = RecordingContinuationTests.AppendAudio(folder, source);
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        fake.Worker.Response = (_, _, _) =>
            Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 2, "Previously pending and newly recorded words.")]);
        await Pipeline(fake).TranscribeAsync(session, Settings(folder), () => (session.Frames, false),
            new Updates(), CancellationToken.None);
        var result = AppStorage.LoadTranscript(session);
        Assert.Equal(previous.Segments, result.Segments.Take(previous.Segments.Count));
        var added = Assert.Single(result.Segments.Skip(previous.Segments.Count));
        Assert.Equal(1, added.StartSeconds);
        Assert.Equal(3, added.EndSeconds);
        using var reader = new WaveFileReader(new MemoryStream(Assert.Single(fake.Worker.Chunks)));
        Assert.InRange(reader.TotalTime.TotalSeconds, 1.99, 2.01);
    }

    [Fact]
    public async Task ARefinementCheckpointCannotReplaceThePreservedTextWithAnOlderVersion()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var previous = AppStorage.LoadTranscript(source);
        previous.Segments[0] = previous.Segments[0] with { Text = "Latest correction.", IsUserEdited = true };
        AppStorage.SaveTranscript(source, previous);
        var session = RecordingContinuationTests.AppendAudio(folder, source);
        var checkpoint = previous.Copy();
        checkpoint.Model = "small";
        checkpoint.Segments[0] = checkpoint.Segments[0] with { Text = "Stale words." };
        checkpoint.ProcessedFrames = session.Frames;
        AtomicFile.WriteJson(Path.Combine(session.Folder, "transcript.refining.json"), checkpoint);
        var settings = Settings(folder);
        settings.SpeechModel = "small";
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        await Pipeline(fake).TranscribeAsync(session, settings, () => (session.Frames, false),
            new Updates(), CancellationToken.None);
        Assert.Equal(previous.Segments, AppStorage.LoadTranscript(session).Segments.Take(previous.Segments.Count));
        Assert.Equal(1, fake.OpenCount);
        Assert.False(File.Exists(Path.Combine(session.Folder, "transcript.refining.json")));
    }

    [Fact]
    public async Task CorrectionsMadeInTheNewTailAreNotSilentlyOverwrittenByAChangedModel()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var session = RecordingContinuationTests.AppendAudio(folder, source);
        var document = AppStorage.LoadTranscript(session);
        document.Segments.Add(new(2, 3, "A correction after continuing.") { IsUserEdited = true });
        document.ProcessedFrames = session.Frames;
        AppStorage.SaveTranscript(session, document);
        var before = EditedSessionTests.Hashes(session.Folder);
        var settings = Settings(folder);
        settings.SpeechModel = "small";
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pipeline(fake).TranscribeAsync(session, settings, () => (session.Frames, false),
                new Updates(), CancellationToken.None));
        Assert.Equal(0, fake.OpenCount);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task AutomaticSpeakerAnalysisPreservesEarlierAssignmentsNamesAndColors()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var previous = AppStorage.LoadTranscript(source);
        previous.SpeakerColors["spkr1"] = "#123456";
        previous.SpeakerColors["spkr2"] = "#ABCDEF";
        AppStorage.SaveTranscript(source, previous);
        var session = RecordingContinuationTests.AppendAudio(folder, source);
        var document = previous.Copy();
        document.ProcessedFrames = session.Frames;
        document.Segments.Add(new(2, 2.5, "A known speaker."));
        document.Segments.Add(new(2.5, 3, "A new speaker."));
        document.SpeakersIdentified = false;
        AppStorage.SaveTranscript(session, document);
        var settings = Settings(folder);
        settings.IdentifySpeakersOnStop = true;
        var speaker = new RenumberingSpeakers();
        await new AiPipeline(speakerService: speaker).CompleteTranscriptionAsync(session, settings,
            document, new Updates(), CancellationToken.None);
        var result = AppStorage.LoadTranscript(session);
        Assert.Equal(1, speaker.Calls);
        Assert.Equal(previous.Segments, result.Segments.Take(previous.Segments.Count));
        Assert.Equal(previous.SpeakerNames, result.SpeakerNames);
        Assert.Equal(previous.SpeakerColors, result.SpeakerColors);
        Assert.Equal("spkr1", result.Segments[^2].SpeakerId);
        Assert.Equal("spkr3", result.Segments[^1].SpeakerId);
        Assert.True(result.SpeakersIdentified);
    }

    [Fact]
    public void PreservationIncludesAnEntireCrossingRowAndRejectsTruncatedTranscripts()
    {
        var source = AudioEditPlanTests.Document(3, new(0, 1, "Earlier."),
            new(1.5, 2.25, "Crossing the boundary."), new(2.25, 3, "Newer."));
        var frames = TranscriptContinuation.PreservedFrames(source, 2L * PcmFormat.SampleRate);
        Assert.Equal((long)(2.25 * PcmFormat.SampleRate), frames);
        var refined = TranscriptContinuation.CreateRefinement(source, frames, "small", "zh");
        Assert.Equal(source.Segments.Take(2), refined.Segments);
        Assert.Equal(frames, refined.ProcessedFrames);
        Assert.True(TranscriptContinuation.MatchesPrefix(refined, source, frames));
        source.Segments[0] = source.Segments[0] with { Text = "Changed after checkpoint." };
        Assert.False(TranscriptContinuation.MatchesPrefix(refined, source, frames));
        Assert.Throws<InvalidDataException>(() => TranscriptContinuation.PreservedFrames(source, -1));
        Assert.Throws<InvalidDataException>(() => TranscriptContinuation.PreservedFrames(source, source.ProcessedFrames + 1));
    }

    [Fact]
    public void ContinuedMainPointsStayStaleUntilBothTranscriptionAndSummaryCatchUp()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var transcript = AppStorage.LoadTranscript(source);
        var summary = new SummaryDocument
        {
            Text = "Earlier main points.", TranscriptRevision = transcript.Revision,
            SummarizedSegments = transcript.Segments.Count
        };
        var path = Path.Combine(source.Folder, "summary.json");
        AtomicFile.WriteJson(path, summary);
        Assert.False(AppStorage.SummaryNeedsRefresh(source, transcript));
        var continued = RecordingContinuationTests.AppendAudio(folder, source);
        Assert.True(AppStorage.SummaryNeedsRefresh(continued, transcript));
        transcript.Segments.Add(new(2, 3, "A new point.") { SpeakerId = "spkr1" });
        transcript.ProcessedFrames = continued.Frames;
        AppStorage.SaveTranscript(continued, transcript);
        Assert.True(AppStorage.SummaryNeedsRefresh(continued, transcript));
        summary.SummarizedSegments = transcript.Segments.Count;
        AtomicFile.WriteJson(path, summary);
        Assert.False(AppStorage.SummaryNeedsRefresh(continued, transcript));
        AppStorage.SetSpeakerColor(continued, "spkr1", "#123456");
        Assert.False(AppStorage.SummaryNeedsRefresh(continued, AppStorage.LoadTranscript(continued)));
    }

    private static AppSettings Settings(TestFolder folder) => new()
    {
        SaveFolder = folder.Root, SpeechModel = "base", Language = "en",
        FastLiveDrafts = true, IdentifySpeakersOnStop = false, SummarizeOnStop = false, LiveSummary = false
    };

    private static AiPipeline Pipeline(TranscriptCorrectionTests.FakeTranscriber fake) =>
        new(fake, null, null, (_, _) => Task.CompletedTask);

    private sealed class Updates : IProgress<AiUpdate>
    {
        public void Report(AiUpdate value) { }
    }

    private sealed class RenumberingSpeakers : ISpeakerService
    {
        public int Calls { get; private set; }
        public Task IdentifyAsync(RecordingSession session, TranscriptDocument transcript, int count, CancellationToken cancellationToken,
            Action<AiWorkProgress>? progress = null)
        {
            Calls++;
            transcript.Segments = transcript.Segments.Select(segment => segment with
            {
                SpeakerId = segment.StartSeconds >= 2.5 ? "cluster-new" :
                    segment.StartSeconds >= 2 || segment.SpeakerId == "spkr1" ? "cluster-first" : "cluster-second"
            }).ToList();
            transcript.SpeakerNames.Clear();
            transcript.SpeakerColors.Clear();
            transcript.SpeakersIdentified = true;
            return Task.CompletedTask;
        }
    }
}
