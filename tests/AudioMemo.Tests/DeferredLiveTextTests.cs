using AudioMemo.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class DeferredLiveTextTests
{
    [Fact]
    public async Task TurningOffAutomaticTranscriptionKeepsLiveTextAndLiveMainPointsUntilManualRefinement()
    {
        using var folder = new TestFolder();
        var session = AppStorage.CreateSession(new() { SaveFolder = folder.Root }, "Synthetic live text");
        File.Copy(folder.WriteTone(92), session.WavePath);
        session.Frames = 92L * PcmFormat.SampleRate;
        session.Status = "Saved";
        session.Save();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        settings.SpeechModel = "small";
        settings.TranscribeOnStop = false;
        settings.LiveSummary = settings.SummarizeOnStop = true;
        settings.IdentifySpeakersOnStop = true;
        var recording = true;
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        fake.Worker.Response = (path, number, _) =>
        {
            using var wave = new WaveFileReader(path);
            if (number == 4)
                recording = false;
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, wave.TotalTime.TotalSeconds, $"Words from chunk {number}.")]);
        };
        var summary = new Summaries();
        var speakers = new Speakers();
        var pipeline = new AiPipeline(fake, summary, speakers, (_, _) => Task.CompletedTask);
        await pipeline.TranscribeAsync(session, settings, () => (session.Frames, recording), new Updates(), CancellationToken.None);
        var draft = AppStorage.LoadTranscript(session);
        Assert.True(draft.IsDraft);
        Assert.Equal("base", draft.Model);
        Assert.Equal(1, fake.OpenCount);
        Assert.NotEmpty(draft.Segments);
        Assert.NotEmpty(summary.Live);
        Assert.All(summary.Live, live => Assert.True(live));
        Assert.Equal(0, summary.OverallCalls);
        Assert.Equal(0, speakers.Calls);
        Assert.False(File.Exists(Path.Combine(session.Folder, "transcript.refining.json")));
        var saved = File.ReadAllBytes(session.TranscriptPath);
        var points = File.ReadAllBytes(session.SummaryPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.SummarizeAsync(session, settings, new Updates(), false, CancellationToken.None));
        Assert.Equal(saved, File.ReadAllBytes(session.TranscriptPath));
        Assert.Equal(points, File.ReadAllBytes(session.SummaryPath));
        await pipeline.TranscribeAsync(session, settings, () => (session.Frames, false), new Updates(), CancellationToken.None);
        Assert.False(AppStorage.LoadTranscript(session).IsDraft);
        Assert.Equal(session.Frames, AppStorage.LoadTranscript(session).ProcessedFrames);
        Assert.Equal("small", AppStorage.LoadTranscript(session).Model);
        Assert.Equal(2, fake.OpenCount);
        Assert.Contains(false, summary.Live);
        Assert.Equal(1, summary.OverallCalls);
        Assert.Equal(1, speakers.Calls);
    }

    [Fact]
    public async Task StoppingDoesNotTranscribeAnUnprocessedBacklogWhenAutomaticTranscriptionIsOff()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        File.Copy(folder.WriteTone(35), source.WavePath, overwrite: true);
        source.Frames = 35L * PcmFormat.SampleRate;
        source.Save();
        File.Delete(source.TranscriptPath);
        var recording = true;
        var fake = new TranscriptCorrectionTests.FakeTranscriber();
        fake.Worker.Response = (_, _, _) =>
        {
            recording = false;
            return Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 1, "Saved live words.")]);
        };
        var settings = new AppSettings { SaveFolder = folder.Root, TranscribeOnStop = false, IdentifySpeakersOnStop = false };
        await new AiPipeline(fake, null, null, (_, _) => Task.CompletedTask)
            .TranscribeAsync(source, settings, () => (source.Frames, recording), new Updates(), CancellationToken.None);
        Assert.True(AppStorage.LoadTranscript(source).IsDraft);
        Assert.InRange(AppStorage.LoadTranscript(source).ProcessedFrames, 1, source.Frames - 1);
        Assert.Equal(1, fake.OpenCount);
        Assert.Single(fake.Worker.Chunks);
    }

    private sealed class Summaries : ISummaryService
    {
        public List<bool> Live { get; } = [];
        public int OverallCalls { get; private set; }
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
            Action<string>? warning, CancellationToken token)
        {
            Live.Add(live);
            return Task.FromResult("Synthetic main points.");
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string points, Action<string>? warning, CancellationToken token)
        {
            OverallCalls++;
            return Task.FromResult("Synthetic overall summary.");
        }
    }

    private sealed class Speakers : ISpeakerService
    {
        public int Calls { get; private set; }
        public Task IdentifyAsync(RecordingSession session, TranscriptDocument transcript, int count, CancellationToken token,
            Action<AiWorkProgress>? progress = null)
        {
            Calls++;
            transcript.SpeakersIdentified = true;
            AppStorage.SaveTranscript(session, transcript);
            return Task.CompletedTask;
        }
    }

    private sealed class Updates : IProgress<AiUpdate>
    {
        public void Report(AiUpdate value) { }
    }
}
