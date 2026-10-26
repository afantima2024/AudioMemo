using AudioMemo.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class SilenceRecoveryTests
{
    [Fact]
    public async Task SelectiveRecoveryChangesOnlyRequestedSamplesAndAddsOnlyRecoveredTextToNewSummaries()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var muted = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        using (var retainedAudio = new FileStream(muted.WavePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            retainedAudio.Position = DurableWaveFile.HeaderSize + 100 * PcmFormat.BlockAlign;
            retainedAudio.Write([0x39, 0x30, 0x3e, 0x22]);
        }
        AppStorage.RenameSpeaker(muted, "spkr1", "Jamie");
        File.WriteAllText(muted.NotesPath, "CURRENT PRIVATE NOTE");
        AtomicFile.WriteText(muted.SummaryPath, "OLD SUMMARY MUST NOT RETURN");
        var originalHash = EditedSessionTests.Hashes(source.Folder);
        var mutedHash = EditedSessionTests.Hashes(muted.Folder);
        var service = new SilenceRecoveryService();
        var recovery = await service.PrepareAsync(folder.Root, muted, CancellationToken.None);
        Assert.Equal(source.Id, recovery.Original.Id);
        Assert.Equal(2, recovery.MutedRanges.Count);
        var first = recovery.MutedRanges[0];
        var plan = recovery.Plan([first]);
        Assert.False(plan.RebuildsTranscript);
        var restored = await service.CreateAsync(folder.Root, recovery, plan, "Partially restored", "WAV", new Updates(), CancellationToken.None);
        var expected = File.ReadAllBytes(muted.WavePath);
        var start = checked((int)(DurableWaveFile.HeaderSize + first.StartFrame * PcmFormat.BlockAlign));
        var count = checked((int)(first.Length * PcmFormat.BlockAlign));
        File.ReadAllBytes(source.WavePath).AsSpan(start, count).CopyTo(expected.AsSpan(start, count));
        Assert.Equal(expected, File.ReadAllBytes(restored.WavePath));
        Assert.Equal(source.Frames, restored.Frames);
        Assert.Equal(muted.Devices, restored.Devices);
        Assert.Equal("Restore", restored.Edit!.Mode);
        Assert.Empty(restored.Edit.RemovedRanges);
        Assert.Equal(first, Assert.Single(restored.Edit.RestoredRanges));
        Assert.Equal("CURRENT PRIVATE NOTE", File.ReadAllText(restored.NotesPath));
        Assert.False(File.Exists(restored.SummaryPath));
        Assert.False(File.Exists(Path.Combine(restored.Folder, "summary.json")));
        var document = AppStorage.LoadTranscript(restored);
        Assert.Equal([0d, 0.5d, 1d], document.Segments.Select(segment => segment.StartSeconds));
        Assert.Equal("Jamie", document.SpeakerNames["spkr1"]);
        Assert.Equal("Noise", document.SpeakerNames["spkr2"]);
        Assert.Contains("REMOVE this voice", document.ToText());
        Assert.DoesNotContain("REMOVE this other voice", document.ToText());
        var capture = new SummaryCapture();
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        await new AiPipeline(summaryService: capture).SummarizeAsync(restored, settings, new Progress<AiUpdate>(), false, CancellationToken.None);
        var summary = Assert.Single(capture.Calls);
        Assert.Empty(summary.Previous);
        Assert.Contains("REMOVE this voice", summary.Text);
        Assert.DoesNotContain("REMOVE this other voice", summary.Text);
        Assert.DoesNotContain("PRIVATE NOTE", summary.Text);
        Assert.DoesNotContain("OLD SUMMARY", summary.Text);
        Assert.Equal(originalHash, EditedSessionTests.Hashes(source.Folder));
        Assert.Equal(mutedHash, EditedSessionTests.Hashes(muted.Folder));
        var remaining = await service.PrepareAsync(folder.Root, restored, CancellationToken.None);
        Assert.Equal(recovery.MutedRanges[1], Assert.Single(remaining.MutedRanges));
    }

    [Fact]
    public async Task RestoreAllRecoversTheOriginalAudioAcrossMultipleEditsAndPartialRestores()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var first = await Mute(folder, source, row => row.StartSeconds == 0.5);
        var second = await Mute(folder, first, row => row.StartSeconds == 1.5);
        var service = new SilenceRecoveryService();
        var history = await service.PrepareAsync(folder.Root, second, CancellationToken.None);
        Assert.Equal(2, history.MutedRanges.Count);
        var partial = await service.CreateAsync(folder.Root, history, history.Plan([history.MutedRanges[0]]),
            "Recovered first", "WAV", new Updates(), CancellationToken.None);
        var remuted = await Mute(folder, partial, row => row.SpeakerId == "spkr1");
        var remutedHistory = await service.PrepareAsync(folder.Root, remuted, CancellationToken.None);
        var final = await service.CreateAsync(folder.Root, remutedHistory, remutedHistory.Plan(remutedHistory.MutedRanges),
            "All recovered", "WAV", new Updates(), CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(source.WavePath), File.ReadAllBytes(final.WavePath));
        Assert.Equal(AppStorage.LoadTranscript(source).Segments, AppStorage.LoadTranscript(final).Segments);
        Assert.Equal(source.Frames, final.Frames);
        Assert.Equal(source.Id, remutedHistory.Original.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareAsync(folder.Root, final, CancellationToken.None));
    }

    [Fact]
    public async Task AWholeRecordingMutedOnTopOfEarlierSilenceCanStillBeFullyRecovered()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var first = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        var second = await Mute(folder, first, _ => true);
        Assert.Empty(AppStorage.LoadTranscript(second).Segments);
        var service = new SilenceRecoveryService();
        var history = await service.PrepareAsync(folder.Root, second, CancellationToken.None);
        Assert.Equal(new AudioFrameRange(0, source.Frames), Assert.Single(history.MutedRanges));
        var restored = await service.CreateAsync(folder.Root, history, history.Plan(history.MutedRanges),
            "All audible again", "WAV", new Updates(), CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(source.WavePath), File.ReadAllBytes(restored.WavePath));
    }

    [Fact]
    public async Task APreviousCutIsATimelineBoundaryRatherThanARecoverySourceWithDifferentOffsets()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        source.Edit = new("unavailable-pre-cut-session", "An older long recording", source.CreatedAt, source.Frames * 2, []);
        source.Save();
        var muted = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        var service = new SilenceRecoveryService();
        var recovery = await service.PrepareAsync(folder.Root, muted, CancellationToken.None);
        Assert.Equal(source.Id, recovery.Original.Id);
        var restored = await service.CreateAsync(folder.Root, recovery, recovery.Plan(recovery.MutedRanges),
            "Recovered on cut timeline", "WAV", new Updates(), CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(source.WavePath), File.ReadAllBytes(restored.WavePath));
    }

    [Fact]
    public async Task MissingSourceAndCyclesProduceExplicitFailuresWithoutWritingCopies()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var muted = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        var service = new SilenceRecoveryService();
        var wave = File.ReadAllBytes(source.WavePath);
        File.Delete(source.WavePath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.PrepareAsync(folder.Root, muted, CancellationToken.None));
        File.WriteAllBytes(source.WavePath, wave);
        var metadata = File.ReadAllText(source.MetadataPath);
        File.Delete(source.MetadataPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.PrepareAsync(folder.Root, muted, CancellationToken.None));
        File.WriteAllText(source.MetadataPath, metadata);
        muted.Edit = muted.Edit! with { SourceSessionId = muted.Id };
        muted.Save();
        await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(folder.Root, muted, CancellationToken.None));
        Assert.Equal(2, Directory.GetDirectories(folder.Root).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothCurrentAndOriginalTranscriptsAreRequired(bool original)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var muted = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        File.Delete((original ? source : muted).TranscriptPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SilenceRecoveryService().PrepareAsync(folder.Root, muted, CancellationToken.None));
        Assert.Equal(2, Directory.GetDirectories(folder.Root).Length);
    }

    [Fact]
    public async Task MissingIntermediateHistoryCannotBeSkippedEvenWhenOriginalAudioExists()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var first = await Mute(folder, source, row => row.StartSeconds == 0.5);
        var second = await Mute(folder, first, row => row.StartSeconds == 1.5);
        File.Delete(first.MetadataPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new SilenceRecoveryService().PrepareAsync(folder.Root, second, CancellationToken.None));
        Assert.Equal(3, Directory.GetDirectories(folder.Root).Length);
    }

    [Theory]
    [InlineData("frames")]
    [InlineData("source-frames")]
    [InlineData("mode")]
    [InlineData("empty-silence")]
    [InlineData("mixed-silence")]
    [InlineData("mixed-restore")]
    [InlineData("empty-restore")]
    [InlineData("restore-outside-mutes")]
    [InlineData("duplicate-id")]
    public async Task InvalidRecoveryHistoryIsRejected(string corruption)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var muted = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        var interval = muted.Edit!.RemovedRanges[0];
        switch (corruption)
        {
            case "frames":
                source.Frames--;
                source.Save();
                break;
            case "source-frames":
                muted.Edit = muted.Edit with { SourceFrames = muted.Frames - 1 };
                break;
            case "mode":
                muted.Edit = muted.Edit with { Mode = "Unknown" };
                break;
            case "empty-silence":
                muted.Edit = muted.Edit with { RemovedRanges = [] };
                break;
            case "mixed-silence":
                muted.Edit = muted.Edit with { RestoredRanges = [interval] };
                break;
            case "mixed-restore":
                muted.Edit = muted.Edit with { Mode = "Restore", RestoredRanges = [interval] };
                break;
            case "empty-restore":
                muted.Edit = muted.Edit with { Mode = "Restore", RemovedRanges = [] };
                break;
            case "restore-outside-mutes":
                muted.Edit = muted.Edit with { Mode = "Restore", RemovedRanges = [], RestoredRanges = [interval] };
                break;
            case "duplicate-id":
                var duplicate = Path.Combine(folder.Root, "duplicate");
                Directory.CreateDirectory(duplicate);
                File.Copy(source.MetadataPath, Path.Combine(duplicate, "session.json"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        muted.Save();
        var count = Directory.GetDirectories(folder.Root).Length;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SilenceRecoveryService().PrepareAsync(folder.Root, muted, CancellationToken.None));
        Assert.Equal(count, Directory.GetDirectories(folder.Root).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedWavePayloadCannotPublishAnIncompleteRestoredCopy(bool original)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var muted = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        var service = new SilenceRecoveryService();
        var recovery = await service.PrepareAsync(folder.Root, muted, CancellationToken.None);
        var plan = recovery.Plan(recovery.MutedRanges);
        using (var audio = new FileStream((original ? source : muted).WavePath, FileMode.Open, FileAccess.Write, FileShare.None))
            audio.SetLength(audio.Length + 1);
        var originalHash = EditedSessionTests.Hashes(source.Folder);
        var mutedHash = EditedSessionTests.Hashes(muted.Folder);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(folder.Root, recovery, plan,
            "Invalid audio", "WAV", new Updates(), CancellationToken.None));
        Assert.Equal(originalHash, EditedSessionTests.Hashes(source.Folder));
        Assert.Equal(mutedHash, EditedSessionTests.Hashes(muted.Folder));
        Assert.Equal(2, Directory.GetDirectories(folder.Root).Length);
        using var originalExclusive = new FileStream(source.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var currentExclusive = new FileStream(muted.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(SessionEditStage.CopyingAudio)]
    [InlineData(SessionEditStage.EncodingMp3)]
    [InlineData(SessionEditStage.SavingCopy)]
    public async Task CancelledRecoveryLeavesSourceAndCurrentCopiesUntouched(SessionEditStage stage)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var muted = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        var originalHash = EditedSessionTests.Hashes(source.Folder);
        var mutedHash = EditedSessionTests.Hashes(muted.Folder);
        var service = new SilenceRecoveryService();
        var recovery = await service.PrepareAsync(folder.Root, muted, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var progress = new Updates(update =>
        {
            if (update.Stage == stage && (stage != SessionEditStage.CopyingAudio || update.Percent > 0))
                cancel.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(folder.Root, recovery,
            recovery.Plan(recovery.MutedRanges), "Cancelled recovery", "MP3", progress, cancel.Token));
        Assert.Equal(originalHash, EditedSessionTests.Hashes(source.Folder));
        Assert.Equal(mutedHash, EditedSessionTests.Hashes(muted.Folder));
        Assert.Equal(2, Directory.GetDirectories(folder.Root).Length);
    }

    [Fact]
    public async Task StaleSourceOrHistoryCannotBeAppliedAfterPreview()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var muted = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        var service = new SilenceRecoveryService();
        var recovery = await service.PrepareAsync(folder.Root, muted, CancellationToken.None);
        var plan = recovery.Plan(recovery.MutedRanges);
        AppStorage.RenameSpeaker(muted, "spkr1", "Renamed after preview");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(folder.Root, recovery, plan,
            "Stale recovery", "WAV", new Updates(), CancellationToken.None));
        recovery = await service.PrepareAsync(folder.Root, muted, CancellationToken.None);
        plan = recovery.Plan(recovery.MutedRanges);
        var progress = new Updates(update =>
        {
            if (update.Stage == SessionEditStage.SavingCopy)
            {
                source.Title = "Concurrent title change";
                source.Save();
            }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(folder.Root, recovery, plan,
            "Changed during recovery", "WAV", progress, CancellationToken.None));
        Assert.Equal(2, Directory.GetDirectories(folder.Root).Length);
        Assert.Equal("Renamed after preview", AppStorage.LoadTranscript(muted).SpeakerNames["spkr1"]);
    }

    [Fact]
    public async Task RecoveredMp3ContainsAudioWhereTheSelectedSilenceUsedToBe()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var muted = await Mute(folder, source, row => row.SpeakerId == "spkr2");
        var service = new SilenceRecoveryService();
        var recovery = await service.PrepareAsync(folder.Root, muted, CancellationToken.None);
        var restored = await service.CreateAsync(folder.Root, recovery, recovery.Plan([recovery.MutedRanges[0]]),
            "Recovered MP3", "MP3", new Updates(), CancellationToken.None);
        using var reader = new MediaFoundationReader(restored.Mp3Path);
        Assert.InRange(reader.TotalTime.TotalSeconds, 1.9, 2.2);
        var provider = reader.ToSampleProvider();
        var samples = new List<float>();
        var buffer = new float[4096];
        int count;
        while ((count = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, count).ToArray());
        double Rms(double start) => Math.Sqrt(samples.Skip((int)(start * provider.WaveFormat.SampleRate * provider.WaveFormat.Channels))
            .Take((int)(0.15 * provider.WaveFormat.SampleRate * provider.WaveFormat.Channels)).Average(value => value * value));
        Assert.True(Rms(0.7) > 0.03);
        Assert.InRange(Rms(1.7), 0, 0.001);
    }

    internal static Task<RecordingSession> Mute(TestFolder folder, RecordingSession source, Func<TranscriptRow, bool> selection)
    {
        var transcript = AppStorage.LoadTranscript(source);
        var plan = AudioEditPlan.Create(transcript, source.Frames, TranscriptPresentation.Rows(transcript).Where(selection).ToArray());
        return new EditedSessionService().CreateAsync(folder.Root, source, plan, "Synthetic silence edit", "WAV", new Updates(), CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuingAnOriginalDoesNotBreakRestorationOfItsOlderEditedCopy(bool refiningTail)
    {
        using var folder = new TestFolder();
        var original = EditedSessionTests.MakeSource(folder);
        var originalAudio = File.ReadAllBytes(original.WavePath);
        var originalText = AppStorage.LoadTranscript(original);
        var muted = await Mute(folder, original, row => row.SpeakerId == "spkr2");
        var continued = RecordingContinuationTests.AppendAudio(folder, original);
        var document = AppStorage.LoadTranscript(continued);
        document.Segments.Add(new(2, 3, "New words in the original recording.") { SpeakerId = "spkr1" });
        document.ProcessedFrames = continued.Frames;
        document.IsDraft = refiningTail;
        AppStorage.SaveTranscript(continued, document);
        var before = EditedSessionTests.Hashes(continued.Folder);
        var service = new SilenceRecoveryService();
        var recovery = await service.PrepareAsync(folder.Root, muted, CancellationToken.None);
        var restored = await service.CreateAsync(folder.Root, recovery, recovery.Plan(recovery.MutedRanges),
            "Restore the older prefix only", "WAV", new Updates(), CancellationToken.None);
        Assert.Equal(originalAudio, File.ReadAllBytes(restored.WavePath));
        Assert.Equal(originalText.Segments, AppStorage.LoadTranscript(restored).Segments);
        Assert.Equal(before, EditedSessionTests.Hashes(continued.Folder));
    }

    [Fact]
    public async Task ContinuedEditsRestoreEarlierSilenceWithoutReplacingTheirOwnNewAudioOrCorrections()
    {
        using var folder = new TestFolder();
        var original = EditedSessionTests.MakeSource(folder);
        var originalAudio = File.ReadAllBytes(original.WavePath);
        var muted = await Mute(folder, original, row => row.SpeakerId == "spkr2");
        var parent = RecordingContinuationTests.AppendAudio(folder, original, amplitude: 0.8);
        var parentText = AppStorage.LoadTranscript(parent);
        parentText.Segments.Add(new(2, 3, "Only in the parent.") { SpeakerId = "spkr1" });
        parentText.ProcessedFrames = parent.Frames;
        AppStorage.SaveTranscript(parent, parentText);
        var child = RecordingContinuationTests.AppendAudio(folder, muted, amplitude: 0.2);
        var childText = AppStorage.LoadTranscript(child);
        childText.Segments[0] = childText.Segments[0] with { Text = "A retained correction.", IsUserEdited = true };
        var newRow = new TranscriptSegment(2, 3, "Only in the continued child.") { SpeakerId = "spkr2", IsUserEdited = true };
        childText.Segments.Add(newRow);
        childText.SpeakerNames["spkr2"] = "New voice";
        childText.SpeakerColors["spkr2"] = "#123456";
        childText.ProcessedFrames = child.Frames;
        AppStorage.SaveTranscript(child, childText);
        var parentBefore = EditedSessionTests.Hashes(parent.Folder);
        var childBefore = EditedSessionTests.Hashes(child.Folder);
        var service = new SilenceRecoveryService();
        var recovery = await service.PrepareAsync(folder.Root, child, CancellationToken.None);
        var plan = recovery.Plan(recovery.MutedRanges);
        Assert.False(plan.RebuildsTranscript);
        var restored = await service.CreateAsync(folder.Root, recovery, plan,
            "Restore the original parts and keep the new tail", "WAV", new Updates(), CancellationToken.None);
        var expected = File.ReadAllBytes(child.WavePath);
        originalAudio.AsSpan(DurableWaveFile.HeaderSize).CopyTo(expected.AsSpan(DurableWaveFile.HeaderSize));
        Assert.Equal(expected, File.ReadAllBytes(restored.WavePath));
        var result = AppStorage.LoadTranscript(restored);
        Assert.Equal(childText.Segments[0], result.Segments[0]);
        Assert.Equal(newRow, result.Segments[^1]);
        Assert.Equal("New voice", result.SpeakerNames["spkr2"]);
        Assert.Equal("#123456", result.SpeakerColors["spkr2"]);
        Assert.Equal("Noise", result.SpeakerNames[result.Segments[1].SpeakerId!]);
        Assert.NotEqual("spkr2", result.Segments[1].SpeakerId);
        Assert.DoesNotContain("Only in the parent.", result.ToText());
        Assert.Equal(parentBefore, EditedSessionTests.Hashes(parent.Folder));
        Assert.Equal(childBefore, EditedSessionTests.Hashes(child.Folder));

        var tailMuted = await Mute(folder, restored, row => row.StartSeconds >= 2);
        var tailRecovery = await service.PrepareAsync(folder.Root, tailMuted, CancellationToken.None);
        var final = await service.CreateAsync(folder.Root, tailRecovery, tailRecovery.Plan(tailRecovery.MutedRanges),
            "Restore the new tail through the edit chain", "WAV", new Updates(), CancellationToken.None);
        Assert.Equal(expected, File.ReadAllBytes(final.WavePath));
        Assert.Equal(newRow, AppStorage.LoadTranscript(final).Segments[^1]);
    }

    private sealed class Updates(Action<SessionEditProgress>? action = null) : IProgress<SessionEditProgress>
    {
        public void Report(SessionEditProgress value) => action?.Invoke(value);
    }

    private sealed class SummaryCapture : ISummaryService
    {
        public List<(string Text, string Previous)> Calls { get; } = [];
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous,
            bool live, Action<string>? warning, CancellationToken token)
        {
            Calls.Add((transcript, previous));
            return Task.FromResult("Recovered and retained text only.");
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
            Action<string>? warning, CancellationToken cancellationToken) =>
            Task.FromResult("Recovered and retained text only, summarized overall.");
    }
}
