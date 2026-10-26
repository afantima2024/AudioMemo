using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class SelfSpeakerTests
{
    [Fact]
    public void TagPersistsAndCopiesWithoutRenamingSpeakersOrChangingGeneralSummaryInput()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var transcript = AppStorage.LoadTranscript(session);
        var summaryInput = SummaryInput.Fingerprint(transcript);
        var audio = File.ReadAllBytes(session.WavePath);
        var notes = File.ReadAllBytes(session.NotesPath);
        transcript.Me = SelfSpeaker.Create("spkr1", " Alex \r\nAl\nAlex\n\u9ad4\u9a57");
        AppStorage.SaveTranscript(session, transcript);
        var loaded = AppStorage.LoadTranscript(session);
        Assert.True(transcript.Me.Matches(loaded.Me));
        Assert.Equal(["Alex", "Al", "\u4f53\u9a8c"], loaded.Me!.Names);
        Assert.Equal("Alex", loaded.SpeakerName("spkr1"));
        Assert.Equal(summaryInput, SummaryInput.Fingerprint(loaded));
        var row = TranscriptPresentation.Rows(loaded)[0];
        Assert.Equal("Alex", row.Speaker);
        Assert.Equal($"Alex ({UiText.Get("MeTag")})", row.DisplaySpeaker);
        Assert.DoesNotContain($"({UiText.Get("MeTag")})", loaded.ToText());
        var copy = loaded.Copy();
        copy.Me!.Names.Add("Separate alias");
        Assert.DoesNotContain("Separate alias", loaded.Me.Names);
        Assert.Equal(loaded.Me.TagId, copy.Me.TagId);
        Assert.Equal(audio, File.ReadAllBytes(session.WavePath));
        Assert.Equal(notes, File.ReadAllBytes(session.NotesPath));
    }

    [Theory]
    [InlineData("", "Alex")]
    [InlineData("spkr1", "A\tB")]
    [InlineData("spkr1", "A\0B")]
    public void InvalidTagDataFailsExplicitly(string id, string names) =>
        Assert.Throws<InvalidDataException>(() => SelfSpeaker.Create(id, names));

    [Fact]
    public void AliasesAreOptionalAndBoundedAndStoredTagsNeedAStableIdentifier()
    {
        Assert.Empty(SelfSpeaker.Create("spkr1", "").Names);
        Assert.Throws<InvalidDataException>(() => SelfSpeaker.Create("spkr1", new string('x', 81)));
        Assert.Throws<InvalidDataException>(() => SelfSpeaker.Create("spkr1",
            string.Join("\n", Enumerable.Range(0, 11).Select(index => "Name " + index))));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SelfSpeaker>("""{"SpeakerId":"spkr1","Names":[]}"""));
        Assert.Throws<InvalidDataException>(() => (SelfSpeaker.Create("spkr1", "") with { TagId = "bad" }).Validate());
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var transcript = AppStorage.LoadTranscript(session);
        transcript.Me = SelfSpeaker.Create("absent-speaker", "");
        AppStorage.SaveTranscript(session, transcript);
        Assert.Throws<InvalidDataException>(() => AppStorage.LoadTranscript(session));
    }

    [Fact]
    public void PartialCorrectionKeepsTheTagAndAWholeSpeakerMergeRemapsIt()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var transcript = AppStorage.LoadTranscript(session);
        transcript.Me = SelfSpeaker.Create("spkr1", "Alex");
        var selection = TranscriptCorrectionPlan.Create(transcript, session.Frames, [TranscriptPresentation.Rows(transcript)[0]]);
        var partial = TranscriptSpeakerCorrection.Apply(transcript, selection, "spkr2", false);
        Assert.True(transcript.Me.Matches(partial.Me));
        var merged = TranscriptSpeakerCorrection.Apply(transcript, selection, "spkr2", true);
        Assert.Equal("spkr2", merged.Me!.SpeakerId);
        Assert.NotEqual(transcript.Me.TagId, merged.Me.TagId);
        Assert.Equal(transcript.Me.Names, merged.Me.Names);
        Assert.DoesNotContain(merged.Segments, segment => segment.SpeakerId == "spkr1");
    }

    [Fact]
    public void ReanalysisOnlyClearsTheTagAfterAConfidentSuccessfulAssignment()
    {
        var transcript = Document();
        var before = AudioEditPlan.Fingerprint(transcript);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SpeakerAssignment.Apply(transcript, [new(0, 2, 0)], cancel.Token));
        Assert.Equal(before, AudioEditPlan.Fingerprint(transcript));
        Assert.Throws<InvalidOperationException>(() => SpeakerAssignment.Apply(transcript, [new(5, 6, 0)]));
        Assert.Equal(before, AudioEditPlan.Fingerprint(transcript));
        SpeakerAssignment.Apply(transcript, [new(0, 1, 0), new(1, 2, 1)]);
        Assert.Null(transcript.Me);
    }

    [Fact]
    public void ContinuationKeepsOnlyAnchoredIdentityAndRejectsAStaleTagCheckpoint()
    {
        var source = Document();
        var refined = TranscriptContinuation.CreateRefinement(source, PcmFormat.SampleRate, "small", "en");
        Assert.True(source.Me!.Matches(refined.Me));
        Assert.True(TranscriptContinuation.MatchesPrefix(refined, source, PcmFormat.SampleRate));
        source.Me = SelfSpeaker.Create("spkr1", "Changed alias");
        Assert.False(TranscriptContinuation.MatchesPrefix(refined, source, PcmFormat.SampleRate));
        Assert.Null(TranscriptContinuation.CreateRefinement(source, 0, "small", "en").Me);
        source.Me = SelfSpeaker.Create("spkr2", "");
        refined = TranscriptContinuation.CreateRefinement(source, PcmFormat.SampleRate, "small", "en");
        Assert.Null(refined.Me);
        Assert.True(TranscriptContinuation.MatchesPrefix(refined, source, PcmFormat.SampleRate));
        source.Me = SelfSpeaker.Create("spkr1", "Alex");
        var analyzed = source.Copy();
        SpeakerAssignment.Apply(analyzed, [new(0, 1, 7), new(1, 2, 4)]);
        TranscriptContinuation.ApplySpeakers(source, analyzed, PcmFormat.SampleRate);
        Assert.Equal("spkr1", source.Me!.SpeakerId);
        Assert.Equal("spkr1", source.Segments[0].SpeakerId);
    }

    [Fact]
    public void MuteAndRestoreDoNotResurrectClearedOrRecycledIdentities()
    {
        var original = Document();
        AudioFrameRange[] muted = [new(PcmFormat.SampleRate, 2L * PcmFormat.SampleRate)];
        var retained = AudioEditPlan.ForMutedRanges(original, original.ProcessedFrames, muted).CreateTranscript();
        Assert.True(original.Me!.Matches(retained.Me));
        var restored = new AudioRestorePlan(retained, original, original.ProcessedFrames, muted, muted).CreateTranscript();
        Assert.True(original.Me.Matches(restored.Me));
        retained.Me = null;
        Assert.Null(new AudioRestorePlan(retained, original, original.ProcessedFrames, muted, muted).CreateTranscript().Me);
        retained.Me = SelfSpeaker.Create("spkr2", "");
        retained.Segments[0] = retained.Segments[0] with { SpeakerId = "spkr2" };
        var rebuilt = new AudioRestorePlan(retained, original, original.ProcessedFrames, muted, muted);
        Assert.True(rebuilt.RebuildsTranscript);
        Assert.Null(rebuilt.CreateTranscript().Me);
        original.Me = SelfSpeaker.Create("spkr2", "");
        Assert.Null(AudioEditPlan.ForMutedRanges(original, original.ProcessedFrames, muted).CreateTranscript().Me);
    }

    private static TranscriptDocument Document()
    {
        var document = AudioEditPlanTests.Document(2,
            new(0, 1, "My contribution.") { SpeakerId = "spkr1" },
            new(1, 2, "Alex will check this.") { SpeakerId = "spkr2" });
        document.Me = SelfSpeaker.Create("spkr1", "Alex");
        return document;
    }
}
