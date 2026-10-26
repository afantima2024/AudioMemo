using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed partial class ProductUiTests
{
    private static void VerifyProcessingResume(string language)
    {
        foreach (var automatic in new[] { false, true })
            VerifySummaryCloseAndReopen(language, automatic);
        VerifyCloseDuringPostRecordingExport(language);
        foreach (var automatic in new[] { true, false })
            VerifyCloseDuringActiveRecording(language, automatic);
        VerifyContinuationWithPausedWork(language);
        VerifyCloseCompletionRace(language);
        VerifyClosePlanFailureAndRetry(language);
        VerifyCloseAfterAnEarlierFailure(language);
    }

    private static void VerifySummaryCloseAndReopen(string language, bool automatic)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = OllamaTests.LocalSettings();
        settings.ReviewUnclearTranscript = false;
        settings.SaveFolder = folder.Root;
        settings.UiLanguage = language;
        settings.SpeechModel = "base";
        settings.Language = "en";
        settings.IdentifySpeakersOnStop = false;
        settings.SummarizeOnStop = automatic;
        var originalModel = settings.OllamaModel;
        var document = new TranscriptDocument
        {
            Model = "base", Language = "en", ProcessedFrames = session.Frames, Revision = "paused-summary",
            SpeakersIdentified = true, Segments = [new(0, 2, "SAVED-FIRST-" + new string('x', 9500))]
        };
        AppStorage.SaveTranscript(session, document);
        var originalWave = File.ReadAllBytes(session.WavePath);
        var originalTranscript = File.ReadAllBytes(session.TranscriptPath);
        var first = new PausableSummary { HoldOnCall = 2 };
        var pipeline = SummaryOnlyPipeline(first);
        var allowClose = false;
        var closePrompts = 0;
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, new RecordingEngine(),
            pipeline: pipeline, confirmCloseProcessing: _ => { closePrompts++; return allowClose; });
        var root = MountDialog(window, new Size(1060, 740));
        SelectProcessingSession(window, session);
        Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> operation = (snapshot, progress, token) =>
            automatic ? pipeline.TranscribeAsync(session, snapshot, () => (session.Frames, false), progress, token)
                : pipeline.SummarizeAsync(session, snapshot, progress, false, token);
        Call(window, "StartAiJob", session, automatic ? "TranscribeResume" : "UpdateMainPoints", operation);
        try
        {
            CompleteUiTask(root, first.Held.Task);
            Assert.True(ReadProcessingSummary(session).Provisional);
            var savedBatch = File.ReadAllBytes(Path.Combine(session.Folder, "summary.json"));
            var denied = PrepareTestClose(window);
            CompleteUiTask(root, denied);
            Assert.False(denied.Result);
            Assert.False(first.Cancelled);
            Assert.False(ActiveUiAiTask(window).IsCompleted);
            Assert.Null(PendingProcessingStore.Load(session));
            Assert.Equal(savedBatch, File.ReadAllBytes(Path.Combine(session.Folder, "summary.json")));
            allowClose = true;
            var accepted = PrepareTestClose(window);
            CompleteUiTask(root, accepted);
            Assert.True(accepted.Result);
            Assert.True(first.Cancelled);
            Assert.Equal(2, closePrompts);
            Assert.Equal(savedBatch, File.ReadAllBytes(Path.Combine(session.Folder, "summary.json")));
            var plan = PendingProcessingStore.Load(session)!;
            Assert.True(plan.PromptOnStartup);
            Assert.Equal(automatic ? ProcessingWorkKind.Transcribe : ProcessingWorkKind.MainPoints, Assert.Single(plan.Work).Kind);
            Assert.Equal(originalTranscript, File.ReadAllBytes(session.TranscriptPath));
            Assert.Equal(originalWave, File.ReadAllBytes(session.WavePath));
        }
        finally
        {
            allowClose = true;
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "CancelAiAsync")));
            window.Close();
        }

        var resumed = new PausableSummary();
        var prompts = 0;
        var before = PendingProcessingTests.DataHashes(session);
        settings.OllamaModel = "new-default-must-not-restart-saved-batches";
        var reopened = new MainWindow(new AppStorage(folder.File("reopened-preferences")), settings, new RecordingEngine(),
            pipeline: SummaryOnlyPipeline(resumed), confirmCloseProcessing: _ => true,
            confirmResumeProcessing: message =>
            {
                prompts++;
                Assert.Contains(session.Title, message);
                Assert.Contains(originalModel, message);
                return automatic;
            });
        root = MountDialog(reopened, new Size(1060, 740));
        try
        {
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(reopened, "OfferPendingProcessingAsync")));
            Assert.Equal(1, prompts);
            if (!automatic)
            {
                Assert.Empty(resumed.Calls);
                Assert.False(PendingProcessingStore.Load(session)!.PromptOnStartup);
                Assert.Equal(before, PendingProcessingTests.DataHashes(session));
                var resume = Assert.IsType<Button>(reopened.FindName("TranscribeButton"));
                Assert.Equal(UiText.Get("ResumeProcessing"), resume.Content);
                Assert.True(resume.IsEnabled);
                CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(reopened, "OfferPendingProcessingAsync")));
                Assert.Equal(1, prompts);
                Click(resume);
                CompleteUiTask(root, ActiveUiAiTask(reopened));
            }
            Assert.Null(PendingProcessingStore.Load(session));
            Assert.True(ReadProcessingSummary(session).HasOverallSummary);
            Assert.Equal(1, resumed.OverallCalls);
            Assert.Equal("Completed main-points batch 1.", resumed.Calls[0].Previous);
            Assert.All(resumed.Calls, call => Assert.Equal(originalModel, call.Model));
            var expected = string.Concat(SummaryPrompt.ResumableBatches(document.Segments, maximumCharacters: 4000)
                .Select(batch => batch.Text));
            Assert.Equal(expected, first.Calls[0].Text + string.Concat(resumed.Calls.Select(call => call.Text)));
            Assert.Equal(originalWave, File.ReadAllBytes(session.WavePath));
            Assert.Equal(originalTranscript, File.ReadAllBytes(session.TranscriptPath));
            Assert.Contains("PRIVATE NOTE", File.ReadAllText(session.NotesPath));
            Assert.Equal(UiText.Get("TranscribeResume"), Assert.IsType<Button>(reopened.FindName("TranscribeButton")).Content);
        }
        finally { reopened.Close(); }
    }

    private static void VerifyCloseDuringPostRecordingExport(string language)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root, UiLanguage = language, Format = "MP3", ReviewUnclearTranscript = false,
            SpeechModel = "base", Language = "en", IdentifySpeakersOnStop = false
        };
        var factory = new RecordingEngineTests.FakeFactory();
        var recorder = new RecordingEngine(factory.Create);
        var exportStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closePrompts = 0;
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, recorder,
            confirmCloseProcessing: _ => { closePrompts++; return true; },
            exportAudio: async (_, _, token) =>
            {
                exportStarted.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    cancellationObserved.SetResult();
                    await release.Task;
                    throw;
                }
            });
        var root = MountDialog(window, new Size(1060, 740));
        CompleteUiTask(root, recorder.StartAsync(settings, "Synthetic close during export",
            new(new SourceSelection("test-mic", "Synthetic microphone", 1), null)));
        factory.Inputs[0].EmitTone();
        CompleteUiTask(root, Task.Delay(200));
        var session = Assert.IsType<RecordingSession>(recorder.Session);
        SelectProcessingSession(window, session);
        var stopped = Assert.IsAssignableFrom<Task>(Call(window, "RunRecordingCommandAsync",
            new Func<Task>(() => Assert.IsAssignableFrom<Task>(Call(window, "StopRecordingAsync")))));
        var closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            CompleteUiTask(root, exportStarted.Task);
            Assert.Equal(RecorderState.Idle, recorder.State);
            var closing = PrepareTestClose(window);
            CompleteUiTask(root, cancellationObserved.Task);
            window.Close();
            Assert.False(closed);
            Assert.False(closing.IsCompleted);
            Assert.Equal(1, closePrompts);
            var again = PrepareTestClose(window);
            CompleteUiTask(root, again);
            Assert.False(again.Result);
            release.SetResult();
            CompleteUiTask(root, closing);
            CompleteUiTask(root, stopped);
            Assert.True(closing.Result);
            Assert.Equal("Saved", session.Status);
            using (var wave = File.OpenRead(session.WavePath))
                Assert.Equal(session.Frames, DurableWaveFile.ValidateAndGetFrames(wave));
            Assert.Equal([ProcessingWorkKind.ExportAudio, ProcessingWorkKind.Transcribe],
                PendingProcessingStore.Load(session)!.Work.Select(item => item.Kind));
            Assert.False(File.Exists(session.Mp3Path));
        }
        finally
        {
            release.TrySetResult();
            CompleteUiTask(root, stopped);
            window.Close();
        }
        var originalWave = File.ReadAllBytes(session.WavePath);
        var transcriber = new TranscriptCorrectionTests.FakeTranscriber();
        var resumed = new MainWindow(new AppStorage(folder.File("reopened-preferences")), settings, new RecordingEngine(),
            pipeline: new AiPipeline(transcriber, null, null, (_, _) => Task.CompletedTask),
            confirmResumeProcessing: _ => true, confirmCloseProcessing: _ => true);
        root = MountDialog(resumed, new Size(1060, 740));
        try
        {
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(resumed, "OfferPendingProcessingAsync")));
            Assert.True(PendingProcessingStore.Load(session) is null,
                $"Resume did not complete: {Assert.IsType<TextBlock>(resumed.FindName("WarningText")).Text}; " +
                Assert.IsType<TextBlock>(resumed.FindName("StatusText")).Text);
            Assert.True(new FileInfo(session.Mp3Path).Length > 0);
            Assert.Equal(session.Frames, AppStorage.LoadTranscript(session).ProcessedFrames);
            Assert.Equal(1, transcriber.OpenCount);
            Assert.Equal(originalWave, File.ReadAllBytes(session.WavePath));
        }
        finally { resumed.Close(); }
    }

    private static void VerifyClosePlanFailureAndRetry(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = new AppSettings { SaveFolder = folder.Root, UiLanguage = language };
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, new RecordingEngine(),
            confirmCloseProcessing: _ => true);
        var root = MountDialog(window, new Size(1060, 740));
        SelectProcessingSession(window, session);
        var before = PendingProcessingTests.DataHashes(session);
        var path = PendingProcessingStore.PathFor(session);
        Directory.CreateDirectory(path);
        Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> job = (_, _, token) =>
            Task.Delay(Timeout.InfiniteTimeSpan, token);
        Call(window, "StartAiJob", session, "UpdateMainPoints", job);
        try
        {
            var failed = PrepareTestClose(window);
            Assert.Throws<IOException>(() => CompleteUiTask(root, failed));
            Assert.False((bool)typeof(MainWindow).GetField("closing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!);
            Assert.True(ActiveUiAiTask(window).IsCompleted);
            Assert.Equal(before, PendingProcessingTests.DataHashes(session));
            Directory.Delete(path);
            var retry = PrepareTestClose(window);
            CompleteUiTask(root, retry);
            Assert.True(retry.Result);
            Assert.Equal(ProcessingWorkKind.MainPoints, Assert.Single(PendingProcessingStore.Load(session)!.Work).Kind);
            Assert.Equal(before, PendingProcessingTests.DataHashes(session));
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path);
            window.Close();
        }
    }

    private static void VerifyCloseDuringActiveRecording(string language, bool automatic)
    {
        using var folder = new TestFolder();
        var settings = new AppSettings { SaveFolder = folder.Root, UiLanguage = language, TranscribeOnStop = automatic };
        var factory = new RecordingEngineTests.FakeFactory();
        var recorder = new RecordingEngine(factory.Create);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcriber = new TranscriptCorrectionTests.FakeTranscriber();
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, recorder,
            confirmCloseProcessing: _ => true,
            pipeline: new AiPipeline(transcriber, null, null, (_, token) =>
            {
                started.TrySetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            }));
        var root = MountDialog(window, new Size(1060, 740));
        try
        {
            CompleteUiTask(root, recorder.StartAsync(settings, "Synthetic save on close",
                new(new SourceSelection("test-mic", "Synthetic microphone", 1), null)));
            factory.Inputs[0].EmitTone();
            CompleteUiTask(root, Task.Delay(200));
            var session = recorder.Session!;
            SelectProcessingSession(window, session);
            if (!automatic)
            {
                Call(window, "StartTranscription", session, true);
                CompleteUiTask(root, started.Task);
            }
            var close = PrepareTestClose(window);
            CompleteUiTask(root, close);
            Assert.True(close.Result);
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.False(factory.Inputs[0].IsRunning);
            Assert.Equal("Saved", session.Status);
            Assert.True(session.Frames > 0);
            using var stream = File.OpenRead(session.WavePath);
            Assert.Equal(session.Frames, DurableWaveFile.ValidateAndGetFrames(stream));
            if (automatic)
                Assert.Equal(ProcessingWorkKind.Transcribe, Assert.Single(PendingProcessingStore.Load(session)!.Work).Kind);
            else
                Assert.Null(PendingProcessingStore.Load(session));
            Assert.Equal(0, transcriber.OpenCount);
        }
        finally { window.Close(); }
    }

    private static void VerifyContinuationWithPausedWork(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = new AppSettings { SaveFolder = folder.Root, UiLanguage = language, TranscribeOnStop = false };
        var factory = new RecordingEngineTests.FakeFactory();
        var recorder = new RecordingEngine(factory.Create);
        var window = new MainWindow(new AppStorage(folder.File("preferences")), settings, recorder,
            confirmCloseProcessing: _ => true);
        var root = MountDialog(window, new Size(1060, 740));
        SelectProcessingSession(window, session);
        PendingProcessingStore.Defer(session, [ProcessingWork.ForAi("UpdateMainPoints", settings)]);
        var path = PendingProcessingStore.PathFor(session);
        var original = File.ReadAllBytes(session.WavePath);
        var originalMetadata = File.ReadAllBytes(session.MetadataPath);
        var sources = new RecordingSources(new("test-mic", "Synthetic microphone", 1), null);
        try
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            var blocked = Assert.IsAssignableFrom<Task>(Call(window, "ContinueRecordingAsync", session, sources));
            Assert.Throws<UnauthorizedAccessException>(() => CompleteUiTask(root, blocked));
            Assert.Empty(factory.Inputs);
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Equal(original, File.ReadAllBytes(session.WavePath));
            File.SetAttributes(path, FileAttributes.Normal);

            var failed = Assert.IsAssignableFrom<Task>(Call(window, "ContinueRecordingAsync", session,
                new RecordingSources(new("broken", "Synthetic broken microphone", 1), null)));
            Assert.Throws<RecordingDeviceException>(() => CompleteUiTask(root, failed));
            Assert.Equal(RecorderState.Idle, recorder.State);
            Assert.Equal(original, File.ReadAllBytes(session.WavePath));
            Assert.Equal(originalMetadata, File.ReadAllBytes(session.MetadataPath));
            PendingProcessingStore.VerifyAudio(session, PendingProcessingStore.Load(session)!);

            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "ContinueRecordingAsync", session, sources)));
            Assert.Equal(RecorderState.Recording, recorder.State);
            Assert.Null(PendingProcessingStore.Load(session));
            factory.Inputs.Last().EmitTone();
            CompleteUiTask(root, Task.Delay(150));
            CompleteUiTask(root, recorder.StopAsync());
            Assert.True(recorder.Session!.Frames > session.Frames);
        }
        finally
        {
            if (File.Exists(path))
                File.SetAttributes(path, FileAttributes.Normal);
            window.Close();
        }
    }

    private static void VerifyCloseCompletionRace(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var window = new MainWindow(new AppStorage(folder.File("preferences")),
            new() { SaveFolder = folder.Root, UiLanguage = language }, new RecordingEngine(),
            confirmCloseProcessing: _ => true);
        var root = MountDialog(window, new Size(1060, 740));
        SelectProcessingSession(window, session);
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<AppSettings, IProgress<AiUpdate>, CancellationToken, Task> job = (_, _, _) => publishing.Task;
        Call(window, "StartAiJob", session, "UpdateMainPoints", job);
        try
        {
            var close = PrepareTestClose(window);
            Assert.False(close.IsCompleted);
            publishing.SetResult();
            CompleteUiTask(root, close);
            Assert.True(close.Result);
            Assert.Null(PendingProcessingStore.Load(session));
        }
        finally { publishing.TrySetResult(); window.Close(); }
    }

    private static void VerifyCloseAfterAnEarlierFailure(string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var window = new MainWindow(new AppStorage(folder.File("preferences")),
            new() { SaveFolder = folder.Root, UiLanguage = language }, new RecordingEngine(),
            confirmCloseProcessing: _ => throw new InvalidOperationException("A completed old failure must not block closing."),
            exportAudio: (_, _, _) => Task.FromException(new IOException("Synthetic old export failure")));
        var root = MountDialog(window, new Size(1060, 740));
        try
        {
            var failed = Assert.IsAssignableFrom<Task>(Call(window, "ExportConfiguredAsync", session));
            Assert.Throws<IOException>(() => CompleteUiTask(root, failed));
            CompleteUiTask(root, Assert.IsAssignableFrom<Task>(Call(window, "RunRecordingCommandAsync",
                new Func<Task>(() => Task.FromException(new IOException("Synthetic old recording command failure"))))));
            var close = PrepareTestClose(window);
            CompleteUiTask(root, close);
            Assert.True(close.Result);
            Assert.Null(PendingProcessingStore.Load(session));
        }
        finally { window.Close(); }
    }

    private static Task<bool> PrepareTestClose(MainWindow window) =>
        Assert.IsAssignableFrom<Task<bool>>(Call(window, "PrepareCloseAsync"));

    private static void SelectProcessingSession(MainWindow window, RecordingSession session)
    {
        typeof(MainWindow).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, session);
        Call(window, "LoadSessionContent", session);
        Invoke(window, "UpdateControls");
    }

    private static SummaryDocument ReadProcessingSummary(RecordingSession session) =>
        JsonSerializer.Deserialize<SummaryDocument>(File.ReadAllText(Path.Combine(session.Folder, "summary.json")))!;

    private static AiPipeline SummaryOnlyPipeline(ISummaryService service) => new(null, service, null,
        (_, _) => throw new InvalidOperationException("Finished speech must not be repeated or download a model."));

    private sealed class PausableSummary : ISummaryService
    {
        public int HoldOnCall { get; init; }
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public int OverallCalls { get; private set; }
        public List<(string Text, string Previous, string Model)> Calls { get; } = [];

        public async Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            Assert.False(live);
            Assert.DoesNotContain("PRIVATE NOTE", transcript);
            Calls.Add((transcript, previous, settings.ActiveSummaryModel));
            if (Calls.Count == HoldOnCall)
            {
                Held.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return $"Completed main-points batch {Calls.Count}.";
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
            Action<string>? warning, CancellationToken cancellationToken)
        {
            OverallCalls++;
            return Task.FromResult("A completed overall summary.");
        }
    }
}
