using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class SpeakerColorConcurrencyTests
{
    [Fact]
    public void UnlabeledColorChangesOnlyRecordingLocalDisplayMetadata()
    {
        using var folder = new TestFolder();
        var session = UnlabeledSpeakerTests.MakeSession(folder);
        var original = AppStorage.LoadTranscript(session);
        var before = EditedSessionTests.Hashes(session.Folder).ToDictionary();
        AppStorage.SetSpeakerColor(session, null, "#a1b2c3");
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal("#A1B2C3", saved.UnlabeledSpeakerColor);
        Assert.Equal(saved.UnlabeledSpeakerColor, saved.SpeakerColor(null));
        Assert.Equal(original.Segments, saved.Segments);
        Assert.Equal(original.Revision, saved.Revision);
        Assert.Equal(original.SpeakerNames, saved.SpeakerNames);
        Assert.Equal(original.SpeakerColors, saved.SpeakerColors);
        Assert.True(original.Me!.Matches(saved.Me));
        Assert.Equal(original.ToText(), saved.ToText());
        Assert.Equal(SummaryInput.Fingerprint(original), SummaryInput.Fingerprint(saved));
        Assert.Equal(AudioEditPlan.ContentFingerprint(original), AudioEditPlan.ContentFingerprint(saved));
        Assert.NotEqual(AudioEditPlan.Fingerprint(original), AudioEditPlan.Fingerprint(saved));
        var after = EditedSessionTests.Hashes(session.Folder).ToDictionary();
        foreach (var name in before.Keys.Except(["transcript.json"]))
            Assert.Equal(before[name], after[name]);
        Assert.All(TranscriptPresentation.Rows(saved).Where(row => row.SpeakerId is null), row =>
        {
            Assert.Equal("", row.Speaker);
            Assert.Equal(UiText.Get("UnlabeledSpeakers"), row.DisplaySpeaker);
        });
        var copy = saved.Copy();
        copy.SetSpeakerColor(null, "#112233");
        Assert.Equal("#A1B2C3", saved.UnlabeledSpeakerColor);
        Assert.Null(original.UnlabeledSpeakerColor);
        AppStorage.SetSpeakerColor(session, null, null);
        Assert.Null(AppStorage.LoadTranscript(session).UnlabeledSpeakerColor);
        Assert.DoesNotContain("UnlabeledSpeakerColor", File.ReadAllText(session.TranscriptPath));
        Assert.Null(new TranscriptDocument().UnlabeledSpeakerColor);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#00112233")]
    [InlineData(" #112233")]
    public void InvalidUnlabeledColorsAreRejectedBeforeSavingAndOnLoad(string value)
    {
        using var folder = new TestFolder();
        var session = UnlabeledSpeakerTests.MakeSession(folder);
        var before = File.ReadAllBytes(session.TranscriptPath);
        Assert.Throws<InvalidDataException>(() => AppStorage.SetSpeakerColor(session, null, value));
        Assert.Equal(before, File.ReadAllBytes(session.TranscriptPath));
        var invalid = AppStorage.LoadTranscript(session);
        invalid.UnlabeledSpeakerColor = value;
        AtomicFile.WriteJson(session.TranscriptPath, invalid);
        Assert.Throws<InvalidDataException>(() => AppStorage.LoadTranscript(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AiPublicationKeepsLatestColorsAndNeutralResetsFromAnotherSessionInstance(bool reset)
    {
        using var folder = new TestFolder();
        var session = UnlabeledSpeakerTests.MakeSession(folder);
        AppStorage.SetSpeakerColor(session, null, "#AABBCC");
        var stale = AppStorage.LoadTranscript(session);
        var queued = stale.Copy();
        var other = JsonSerializer.Deserialize<RecordingSession>(File.ReadAllText(session.MetadataPath))!;
        other.Folder = session.Folder;
        AppStorage.SetSpeakerColor(other, "spkr1", reset ? null : "#102030");
        AppStorage.SetSpeakerColor(other, null, reset ? null : "#405060");
        AppStorage.SaveAiTranscript(session, stale);
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal(reset ? null : "#102030", saved.SpeakerColor("spkr1"));
        Assert.Equal(reset ? null : "#405060", saved.SpeakerColor(null));
        Assert.Equal("#ABCDEF", saved.SpeakerColor("spkr2"));
        Assert.Equal(saved.SpeakerColors, stale.SpeakerColors);
        AppStorage.RefreshTranscriptColors(session, queued);
        Assert.Equal(saved.SpeakerColors, queued.SpeakerColors);
        Assert.Equal(saved.UnlabeledSpeakerColor, queued.UnlabeledSpeakerColor);
    }

    [Fact]
    public async Task IndependentColorSavesCannotLoseEachOthersChoices()
    {
        using var folder = new TestFolder();
        var session = UnlabeledSpeakerTests.MakeSession(folder);
        await Task.WhenAll(
            Task.Run(() => AppStorage.SetSpeakerColor(session, "spkr1", "#112233")),
            Task.Run(() => AppStorage.SetSpeakerColor(session, "spkr2", "#445566")),
            Task.Run(() => AppStorage.SetSpeakerColor(session, null, "#778899")));
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal("#112233", saved.SpeakerColor("spkr1"));
        Assert.Equal("#445566", saved.SpeakerColor("spkr2"));
        Assert.Equal("#778899", saved.SpeakerColor(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorrectionPlansIgnoreColorChangesAndPublishWithTheLatestPalette(bool noTextChange)
    {
        using var folder = new TestFolder();
        var session = UnlabeledSpeakerTests.MakeSession(folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0);
        AppStorage.SetSpeakerColor(session, "spkr1", null);
        AppStorage.SetSpeakerColor(session, null, "#334455");
        var before = EditedSessionTests.Hashes(session.Folder);
        var result = TranscriptCorrectionService.Save(session, plan,
            noTextChange ? plan.Segments.Select(segment => segment.Text).ToArray() : ["Corrected words."],
            CancellationToken.None);
        Assert.Null(result.SpeakerColor("spkr1"));
        Assert.Equal("#334455", result.UnlabeledSpeakerColor);
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal(result.SpeakerColors, saved.SpeakerColors);
        Assert.Equal(result.UnlabeledSpeakerColor, saved.UnlabeledSpeakerColor);
        if (noTextChange)
            Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        else
            Assert.Equal("Corrected words.", saved.Segments[0].Text);
    }

    [Fact]
    public void ContentFingerprintStillDetectsNonAppearanceChanges()
    {
        var document = UnlabeledSpeakerTests.Document();
        var original = AudioEditPlan.ContentFingerprint(document);
        Action<TranscriptDocument>[] changes =
        [
            value => value.Segments[0] = value.Segments[0] with { Text = "Different words." },
            value => value.Segments[0] = value.Segments[0] with { SpeakerId = "spkr2" },
            value => value.Segments[0] = value.Segments[0] with { EndSeconds = 2.5 },
            value => value.SpeakerNames["spkr1"] = "New name",
            value => value.Revision = "different",
            value => value.Model = "different",
            value => value.Language = "different",
            value => value.ProcessedFrames--,
            value => value.Me = null,
            value => value.IsDraft = !value.IsDraft,
            value => value.SpeakersIdentified = !value.SpeakersIdentified
        ];
        foreach (var change in changes)
        {
            var changed = document.Copy();
            change(changed);
            Assert.NotEqual(original, AudioEditPlan.ContentFingerprint(changed));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColoringDuringUnlabeledMatchingDoesNotInvalidateOrOverwriteTheResult(bool reset)
    {
        using var folder = new TestFolder();
        var session = UnlabeledSpeakerTests.MakeSession(folder);
        AppStorage.SetSpeakerColor(session, null, "#AABBCC");
        var original = AppStorage.LoadTranscript(session);
        var fake = new UnlabeledSpeakerTests.FakeSpeakers
        {
            BeforeReturn = () =>
            {
                AppStorage.SetSpeakerColor(session, "spkr1", reset ? null : "#112233");
                AppStorage.SetSpeakerColor(session, null, reset ? null : "#445566");
            }
        };
        var updates = new Updates();
        await new AiPipeline(speakerService: fake).RecheckUnlabeledSpeakersAsync(session,
            new AppSettings { SaveFolder = folder.Root }, updates, CancellationToken.None);
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal(1, fake.MatchCalls);
        Assert.Equal("spkr1", saved.Segments[2].SpeakerId);
        Assert.Equal(original.Segments.Skip(3), saved.Segments.Skip(3));
        Assert.Equal(reset ? null : "#112233", saved.SpeakerColor("spkr1"));
        Assert.Equal(reset ? null : "#445566", saved.UnlabeledSpeakerColor);
        Assert.Equal(saved.SpeakerColors, updates.Values[^1].Document!.SpeakerColors);
        Assert.Equal(saved.UnlabeledSpeakerColor, updates.Values[^1].Document!.UnlabeledSpeakerColor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullReanalysisClearsOldIdentityColorsButKeepsUnlabeledPreference(bool reset)
    {
        using var folder = new TestFolder();
        var session = UnlabeledSpeakerTests.MakeSession(folder);
        var displayed = AppStorage.LoadTranscript(session);
        AppStorage.SetSpeakerColor(session, null, "#AABBCC");
        var fake = new ReplacingSpeakers(() =>
        {
            AppStorage.SetSpeakerColor(session, "spkr1", "#112233");
            AppStorage.SetSpeakerColor(session, null, reset ? null : "#445566");
        });
        await new AiPipeline(speakerService: fake).IdentifySpeakersAsync(session,
            new AppSettings { SaveFolder = folder.Root }, new Updates(), CancellationToken.None);
        var saved = AppStorage.LoadTranscript(session);
        Assert.Empty(saved.SpeakerColors);
        Assert.Empty(saved.SpeakerNames);
        Assert.NotEqual(displayed.Revision, saved.Revision);
        Assert.Equal(reset ? null : "#445566", saved.UnlabeledSpeakerColor);
        var before = File.ReadAllBytes(session.TranscriptPath);
        var error = Assert.Throws<InvalidOperationException>(() =>
            AppStorage.SetSpeakerColor(session, "spkr1", "#667788", displayed.Revision));
        Assert.Equal(UiText.Get("SpeakerColorTargetChanged"), error.Message);
        Assert.Equal(before, File.ReadAllBytes(session.TranscriptPath));
        AppStorage.SetSpeakerColor(session, "spkr1", "#778899", saved.Revision);
        AppStorage.RefreshTranscriptColors(session, displayed);
        Assert.Equal("#123456", displayed.SpeakerColor("spkr1"));
        Assert.Equal(saved.UnlabeledSpeakerColor, displayed.UnlabeledSpeakerColor);
        AppStorage.SetSpeakerColor(session, null, "#CCDDEE", displayed.Revision);
        Assert.Equal("#CCDDEE", AppStorage.LoadTranscript(session).UnlabeledSpeakerColor);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RecoveryKeepsCurrentUnlabeledColorEvenWhenRebuildingTheTranscript(bool reset, bool rebuild)
    {
        var original = AudioEditPlanTests.Document(4,
            new(0, 1, "First.") { SpeakerId = "spkr1" },
            new(1, 2, "Unknown."),
            new(2, 3, "Third.") { SpeakerId = "spkr1" },
            new(3, 4, "Other unknown."));
        original.SetSpeakerColor(null, "#AABBCC");
        var range = new AudioFrameRange(PcmFormat.SampleRate, 2L * PcmFormat.SampleRate);
        var current = AudioEditPlan.ForMutedRanges(original, original.ProcessedFrames, [range]).CreateTranscript();
        Assert.Equal("#AABBCC", current.UnlabeledSpeakerColor);
        current.SetSpeakerColor(null, reset ? null : "#112233");
        if (rebuild)
            current.Model = "different-model";
        var plan = new AudioRestorePlan(current, original, current.ProcessedFrames, [range], [range]);
        Assert.Equal(rebuild, plan.RebuildsTranscript);
        var restored = plan.CreateTranscript();
        Assert.Equal(reset ? null : "#112233", restored.UnlabeledSpeakerColor);
        Assert.Equal(original.Segments, restored.Segments);
        SpeakerAssignment.Apply(restored, [new(0, 4, 0)]);
        Assert.Equal(reset ? null : "#112233", restored.UnlabeledSpeakerColor);
        Assert.DoesNotContain(restored.Segments, segment => segment.SpeakerId is null);
        restored.Segments.Add(new(4, 5, "A future unlabeled row."));
        Assert.Equal(reset ? null : "#112233", restored.SpeakerColor(null));
    }

    private sealed class ReplacingSpeakers(Action beforeResult) : ISpeakerService
    {
        public Task IdentifyAsync(RecordingSession session, TranscriptDocument transcript, int count, CancellationToken token,
            Action<AiWorkProgress>? progress = null)
        {
            beforeResult();
            SpeakerAssignment.Apply(transcript, [new(0, (double)session.Frames / PcmFormat.SampleRate, 7)]);
            return Task.CompletedTask;
        }
    }

    private sealed class Updates : IProgress<AiUpdate>
    {
        public List<AiUpdate> Values { get; } = [];
        public void Report(AiUpdate value) => Values.Add(value);
    }
}
