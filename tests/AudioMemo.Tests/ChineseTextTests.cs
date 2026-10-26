using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class ChineseTextTests
{
    [Theory]
    [InlineData("\u6703\u8b70\u8a18\u9304\uff1a\u9810\u7b97\u5df2\u6279\u51c6\uff0c\u9031\u4e94\u6e96\u5099\u6295\u5f71\u7247\u3002",
        "\u4f1a\u8bae\u8bb0\u5f55\uff1a\u9884\u7b97\u5df2\u6279\u51c6\uff0c\u5468\u4e94\u51c6\u5907\u6295\u5f71\u7247\u3002")]
    [InlineData("\u4e7e\u5764\u3001\u4e7e\u9686\u3001\u4e7e\u71e5\u3001\u8457\u4f5c\u3001\u8457\u540d\u3001\u770b\u8457\u3001\u982d\u9aee\u3001\u767c\u5c55",
        "\u4e7e\u5764\u3001\u4e7e\u9686\u3001\u5e72\u71e5\u3001\u8457\u4f5c\u3001\u8457\u540d\u3001\u770b\u7740\u3001\u5934\u53d1\u3001\u53d1\u5c55")]
    [InlineData("\u8acb\u95dc\u9589\u9ea5\u514b\u98a8\u4e26\u5132\u5b58\u9304\u97f3\u3002",
        "\u8bf7\u5173\u95ed\u9ea6\u514b\u98ce\u5e76\u50a8\u5b58\u5f55\u97f3\u3002")]
    public void TraditionalTextUsesPhraseAwareSimplifiedChinese(string original, string expected)
    {
        var result = ChineseText.ToSimplified(original);
        Assert.Equal(expected, result);
        Assert.Equal(result, ChineseText.ToSimplified(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("English meeting: Alex will prepare the API slides by Friday.\r\n# Notes 123 https://example.com/a?b=1")]
    [InlineData("English, caf\u00e9, na\u00efve, \ud83d\ude00, 1 + 2 = 3.")]
    [InlineData("\u7b80\u4f53\u4e2d\u6587\uff1a\u4f1a\u8bae\u8bb0\u5f55\u3001\u8bbe\u7f6e\u3001\u6458\u8981\u3001\u5f55\u97f3\u3001\u9884\u7b97\u3001\u5468\u4e94\u3002")]
    [InlineData("\u8457\u4f5c\u6743\u3001\u8457\u540d\u4f5c\u5bb6\u3001\u4e7e\u5764\u3001\u5e72\u71e5\u3002")]
    public void EnglishFormattingAndValidSimplifiedWordsAreUnchanged(string text) =>
        Assert.Equal(text, ChineseText.ToSimplified(text));

    [Fact]
    public void TranscriptSegmentsNormalizeConstructionCopyingAndJsonWithoutChangingTimestamps()
    {
        const string traditional = "Alex \u6703\u5728\u9031\u4e94\u6e96\u5099\u6703\u8b70\u8a18\u9304\u3002";
        const string simplified = "Alex \u4f1a\u5728\u5468\u4e94\u51c6\u5907\u4f1a\u8bae\u8bb0\u5f55\u3002";
        var segment = new TranscriptSegment(12.5, 15.75, traditional);
        Assert.Equal(simplified, segment.Text);
        Assert.Equal(simplified, (segment with { Text = traditional }).Text);
        var json = JsonSerializer.Serialize(new { StartSeconds = 12.5, EndSeconds = 15.75, Text = traditional });
        var restored = JsonSerializer.Deserialize<TranscriptSegment>(json);
        Assert.Equal(segment, restored);
        using var persisted = JsonDocument.Parse(JsonSerializer.Serialize(restored));
        Assert.Equal(simplified, persisted.RootElement.GetProperty("Text").GetString());
        Assert.Equal(12.5, persisted.RootElement.GetProperty("StartSeconds").GetDouble());
        Assert.Equal(15.75, persisted.RootElement.GetProperty("EndSeconds").GetDouble());
    }

    [Fact]
    public void HistoricalTextDisplaysSimplifiedWithoutRewritingFilesOrPersonalNotes()
    {
        using var folder = new TestFolder();
        var session = AppStorage.CreateSession(new AppSettings { SaveFolder = folder.Root }, "Original title");
        const string traditional = "\u6703\u8b70\u8a18\u9304\uff1aAlex \u5df2\u78ba\u8a8d\u9810\u7b97\u3002";
        const string simplified = "\u4f1a\u8bae\u8bb0\u5f55\uff1aAlex \u5df2\u786e\u8ba4\u9884\u7b97\u3002";
        var transcriptJson = JsonSerializer.Serialize(new
        {
            ProcessedFrames = 96_000,
            Segments = new[] { new { StartSeconds = 0, EndSeconds = 2, Text = traditional } }
        });
        AtomicFile.WriteText(session.TranscriptPath, transcriptJson);
        AtomicFile.WriteText(session.SummaryPath, traditional);
        AtomicFile.WriteText(session.NotesPath, traditional);

        var transcript = AppStorage.LoadTranscript(session);
        Assert.Equal("[00:00:00] " + simplified, transcript.ToText());
        Assert.Equal(96_000, transcript.ProcessedFrames);
        Assert.Equal(simplified, AppStorage.LoadSummaryText(session));
        Assert.Equal(transcriptJson, File.ReadAllText(session.TranscriptPath));
        Assert.Equal(traditional, File.ReadAllText(session.SummaryPath));
        Assert.Equal(traditional, File.ReadAllText(session.NotesPath));

        AppStorage.SaveTranscript(session, transcript);
        using var persisted = JsonDocument.Parse(File.ReadAllText(session.TranscriptPath));
        Assert.Equal(simplified, persisted.RootElement.GetProperty("Segments")[0].GetProperty("Text").GetString());
        Assert.Equal("[00:00:00] " + simplified, File.ReadAllText(Path.Combine(session.Folder, "transcript.txt")));
        Assert.Equal(traditional, File.ReadAllText(session.NotesPath));
        Assert.Equal("Original title", session.Title);
    }

    [Fact]
    public void TraditionalAndSimplifiedOverlapIsDeduplicatedBeforeSaving()
    {
        var transcript = new TranscriptDocument
        {
            Segments = [new(0, 30, "\u6211\u5011\u661f\u671f\u4e94\u898b")]
        };
        TranscriptMerger.Append(transcript,
            [new(0, 5, "\u661f\u671f\u4e94\u89c1\u8ba8\u8bba\u9879\u76ee")], 28, 30, 60);
        Assert.Equal("\u6211\u4eec\u661f\u671f\u4e94\u89c1", transcript.Segments[0].Text);
        Assert.Equal("\u8ba8\u8bba\u9879\u76ee", transcript.Segments[1].Text);
        Assert.Equal(30, transcript.Segments[1].StartSeconds);
    }
}
