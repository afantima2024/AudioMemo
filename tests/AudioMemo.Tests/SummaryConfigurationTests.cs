using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class SummaryConfigurationTests
{
    [Theory]
    [InlineData(SummaryProviders.Ollama)]
    [InlineData(SummaryProviders.Copilot)]
    [InlineData(CloudLlmCatalog.Qwen)]
    public async Task ValidationUsesOnlyASyntheticPromptAndAnIndependentSettingsSnapshot(string provider)
    {
        using var folder = new TestFolder();
        var settings = provider == SummaryProviders.Ollama ? OllamaTests.LocalSettings() :
            provider == SummaryProviders.Copilot ? new AppSettings { CloudConsent = true, SummaryModel = "test-model" } :
            CloudLlmTests.CloudSettings(provider);
        settings.SaveFolder = folder.Root;
        settings.SpeechVocabularyTerms = ["PRIVATE-VOCABULARY"];
        File.WriteAllText(folder.File("notes.md"), "PRIVATE-NOTES");
        File.WriteAllText(folder.File("transcript.json"), "PRIVATE-TRANSCRIPT");
        var fake = new FakeSummary();
        await new SummaryConfigurationService(fake).ValidateAsync(settings, CancellationToken.None);
        Assert.Equal(SummaryConfigurationService.SamplePrompt, fake.Prompt);
        foreach (var privateText in new[] { "PRIVATE-VOCABULARY", "PRIVATE-NOTES", "PRIVATE-TRANSCRIPT", folder.Root })
            Assert.DoesNotContain(privateText, fake.Prompt);
        Assert.NotSame(settings, fake.Settings);
        Assert.Equal(settings.SummaryProvider, fake.Settings!.SummaryProvider);
        Assert.Equal(settings.ActiveSummaryModel, fake.Settings.ActiveSummaryModel);
    }

    [Theory]
    [InlineData(SummaryProviders.Copilot)]
    [InlineData(CloudLlmCatalog.Qwen)]
    public async Task NoConsentMeansNoProviderRequest(string provider)
    {
        var fake = new FakeSummary();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SummaryConfigurationService(fake).ValidateAsync(new AppSettings { SummaryProvider = provider }, CancellationToken.None));
        Assert.Null(fake.Prompt);
    }

    [Fact]
    public async Task FailedEmptyCancelledAndTimedOutTestsNeverReportSuccess()
    {
        var settings = OllamaTests.LocalSettings();
        var fake = new FakeSummary { Respond = _ => throw new IOException("Synthetic authentication failure.") };
        await Assert.ThrowsAsync<IOException>(() => new SummaryConfigurationService(fake).ValidateAsync(settings, CancellationToken.None));
        fake.Respond = _ => Task.FromResult(" ");
        await Assert.ThrowsAsync<InvalidDataException>(() => new SummaryConfigurationService(fake).ValidateAsync(settings, CancellationToken.None));
        fake.Respond = async token => { await Task.Delay(Timeout.Infinite, token); return "unreachable"; };
        using var cancellation = new CancellationTokenSource();
        var cancelled = new SummaryConfigurationService(fake).ValidateAsync(settings, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new SummaryConfigurationService(fake, TimeSpan.FromMilliseconds(25)).ValidateAsync(settings, CancellationToken.None));
    }

    [Fact]
    public void CommittingValidatedSelectionPreservesConcurrentNonLlmPreferences()
    {
        using var folder = new TestFolder();
        var original = OllamaTests.LocalSettings();
        original.SaveFolder = folder.Root;
        original.SummarySetupDeferred = true;
        var candidate = SummaryConfigurationService.Copy(original);
        candidate.OllamaModel = "tested-model";
        candidate.OllamaContextTokens = 16384;
        original.MicrophoneEnabled = false;
        original.PlaybackId = "new-device";
        original.StatusBar.Llm = false;
        original.SpeechVocabularyTerms = ["KEEP-PRIVATE"];
        original.MyNotesVisible = false;
        var committed = SummaryConfigurationService.Merge(original, candidate);
        Assert.Equal("tested-model", committed.OllamaModel);
        Assert.Equal(16384, committed.OllamaContextTokens);
        Assert.False(committed.MicrophoneEnabled);
        Assert.Equal("new-device", committed.PlaybackId);
        Assert.False(committed.StatusBar.Llm);
        Assert.Equal(["KEEP-PRIVATE"], committed.SpeechVocabularyTerms);
        Assert.False(committed.MyNotesVisible);
        Assert.False(committed.SummarySetupDeferred);
        Assert.NotEqual(committed.OllamaModel, original.OllamaModel);
        Assert.NotSame(committed.StatusBar, original.StatusBar);
        Assert.NotSame(committed.CloudModels, candidate.CloudModels);
    }

    internal sealed class FakeSummary : ISummaryService
    {
        public Func<CancellationToken, Task<string>> Respond { get; set; } = _ => Task.FromResult("READY");
        public string? Prompt { get; private set; }
        public AppSettings? Settings { get; private set; }
        public Task<string> SummarizePromptAsync(AppSettings settings, string prompt, Action<string>? warning, CancellationToken token)
        {
            Settings = settings; Prompt = prompt;
            return Respond(token);
        }
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
            Action<string>? warning, CancellationToken token) => throw new InvalidOperationException("No recording summary is allowed.");
        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
            Action<string>? warning, CancellationToken token) => throw new InvalidOperationException("No main-points request is allowed.");
    }
}
