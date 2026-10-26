using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class TranscriptSpeakerCorrectionTests
{
    private static TranscriptDocument Document()
    {
        var document = AudioEditPlanTests.Document(8,
            new(0, 1, "One") { SpeakerId = "spkr1", IsUserEdited = true },
            new(1, 2, "Same row.") { SpeakerId = "spkr1" },
            new(2, 3, "Two.") { SpeakerId = "spkr2" },
            new(3, 4, "One again.") { SpeakerId = "spkr1" },
            new(4, 5, "Three.") { SpeakerId = "spkr3" },
            new(5, 6, "Unlabeled."),
            new(6, 7, "Two again.") { SpeakerId = "spkr2" },
            new(7, 8, "Unlabeled again."));
        document.SpeakersIdentified = true;
        document.SpeakerNames = new() { ["spkr1"] = "Alex split", ["spkr2"] = "Alex", ["spkr3"] = "Chris" };
        document.SpeakerColors = new() { ["spkr1"] = "#123456", ["spkr2"] = "#ABCDEF", ["spkr3"] = "#EEEEEE" };
        return document;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedRowsOrWholeSpeakerChangeOnlyAssignmentsAndPruneUnusedMetadata(bool wholeSpeaker)
    {
        var original = Document();
        var before = JsonSerializer.Serialize(original);
        var rows = TranscriptPresentation.Rows(original);
        Assert.Equal(0, rows[0].StartSeconds);
        Assert.Equal(2, rows[0].EndSeconds);
        var selection = TranscriptCorrectionPlan.Create(original, original.ProcessedFrames, [rows[0]]);
        var corrected = TranscriptSpeakerCorrection.Apply(original, selection, "spkr2", wholeSpeaker);
        Assert.NotEqual(original.Revision, corrected.Revision);
        Assert.Equal(original.ProcessedFrames, corrected.ProcessedFrames);
        Assert.True(corrected.SpeakersIdentified);
        var changed = wholeSpeaker ? new[] { 0, 1, 3 } : [0, 1];
        for (var index = 0; index < original.Segments.Count; index++)
            Assert.Equal(original.Segments[index] with { SpeakerId = changed.Contains(index) ? "spkr2" : original.Segments[index].SpeakerId },
                corrected.Segments[index]);
        Assert.Equal("Alex", corrected.SpeakerNames["spkr2"]);
        Assert.Equal("#ABCDEF", corrected.SpeakerColors["spkr2"]);
        Assert.Equal(!wholeSpeaker, corrected.SpeakerNames.ContainsKey("spkr1"));
        Assert.Equal(!wholeSpeaker, corrected.SpeakerColors.ContainsKey("spkr1"));
        Assert.Equal(before, JsonSerializer.Serialize(original));
    }

    [Fact]
    public void MixedSelectionsAndUnlabeledMergingDoNotAffectUnselectedSpeakers()
    {
        var original = Document();
        var rows = TranscriptPresentation.Rows(original);
        var selection = TranscriptCorrectionPlan.Create(original, original.ProcessedFrames, [rows[2], rows[3]]);
        var corrected = TranscriptSpeakerCorrection.Apply(original, selection, "spkr2", false);
        Assert.Equal("spkr2", corrected.Segments[3].SpeakerId);
        Assert.Equal("spkr2", corrected.Segments[4].SpeakerId);
        Assert.Equal("spkr1", corrected.Segments[0].SpeakerId);
        selection = TranscriptCorrectionPlan.Create(original, original.ProcessedFrames, [rows.Single(row => row.StartSeconds == 5)]);
        corrected = TranscriptSpeakerCorrection.Apply(original, selection, "spkr2", true);
        Assert.Equal("spkr2", corrected.Segments[5].SpeakerId);
        Assert.Equal("spkr2", corrected.Segments[7].SpeakerId);
        Assert.Equal(original.Segments[0], corrected.Segments[0]);
    }

    [Fact]
    public void NoOpInvalidTargetAndStaleSelectionAreExplicitFailures()
    {
        var original = Document();
        var selection = TranscriptCorrectionPlan.Create(original, original.ProcessedFrames, [TranscriptPresentation.Rows(original)[0]]);
        Assert.Throws<InvalidOperationException>(() => TranscriptSpeakerCorrection.Apply(original, selection, "spkr1", false));
        Assert.Throws<InvalidOperationException>(() => TranscriptSpeakerCorrection.Apply(original, selection, "missing", true));
        var changed = original.Copy();
        changed.Segments[0] = changed.Segments[0] with { Text = "Concurrent change." };
        Assert.Throws<InvalidOperationException>(() => TranscriptSpeakerCorrection.Apply(changed, selection, "spkr2", false));
    }

    [Fact]
    public void SavingSpeakerCorrectionMarksSummaryOutdatedWithoutChangingAudioOrNotes()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var original = AppStorage.LoadTranscript(session);
        var before = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        var selection = TranscriptCorrectionPlan.Create(original, session.Frames, [TranscriptPresentation.Rows(original)[1]]);
        var corrected = TranscriptCorrectionService.SaveSpeakerAssignment(session, selection, "spkr1", true, CancellationToken.None);
        Assert.True(AppStorage.SummaryNeedsRefresh(session, corrected));
        Assert.Equal(corrected.Revision, AppStorage.LoadTranscript(session).Revision);
        var after = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var path in new[] { "audio.wav", "audio.mp3", "notes.md", "summary.md", "summary.json" })
            Assert.Equal(before[path], after[path]);
    }

    [Fact]
    public void FailedCancelledOrStaleSpeakerSaveDoesNotPartiallyCommitExports()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var original = AppStorage.LoadTranscript(session);
        var selection = TranscriptCorrectionPlan.Create(original, session.Frames, [TranscriptPresentation.Rows(original)[1]]);
        var before = EditedSessionTests.Hashes(session.Folder);
        Assert.Throws<OperationCanceledException>(() =>
            TranscriptCorrectionService.SaveSpeakerAssignment(session, selection, "spkr1", true, new CancellationToken(true)));
        using (var blocked = new FileStream(session.TranscriptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() =>
                TranscriptCorrectionService.SaveSpeakerAssignment(session, selection, "spkr1", true, CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected write failure: {error}");
        }
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        TranscriptCorrectionService.SaveSpeakerAssignment(session, selection, "spkr1", true, CancellationToken.None);
        var after = EditedSessionTests.Hashes(session.Folder);
        Assert.Throws<InvalidOperationException>(() =>
            TranscriptCorrectionService.SaveSpeakerAssignment(session, selection, "spkr1", true, CancellationToken.None));
        Assert.Equal(after, EditedSessionTests.Hashes(session.Folder));
    }
}
