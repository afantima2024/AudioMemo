using System.Security.Cryptography;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class ReusableAudioTests
{
    [Fact]
    public async Task RepeatedMutesAndSelectiveRestoresReuseOneCopyAndPreserveOriginalNotesAndCorrections()
    {
        using var folder = new TestFolder();
        var original = EditedSessionTests.MakeSource(folder);
        var originalFiles = Hashes(original.Folder);
        var first = await Mute(folder, original, row => row.StartSeconds == 0.5);
        Assert.NotEqual(original.Id, first.Id);
        Assert.Equal(AudioWorkspaceService.Mode, first.Edit!.Mode);
        var identity = (first.Id, first.Folder);
        SessionCommands.Rename(first, "My edited meeting");
        File.WriteAllText(first.NotesPath, "Private notes must survive every edit.");
        var document = AppStorage.LoadTranscript(first);
        document.Segments[0] = document.Segments[0] with { Text = "My corrected words.", IsUserEdited = true };
        document.SpeakerNames["spkr1"] = "Jamie";
        AppStorage.SaveTranscript(first, document);
        var second = await Mute(folder, first, row => row.StartSeconds == 1.5);
        Assert.Equal(identity, (second.Id, second.Folder));
        Assert.Equal("My edited meeting", second.Title);
        Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
        var recovery = new SilenceRecoveryService();
        var context = await recovery.PrepareAsync(folder.Root, second, CancellationToken.None);
        Assert.Equal(2, context.MutedRanges.Count);
        Assert.Contains("REMOVE this voice", context.DescribeRange(context.MutedRanges[0]));
        var partial = await recovery.SaveAsync(folder.Root, context, context.Plan([context.MutedRanges[0]]), new Updates(), CancellationToken.None);
        Assert.Equal(identity, (partial.Id, partial.Folder));
        var expected = File.ReadAllBytes(original.WavePath);
        var mutedStart = checked((int)(DurableWaveFile.HeaderSize + 1.5 * PcmFormat.BytesPerSecond));
        Array.Clear(expected, mutedStart, PcmFormat.BytesPerSecond / 2);
        Assert.Equal(expected, File.ReadAllBytes(partial.WavePath));
        var remaining = await recovery.PrepareAsync(folder.Root, partial, CancellationToken.None);
        var all = await recovery.SaveAsync(folder.Root, remaining, remaining.Plan(remaining.MutedRanges), new Updates(), CancellationToken.None);
        Assert.Equal(identity, (all.Id, all.Folder));
        Assert.Empty(all.Edit!.RemovedRanges);
        Assert.Equal(File.ReadAllBytes(original.WavePath), File.ReadAllBytes(all.WavePath));
        Assert.Equal("My corrected words.", AppStorage.LoadTranscript(all).Segments[0].Text);
        Assert.Equal("Jamie", AppStorage.LoadTranscript(all).SpeakerNames["spkr1"]);
        Assert.Equal("Private notes must survive every edit.", File.ReadAllText(all.NotesPath));
        Assert.False(File.Exists(all.SummaryPath));
        Assert.False(File.Exists(Path.Combine(all.Folder, "summary.json")));
        Assert.False(File.Exists(all.Mp3Path));
        var again = await Mute(folder, all, row => row.StartSeconds == 0.5);
        Assert.Equal(identity, (again.Id, again.Folder));
        Assert.Equal(originalFiles, Hashes(original.Folder));
        Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
        Assert.Empty(Directory.GetDirectories(again.Folder, ".audio-edit-*"));
        Assert.False(File.Exists(Path.Combine(again.Folder, ".audio-edit.json")));
    }

    [Fact]
    public async Task ContinuedAudioCanBeMutedAndRestoredWithoutLosingItsNewTail()
    {
        using var folder = new TestFolder();
        var original = EditedSessionTests.MakeSource(folder);
        var muted = await Mute(folder, original, row => row.StartSeconds == 0.5);
        var continued = RecordingContinuationTests.AppendAudio(folder, muted);
        var transcript = AppStorage.LoadTranscript(continued);
        transcript.ProcessedFrames = continued.Frames;
        transcript.Segments.Add(new(2, 3, "Newly recorded tail.") { SpeakerId = "spkr3" });
        AppStorage.SaveTranscript(continued, transcript);
        var before = File.ReadAllBytes(continued.WavePath);
        var updated = await Mute(folder, continued, row => row.StartSeconds == 2);
        Assert.Equal(muted.Id, updated.Id);
        var service = new SilenceRecoveryService();
        var context = await service.PrepareAsync(folder.Root, updated, CancellationToken.None);
        var restored = await service.SaveAsync(folder.Root, context, context.Plan(context.MutedRanges), new Updates(), CancellationToken.None);
        var originalBytes = File.ReadAllBytes(original.WavePath);
        originalBytes.AsSpan(DurableWaveFile.HeaderSize, checked((int)(original.Frames * PcmFormat.BlockAlign)))
            .CopyTo(before.AsSpan(DurableWaveFile.HeaderSize));
        Assert.Equal(before, File.ReadAllBytes(restored.WavePath));
        Assert.Equal(continued.Frames, restored.Frames);
        Assert.Contains(AppStorage.LoadTranscript(restored).Segments, segment => segment.Text == "Newly recorded tail.");
        Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
    }

    [Fact]
    public async Task ANewTranscriptAfterRestoringEverythingBecomesTheNextRecoveryBaseline()
    {
        using var folder = new TestFolder();
        var edited = await Mute(folder, EditedSessionTests.MakeSource(folder), row => row.StartSeconds == 0.5);
        var service = new SilenceRecoveryService();
        var context = await service.PrepareAsync(folder.Root, edited, CancellationToken.None);
        var restored = await service.SaveAsync(folder.Root, context, context.Plan(context.MutedRanges), new Updates(), CancellationToken.None);
        var transcript = AppStorage.LoadTranscript(restored);
        transcript.Model = "small";
        transcript.Segments[1] = transcript.Segments[1] with { Text = "New recognition must be recoverable." };
        AppStorage.SaveTranscript(restored, transcript);
        var mutedAgain = await Mute(folder, restored, row => row.StartSeconds == 0.5);
        var next = await service.PrepareAsync(folder.Root, mutedAgain, CancellationToken.None);
        var plan = next.Plan(next.MutedRanges);
        Assert.False(plan.RebuildsTranscript);
        var result = await service.SaveAsync(folder.Root, next, plan, new Updates(), CancellationToken.None);
        Assert.Equal("small", AppStorage.LoadTranscript(result).Model);
        Assert.Equal("New recognition must be recoverable.", AppStorage.LoadTranscript(result).Segments[1].Text);
        Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
    }

    [Theory]
    [InlineData(SessionEditStage.CopyingAudio)]
    [InlineData(SessionEditStage.SavingCopy)]
    [InlineData(SessionEditStage.EncodingMp3)]
    public async Task CancellationNeverChangesTheExistingEditedRecording(SessionEditStage stage)
    {
        using var folder = new TestFolder();
        var original = EditedSessionTests.MakeSource(folder);
        var edited = await Mute(folder, original, row => row.StartSeconds == 0.5,
            stage == SessionEditStage.EncodingMp3 ? "MP3" : "WAV");
        var before = Hashes(edited.Folder);
        var document = AppStorage.LoadTranscript(edited);
        var plan = AudioEditPlan.Create(document, edited.Frames, TranscriptPresentation.Rows(document).Where(row => row.StartSeconds == 1.5).ToArray());
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EditedSessionService().SaveAsync(folder.Root, edited, plan,
            "Not a new copy", "WAV", new Updates(update =>
            {
                if (update.Stage == stage && (stage != SessionEditStage.CopyingAudio || update.Percent > 0))
                    cancellation.Cancel();
            }), cancellation.Token));
        Assert.Equal(before, Hashes(edited.Folder));
        Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
    }

    [Theory]
    [InlineData("WAV")]
    [InlineData("MP3")]
    public async Task RepeatedEditsRegenerateConfiguredOutputAndDiscardStaleProcessing(string format)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        source.AudioOutput = new() { SampleRate = 44_100, Channels = 1 };
        source.Save();
        var edited = await Mute(folder, source, row => row.StartSeconds == 0.5, format);
        var output = format == "MP3" ? edited.Mp3Path : edited.OutputPath;
        var firstBytes = File.ReadAllBytes(output);
        foreach (var name in new[] { "summary.json", "summary.md", "transcript.refining.json", "pending-processing.json" })
            File.WriteAllText(Path.Combine(edited.Folder, name), "Outdated content.");
        var updated = await Mute(folder, edited, row => row.StartSeconds == 1.5);
        Assert.Equal(format, updated.RequestedFormat);
        Assert.Equal(edited.Id, updated.Id);
        Assert.NotEqual(firstBytes, File.ReadAllBytes(output));
        using (WaveStream reader = format == "MP3" ? new MediaFoundationReader(output) : new WaveFileReader(output))
        {
            Assert.Equal(44_100, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            Assert.InRange(reader.TotalTime.TotalSeconds, 1.99, 2.2);
        }
        foreach (var name in new[] { "summary.json", "summary.md", "transcript.refining.json", "pending-processing.json" })
            Assert.False(File.Exists(Path.Combine(updated.Folder, name)));
        Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
    }

    [Fact]
    public async Task AnOlderLeafCopyUpgradesInPlaceButAnAncestorWithDependentsIsProtected()
    {
        using var folder = new TestFolder();
        var original = EditedSessionTests.MakeSource(folder);
        var first = await SilenceRecoveryTests.Mute(folder, original, row => row.StartSeconds == 0.5);
        var second = await SilenceRecoveryTests.Mute(folder, first, row => row.StartSeconds == 1.5);
        var before = Hashes(first.Folder);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Mute(folder, first, row => row.StartSeconds == 1.5));
        Assert.Equal(before, Hashes(first.Folder));
        var service = new SilenceRecoveryService();
        var context = await service.PrepareAsync(folder.Root, second, CancellationToken.None);
        var updated = await service.SaveAsync(folder.Root, context, context.Plan(context.MutedRanges), new Updates(), CancellationToken.None);
        Assert.Equal(second.Id, updated.Id);
        Assert.Equal(second.Folder, updated.Folder);
        Assert.Equal(AudioWorkspaceService.Mode, updated.Edit!.Mode);
        Assert.Equal(File.ReadAllBytes(original.WavePath), File.ReadAllBytes(updated.WavePath));
        Assert.Equal(3, AppStorage.LoadSessions(folder.Root).Sessions.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(10)]
    public void FailedPublicationRollsBackAudioTranscriptAndMetadata(int failureAfter)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = Hashes(session.Folder);
        using (var transaction = new AudioEditTransaction(session.Folder))
        {
            File.WriteAllText(Path.Combine(transaction.Staging, "audio.wav"), "replacement audio");
            File.WriteAllText(Path.Combine(transaction.Staging, "transcript.json"), "replacement transcript");
            File.Copy(session.MetadataPath, Path.Combine(transaction.Staging, "session.json"));
            Directory.CreateDirectory(Path.Combine(transaction.Staging, ".restore-source"));
            foreach (var name in new[] { "audio.wav", "transcript.json", "session.json" })
                File.Copy(Path.Combine(session.Folder, name), Path.Combine(transaction.Staging, ".restore-source", name));
            Assert.Throws<IOException>(() => transaction.Commit(() => { }, CancellationToken.None,
                count => { if (count == failureAfter) throw new IOException("Synthetic publication failure."); }));
        }
        Assert.Equal(before, Hashes(session.Folder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LoadingHistoryRecoversAnInterruptedPublication(bool committed)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = File.ReadAllBytes(session.WavePath);
        var changed = new byte[] { 4, 3, 2, 1 };
        using (var transaction = new AudioEditTransaction(session.Folder))
        {
            var working = Path.GetDirectoryName(transaction.Staging)!;
            var id = Path.GetFileName(working)[".audio-edit-".Length..];
            AtomicFile.WriteJson(Path.Combine(session.Folder, ".audio-edit.json"),
                new AudioEditTransaction.Journal(id, committed, [new("audio.wav", true, true)]));
            File.WriteAllBytes(Path.Combine(transaction.Staging, "audio.wav"), changed);
            File.Replace(Path.Combine(transaction.Staging, "audio.wav"), session.WavePath, Path.Combine(working, "old", "audio.wav"));
            var loaded = AppStorage.LoadSessions(folder.Root);
            Assert.Empty(loaded.Errors);
            Assert.Single(loaded.Sessions);
        }
        Assert.Equal(committed ? changed : before, File.ReadAllBytes(session.WavePath));
        Assert.False(File.Exists(Path.Combine(session.Folder, ".audio-edit.json")));
        Assert.Empty(Directory.GetDirectories(session.Folder, ".audio-edit-*"));
    }

    [Fact]
    public void InterruptedStagingIsCleanedWithoutTouchingTheSavedRecording()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = Hashes(session.Folder);
        using (var transaction = new AudioEditTransaction(session.Folder))
        {
            File.WriteAllText(Path.Combine(transaction.Staging, "audio.wav"), "unfinished staging");
            Assert.True(File.Exists(Path.Combine(session.Folder, ".audio-edit.json")));
            var loaded = AppStorage.LoadSessions(folder.Root);
            Assert.Empty(loaded.Errors);
            Assert.Single(loaded.Sessions);
        }
        Assert.Equal(before, Hashes(session.Folder));
    }

    [Fact]
    public void InvalidRecoveryJournalCannotRemoveFilesOutsideTheTransaction()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var sentinel = folder.File("keep.txt");
        File.WriteAllText(sentinel, "Keep this unrelated file.");
        var path = Path.Combine(session.Folder, ".audio-edit.json");
        AtomicFile.WriteJson(path, new AudioEditTransaction.Journal(Guid.NewGuid().ToString("N"), false,
            [new("..\\keep.txt", false, true)]));
        var loaded = AppStorage.LoadSessions(folder.Root);
        Assert.Empty(loaded.Sessions);
        Assert.Single(loaded.Errors);
        Assert.Equal("Keep this unrelated file.", File.ReadAllText(sentinel));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void CommittedRecoveryCanFinishCleanupAfterItsWorkingDirectoryIsGone()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = Hashes(session.Folder);
        AtomicFile.WriteJson(Path.Combine(session.Folder, ".audio-edit.json"),
            new AudioEditTransaction.Journal(Guid.NewGuid().ToString("N"), true, [new("audio.wav", true, true)]));
        Assert.Empty(AppStorage.LoadSessions(folder.Root).Errors);
        Assert.Equal(before, Hashes(session.Folder));
    }

    private static Task<RecordingSession> Mute(TestFolder folder, RecordingSession source, Func<TranscriptRow, bool> predicate, string format = "WAV")
    {
        var transcript = AppStorage.LoadTranscript(source);
        var plan = AudioEditPlan.Create(transcript, source.Frames, TranscriptPresentation.Rows(transcript).Where(predicate).ToArray());
        return new EditedSessionService().SaveAsync(folder.Root, source, plan, "Edited working copy", format, new Updates(), CancellationToken.None);
    }

    private static KeyValuePair<string, string>[] Hashes(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order().Select(path =>
            KeyValuePair.Create(Path.GetRelativePath(folder, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();

    private sealed class Updates(Action<SessionEditProgress>? action = null) : IProgress<SessionEditProgress>
    {
        public void Report(SessionEditProgress value) => action?.Invoke(value);
    }
}
