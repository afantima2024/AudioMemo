using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class ProcessingDescriptionTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void CountersExplicitlyDistinguishTranscriptBatchesFromAudioDuration(string language)
    {
        var text = new UiText(language);
        foreach (var key in new[] { "SummaryProgress", "LiveSummaryProgress", "LocalSummaryProgress",
            "LocalLiveSummaryProgress", "CloudSummaryProgress", "CloudLiveSummaryProgress",
            "SummaryCheckpointSaved", "AboutMeProgress" })
        {
            var message = text.FormatValue(key, 2, 7);
            Assert.DoesNotContain("2/7", message);
            if (language == "en")
            {
                Assert.Contains("transcript", message, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("batch 2 of 7", message);
            }
            else
            {
                Assert.Contains("\u7b2c 2 \u6279", message);
                Assert.Contains("\u5171 7 \u6279", message);
            }
        }
        foreach (var key in new[] { "TranscribingProgress", "RefiningTranscriptProgress" })
        {
            var message = text.FormatValue(key, "00:30:00", "02:00:00");
            Assert.Contains("00:30:00", message);
            Assert.Contains("02:00:00", message);
            Assert.Contains(language == "en" ? "audio processed" : "\u5df2\u5904\u7406\u97f3\u9891", message);
        }
        Assert.Contains(language == "en" ? "separate step" : "\u5355\u72ec\u7684\u5904\u7406\u6b65\u9aa4",
            text["OverallSummaryProgress"]);
        Assert.Contains(language == "en" ? "audio is unchanged" : "\u97f3\u9891\u4fdd\u6301\u4e0d\u53d8",
            text["RefiningTranscript"]);
        Assert.DoesNotContain("Copilot", text["EmptySummary"]);
        Assert.DoesNotContain(language == "en" ? "notes" : "\u7b14\u8bb0", text["TranscriptionComplete"]);
    }

    public static IEnumerable<object[]> SummaryCases()
    {
        foreach (var language in new[] { "en", "zh-CN" })
            foreach (var provider in new[] { SummaryProviders.Copilot, SummaryProviders.Ollama, CloudLlmCatalog.DeepSeek })
                foreach (var live in new[] { false, true })
                    yield return [language, provider, live];
    }

    [Theory]
    [MemberData(nameof(SummaryCases))]
    public async Task SevenTranscriptBatchesAreFollowedByASeparateOverallStepOnlyForFinalSummaries(
        string language, string provider, bool live)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = CloudLlmCatalog.IsProvider(provider) ? CloudLlmTests.CloudSettings(provider) : new AppSettings
        {
            SummaryProvider = provider, SummaryModel = "synthetic-model",
            OllamaModel = "synthetic-model", CloudConsent = true
        };
        settings.UiLanguage = language;
        settings.SaveFolder = folder.Root;
        var document = AppStorage.LoadTranscript(session);
        var limit = settings.UsesLocalSummaries ? 4000 : SummaryPrompt.MaximumBatchCharacters;
        document.Segments[0] = document.Segments[0] with { Text = new string('x', limit * 6 + 80) };
        AppStorage.SaveTranscript(session, document);
        var attempts = 0;
        var service = new SummaryFreshnessTests.Capture
        {
            StartingBatch = () =>
            {
                if (++attempts == 2)
                {
                    AppStorage.SetSpeakerColor(session, "spkr1", "#123456");
                    AppStorage.SetSpeakerColor(session, null, "#789ABC");
                }
            }
        };
        var updates = new Updates();
        await new AiPipeline(summaryService: service).SummarizeAsync(session, settings, updates, live, CancellationToken.None);
        Assert.Equal(7, service.Batches.Count);
        var key = provider == SummaryProviders.Ollama ? live ? "LocalLiveSummaryProgress" : "LocalSummaryProgress" :
            CloudLlmCatalog.IsProvider(provider) ? live ? "CloudLiveSummaryProgress" : "CloudSummaryProgress" :
            live ? "LiveSummaryProgress" : "SummaryProgress";
        var text = new UiText(language);
        for (var batch = 1; batch <= 7; batch++)
        {
            Assert.Contains(updates.Values, value => value.Status == text.FormatValue(key, batch, 7));
            Assert.Contains(updates.Values, value => value.Status == text.FormatValue("SummaryCheckpointSaved", batch, 7));
        }
        Assert.Equal(live ? 0 : 1, service.OverallCalls);
        if (!live)
        {
            var overall = updates.Values.FindIndex(value => value.Status == text["OverallSummaryProgress"]);
            Assert.True(overall > updates.Values.FindIndex(value =>
                value.Status == text.FormatValue("SummaryCheckpointSaved", 7, 7)));
        }
        var saved = AppStorage.LoadTranscript(session);
        Assert.Equal("#123456", saved.SpeakerColor("spkr1"));
        Assert.Equal("#789ABC", saved.UnlabeledSpeakerColor);
        Assert.False(AppStorage.SummaryNeedsRefresh(session, saved));
        Assert.DoesNotContain(service.Batches, batch =>
            batch.Text.Contains("#123456", StringComparison.Ordinal) || batch.Text.Contains("#789ABC", StringComparison.Ordinal));
    }

    private sealed class Updates : IProgress<AiUpdate>
    {
        public List<AiUpdate> Values { get; } = [];
        public void Report(AiUpdate value) => Values.Add(value);
    }
}
