using System.Diagnostics;
using System.Text.Json;
using AudioMemo.Core.Models;

namespace AudioMemo.Tests;

public sealed class TranscriptSearchTests
{
    [Fact]
    public void ExactSearchFindsEveryLiteralOccurrenceWithoutChangingRows()
    {
        var rows = Rows("Friday: Friday plans.", "A different speaker.", "FRIDAY [a-z] budgets.");
        var before = JsonSerializer.Serialize(rows);
        var found = TranscriptSearch.Find(rows, "Friday", false);
        Assert.Equal([0, 0, 2], found.Select(match => match.RowIndex));
        Assert.Equal([0, 8, 0], found.Select(match => match.Start));
        Assert.All(found, match => Assert.Equal(6, match.Length));
        Assert.All(found, match => Assert.Equal(0, match.Distance));
        Assert.Single(TranscriptSearch.Find(rows, "[a-z]", false));
        Assert.Equal(before, JsonSerializer.Serialize(rows));
    }

    [Theory]
    [InlineData("Frdiay", "Friday", "Friday", 1)]
    [InlineData("Friday", "Frday", "Frday", 1)]
    [InlineData("Friday", "Fridaay", "Fridaay", 1)]
    [InlineData("Friday", "Fridai", "Fridai", 1)]
    [InlineData("transcription", "transcriptoin", "transcriptoin", 1)]
    [InlineData("transcription", "tramscriptiom", "tramscriptiom", 2)]
    [InlineData("\u9879\u76ee\u8ba1\u5212", "\u9879\u76ee\u8bb0\u5212", "\u9879\u76ee\u8bb0\u5212", 1)]
    [InlineData("\u9879\u76ee\u8ba1\u5212", "\u9879\u8ba1\u76ee\u5212", "\u9879\u8ba1\u76ee\u5212", 1)]
    public void FuzzySearchToleratesSmallEditsAndKeepsExactHighlightOffsets(string query, string text, string expected, int edits)
    {
        var rows = Rows("Before: " + text + ". After.");
        Assert.Empty(TranscriptSearch.Find(rows, query, false));
        var match = Assert.Single(TranscriptSearch.Find(rows, query, true));
        Assert.Equal(8, match.Start);
        Assert.Equal(expected, rows[0].Text.Substring(match.Start, match.Length));
        Assert.Equal(edits, match.Distance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedAndAdjacentOccurrencesAreNotCollapsed(bool fuzzy)
    {
        var matches = TranscriptSearch.Find(Rows("aaaaaaaaa"), "aaa", fuzzy);
        Assert.Equal([0, 3, 6], matches.Select(match => match.Start));
        Assert.All(matches, match => Assert.Equal(3, match.Length));
        var phrases = TranscriptSearch.Find(Rows("Friday Friday Friday"), "Friday", fuzzy);
        Assert.Equal([0, 7, 14], phrases.Select(match => match.Start));
    }

    [Fact]
    public void ShortQueriesStayPreciseAndDoNotInventSemanticMatches()
    {
        Assert.Empty(TranscriptSearch.Find(Rows("ab"), "ac", true));
        Assert.Empty(TranscriptSearch.Find(Rows("\u9879\u76ee"), "\u9879\u65e5", true));
        Assert.Empty(TranscriptSearch.Find(Rows("A meeting tomorrow."), "conference", true));
        Assert.Empty(TranscriptSearch.Find(Rows("\u9879\u76ee\u8ba1\u5212"), "xiangmu", true));
        Assert.Empty(TranscriptSearch.Find(Rows("Frxxxay"), "Friday", true));
    }

    [Fact]
    public void ChineseQueryNormalizationAndUnicodeOffsetsStayCompatibleWithTheDisplayedText()
    {
        var rows = Rows("\u4f1a\u8bae\u8bb0\u5f55 \U0001F4DD Alpha \U0001F4DD Alpha");
        Assert.Equal(0, Assert.Single(TranscriptSearch.Find(rows, "\u6703\u8b70\u8a18\u9304", false)).Start);
        Assert.Equal([5, 14], TranscriptSearch.Find(rows, "\U0001F4DD", true).Select(match => match.Start));
        var matched = TranscriptSearch.Find(rows, "\U0001F4DD Alhpa", true);
        Assert.Equal(2, matched.Count);
        Assert.All(matched, match => Assert.Equal("\U0001F4DD Alpha", rows[0].Text.Substring(match.Start, match.Length)));
    }

    [Fact]
    public void EmptyInputIsClearAndCancellationAndQueryLimitsAreExplicit()
    {
        Assert.Empty(TranscriptSearch.Find(Rows("hello"), "   ", true));
        Assert.Empty(TranscriptSearch.Find([], "hello", true));
        Assert.Single(TranscriptSearch.Find(Rows(new string('a', 128)), new string('a', 128), false));
        Assert.Throws<ArgumentException>(() => TranscriptSearch.Find(Rows("hello"), new string('a', 129), true));
        Assert.Throws<OperationCanceledException>(() => TranscriptSearch.Find(Rows("hello"), "hello", true, new(true)));
    }

    [Fact]
    public void LargeTranscriptSearchRemainsBoundedAndCancellable()
    {
        var rows = Enumerable.Range(0, 15000)
            .Select(index => new TranscriptRow(index, index + 1, null, "", "The project team will review the Friday meeting notes.")).ToArray();
        var clock = Stopwatch.StartNew();
        var matches = TranscriptSearch.Find(rows, "Frdiay", true);
        Assert.Equal(rows.Length, matches.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"Fuzzy search took {clock.Elapsed} for {rows.Length} rows.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        Assert.Throws<OperationCanceledException>(() =>
            TranscriptSearch.Find(rows, new string('z', 128), true, cancellation.Token));
    }

    [Theory]
    [InlineData(AppThemes.Light)]
    [InlineData(AppThemes.Dark)]
    [InlineData(AppThemes.System)]
    public void ThemeSettingsRoundTripWithoutChangingOtherPreferences(string theme)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("""{"SpeechModel":"small"}""")!;
        Assert.Equal(AppThemes.Light, legacy.Theme);
        legacy.Theme = theme;
        legacy.SpeechVocabularyTerms = ["AudioMemo"];
        var saved = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(legacy))!;
        saved.Validate();
        Assert.Equal(theme, saved.Theme);
        Assert.Equal(["AudioMemo"], saved.SpeechVocabularyTerms);
        saved.Theme = "unsupported";
        Assert.Throws<InvalidDataException>(saved.Validate);
    }

    private static IReadOnlyList<TranscriptRow> Rows(params string[] texts) =>
        texts.Select((text, index) => new TranscriptRow(index, index + 1, $"spkr{index + 1}", $"Speaker {index + 1}", text)).ToArray();
}
