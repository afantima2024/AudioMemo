using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class PendingProcessingTests
{
    [Fact]
    public void PendingWorkReopensWithoutCredentialsOrChangingRecordingData()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = DataHashes(session);
        var settings = OllamaTests.LocalSettings();
        settings.SaveFolder = folder.Root;
        settings.SpeechModel = "base";
        settings.Language = "en";
        settings.SpeechVocabularyTerms = ["AudioMemo"];
        settings.SummarizeOnStop = true;
        var work = ProcessingWork.ForAi("TranscribeResume", settings);
        PendingProcessingStore.Defer(session, [work, new(ProcessingWorkKind.ExportAudio)]);
        var reopened = Assert.Single(AppStorage.LoadSessions(folder.Root).Sessions);
        var plan = PendingProcessingStore.Load(reopened)!;
        Assert.True(plan.PromptOnStartup);
        Assert.Equal([ProcessingWorkKind.ExportAudio, ProcessingWorkKind.Transcribe], plan.Work.Select(item => item.Kind));
        PendingProcessingStore.VerifyAudio(reopened, plan);
        Assert.Equal(before, DataHashes(session));
        var json = File.ReadAllText(PendingProcessingStore.PathFor(session));
        foreach (var excluded in new[] { "ProtectedApiKeys", "CloudConsent", "ConsentEndpoint", "CopilotExecutable", "PRIVATE NOTE" })
            Assert.DoesNotContain(excluded, json);

        settings.SpeechModel = "small";
        settings.Language = "zh";
        settings.OllamaModel = "changed-model";
        var restored = plan.Work[1].Options!.Restore(settings);
        Assert.Equal("base", restored.SpeechModel);
        Assert.Equal("en", restored.Language);
        Assert.Equal(work.Options!.SummaryModel, restored.OllamaModel);
        Assert.Equal(["AudioMemo"], restored.SpeechVocabularyTerms);
        Assert.Equal("changed-model", settings.OllamaModel);
        Assert.Equal("small", settings.SpeechModel);
        PendingProcessingStore.SetPromptOnStartup(reopened, plan, false);
        Assert.False(PendingProcessingStore.Load(reopened)!.PromptOnStartup);
        Assert.Equal(before, DataHashes(session));
        PendingProcessingStore.Complete(reopened, ProcessingWorkKind.ExportAudio);
        Assert.Equal(ProcessingWorkKind.Transcribe, Assert.Single(PendingProcessingStore.Load(reopened)!.Work).Kind);
        PendingProcessingStore.Complete(reopened, ProcessingWorkKind.Transcribe);
        Assert.Null(PendingProcessingStore.Load(reopened));
        Assert.Equal(before, DataHashes(session));
    }

    [Theory]
    [InlineData(SummaryProviders.Copilot)]
    [InlineData(CloudLlmCatalog.Qwen)]
    public void RestoringWorkNeverRestoresRevokedSharingConsent(string provider)
    {
        var original = new AppSettings { SummaryProvider = provider, CloudConsent = true, SummarizeOnStop = true };
        if (original.UsesCloudApi)
            original.GetCloudProfile().ConsentEndpoint = original.ActiveSummaryEndpoint;
        Assert.True(original.CanSummarize);
        var options = ProcessingOptions.Capture(original);
        var current = new AppSettings { SummaryProvider = provider, CloudConsent = false };
        if (current.UsesCloudApi)
            current.GetCloudProfile().ConsentEndpoint = "";
        Assert.False(options.Restore(current).CanSummarize);
        Assert.True(options.Restore(current).SummarizeOnStop);
        Assert.False(current.CloudConsent);
    }

    [Fact]
    public void CurrentCredentialsAreUsedWithoutWritingThemIntoThePlan()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var original = new AppSettings { SummaryProvider = CloudLlmCatalog.Qwen };
        var endpoint = original.ActiveSummaryEndpoint;
        original.GetCloudProfile().ConsentEndpoint = endpoint;
        original.GetCloudProfile().ProtectedApiKeys[endpoint] = "synthetic-old-protected-key";
        PendingProcessingStore.Defer(session, [ProcessingWork.ForAi("UpdateMainPoints", original)]);
        var current = new AppSettings { SummaryProvider = CloudLlmCatalog.Qwen };
        current.GetCloudProfile().ConsentEndpoint = endpoint;
        current.GetCloudProfile().ProtectedApiKeys[endpoint] = "synthetic-current-protected-key";
        var restored = Assert.Single(PendingProcessingStore.Load(session)!.Work).Options!.Restore(current);
        Assert.Equal("synthetic-current-protected-key", restored.GetCloudProfile().ProtectedApiKeys[endpoint]);
        Assert.True(restored.CanSummarize);
        Assert.DoesNotContain("protected-key", File.ReadAllText(PendingProcessingStore.PathFor(session)));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-CN")]
    public void EveryDeferredStepHasALocalizedLabel(string language)
    {
        var text = new AudioMemo.Core.Localization.UiText(language);
        foreach (var kind in Enum.GetValues<ProcessingWorkKind>())
            Assert.NotEmpty(text[new ProcessingWork(kind).LabelKey]);
    }

    [Fact]
    public void MergingPausedStepsKeepsExportsBeforeAnalysisAndDoesNotDuplicateWork()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = new AppSettings { SaveFolder = folder.Root };
        PendingProcessingStore.Defer(session, [ProcessingWork.ForAi("UpdateMainPoints", settings)]);
        PendingProcessingStore.Defer(session,
        [
            ProcessingWork.ForAi("TranscribeResume", settings), new(ProcessingWorkKind.ExportAudio),
            ProcessingWork.ForAi("UpdateMainPoints", settings)
        ]);
        var plan = PendingProcessingStore.Load(session)!;
        Assert.Equal([ProcessingWorkKind.ExportAudio, ProcessingWorkKind.Transcribe, ProcessingWorkKind.MainPoints],
            plan.Work.Select(item => item.Kind));
        Assert.True(plan.PromptOnStartup);
    }

    [Fact]
    public void ChangedAudioCannotResumeButDecliningTheOfferStillWorks()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        PendingProcessingStore.Defer(session, [ProcessingWork.ForAi("TranscribeResume", new())]);
        var plan = PendingProcessingStore.Load(session)!;
        File.SetLastWriteTimeUtc(session.WavePath, plan.AudioVersion.LastWriteUtc.AddSeconds(10));
        Assert.Throws<InvalidOperationException>(() => PendingProcessingStore.VerifyAudio(session, plan));
        PendingProcessingStore.SetPromptOnStartup(session, plan, false);
        Assert.False(PendingProcessingStore.Load(session)!.PromptOnStartup);
        Assert.Throws<InvalidOperationException>(() => PendingProcessingStore.Complete(session, ProcessingWorkKind.Transcribe));
    }

    [Fact]
    public void InvalidOrUnwritablePlansFailExplicitlyWithoutChangingAudioOrResults()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = DataHashes(session);
        var path = PendingProcessingStore.PathFor(session);
        Directory.CreateDirectory(path);
        Assert.Throws<IOException>(() => PendingProcessingStore.Defer(session, [new(ProcessingWorkKind.ExportAudio)]));
        Directory.Delete(path);
        PendingProcessingStore.Defer(session, [new(ProcessingWorkKind.ExportAudio)]);
        var plan = PendingProcessingStore.Load(session)!;
        foreach (var invalid in new[]
        {
            plan with { SessionId = "different-session" },
            plan with { Work = [] },
            plan with { Work = [new((ProcessingWorkKind)999)] },
            plan with { Work = [new(ProcessingWorkKind.MainPoints)] },
            plan with { Work = [new(ProcessingWorkKind.ExportAudio), new(ProcessingWorkKind.ExportAudio)] }
        })
        {
            File.WriteAllText(path, JsonSerializer.Serialize(invalid));
            Assert.Throws<InvalidDataException>(() => PendingProcessingStore.Load(session));
        }
        Assert.Equal(before, DataHashes(session));
    }

    [Fact]
    public async Task CompletedTranscriptionDoesNotReloadAModelWhenOnlyFinalProcessingRemains()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var document = AppStorage.LoadTranscript(session);
        document.Model = "base";
        document.Language = "en";
        document.SpeakersIdentified = true;
        AppStorage.SaveTranscript(session, document);
        var settings = new AppSettings { SaveFolder = folder.Root, SpeechModel = "base", Language = "en", SummarizeOnStop = false };
        var before = DataHashes(session);
        var transcriber = new TranscriptCorrectionTests.FakeTranscriber();
        var pipeline = new AiPipeline(transcriber, null, null, (_, _) =>
            throw new InvalidOperationException("Completed transcription must not check or download a model."));
        await pipeline.TranscribeAsync(session, settings, () => (session.Frames, false), new Updates(), CancellationToken.None);
        Assert.Equal(0, transcriber.OpenCount);
        Assert.Equal(before, DataHashes(session));
    }

    internal static KeyValuePair<string, string>[] DataHashes(RecordingSession session) =>
        EditedSessionTests.Hashes(session.Folder).Where(item => item.Key != PendingProcessingStore.FileName).ToArray();

    private sealed class Updates : IProgress<AiUpdate>
    {
        public void Report(AiUpdate value) { }
    }
}
