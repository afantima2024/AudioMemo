using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class EditedSessionTests
{
    [Fact]
    public async Task EditedAudioTextAndSummaryInputsAgreeWithoutChangingTheOriginal()
    {
        using var folder = new TestFolder();
        var source = MakeSource(folder);
        var originalHashes = Hashes(source.Folder);
        var sourceBytes = File.ReadAllBytes(source.WavePath);
        var plan = Plan(source);
        var edited = await new EditedSessionService().CreateAsync(folder.Root, source, plan, "Edited synthetic meeting",
            "WAV", new Updates(), CancellationToken.None);
        Assert.NotEqual(source.Id, edited.Id);
        Assert.Equal("Saved", edited.Status);
        Assert.Equal(source.Frames, edited.Frames);
        Assert.Equal(edited.Frames, edited.TranscribedFrames);
        Assert.Equal(source.Id, edited.Edit!.SourceSessionId);
        Assert.Equal("Silence", edited.Edit.Mode);
        Assert.Equal(2, edited.Edit.RemovedRanges.Count);
        Assert.Equal(originalHashes, Hashes(source.Folder));
        Assert.False(File.Exists(edited.Mp3Path));
        Assert.False(File.Exists(edited.SummaryPath));
        Assert.False(File.Exists(Path.Combine(edited.Folder, "summary.json")));
        Assert.Equal(File.ReadAllText(source.NotesPath), File.ReadAllText(edited.NotesPath));
        var result = File.ReadAllBytes(edited.WavePath);
        Assert.Equal(sourceBytes.Length, result.Length);
        var expected = sourceBytes.ToArray();
        foreach (var range in plan.MutedRanges)
            Array.Clear(expected, checked((int)(DurableWaveFile.HeaderSize + range.StartFrame * PcmFormat.BlockAlign)),
                checked((int)(range.Length * PcmFormat.BlockAlign)));
        Assert.Equal(expected, result);
        var transcript = AppStorage.LoadTranscript(edited);
        Assert.Equal([0d, 1d], transcript.Segments.Select(segment => segment.StartSeconds));
        Assert.Equal([0.5d, 1.5d], transcript.Segments.Select(segment => segment.EndSeconds));
        Assert.DoesNotContain("REMOVE", transcript.ToText());
        Assert.DoesNotContain("spkr2", transcript.SpeakerNames.Keys);
        Assert.Equal(source.Devices, edited.Devices);
        var capture = new CapturingSummary();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        await new AiPipeline(summaryService: capture).SummarizeAsync(edited, settings, new Progress<AiUpdate>(), false, CancellationToken.None);
        var call = Assert.Single(capture.Calls);
        Assert.Empty(call.Previous);
        Assert.Contains("Keep the first decision", call.Text);
        Assert.Contains("Keep the second decision", call.Text);
        Assert.DoesNotContain("REMOVE", call.Text);
        Assert.DoesNotContain("PRIVATE NOTE", call.Text);
        Assert.DoesNotContain("OLD SUMMARY", call.Text);
        Assert.DoesNotContain("REMOVE", AppStorage.LoadSummaryText(edited));
        Assert.Equal(originalHashes, Hashes(source.Folder));
        Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
    }

    [Fact]
    public async Task RequestedMp3IsEncodedFromTheEditedWaveRatherThanCopied()
    {
        using var folder = new TestFolder();
        var source = MakeSource(folder);
        var edited = await new EditedSessionService().CreateAsync(folder.Root, source, Plan(source), "Edited MP3",
            "MP3", new Updates(), CancellationToken.None);
        Assert.True(File.Exists(edited.WavePath));
        using var reader = new MediaFoundationReader(edited.Mp3Path);
        Assert.InRange(reader.TotalTime.TotalSeconds, 1.9, 2.2);
        var samples = new List<float>();
        var buffer = new float[4096];
        var provider = reader.ToSampleProvider();
        int count;
        while ((count = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, count).ToArray());
        double Rms(double start, double end) => Math.Sqrt(samples.Skip((int)(start * provider.WaveFormat.SampleRate * provider.WaveFormat.Channels))
            .Take((int)((end - start) * provider.WaveFormat.SampleRate * provider.WaveFormat.Channels)).Average(value => value * value));
        Assert.InRange(Rms(0.7, 0.9), 0, 0.001);
        Assert.True(Rms(0.15, 0.3) > 0.03);
        Assert.Equal("OLD MP3 MUST NOT BE COPIED", File.ReadAllText(source.Mp3Path));
    }

    [Theory]
    [InlineData(SessionEditStage.CopyingAudio)]
    [InlineData(SessionEditStage.EncodingMp3)]
    [InlineData(SessionEditStage.SavingCopy)]
    public async Task CancellationNeverPublishesAnIncompleteCopy(SessionEditStage stage)
    {
        using var folder = new TestFolder();
        var source = MakeSource(folder);
        var before = Hashes(source.Folder);
        using var cancel = new CancellationTokenSource();
        var progress = new Updates(update =>
        {
            if (update.Stage == stage && (stage != SessionEditStage.CopyingAudio || update.Percent > 0))
                cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EditedSessionService().CreateAsync(
            folder.Root, source, Plan(source), "Cancelled edit", "MP3", progress, cancel.Token));
        Assert.Equal(before, Hashes(source.Folder));
        Assert.Single(Directory.EnumerateDirectories(folder.Root));
        Assert.Equal(source.Id, Assert.Single(AppStorage.LoadSessions(folder.Root).Sessions).Id);
    }

    [Fact]
    public async Task ChangedTranscriptAndTruncatedAudioDoNotProduceEditedSessions()
    {
        using var folder = new TestFolder();
        var source = MakeSource(folder);
        var plan = Plan(source);
        var transcript = AppStorage.LoadTranscript(source);
        transcript.Segments[0] = transcript.Segments[0] with { Text = "Changed since preview." };
        AppStorage.SaveTranscript(source, transcript);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EditedSessionService().CreateAsync(
            folder.Root, source, plan, "Stale preview", "WAV", new Updates(), CancellationToken.None));
        plan = Plan(source);
        using (var stream = File.Open(source.WavePath, FileMode.Open, FileAccess.Write))
            stream.SetLength(stream.Length - PcmFormat.BlockAlign);
        await Assert.ThrowsAsync<InvalidDataException>(() => new EditedSessionService().CreateAsync(
            folder.Root, source, plan, "Truncated source", "WAV", new Updates(), CancellationToken.None));
        Assert.Single(Directory.EnumerateDirectories(folder.Root));
    }

    [Fact]
    public async Task SourceAudioIsReadLockedAndNestedOutputIsRejected()
    {
        using var folder = new TestFolder();
        var source = MakeSource(folder);
        var service = new EditedSessionService();
        var plan = Plan(source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(source.Folder, source, plan,
            "Invalid destination", "WAV", new Updates(), CancellationToken.None));
        var checkedLock = false;
        await service.CreateAsync(folder.Root, source, plan, "Read locked source", "WAV", new Updates(update =>
        {
            if (update.Stage == SessionEditStage.CopyingAudio)
            {
                Assert.Throws<IOException>(() => File.Open(source.WavePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
                checkedLock = true;
            }
        }), CancellationToken.None);
        Assert.True(checkedLock);
    }

    [Fact]
    public async Task AConcurrentTranscriptCorrectionAbortsTheCopyWithoutRevertingThatCorrection()
    {
        using var folder = new TestFolder();
        var source = MakeSource(folder);
        var plan = Plan(source);
        var changed = false;
        var progress = new Updates(update =>
        {
            if (!changed && update.Stage == SessionEditStage.CopyingAudio && update.Percent == 100)
            {
                var document = AppStorage.LoadTranscript(source);
                document.Segments[0] = document.Segments[0] with { Text = "External correction must survive." };
                AppStorage.SaveTranscript(source, document);
                changed = true;
            }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EditedSessionService().CreateAsync(
            folder.Root, source, plan, "Stale in-flight edit", "WAV", progress, CancellationToken.None));
        Assert.True(changed);
        Assert.Contains("External correction must survive", AppStorage.LoadTranscript(source).ToText());
        Assert.Single(Directory.EnumerateDirectories(folder.Root));
    }

    [Fact]
    public async Task AnEditedCopyCanBeEditedAgainWithoutChangingItsParent()
    {
        using var folder = new TestFolder();
        var source = MakeSource(folder);
        var service = new EditedSessionService();
        var first = await service.CreateAsync(folder.Root, source, Plan(source), "First edit", "WAV", new Updates(), CancellationToken.None);
        var before = Hashes(first.Folder);
        var transcript = AppStorage.LoadTranscript(first);
        var nextPlan = AudioEditPlan.Create(transcript, first.Frames, [TranscriptPresentation.Rows(transcript)[0]]);
        var next = await service.CreateAsync(folder.Root, first, nextPlan, "Second edit", "WAV", new Updates(), CancellationToken.None);
        Assert.Equal(first.Id, next.Edit!.SourceSessionId);
        Assert.Equal(source.Frames, next.Frames);
        Assert.Equal(1, Assert.Single(AppStorage.LoadTranscript(next).Segments).StartSeconds);
        Assert.Equal(before, Hashes(first.Folder));
    }

    [Fact]
    public void IncompleteStagingFoldersNeverAppearAsSavedSessions()
    {
        using var folder = new TestFolder();
        var staging = Path.Combine(folder.Root, AppStorage.EditStagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        AtomicFile.WriteJson(Path.Combine(staging, "session.json"), new RecordingSession { Status = "Saved" });
        Assert.Empty(AppStorage.LoadSessions(folder.Root).Sessions);
    }

    [Fact]
    public void OldCutMetadataIsNotReinterpretedAsASilenceEdit()
    {
        var legacy = JsonSerializer.Deserialize<RecordingEdit>("""
            {"SourceSessionId":"source","SourceTitle":"Original","SourceCreatedAt":"2026-10-14T10:00:00Z",
             "SourceFrames":96000,"RemovedRanges":[{"StartFrame":24000,"EndFrame":48000}]}
            """)!;
        Assert.Equal("Cut", legacy.Mode);
        var current = legacy with { Mode = "Silence" };
        Assert.Equal("Silence", JsonSerializer.Deserialize<RecordingEdit>(JsonSerializer.Serialize(current))!.Mode);
        Assert.Equal("Cut", legacy.Mode);
    }

    internal static RecordingSession MakeSource(TestFolder folder)
    {
        var session = AppStorage.CreateSession(new() { SaveFolder = folder.Root }, "Synthetic audio editing source");
        using (var writer = new DurableWaveFile(session.WavePath))
        {
            var pcm = new byte[2 * PcmFormat.BytesPerSecond];
            for (var frame = 0; frame < 2 * PcmFormat.SampleRate; frame++)
            {
                var section = frame / (PcmFormat.SampleRate / 2);
                var sample = (short)(Math.Sin(frame * 2 * Math.PI * (220 + section * 137) / PcmFormat.SampleRate) * 8000);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * PcmFormat.BlockAlign), sample);
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(frame * PcmFormat.BlockAlign + 2), (short)(sample / 2));
            }
            writer.Write(pcm);
        }
        session.Status = "Saved";
        session.Frames = 2L * PcmFormat.SampleRate;
        session.Devices = [new(0, "Microphone", "One", true), new(0.75, "Microphone", "Two", true), new(1.25, "Microphone", "Three", true)];
        var transcript = AudioEditPlanTests.Document(2,
            new(0, 0.5, "Keep the first decision.") { SpeakerId = "spkr1" },
            new(0.5, 1, "REMOVE this voice.") { SpeakerId = "spkr2" },
            new(1, 1.5, "Keep the second decision.") { SpeakerId = "spkr1" },
            new(1.5, 2, "REMOVE this other voice.") { SpeakerId = "spkr2" });
        transcript.SpeakerNames = new() { ["spkr1"] = "Alex", ["spkr2"] = "Noise" };
        AppStorage.SaveTranscript(session, transcript);
        AtomicFile.WriteText(session.NotesPath, "PRIVATE NOTE: original timestamp 00:00:01");
        AtomicFile.WriteText(session.Mp3Path, "OLD MP3 MUST NOT BE COPIED");
        AtomicFile.WriteJson(Path.Combine(session.Folder, "summary.json"),
            new SummaryDocument { Text = "OLD SUMMARY includes REMOVE.", TranscriptRevision = transcript.Revision });
        AtomicFile.WriteText(session.SummaryPath, "OLD SUMMARY includes REMOVE.");
        return session;
    }

    internal static AudioEditPlan Plan(RecordingSession source)
    {
        var document = AppStorage.LoadTranscript(source);
        return AudioEditPlan.Create(document, source.Frames,
            TranscriptPresentation.Rows(document).Where(row => row.SpeakerId == "spkr2").ToArray());
    }

    internal static KeyValuePair<string, string>[] Hashes(string folder) =>
        Directory.GetFiles(folder).Order().Select(path => KeyValuePair.Create(Path.GetFileName(path),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();

    private sealed class Updates(Action<SessionEditProgress>? action = null) : IProgress<SessionEditProgress>
    {
        public void Report(SessionEditProgress value) => action?.Invoke(value);
    }

    private sealed class CapturingSummary : ISummaryService
    {
        public List<(string Text, string Previous)> Calls { get; } = [];
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous,
            bool live, Action<string>? warning, CancellationToken cancellationToken)
        {
            Calls.Add((transcript, previous));
            return Task.FromResult("Only the retained decisions.");
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
            Action<string>? warning, CancellationToken cancellationToken) =>
            Task.FromResult("Only the retained decisions, summarized overall.");
    }
}
