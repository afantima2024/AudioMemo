using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class AppearanceSettingsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RainbowRecordingLightIsOptInAndPersistsWithoutChangingOtherPreferences(bool rainbow)
    {
        using var folder = new TestFolder();
        File.WriteAllText(folder.File("settings.json"), """
            {"Theme":"dark","SpeechModel":"small","Format":"MP3","IdentifySpeakersOnStop":false,
             "Appearance":{"ButtonStyle":"office","Light":{"Selection":"#123456"}}}
            """);
        var storage = new AppStorage(folder.Root);
        var settings = storage.LoadSettings();
        Assert.False(settings.Appearance.RainbowRecordingIndicator);
        settings.Appearance.RainbowRecordingIndicator = rainbow;
        storage.SaveSettings(settings);
        var reopened = storage.LoadSettings();
        Assert.Equal(rainbow, reopened.Appearance.RainbowRecordingIndicator);
        Assert.Equal(ButtonStyles.Office, reopened.Appearance.ButtonStyle);
        Assert.Equal("#123456", reopened.Appearance.Light.Selection);
        Assert.Equal(AppThemes.Dark, reopened.Theme);
        Assert.Equal("small", reopened.SpeechModel);
        Assert.Equal("MP3", reopened.Format);
        Assert.False(reopened.IdentifySpeakersOnStop);
        Assert.False(reopened.CloudConsent);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WorkspacePanelPreferencesRoundTripWithoutChangingAiSettings(bool mainPoints, bool notes)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Theme":"dark","IdentifySpeakersOnStop":false}""")!;
        Assert.True(settings.MainPointsVisible);
        Assert.True(settings.MyNotesVisible);
        settings.MainPointsVisible = mainPoints;
        settings.MyNotesVisible = notes;
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        copy.Validate();
        Assert.Equal(mainPoints, copy.MainPointsVisible);
        Assert.Equal(notes, copy.MyNotesVisible);
        Assert.False(copy.IdentifySpeakersOnStop);
        Assert.True(copy.TranscribeOnStop);
        Assert.False(copy.SummarizeOnStop);
        Assert.Equal(AppThemes.Dark, copy.Theme);
    }

    [Fact]
    public void ComparisonPalettesUseThemeAppropriateDefaultsForExistingSettingsAndRoundTripIndependently()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"Appearance":{"Light":{"Selection":"#123456","Match":"#ABCDEF","ActiveMatch":"#234567"},
                "Dark":{"Selection":"#654321","Match":"#FEDCBA","ActiveMatch":"#765432"}}}
            """)!;
        settings.Validate();
        Assert.Equal("#FCE4EC", settings.Appearance.LightComparison.Different);
        Assert.Equal("#522D3F", settings.Appearance.DarkComparison.Different);
        Assert.Equal("#244838", settings.Appearance.DarkComparison.Matching);
        settings.Appearance.LightComparison.Matching = "#ABCDEF";
        settings.Appearance.DarkComparison.Unclear = "#123456";
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        copy.Validate();
        Assert.Equal("#ABCDEF", copy.Appearance.LightComparison.Matching);
        Assert.Equal("#123456", copy.Appearance.DarkComparison.Unclear);
        Assert.Equal("#123456", copy.Appearance.Light.Selection);
        Assert.Equal("#654321", copy.Appearance.Dark.Selection);
        Assert.NotSame(settings.Appearance.LightComparison, copy.Appearance.LightComparison);
        copy.Appearance.DarkComparison.Unclear = "invalid";
        Assert.Throws<InvalidDataException>(copy.Validate);
        copy.Appearance.DarkComparison = null!;
        Assert.Throws<InvalidDataException>(copy.Validate);
    }

    [Theory]
    [InlineData("Same text.", null, TranscriptComparisonState.NotCompared)]
    [InlineData("Same text.", "Same text.", TranscriptComparisonState.Matching)]
    [InlineData("Same text.", "Same text!", TranscriptComparisonState.Different)]
    [InlineData("Same text.", "same text.", TranscriptComparisonState.Different)]
    [InlineData("Original.", "", TranscriptComparisonState.Unclear)]
    [InlineData("Original.", " \r\n", TranscriptComparisonState.Unclear)]
    [InlineData("", "Online.", TranscriptComparisonState.Unclear)]
    [InlineData("", "", TranscriptComparisonState.Unclear)]
    public void OnlineComparisonStatesIgnoreIndependentCorrectionDraftsAndMissingRecognition(
        string local, string? online, TranscriptComparisonState expected)
    {
        var row = new TranscriptEditWindow.CorrectionRow("0:00", "spkr1", local)
        {
            OnlineText = online,
            ComparingOnline = true
        };
        row.SetCorrection("An independent unsaved manual edit.", unrecognized: true);
        Assert.Equal(expected, row.ComparisonState);
        Assert.Equal(online is not null && expected != TranscriptComparisonState.Matching, row.OnlineDifferent);
        Assert.Equal(local, row.LocalText);
        row.ComparingOnline = false;
        Assert.Equal(TranscriptComparisonState.Unclear, row.ComparisonState);
        Assert.Equal(online is not null && expected != TranscriptComparisonState.Matching, row.OnlineDifferent);
    }

    [Theory]
    [InlineData("Saved text.", "Saved text.", false, TranscriptComparisonState.Matching)]
    [InlineData("Saved text.", "Saved text!", false, TranscriptComparisonState.Different)]
    [InlineData("Saved text.", "saved text.", false, TranscriptComparisonState.Different)]
    [InlineData("Saved text.", "", false, TranscriptComparisonState.Unclear)]
    [InlineData("Saved text.", " \r\n", false, TranscriptComparisonState.Unclear)]
    [InlineData("", "Recognized text.", false, TranscriptComparisonState.Unclear)]
    [InlineData("Saved text.", "Saved text.", true, TranscriptComparisonState.Unclear)]
    [InlineData("Saved text.", "Retained manual draft.", true, TranscriptComparisonState.Unclear)]
    public void CorrectedResultsCompareAgainstTheSavedBaseline(
        string saved, string corrected, bool unrecognized, TranscriptComparisonState expected)
    {
        var row = new TranscriptEditWindow.CorrectionRow("0:00", "spkr1", saved) { OnlineText = saved };
        Assert.Equal(TranscriptComparisonState.NotCompared, row.ComparisonState);
        row.SetCorrection(corrected, unrecognized);
        Assert.Equal(expected, row.ComparisonState);
        Assert.True(row.HasCorrection);
        Assert.Equal(saved, row.OriginalText);
        Assert.Equal(saved, row.LocalText);
        Assert.Equal(corrected, row.Text);
    }

    [Fact]
    public void CorrectionRowsNotifyLiveEditsAndRestoreComparisonStateWithoutChangingSavedText()
    {
        var row = new TranscriptEditWindow.CorrectionRow("0:00", "spkr1", "Saved text.");
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        row.Text = row.LocalText;
        Assert.Empty(changed);
        Assert.False(row.HasCorrection);
        Assert.Equal(TranscriptComparisonState.NotCompared, row.ComparisonState);

        row.SetCorrection(row.LocalText);
        Assert.Equal(TranscriptComparisonState.Matching, row.ComparisonState);
        Assert.Equal([nameof(row.HasCorrection), nameof(row.ComparisonState)], changed);
        changed.Clear();
        row.Text = "Manual correction.";
        Assert.Equal(TranscriptComparisonState.Different, row.ComparisonState);
        Assert.Equal([nameof(row.Text), nameof(row.ComparisonState)], changed);
        changed.Clear();
        row.OnlineText = row.LocalText;
        Assert.False(row.OnlineDifferent);
        Assert.Equal(TranscriptComparisonState.Different, row.ComparisonState);
        Assert.Contains(nameof(row.OnlineDifferent), changed);
        changed.Clear();
        row.ComparingOnline = true;
        Assert.Equal(TranscriptComparisonState.Matching, row.ComparisonState);
        Assert.Equal([nameof(row.ComparingOnline), nameof(row.ComparisonState)], changed);
        row.ComparingOnline = false;
        row.SetCorrection(row.Text, unrecognized: true);
        Assert.Equal(TranscriptComparisonState.Unclear, row.ComparisonState);
        changed.Clear();
        row.Text = "Reviewed correction.";
        Assert.False(row.Unrecognized);
        Assert.Equal(TranscriptComparisonState.Different, row.ComparisonState);
        Assert.Contains(nameof(row.Unrecognized), changed);
        row.Text = row.LocalText;
        Assert.Equal(TranscriptComparisonState.Matching, row.ComparisonState);
        row.SetCorrection(row.LocalText, hasCorrection: false);
        Assert.False(row.HasCorrection);
        Assert.Equal(TranscriptComparisonState.NotCompared, row.ComparisonState);
        Assert.DoesNotContain(nameof(row.LocalText), changed);
        Assert.DoesNotContain(nameof(row.OriginalText), changed);
        Assert.Equal("Saved text.", row.LocalText);
        Assert.Equal("Saved text.", row.OriginalText);
        Assert.Equal("0:00", row.Time);
        Assert.Equal("spkr1", row.Speaker);
    }

    [Theory]
    [InlineData(ButtonStyles.Windows)]
    [InlineData(ButtonStyles.Office)]
    [InlineData(ButtonStyles.Shadow)]
    [InlineData(ButtonStyles.Outline)]
    public void AppearanceAndEdgePositionRoundTripWhileLegacySettingsKeepTheirDefaults(string style)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Theme":"dark","SpeechModel":"small"}""")!;
        Assert.Equal(ButtonStyles.Windows, settings.Appearance.ButtonStyle);
        Assert.Equal("#294B63", settings.Appearance.Dark.Selection);
        Assert.Equal(0, settings.HistoryTabPosition);
        Assert.Equal(1, settings.TranscriptNavigationX);
        Assert.Equal(1, settings.TranscriptNavigationY);
        settings.Appearance.ButtonStyle = style;
        settings.Appearance.Light.Selection = "#123ABC";
        settings.Appearance.Dark.Match = "#DA62BD";
        settings.HistoryTabPosition = 0.73;
        settings.TranscriptNavigationX = 0.25;
        settings.TranscriptNavigationY = 0.62;
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        copy.Validate();
        Assert.Equal(style, copy.Appearance.ButtonStyle);
        Assert.Equal("#123ABC", copy.Appearance.Light.Selection);
        Assert.Equal("#DA62BD", copy.Appearance.Dark.Match);
        Assert.Equal(0.73, copy.HistoryTabPosition);
        Assert.Equal(0.25, copy.TranscriptNavigationX);
        Assert.Equal(0.62, copy.TranscriptNavigationY);
        Assert.Equal(AppThemes.Dark, copy.Theme);
        Assert.Equal("small", copy.SpeechModel);
        Assert.NotSame(settings.Appearance, copy.Appearance);
        Assert.Equal(4, ButtonStyles.Options(new UiText("en")).Select(option => option.Code).Distinct().Count());
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void InvalidTranscriptNavigationPositionsAreRejected(double position)
    {
        Assert.Throws<InvalidDataException>(() => new AppSettings { TranscriptNavigationX = position }.Validate());
        Assert.Throws<InvalidDataException>(() => new AppSettings { TranscriptNavigationY = position }.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#123")]
    [InlineData("#FF123456")]
    [InlineData("#GGHHJJ")]
    [InlineData("red")]
    [InlineData(" #123456")]
    public void InvalidOrTransparentColorsAreRejectedExplicitly(string? color)
    {
        Assert.Throws<InvalidDataException>(() => RgbColor.Parse(color));
    }

    [Fact]
    public void ColorContrastRemainsReadableAcrossLightDarkAndCustomSpeakerBackgrounds()
    {
        for (var red = 0; red <= 255; red += 17)
            for (var green = 0; green <= 255; green += 17)
                for (var blue = 0; blue <= 255; blue += 17)
                {
                    var color = new RgbColor((byte)red, (byte)green, (byte)blue);
                    Assert.True(color.Contrast(color.ReadableText) >= 4.5);
                    Assert.True(color.ForText(new(255, 255, 255)).Contrast(new(255, 255, 255)) >= 4.5);
                    Assert.True(color.ForText(new(0, 0, 0)).Contrast(new(0, 0, 0)) >= 4.5);
                    Assert.Equal(color, RgbColor.Parse(color.ToString()));
                }
        Assert.Equal("#123ABC", RgbColor.Parse("#123abc").ToString());
        Assert.Throws<InvalidDataException>(() => new AppSettings { HistoryTabPosition = double.NaN }.Validate());
        Assert.Throws<InvalidDataException>(() => new AppSettings { HistoryTabPosition = 1.01 }.Validate());
        var settings = new AppSettings();
        settings.Appearance.ButtonStyle = "unknown";
        Assert.Throws<InvalidDataException>(settings.Validate);
        settings.Appearance.ButtonStyle = ButtonStyles.Windows;
        settings.Appearance.Dark.Selection = "invalid";
        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void SpeakerColorChangesOnlyDisplayMetadataAndNotAudioTextNotesOrSummaryState()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var original = AppStorage.LoadTranscript(session);
        var hashes = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        AppStorage.SetSpeakerColor(session, "spkr1", "#123abc");
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal("#123ABC", saved.SpeakerColors["spkr1"]);
        Assert.Equal(original.Revision, saved.Revision);
        Assert.Equal(original.Segments, saved.Segments);
        Assert.Equal(original.SpeakerNames, saved.SpeakerNames);
        Assert.Equal(original.ToText(), saved.ToText());
        Assert.False(AppStorage.SummaryNeedsRefresh(session, saved));
        var updated = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.NotEqual(hashes["transcript.json"], updated["transcript.json"]);
        foreach (var file in hashes.Keys.Where(name => name != "transcript.json"))
            Assert.Equal(hashes[file], updated[file]);
        var copy = saved.Copy();
        copy.SetSpeakerColor("spkr1", "#FFFFFF");
        Assert.Equal("#123ABC", saved.SpeakerColors["spkr1"]);
        saved.RenameSpeaker("spkr1", "Renamed");
        Assert.Equal("#123ABC", saved.SpeakerColors["spkr1"]);
        AppStorage.SetSpeakerColor(session, "spkr1", null);
        Assert.Empty(AppStorage.LoadTranscript(session).SpeakerColors);
        Assert.Throws<ArgumentException>(() => saved.SetSpeakerColor("missing", "#FFFFFF"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorrectionsAndSilenceRecoveryKeepColorsForTheCorrectSpeakerIdentities(bool reset)
    {
        var original = AudioEditPlanTests.Document(4,
            new(0, 1, "First.") { SpeakerId = "spkr1" },
            new(1, 2, "Second.") { SpeakerId = "spkr2" },
            new(2, 3, "Third.") { SpeakerId = "spkr1" },
            new(3, 4, "Fourth.") { SpeakerId = "spkr2" });
        original.SetSpeakerColor("spkr1", "#C6E8D5");
        original.SetSpeakerColor("spkr2", "#66384C");
        var corrected = TranscriptCorrectionPlan.Create(original, original.ProcessedFrames,
            [TranscriptPresentation.Rows(original)[0]]).Apply(["Corrected first."]);
        Assert.Equal(original.SpeakerColors, corrected.SpeakerColors);
        Assert.NotSame(original.SpeakerColors, corrected.SpeakerColors);
        AudioFrameRange[] ranges = [new(PcmFormat.SampleRate, 2L * PcmFormat.SampleRate), new(3L * PcmFormat.SampleRate, 4L * PcmFormat.SampleRate)];
        var current = AudioEditPlan.ForMutedRanges(corrected, corrected.ProcessedFrames, ranges).CreateTranscript();
        Assert.False(current.SpeakerColors.ContainsKey("spkr2"));
        current.SetSpeakerColor("spkr1", reset ? null : "#123456");
        var restored = new AudioRestorePlan(current, corrected, current.ProcessedFrames, ranges, [ranges[0]]).CreateTranscript();
        Assert.Equal("#66384C", restored.SpeakerColors["spkr2"]);
        Assert.Equal(reset ? null : "#123456", restored.SpeakerColors.GetValueOrDefault("spkr1"));
        Assert.Equal("#C6E8D5", original.SpeakerColors["spkr1"]);
        Assert.True(restored.Segments[0].IsUserEdited);
        var revision = restored.Revision;
        var before = restored.SpeakerColors.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Throws<InvalidDataException>(() => SpeakerAssignment.Apply(restored, []));
        Assert.Equal(before, restored.SpeakerColors);
        SpeakerAssignment.Apply(restored, [new(0, 4, 7)]);
        Assert.Empty(restored.SpeakerColors);
        Assert.NotEqual(revision, restored.Revision);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(0.5, 268)]
    [InlineData(1, 528)]
    public void HistoryTabStaysWithinTheEdgeAndResizesProportionally(double position, double expected)
    {
        var offset = HistoryTabPlacement.Offset(position, 600, 64);
        Assert.Equal(expected, offset, 6);
        Assert.Equal(position, HistoryTabPlacement.Position(offset, 600, 64), 6);
        Assert.InRange(HistoryTabPlacement.Offset(position, 300, 64), 8, 228);
        Assert.Equal(0, HistoryTabPlacement.Position(-100, 600, 64));
        Assert.Equal(1, HistoryTabPlacement.Position(2000, 600, 64));
        Assert.Equal(0, HistoryTabPlacement.Offset(position, 40, 64));
        Assert.Throws<ArgumentOutOfRangeException>(() => HistoryTabPlacement.Position(double.NaN, 600, 64));
    }
}
