using System.Diagnostics;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using AudioMemo.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioMemo.Tests;

public sealed class IntegrationTests
{
    [LocalSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task SavedAudioIsTranscribedAndResumableThroughTheRealWorker()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            Language = "en",
            SpeechModel = "base",
            SpeechAcceleration = "cpu",
            IdentifySpeakersOnStop = false,
            SummarizeOnStop = false
        };
        var session = CreateSpeechSession(settings, Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEECH")!);
        var pipeline = new AiPipeline(new LocalTranscriber(WorkerPath));
        var progress = new InlineProgress();
        await pipeline.TranscribeAsync(session, settings, () => (session.Frames, false), progress, CancellationToken.None);
        var transcript = AppStorage.LoadTranscript(session);
        Assert.Equal(session.Frames, transcript.ProcessedFrames);
        var text = transcript.ToText();
        Assert.Contains("Friday", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("slides", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("budget", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(session.Folder, "transcript.txt")));
        Assert.False(File.Exists(session.SummaryPath));
        await pipeline.TranscribeAsync(session, settings, () => (session.Frames, false), progress, CancellationToken.None);
        Assert.Equal(text, AppStorage.LoadTranscript(session).ToText());
    }

    [ChineseSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task ChinesePresetReachesWhisperAndProducesAPersistedChineseTranscript()
    {
        using var folder = new TestFolder();
        var chinese = new UiText("zh-CN");
        var option = SpeechLanguages.GetOptions(chinese).Single(language => language.Code == "zh");
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            UiLanguage = "zh-CN",
            SpeechModel = "base",
            SpeechAcceleration = "cpu",
            IdentifySpeakersOnStop = false,
            Language = SpeechLanguages.ParseInput(option.Label, chinese)
        };
        var session = CreateSpeechSession(settings, Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_CHINESE_SPEECH")!);
        var progress = new InlineProgress();
        await new AiPipeline(new LocalTranscriber(WorkerPath)).TranscribeAsync(
            session, settings, () => (session.Frames, false), progress, CancellationToken.None);

        var transcript = AppStorage.LoadTranscript(session);
        Assert.Equal(session.Frames, transcript.ProcessedFrames);
        Assert.Equal("zh", session.TranscriptionLanguage);
        var text = transcript.ToText();
        Assert.Matches("\u661f\u671f[\u4e945]", text);
        Assert.Contains("\u9884\u7b97", text);
        Assert.DoesNotContain("\u9810\u7b97", text);
        Assert.Equal(text, ChineseText.ToSimplified(text));
        Assert.Equal(text, File.ReadAllText(Path.Combine(session.Folder, "transcript.txt")));
        using var saved = JsonDocument.Parse(File.ReadAllText(session.TranscriptPath));
        var savedText = string.Concat(saved.RootElement.GetProperty("Segments").EnumerateArray()
            .Select(segment => segment.GetProperty("Text").GetString()));
        Assert.Contains("\u9884\u7b97", savedText);
        Assert.DoesNotContain("\u9810\u7b97", savedText);
        Assert.Equal(chinese["TranscriptionComplete"], progress.Updates[^1].Status);
        Assert.False(File.Exists(session.SummaryPath));
    }

    [ChineseSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task TheRealWorkerEmitsSimplifiedJsonBeforeTheParentParsesIt()
    {
        var start = new ProcessStartInfo(WorkerPath);
        foreach (var argument in new[]
        {
            "--speech-worker", SpeechModels.Get("base").Path,
            Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_CHINESE_SPEECH")!, "zh"
        })
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromMinutes(2), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Error);
        var parts = new List<string>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var segment = JsonDocument.Parse(line);
            var text = segment.RootElement.GetProperty("Text").GetString();
            Assert.NotNull(text);
            Assert.Equal(text, ChineseText.ToSimplified(text));
            parts.Add(text);
        }
        Assert.Contains("\u9884\u7b97", string.Concat(parts));
        Assert.DoesNotContain("\u9810\u7b97", string.Concat(parts));
    }

    [LocalSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task CancellationTerminatesTheNativeSpeechWorker()
    {
        var transcriber = new LocalTranscriber(WorkerPath);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transcriber.TranscribeAsync(
            Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEECH")!, SpeechModels.Get("base"), "en", cancellation.Token));
    }

    [LocalSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task AutomaticSummariesUseTheLocalProviderWithoutCloudConsent()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            Language = "en",
            SpeechModel = "base",
            SpeechAcceleration = "cpu",
            IdentifySpeakersOnStop = false,
            SummaryProvider = SummaryProviders.Ollama,
            OllamaModel = "synthetic-model",
            SummarizeOnStop = true,
            CloudConsent = false
        };
        var session = CreateSpeechSession(settings, Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEECH")!);
        var summaries = new LocalSummaryStub();
        await new AiPipeline(new LocalTranscriber(WorkerPath), summaries).TranscribeAsync(
            session, settings, () => (session.Frames, false), new InlineProgress(), CancellationToken.None);
        Assert.Equal(1, summaries.Calls);
        Assert.False(summaries.WasLive);
        Assert.Equal("Synthetic local notes.", File.ReadAllText(session.SummaryPath));
    }

    [LocalSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task LiveSummariesUseTheLocalProviderWithoutCloudConsent()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            SpeechModel = "base",
            SpeechAcceleration = "cpu",
            IdentifySpeakersOnStop = false,
            SummaryProvider = SummaryProviders.Ollama,
            OllamaModel = "synthetic-model",
            LiveTranscription = true,
            LiveSummary = true,
            CloudConsent = false
        };
        var session = AppStorage.CreateSession(settings, "Synthetic silent live session");
        using (var wave = new DurableWaveFile(session.WavePath))
        {
            var second = new byte[PcmFormat.SampleRate * PcmFormat.BlockAlign];
            for (var index = 0; index < 90; index++)
                wave.Write(second);
            session.Frames = wave.FramesWritten;
        }
        AppStorage.SaveTranscript(session, new()
        {
            Segments = [new(0, 1, "Previously recognized synthetic meeting content.")]
        });
        var summaries = new LocalSummaryStub();
        await new AiPipeline(new LocalTranscriber(WorkerPath), summaries).TranscribeAsync(
            session, settings, () => (session.Frames, AppStorage.LoadTranscript(session).ProcessedFrames < session.Frames),
            new InlineProgress(), CancellationToken.None);
        Assert.Equal(1, summaries.Calls);
        Assert.True(summaries.WasLive);
        Assert.Equal(session.Frames, AppStorage.LoadTranscript(session).ProcessedFrames);
    }

    [CopilotFact]
    [Trait("Category", "Copilot")]
    public async Task TheActualCopilotAdapterSummarizesOnlyTheSyntheticTranscript()
    {
        var service = new CopilotService();
        var settings = new AppSettings { CloudConsent = true };
        var output = await service.SummarizeBatchAsync(settings,
            "[00:00:00] The project demonstration is Friday. Alex will prepare the slides. The budget was approved.",
            "", false, CancellationToken.None);
        Assert.Contains("Friday", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Alex", output, StringComparison.OrdinalIgnoreCase);
        var command = CopilotService.CreateSummaryStartInfo(settings, "Synthetic test");
        Assert.Contains("--available-tools=", command.ArgumentList);
        Assert.Contains("--deny-tool=shell", command.ArgumentList);
        Assert.DoesNotContain("--allow-all-tools", command.ArgumentList);
        Assert.DoesNotContain("--allow-all", command.ArgumentList);
    }

    [LocalSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task LiveBaseDraftsArriveBeforeStopAndAreReplacedBySmallRefinement()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            Language = "en",
            SpeechModel = "small",
            SpeechAcceleration = "cpu",
            FastLiveDrafts = true,
            IdentifySpeakersOnStop = false
        };
        var session = CreateSpeechSession(settings, Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEECH")!);
        var clock = Stopwatch.StartNew();
        double? firstText = null;
        var progress = new InlineProgress(update =>
        {
            if (firstText is null && update.Document is { IsDraft: true, Segments.Count: > 0 })
                firstText = clock.Elapsed.TotalSeconds;
        });
        await new AiPipeline(new LocalTranscriber(WorkerPath)).TranscribeAsync(session, settings, () =>
        {
            var frames = Math.Min(session.Frames, (long)(clock.Elapsed.TotalSeconds * PcmFormat.SampleRate));
            return (frames, frames < session.Frames);
        }, progress, CancellationToken.None);
        Assert.NotNull(firstText);
        Assert.True(firstText < session.Duration.TotalSeconds, $"First draft took {firstText:F2}s.");
        var draft = progress.Updates.First(update => update.Document is { IsDraft: true, Segments.Count: > 0 }).Document!;
        var final = AppStorage.LoadTranscript(session);
        Assert.False(final.IsDraft);
        Assert.Equal("small", final.Model);
        Assert.NotEqual(draft.Revision, final.Revision);
        Assert.Equal(session.Frames, final.ProcessedFrames);
        Assert.Contains("Friday", final.ToText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(progress.Updates, update => update.SpeechRuntime == "base / Cpu");
        Assert.False(File.Exists(Path.Combine(session.Folder, "transcript.refining.json")));
    }

    [LocalSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task CancellingRefinementRetainsTheDraftAndResumePromotesOnlyTheCompletedTranscript()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            Language = "en",
            SpeechModel = "base",
            SpeechAcceleration = "cpu",
            IdentifySpeakersOnStop = false
        };
        var session = CreateSpeechSession(settings, Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEECH")!);
        var draft = new TranscriptDocument
        {
            IsDraft = true,
            Model = "base",
            Language = "en",
            Revision = "synthetic-draft",
            ProcessedFrames = session.Frames,
            Segments = [new(0, 2, "Draft retained during refinement.")]
        };
        AppStorage.SaveTranscript(session, draft);
        using var cancellation = new CancellationTokenSource();
        var updates = new InlineProgress(update =>
        {
            if (update.Status == new UiText(settings.UiLanguage)["RefiningTranscript"])
                cancellation.Cancel();
        });
        var pipeline = new AiPipeline(new LocalTranscriber(WorkerPath));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.TranscribeAsync(session, settings, () => (session.Frames, false), updates, cancellation.Token));
        Assert.Equal(draft.ToText(), AppStorage.LoadTranscript(session).ToText());
        Assert.True(AppStorage.LoadTranscript(session).IsDraft);
        await pipeline.TranscribeAsync(session, settings, () => (session.Frames, false), new InlineProgress(), CancellationToken.None);
        Assert.False(AppStorage.LoadTranscript(session).IsDraft);
        Assert.Contains("Friday", AppStorage.LoadTranscript(session).ToText(), StringComparison.OrdinalIgnoreCase);
    }

    [LocalSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task WarmWorkerHandlesMultipleRequestsAndCancellationClosesItsProcess()
    {
        await using var worker = await new LocalTranscriber(WorkerPath).OpenAsync(SpeechModels.Get("base"), "en", "cpu", true, CancellationToken.None);
        Assert.Equal("Cpu", worker.Runtime);
        var sample = Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEECH")!;
        var first = await worker.TranscribeAsync(sample, CancellationToken.None);
        var second = await worker.TranscribeAsync(sample, CancellationToken.None);
        Assert.Equal(first.Select(segment => segment.Text), second.Select(segment => segment.Text));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.TranscribeAsync(sample, cancellation.Token));
    }

    [LocalSpeechFact]
    [Trait("Category", "LocalSpeech")]
    public async Task SelectedReTranscriptionUsesNativeVocabularyAndKeepsItsPreviewUnsaved()
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            Language = "en",
            SpeechModel = "base",
            SpeechAcceleration = "cpu",
            SpeechVocabularyTerms = ["AudioMemo", "Jamie", "Friday"],
            SummarizeOnStop = true
        };
        var session = CreateSpeechSession(settings, Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEECH")!);
        var original = new TranscriptDocument
        {
            ProcessedFrames = session.Frames,
            Model = "base",
            Language = "en",
            Segments = [new(0, session.Duration.TotalSeconds, "Incorrect saved wording.") { SpeakerId = "spkr1" }],
            SpeakerNames = new() { ["spkr1"] = "Synthetic speaker" }
        };
        AppStorage.SaveTranscript(session, original);
        var before = EditedSessionTests.Hashes(session.Folder);
        var plan = TranscriptCorrectionPlan.Create(original, session.Frames, TranscriptPresentation.Rows(original));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var result = await new SelectedTranscriptionService(new LocalTranscriber(WorkerPath)).GenerateAsync(
            session, plan, settings, new Progress<SelectedTranscriptionProgress>(), cancellation.Token);
        Assert.Empty(result.Unrecognized);
        Assert.Contains("Friday", Assert.Single(result.Texts), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        Assert.False(File.Exists(session.SummaryPath));
        var saved = TranscriptCorrectionService.Save(session, plan, result.Texts, cancellation.Token, result.AudioVersion);
        var segment = Assert.Single(saved.Segments);
        Assert.True(segment.IsUserEdited);
        Assert.Equal(original.Segments[0].StartSeconds, segment.StartSeconds);
        Assert.Equal(original.Segments[0].EndSeconds, segment.EndSeconds);
        Assert.Equal(original.Segments[0].SpeakerId, segment.SpeakerId);
        Assert.Equal(original.SpeakerNames, saved.SpeakerNames);
    }

    private static string WorkerPath =>
        Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_WORKER")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
#if DEBUG
            @"..\..\..\..\..\src\AudioMemo\bin\Debug\net10.0-windows\win-x64\AudioMemo.exe"));
#else
            @"..\..\..\..\..\src\AudioMemo\bin\Release\net10.0-windows\win-x64\AudioMemo.exe"));
#endif

    private static RecordingSession CreateSpeechSession(AppSettings settings, string path)
    {
        var session = AppStorage.CreateSession(settings, "Synthetic speech integration");
        using (var source = new WaveFileReader(path))
        {
            var resampled = new WdlResamplingSampleProvider(source.ToSampleProvider(), 48_000);
            var stereo = new MonoToStereoSampleProvider(resampled);
            var provider = new SampleToWaveProvider16(stereo);
            using var wave = new DurableWaveFile(session.WavePath);
            var buffer = new byte[8192];
            int count;
            while ((count = provider.Read(buffer, 0, buffer.Length)) > 0)
                wave.Write(buffer.AsSpan(0, count));
            session.Frames = wave.FramesWritten;
        }
        session.Status = "Saved";
        session.Save();
        return session;
    }

    private sealed class InlineProgress(Action<AiUpdate>? report = null) : IProgress<AiUpdate>
    {
        public List<AiUpdate> Updates { get; } = [];
        public void Report(AiUpdate value)
        {
            lock (Updates)
                Updates.Add(value);
            report?.Invoke(value);
        }
    }

    private sealed class LocalSummaryStub : ISummaryService
    {
        public int Calls { get; private set; }
        public bool WasLive { get; private set; }
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            Assert.Equal(SummaryProviders.Ollama, settings.SummaryProvider);
            Assert.False(settings.CloudConsent);
            Assert.NotEmpty(transcript);
            Calls++;
            WasLive = live;
            return Task.FromResult("Synthetic local notes.");
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            Assert.Equal(SummaryProviders.Ollama, settings.SummaryProvider);
            Assert.False(settings.CloudConsent);
            Assert.Equal("Synthetic local notes.", mainPoints);
            return Task.FromResult("Synthetic local overall summary.");
        }
    }
}

public sealed class ChineseSpeechFactAttribute : FactAttribute
{
    public ChineseSpeechFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_CHINESE_SPEECH")))
            Skip = "Set AUDIOMEMO_TEST_CHINESE_SPEECH to generated 16-kHz mono Chinese speech mentioning Friday and the budget.";
    }
}

public sealed class LocalSpeechFactAttribute : FactAttribute
{
    public LocalSpeechFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEECH")))
            Skip = "Set AUDIOMEMO_TEST_SPEECH to a generated 16-kHz mono PCM WAV and download the base model.";
    }
}

public sealed class CopilotFactAttribute : FactAttribute
{
    public CopilotFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_COPILOT") != "1")
            Skip = "Set AUDIOMEMO_TEST_COPILOT=1 to use the current CLI login for a synthetic summary (usage charges apply).";
    }
}
