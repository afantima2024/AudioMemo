using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class HistoryExportTests
{
    [Theory]
    [InlineData("Meeting: A/B?", "Meeting_ A_B_")]
    [InlineData("CON", "_CON")]
    [InlineData("AUX.txt", "_AUX.txt")]
    [InlineData("COM1", "_COM1")]
    [InlineData("...", "recording")]
    [InlineData("  Meeting. ", "Meeting")]
    [InlineData("\u4f1a\u8bae", "\u4f1a\u8bae")]
    public void ExportNamesAreSafeWindowsFileNames(string title, string expected) =>
        Assert.Equal(expected, HistoryExportService.FileNameStem(title));

    [Fact]
    public async Task TranscriptExportsUseHistoryNamesAndNeverOverwriteFilesOrIncludeNotes()
    {
        using var folder = new TestFolder();
        var first = EditedSessionTests.MakeSource(folder);
        var second = EditedSessionTests.MakeSource(folder);
        SessionCommands.Rename(first, "Meeting: review");
        SessionCommands.Rename(second, first.Title);
        var destination = folder.File("exports");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "Meeting_ review.txt"), "Existing file.");
        var original = EditedSessionTests.Hashes(first.Folder);
        var updates = new Updates();
        Assert.Equal(2, await HistoryExportService.ExportAsync([first, second], destination, HistoryExportKind.Transcript,
            updates, CancellationToken.None));
        Assert.Equal("Existing file.", File.ReadAllText(Path.Combine(destination, "Meeting_ review.txt")));
        foreach (var name in new[] { "Meeting_ review (2).txt", "Meeting_ review (3).txt" })
        {
            var text = File.ReadAllText(Path.Combine(destination, name));
            Assert.Contains("Keep the first decision", text);
            Assert.Contains("Alex", text);
            Assert.Contains("00:00:00", text);
            Assert.DoesNotContain("PRIVATE NOTE", text);
        }
        Assert.Equal(original, EditedSessionTests.Hashes(first.Folder));
        Assert.Equal(2, updates.Values[^1].Completed);
        Assert.Equal(3, Directory.GetFiles(destination).Length);
    }

    [Fact]
    public async Task Mp3ExportsCopyFreshAudioOrEncodeMissingAudioWithHistoryNames()
    {
        using var folder = new TestFolder();
        var first = EditedSessionTests.MakeSource(folder);
        var second = EditedSessionTests.MakeSource(folder);
        SessionCommands.Rename(first, "Already MP3");
        SessionCommands.Rename(second, "Encode WAV");
        AudioExport.ToMp3(first.WavePath, first.Mp3Path, first.Frames, CancellationToken.None);
        File.Delete(second.Mp3Path);
        var original = EditedSessionTests.Hashes(first.Folder);
        var destination = folder.File("exports");
        Directory.CreateDirectory(destination);
        Assert.Equal(2, await HistoryExportService.ExportAsync([first, second], destination, HistoryExportKind.Mp3,
            new Updates(), CancellationToken.None));
        Assert.Equal(File.ReadAllBytes(first.Mp3Path), File.ReadAllBytes(Path.Combine(destination, "Already MP3.mp3")));
        using var reader = new MediaFoundationReader(Path.Combine(destination, "Encode WAV.mp3"));
        Assert.InRange(reader.TotalTime.TotalSeconds, 1.9, 2.2);
        Assert.False(File.Exists(second.Mp3Path));
        Assert.Equal(original, EditedSessionTests.Hashes(first.Folder));
    }

    [Fact]
    public async Task InvalidBatchIsRejectedBeforePublishingAndCancellationKeepsCompletedFiles()
    {
        using var folder = new TestFolder();
        var first = EditedSessionTests.MakeSource(folder);
        var second = EditedSessionTests.MakeSource(folder);
        var destination = folder.File("exports");
        Directory.CreateDirectory(destination);
        var text = File.ReadAllText(second.TranscriptPath);
        File.Delete(second.TranscriptPath);
        await Assert.ThrowsAsync<HistoryExportException>(() => HistoryExportService.ExportAsync([first, second], destination,
            HistoryExportKind.Transcript, new Updates(), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(destination));
        File.WriteAllText(second.TranscriptPath, text);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => HistoryExportService.ExportAsync([first, second], destination,
            HistoryExportKind.Transcript, new Updates(value => { if (value.Completed == 1) cancellation.Cancel(); }),
            cancellation.Token));
        Assert.Single(Directory.GetFiles(destination));
        Assert.DoesNotContain(Directory.GetFiles(destination), path => Path.GetFileName(path).StartsWith(".export-"));
    }

    [Fact]
    public async Task AnInvalidSafetyWaveCannotBeExportedThroughACachedMp3()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        using (var stream = new FileStream(session.WavePath, FileMode.Open, FileAccess.Write))
            DurableWaveFile.WriteHeader(stream, 0);
        File.SetLastWriteTimeUtc(session.Mp3Path, DateTime.UtcNow.AddMinutes(1));
        var destination = Directory.CreateDirectory(folder.File("exports")).FullName;
        await Assert.ThrowsAsync<HistoryExportException>(() => HistoryExportService.ExportAsync([session], destination,
            HistoryExportKind.Mp3, new Updates(), CancellationToken.None));
        Assert.Empty(Directory.GetFiles(destination));
    }

    [Fact]
    public async Task AStaleMp3IsRegeneratedAndAnInvalidFreshMp3IsNeverCopied()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var destination = Directory.CreateDirectory(folder.File("exports")).FullName;
        File.SetLastWriteTimeUtc(session.Mp3Path, File.GetLastWriteTimeUtc(session.WavePath).AddMinutes(-1));
        await HistoryExportService.ExportAsync([session], destination, HistoryExportKind.Mp3, new Updates(), CancellationToken.None);
        var exported = Assert.Single(Directory.GetFiles(destination));
        using (var reader = new MediaFoundationReader(exported))
            Assert.InRange(reader.TotalTime.TotalSeconds, 1.9, 2.2);
        File.SetLastWriteTimeUtc(session.Mp3Path, DateTime.UtcNow.AddMinutes(1));
        await Assert.ThrowsAsync<HistoryExportException>(() => HistoryExportService.ExportAsync([session], destination,
            HistoryExportKind.Mp3, new Updates(), CancellationToken.None));
        Assert.Equal(exported, Assert.Single(Directory.GetFiles(destination)));
    }

    [Fact]
    public void RenamePreservesFreshMetadataAndDoesNotChangeTheTitleAfterAFailedSave()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var fresh = System.Text.Json.JsonSerializer.Deserialize<RecordingSession>(AtomicFile.ReadText(session.MetadataPath))!;
        fresh.Folder = session.Folder;
        fresh.RequestedFormat = "MP3";
        fresh.Save();
        SessionCommands.Rename(session, "New history name");
        var saved = AppStorage.LoadSessions(folder.Root).Sessions.Single();
        Assert.Equal("MP3", saved.RequestedFormat);
        Assert.Equal("New history name", saved.Title);
        var attributes = File.GetAttributes(session.MetadataPath);
        try
        {
            File.SetAttributes(session.MetadataPath, attributes | FileAttributes.ReadOnly);
            var error = Record.Exception(() => SessionCommands.Rename(session, "Must not appear"));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            Assert.Equal("New history name", session.Title);
            Assert.Equal("New history name", AppStorage.LoadSessions(folder.Root).Sessions.Single().Title);
        }
        finally { File.SetAttributes(session.MetadataPath, attributes); }
    }

    [Fact]
    public void ExplorerSelectionUsesOneCommonParentAndOnlyTheRequestedFolders()
    {
        using var folder = new TestFolder();
        var first = Directory.CreateDirectory(folder.File("first")).FullName;
        var second = Directory.CreateDirectory(folder.File("second")).FullName;
        var third = Directory.CreateDirectory(folder.File("elsewhere\\third")).FullName;
        var selection = ExplorerSelection.Prepare([first, second, first]);
        Assert.Equal(folder.Root, selection.Parent);
        Assert.Equal(new[] { first, second }, selection.Folders);
        Assert.Throws<IOException>(() => ExplorerSelection.Prepare([first, third]));
        Assert.Throws<DirectoryNotFoundException>(() => ExplorerSelection.Prepare([first, folder.File("missing")]));
    }

    private sealed class Updates(Action<HistoryExportProgress>? action = null) : IProgress<HistoryExportProgress>
    {
        public List<HistoryExportProgress> Values { get; } = [];
        public void Report(HistoryExportProgress value) { Values.Add(value); action?.Invoke(value); }
    }
}
