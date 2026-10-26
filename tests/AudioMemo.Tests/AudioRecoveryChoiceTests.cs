using System.Security.Cryptography;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class AudioRecoveryChoiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstMuteHonorsExplicitSaveModeAndKeepsNotesAndRecoverableOriginalAudio(bool copy)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var document = AppStorage.LoadTranscript(source);
        document.Me = SelfSpeaker.Create("spkr1", "Alex");
        AppStorage.SaveTranscript(source, document);
        var before = Hashes(source.Folder);
        var original = File.ReadAllBytes(source.WavePath);
        var edited = await Mute(folder, source, row => row.StartSeconds == 0.5, copy);
        Assert.Equal(copy, source.Id != edited.Id);
        Assert.Equal(copy ? 2 : 1, AppStorage.LoadSessions(folder.Root).Sessions.Count);
        Assert.Equal(copy ? "Explicit copy" : source.Title, edited.Title);
        Assert.Equal(source.CreatedAt, copy ? source.CreatedAt : edited.CreatedAt);
        Assert.Equal(source.Frames, edited.Frames);
        Assert.True(document.Me.Matches(AppStorage.LoadTranscript(edited).Me));
        Assert.Equal(File.ReadAllText(source.NotesPath), File.ReadAllText(edited.NotesPath));
        Assert.False(File.Exists(edited.SummaryPath));
        Assert.Equal(original, File.ReadAllBytes(AudioWorkspaceService.LoadSource(edited).WavePath));
        if (copy)
            Assert.Equal(before, Hashes(source.Folder));
        var recovery = await new SilenceRecoveryService().PrepareAsync(folder.Root, edited, CancellationToken.None);
        var restored = await new SilenceRecoveryService().SaveAsync(folder.Root, recovery,
            recovery.Plan(recovery.MutedRanges), new Updates(), CancellationToken.None);
        Assert.Equal(edited.Id, restored.Id);
        Assert.Equal(original, File.ReadAllBytes(restored.WavePath));
        Assert.Empty(restored.Edit!.RemovedRanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyingAnAlreadyMutedRecordingCarriesAllRecoveryAudioIntoTheNewCopy(bool firstIsCopy)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var original = File.ReadAllBytes(source.WavePath);
        var first = await Mute(folder, source, row => row.StartSeconds == 0.5, firstIsCopy);
        var before = Hashes(first.Folder);
        var second = await Mute(folder, first, row => row.StartSeconds == 1.5, true);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, second.Edit!.RemovedRanges.Count);
        Assert.Equal(before, Hashes(first.Folder));
        var context = await new SilenceRecoveryService().PrepareAsync(folder.Root, second, CancellationToken.None);
        var restored = await new SilenceRecoveryService().SaveAsync(folder.Root, context,
            context.Plan(context.MutedRanges), new Updates(), CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(restored.WavePath));
        Assert.Equal(4, AppStorage.LoadTranscript(restored).Segments.Count);
        Assert.Equal(before, Hashes(first.Folder));
    }

    [Fact]
    public async Task MergedMutedIntervalsCanBeRecoveredOneOriginalRowAtATime()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var original = File.ReadAllBytes(source.WavePath);
        var muted = await Mute(folder, source, _ => true, false);
        var service = new SilenceRecoveryService();
        var context = await service.PrepareAsync(folder.Root, muted, CancellationToken.None);
        Assert.Single(context.MutedRanges);
        var rows = context.MutedRows();
        Assert.Equal(4, rows.Count);
        var row = rows[1];
        Assert.Contains("REMOVE this voice", context.DescribeRange(row));
        var expected = File.ReadAllBytes(muted.WavePath);
        var start = checked((int)(DurableWaveFile.HeaderSize + row.StartFrame * PcmFormat.BlockAlign));
        var length = checked((int)(row.Length * PcmFormat.BlockAlign));
        original.AsSpan(start, length).CopyTo(expected.AsSpan(start, length));
        var restored = await service.SaveAsync(folder.Root, context, context.Plan([row]), new Updates(), CancellationToken.None);
        Assert.Equal(muted.Id, restored.Id);
        Assert.Equal(expected, File.ReadAllBytes(restored.WavePath));
        Assert.Equal("REMOVE this voice.", Assert.Single(AppStorage.LoadTranscript(restored).Segments).Text);
        context = await service.PrepareAsync(folder.Root, restored, CancellationToken.None);
        Assert.Equal(3, context.MutedRows().Count);
        Assert.Throws<InvalidOperationException>(() => context.Plan([new(1, PcmFormat.SampleRate / 4)]));
    }

    [Fact]
    public async Task PreviewPlaysOriginalPcmAndSupportsPauseSeekAndHandleCleanupWithoutSavingFiles()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var original = File.ReadAllBytes(source.WavePath);
        var muted = await Mute(folder, source, row => row.StartSeconds == 0.5, false);
        var context = await new SilenceRecoveryService().PrepareAsync(folder.Root, muted, CancellationToken.None);
        var before = Hashes(folder.Root);
        var outputs = new List<RecordingPlaybackTests.FakePlayer>();
        using var playback = new RecordingPlayback(() =>
        {
            var output = new RecordingPlaybackTests.FakePlayer();
            outputs.Add(output);
            return output;
        });
        var range = Assert.Single(context.MutedRows());
        playback.PlayRecovery(context, range);
        var start = checked((int)(DurableWaveFile.HeaderSize + range.StartFrame * PcmFormat.BlockAlign));
        var length = checked((int)(range.Length * PcmFormat.BlockAlign));
        Assert.Equal(original.AsSpan(start, length).ToArray(), RecordingPlaybackTests.ReadAll(outputs[0].Provider!));
        playback.TogglePause();
        playback.Seek(0.75);
        Assert.True(playback.Snapshot!.IsPaused);
        Assert.Equal(0.5, playback.Snapshot.StartSeconds);
        Assert.Equal(0.75, playback.Snapshot.PositionSeconds, 5);
        Assert.Equal(original.AsSpan(DurableWaveFile.HeaderSize + 3 * PcmFormat.BytesPerSecond / 4,
            PcmFormat.BytesPerSecond / 4).ToArray(), RecordingPlaybackTests.ReadAll(outputs[1].Provider!));
        playback.Stop();
        Assert.Null(playback.Snapshot);
        Assert.All(outputs, output => Assert.True(output.Disposed));
        Assert.Equal(before, Hashes(folder.Root));
        using var exclusive = File.Open(AudioWorkspaceService.LoadSource(muted).WavePath, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task OriginalPreviewCanJoinRecoverySourcesAcrossAContinuedRecording()
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var first = await SilenceRecoveryTests.Mute(folder, source, row => row.StartSeconds == 0.5);
        var continued = RecordingContinuationTests.AppendAudio(folder, first);
        var document = AppStorage.LoadTranscript(continued);
        document.ProcessedFrames = continued.Frames;
        document.Segments.Add(new(2, 3, "Continued synthetic row.") { SpeakerId = "spkr3" });
        AppStorage.SaveTranscript(continued, document);
        var expected = File.ReadAllBytes(source.WavePath).AsSpan(DurableWaveFile.HeaderSize).ToArray()
            .Concat(File.ReadAllBytes(continued.WavePath).AsSpan(DurableWaveFile.HeaderSize + 2 * PcmFormat.BytesPerSecond).ToArray()).ToArray();
        var muted = await SilenceRecoveryTests.Mute(folder, continued, _ => true);
        var context = await new SilenceRecoveryService().PrepareAsync(folder.Root, muted, CancellationToken.None);
        Assert.True(context.AudioSources.Count >= 2);
        var before = Hashes(folder.Root);
        using (var stream = new RecoveryPcmStream(context, 0, muted.Frames))
        {
            using var output = new MemoryStream();
            stream.CopyTo(output, 7919);
            Assert.Equal(expected, output.ToArray());
            stream.Seek(2 * PcmFormat.BytesPerSecond - 20, SeekOrigin.Begin);
            var acrossBoundary = new byte[80];
            Assert.Equal(80, stream.Read(acrossBoundary));
            Assert.Equal(expected.AsSpan(2 * PcmFormat.BytesPerSecond - 20, 80).ToArray(), acrossBoundary);
            Assert.Equal(stream.Length, stream.Seek(0, SeekOrigin.End));
            Assert.Equal(0, stream.Read(acrossBoundary));
        }
        Assert.Equal(before, Hashes(folder.Root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellingTheFirstEditNeverPublishesPartialAudioOrBackupData(bool copy)
    {
        using var folder = new TestFolder();
        var source = EditedSessionTests.MakeSource(folder);
        var before = Hashes(folder.Root);
        using var cancellation = new CancellationTokenSource();
        var document = AppStorage.LoadTranscript(source);
        var plan = AudioEditPlan.Create(document, source.Frames, [TranscriptPresentation.Rows(document)[1]]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EditedSessionService().SaveAsync(folder.Root, source, plan,
            "Cancelled", "WAV", new Updates(update =>
            {
                if (update.Stage == SessionEditStage.CopyingAudio && update.Percent > 0)
                    cancellation.Cancel();
            }), cancellation.Token, createCopy: copy));
        Assert.Equal(before, Hashes(folder.Root));
        Assert.Single(AppStorage.LoadSessions(folder.Root).Sessions);
    }

    internal static Task<RecordingSession> Mute(TestFolder folder, RecordingSession source,
        Func<TranscriptRow, bool> predicate, bool copy)
    {
        var document = AppStorage.LoadTranscript(source);
        var plan = AudioEditPlan.Create(document, source.Frames, TranscriptPresentation.Rows(document).Where(predicate).ToArray());
        return new EditedSessionService().SaveAsync(folder.Root, source, plan, "Explicit copy", "WAV",
            new Updates(), CancellationToken.None, createCopy: copy);
    }

    private static KeyValuePair<string, string>[] Hashes(string folder) =>
        Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => KeyValuePair.Create(Path.GetRelativePath(folder, path),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();

    private sealed class Updates(Action<SessionEditProgress>? callback = null) : IProgress<SessionEditProgress>
    {
        public void Report(SessionEditProgress value) => callback?.Invoke(value);
    }
}
