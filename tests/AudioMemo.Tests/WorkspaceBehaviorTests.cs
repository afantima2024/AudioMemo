using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class WorkspaceBehaviorTests
{
    [Fact]
    public void DisplayGroupsFragmentsWithoutMixingSpeakersOrAddingChineseSpaces()
    {
        var document = new TranscriptDocument
        {
            Segments =
            [
                new(0, 1, "The project") { SpeakerId = "spkr1" },
                new(1, 3, "is ready.") { SpeakerId = "spkr1" },
                new(3, 4, "Next sentence.") { SpeakerId = "spkr1" },
                new(4, 5, "\u6211\u4eec") { SpeakerId = "spkr2" },
                new(5, 6, "\u660e\u5929\u89c1\u3002") { SpeakerId = "spkr2" },
                new(10, 12, "Another thought") { SpeakerId = "spkr2" }
            ]
        };
        var rows = TranscriptPresentation.Rows(document);
        Assert.Equal(4, rows.Count);
        Assert.Equal("The project is ready.", rows[0].Text);
        Assert.Equal("\u6211\u4eec\u660e\u5929\u89c1\u3002", rows[2].Text);
        Assert.Equal(0, rows[0].StartSeconds);
        Assert.Equal(3, rows[0].EndSeconds);
        Assert.DoesNotContain("00:00:", TranscriptPresentation.CopyText(rows));
        Assert.StartsWith("[00:00:00] spkr1: The project is ready.", TranscriptPresentation.CopyText(rows, true));
        Assert.Equal(6, document.Segments.Count);
    }

    [Fact]
    public void SpeakerLabelsFollowFirstOccurrenceAndAmbiguousSpeechStaysUnlabeled()
    {
        var document = new TranscriptDocument
        {
            Segments = [new(0, 2, "First"), new(2, 4, "Second"), new(4, 6, "Overlap")],
            Revision = "old"
        };
        SpeakerAssignment.Apply(document, [new(2, 4, 8), new(0, 2, 17), new(4, 6, 17), new(4, 6, 8)]);
        Assert.Equal("spkr1", document.Segments[0].SpeakerId);
        Assert.Equal("spkr2", document.Segments[1].SpeakerId);
        Assert.Null(document.Segments[2].SpeakerId);
        Assert.True(document.SpeakersIdentified);
        Assert.NotEqual("old", document.Revision);
        document.RenameSpeaker("spkr1", "Alex");
        Assert.Contains("Alex: First", document.ToText());
        Assert.Equal("Alex and spkr2, not [spkr99].",
            TranscriptPresentation.RenderSpeakerReferences("[spkr1] and [spkr2], not [spkr99].", document));
    }

    [Fact]
    public void FailedOrCancelledAssignmentDoesNotChangeTheTranscript()
    {
        var document = new TranscriptDocument { Segments = [new(0, 5, "Saved text")], Revision = "keep" };
        Assert.Throws<InvalidDataException>(() => SpeakerAssignment.Apply(document, [new(double.NaN, 5, 0)]));
        Assert.Throws<InvalidDataException>(() => SpeakerAssignment.Apply(document, []));
        Assert.Throws<InvalidOperationException>(() => SpeakerAssignment.Apply(document, [new(30, 40, 0)]));
        Assert.Throws<OperationCanceledException>(() =>
            SpeakerAssignment.Apply(document, [new(0, 5, 0)], new CancellationToken(true)));
        Assert.False(document.SpeakersIdentified);
        Assert.Equal("keep", document.Revision);
        Assert.Equal("Saved text", Assert.Single(document.Segments).Text);
    }

    [Fact]
    public void RenamingRefreshesSavedSpeakerReferencesButNotPersonalNotes()
    {
        using var folder = new TestFolder();
        var session = AppStorage.CreateSession(new() { SaveFolder = folder.Root }, "Synthetic names");
        var document = new TranscriptDocument
        {
            Segments = [new(0, 3, "First") { SpeakerId = "spkr1" }, new(3, 5, "Second") { SpeakerId = "spkr1" }],
            Revision = "stable"
        };
        AppStorage.SaveTranscript(session, document);
        AtomicFile.WriteJson(Path.Combine(session.Folder, "summary.json"),
            new SummaryDocument { Text = "[spkr1] owns the slides.", TranscriptRevision = "stable" });
        File.WriteAllText(session.NotesPath, "My [spkr1] note.");
        AppStorage.RenameSpeaker(session, "spkr1", "\u9673\u8001\u5e2b");
        var loaded = AppStorage.LoadTranscript(session);
        Assert.Equal("\u9648\u8001\u5e08", loaded.SpeakerName("spkr1"));
        Assert.All(TranscriptPresentation.Rows(loaded), row => Assert.Equal("\u9648\u8001\u5e08", row.Speaker));
        Assert.Equal("stable", loaded.Revision);
        Assert.Equal("\u9648\u8001\u5e08 owns the slides.", AppStorage.LoadSummaryText(session));
        Assert.Equal(AppStorage.LoadSummaryText(session), File.ReadAllText(session.SummaryPath));
        Assert.Contains("[spkr1]", File.ReadAllText(Path.Combine(session.Folder, "summary.json")));
        Assert.Equal("My [spkr1] note.", File.ReadAllText(session.NotesPath));
        Assert.Throws<ArgumentException>(() => loaded.RenameSpeaker("spkr99", "Unknown"));
        Assert.Throws<ArgumentException>(() => loaded.RenameSpeaker("spkr1", "Bad\nName"));
    }

    [Fact]
    public void SessionRenameAndDeletionValidationCannotTargetTheLibraryOrUnrelatedFolders()
    {
        using var folder = new TestFolder();
        var session = AppStorage.CreateSession(new() { SaveFolder = folder.Root }, "Before");
        var originalPath = session.Folder;
        SessionCommands.Rename(session, "After");
        Assert.Equal("After", AppStorage.LoadSessions(folder.Root).Sessions.Single().Title);
        Assert.Equal(originalPath, session.Folder);
        Assert.Throws<ArgumentException>(() => SessionCommands.Rename(session, "Bad\nTitle"));
        Assert.Equal(originalPath, SessionCommands.ValidateDeletion(folder.Root, session));
        session.Folder = folder.Root;
        Assert.Throws<IOException>(() => SessionCommands.ValidateDeletion(folder.Root, session));
        session.Folder = originalPath;
        session.Id = Guid.NewGuid().ToString("N");
        Assert.Throws<IOException>(() => SessionCommands.ValidateDeletion(folder.Root, session));
        Assert.True(File.Exists(session.MetadataPath));
    }

    [Fact]
    public void SpeakerMemoryLimitIsExplicitAndDoesNotAllocateHugeBuffers()
    {
        SpeakerService.CheckMemory(16_000 * 60, 8UL * 1024 * 1024 * 1024);
        Assert.Throws<InvalidOperationException>(() => SpeakerService.CheckMemory(16_000 * 60, 200UL * 1024 * 1024));
        Assert.Throws<InvalidOperationException>(() => SpeakerService.CheckMemory((long)Array.MaxLength + 1, ulong.MaxValue));
    }

    [Fact]
    public void NewSpeechChoicesValidateWithoutChangingAnExistingModelPreference()
    {
        var defaults = new AppSettings();
        Assert.True(defaults.FastLiveDrafts);
        Assert.Equal("small", defaults.SpeechModel);
        foreach (var acceleration in new[] { "auto", "cpu", "vulkan" })
            new AppSettings { SpeechModel = "large-v3-turbo-q5_0", SpeechAcceleration = acceleration }.Validate();
        Assert.Throws<InvalidDataException>(() => new AppSettings { SpeakerCount = 21 }.Validate());
        Assert.Throws<InvalidDataException>(() => new AppSettings { SpeechAcceleration = "made-up" }.Validate());
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"SpeechModel":"base"}""")!;
        Assert.Equal("base", restored.SpeechModel);
    }
}
