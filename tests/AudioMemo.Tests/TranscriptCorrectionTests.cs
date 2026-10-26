using System.Text;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;
using NAudio.Wave;

namespace AudioMemo.Tests;

public sealed class TranscriptCorrectionTests
{
    [Fact]
    public void GroupedRowsMapToTheirExactSegmentsWithoutChangingTimestampsOrSpeakers()
    {
        var source = AudioEditPlanTests.Document(3,
            new(0, 0.5, "Project") { SpeakerId = "spkr1" },
            new(0.5, 1, "Apollo.") { SpeakerId = "spkr1" },
            new(1, 2, "Unselected voice.") { SpeakerId = "spkr2" },
            new(2, 3, "Later decision.") { SpeakerId = "spkr1" });
        var groups = TranscriptPresentation.Groups(source);
        Assert.Equal([0, 1], groups[0].SegmentIndices);
        Assert.Equal("Project Apollo.", groups[0].Row.Text);
        var plan = TranscriptCorrectionPlan.Create(source, source.ProcessedFrames, [groups[0].Row, groups[2].Row]);
        Assert.Equal([0, 1, 3], plan.Indices);
        var edited = plan.Apply(["The project", "\u6703\u8b70\u8a18\u9304", "Corrected decision."]);
        Assert.Equal("\u4f1a\u8bae\u8bb0\u5f55", edited.Segments[1].Text);
        Assert.Equal(source.Segments.Select(segment => (segment.StartSeconds, segment.EndSeconds, segment.SpeakerId)),
            edited.Segments.Select(segment => (segment.StartSeconds, segment.EndSeconds, segment.SpeakerId)));
        Assert.Equal(source.Segments[2], edited.Segments[2]);
        Assert.Equal(source.ProcessedFrames, edited.ProcessedFrames);
        Assert.Equal(source.Model, edited.Model);
        Assert.Equal(source.Language, edited.Language);
        Assert.NotEqual(source.Revision, edited.Revision);
        Assert.True(edited.Segments[0].IsUserEdited);
        Assert.False(edited.Segments[2].IsUserEdited);
        Assert.False(source.Segments[0].IsUserEdited);
        Assert.True(plan.Matches(source));
        Assert.False(plan.Matches(edited));
        Assert.True(plan.Matches(plan.Apply(plan.Segments.Select(segment => segment.Text).ToArray())));
    }

    [Fact]
    public void InvalidStaleOrUnfinishedSelectionsCannotBeApplied()
    {
        var source = AudioEditPlanTests.Document(1, new TranscriptSegment(0, 1, "Original."));
        var row = Assert.Single(TranscriptPresentation.Rows(source));
        Assert.Throws<InvalidOperationException>(() => TranscriptCorrectionPlan.Create(source, source.ProcessedFrames, []));
        Assert.Throws<InvalidOperationException>(() => TranscriptCorrectionPlan.Create(source, source.ProcessedFrames, [row with { Text = "Stale" }]));
        var plan = TranscriptCorrectionPlan.Create(source, source.ProcessedFrames, [row]);
        Assert.Throws<InvalidOperationException>(() => plan.Apply([]));
        Assert.Throws<InvalidDataException>(() => plan.Apply([" "]));
        Assert.Throws<InvalidDataException>(() => plan.Apply(["Bad\0text"]));
        Assert.Throws<InvalidDataException>(() => plan.Apply([new string('x', TranscriptCorrectionPlan.MaximumTextLength + 1)]));
        source.IsDraft = true;
        Assert.Throws<InvalidOperationException>(() => TranscriptCorrectionPlan.Create(source, source.ProcessedFrames, [row]));
        source.IsDraft = false;
        Assert.Throws<InvalidOperationException>(() => TranscriptCorrectionPlan.Create(source, source.ProcessedFrames + 1, [row]));
    }

    [Fact]
    public async Task EditingPersistsOnlySelectedTextAndStartsAFreshSummaryWithoutSendingVocabularyOrNotes()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        var document = AppStorage.LoadTranscript(session);
        var plan = TranscriptCorrectionPlan.Create(document, session.Frames, [TranscriptPresentation.Rows(document)[1]]);
        var result = TranscriptCorrectionService.Save(session, plan, ["Approved corrected wording."], CancellationToken.None);
        Assert.Equal(result.Segments, AppStorage.LoadTranscript(session).Segments);
        Assert.Contains("Approved corrected wording.", File.ReadAllText(folderPath("transcript.txt")));
        Assert.True(AppStorage.SummaryNeedsRefresh(session, result));
        Assert.Equal("OLD SUMMARY includes REMOVE.", AppStorage.LoadSummaryText(session));
        var after = EditedSessionTests.Hashes(session.Folder).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in before.Where(pair => pair.Key is not ("transcript.json" or "transcript.txt")))
            Assert.Equal(pair.Value, after[pair.Key]);
        Assert.Equal(document.Segments[0], result.Segments[0]);
        Assert.Equal(document.Segments[2], result.Segments[2]);
        var capture = new SummaryCapture();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            SummaryProvider = SummaryProviders.Ollama,
            OllamaModel = "fake-local-model",
            SpeechVocabularyTerms = ["PRIVATE_VOCABULARY_NOT_SPOKEN"]
        };
        await new AiPipeline(summaryService: capture).SummarizeAsync(session, settings, new Progress<AiUpdate>(), false, CancellationToken.None);
        Assert.Empty(capture.Previous);
        Assert.Contains("Approved corrected wording.", capture.Text);
        Assert.DoesNotContain("REMOVE this voice.", capture.Text);
        Assert.DoesNotContain("PRIVATE NOTE", capture.Text);
        Assert.DoesNotContain("PRIVATE_VOCABULARY", capture.Text);
        Assert.False(AppStorage.SummaryNeedsRefresh(session, result));

        string folderPath(string name) => Path.Combine(session.Folder, name);
    }

    [Fact]
    public void FailedJsonCommitRestoresTheTextExportAndLeavesTheSessionUnchanged()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var plan = Plan(session, 0);
        var before = EditedSessionTests.Hashes(session.Folder);
        using (var blocked = new FileStream(session.TranscriptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => TranscriptCorrectionService.Save(session, plan, ["A new correction."], CancellationToken.None));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        Assert.Empty(Directory.GetFiles(session.Folder, "*.tmp"));
    }

    [Fact]
    public void CancellationOrAConcurrentTranscriptEditCannotOverwriteSavedWork()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var plan = Plan(session, 0);
        var before = EditedSessionTests.Hashes(session.Folder);
        Assert.Throws<OperationCanceledException>(() => TranscriptCorrectionService.Save(
            session, plan, ["Cancelled."], new CancellationToken(true)));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        var changed = AppStorage.LoadTranscript(session);
        changed.Segments[2] = changed.Segments[2] with { Text = "External correction." };
        AppStorage.SaveTranscript(session, changed);
        var external = EditedSessionTests.Hashes(session.Folder);
        Assert.Throws<InvalidOperationException>(() => TranscriptCorrectionService.Save(session, plan, ["Stale draft."], CancellationToken.None));
        Assert.Equal(external, EditedSessionTests.Hashes(session.Folder));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReTranscriptionReadsOnlyTheSelectedAudioAndDoesNotSaveBeforeApproval(bool vocabularyEnabled)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var plan = Plan(session, 0, 2);
        var before = EditedSessionTests.Hashes(session.Folder);
        var fake = new FakeTranscriber();
        var settings = new AppSettings
        {
            SaveFolder = folder.Root,
            SpeechModel = "base",
            Language = "zh",
            SpeechAcceleration = "cpu",
            SpeechVocabularyTerms = ["AudioMemo"],
            SpeechVocabularyEnabled = vocabularyEnabled,
            SummarizeOnStop = true,
            LiveSummary = true,
            CloudConsent = true
        };
        var result = await Service(fake).GenerateAsync(session, plan, settings, new Updates(), CancellationToken.None);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        Assert.Equal(["Recognized selection 1.", "Recognized selection 2."], result.Texts);
        Assert.Empty(result.Unrecognized);
        Assert.Equal("base", fake.Model!.Id);
        Assert.Equal("zh", fake.Language);
        Assert.False(fake.Draft);
        Assert.Equal(vocabularyEnabled ? ["AudioMemo"] : Array.Empty<string>(), fake.Vocabulary);
        Assert.Equal(1, fake.OpenCount);
        Assert.True(fake.Worker.Disposed);
        for (var index = 0; index < fake.Worker.Chunks.Count; index++)
        {
            var expectedPath = folder.File($"expected-{index}.wav");
            var segment = plan.Segments[index];
            AudioExport.WriteSpeechChunk(session.WavePath, expectedPath,
                AudioEditPlan.FrameAt(segment.StartSeconds, session.Frames),
                AudioEditPlan.FrameAt(segment.EndSeconds, session.Frames) - AudioEditPlan.FrameAt(segment.StartSeconds, session.Frames),
                CancellationToken.None);
            Assert.Equal(File.ReadAllBytes(expectedPath), fake.Worker.Chunks[index]);
            Assert.False(File.Exists(fake.Worker.Paths[index]));
        }
        var saved = TranscriptCorrectionService.Save(session, plan, result.Texts, CancellationToken.None, result.AudioVersion);
        Assert.Equal(result.Texts[0], saved.Segments[0].Text);
        Assert.Equal(result.Texts[1], saved.Segments[2].Text);
        Assert.Equal("REMOVE this voice.", saved.Segments[1].Text);
        Assert.True(saved.Segments[0].IsUserEdited);
    }

    [Fact]
    public async Task MissingSpeechIsReportedAndRetainsThePreviousText()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var plan = Plan(session, 0, 1);
        var fake = new FakeTranscriber();
        fake.Worker.Response = (_, number, _) => Task.FromResult<IReadOnlyList<TranscriptSegment>>(
            number == 1 ? [] : [new(0, 0.5, "Recognized second.")]);
        var result = await Service(fake).GenerateAsync(session, plan, new(), new Updates(), CancellationToken.None);
        Assert.Equal([0], result.Unrecognized);
        Assert.Equal(plan.Segments[0].Text, result.Texts[0]);
        Assert.Equal("Recognized second.", result.Texts[1]);
        fake = new FakeTranscriber();
        fake.Worker.Response = (_, _, _) => Task.FromResult<IReadOnlyList<TranscriptSegment>>([]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(fake).GenerateAsync(session, plan, new(), new Updates(), CancellationToken.None));
        Assert.True(fake.Worker.Disposed);
    }

    [Fact]
    public async Task CancellationClosesTheWorkerBeforeDeletingItsOpenAudioChunk()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var fake = new FakeTranscriber();
        fake.Worker.Response = async (path, _, token) =>
        {
            fake.Worker.HeldAudio = File.OpenRead(path);
            fake.Worker.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        };
        using var cancellation = new CancellationTokenSource();
        var pending = Service(fake).GenerateAsync(session, Plan(session, 0, 1), new(), new Updates(), cancellation.Token);
        await fake.Worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(fake.Worker.Disposed);
        Assert.All(fake.Worker.Paths, path => Assert.False(File.Exists(path)));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task PartialRecognitionFailureCannotPublishPartialText()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var fake = new FakeTranscriber();
        fake.Worker.Response = (_, number, _) => number == 2
            ? throw new InvalidOperationException("Synthetic worker failure.")
            : Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 0.5, "Preview only.")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(fake).GenerateAsync(session, Plan(session, 0, 2), new(), new Updates(), CancellationToken.None));
        Assert.True(fake.Worker.Disposed);
        Assert.All(fake.Worker.Paths, path => Assert.False(File.Exists(path)));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task ChangedAudioCannotCommitAnEarlierRecognitionPreview()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var plan = Plan(session, 1);
        var result = await Service(new FakeTranscriber()).GenerateAsync(session, plan, new(), new Updates(), CancellationToken.None);
        File.SetLastWriteTimeUtc(session.WavePath, result.AudioVersion.LastWriteUtc.AddSeconds(1));
        var before = EditedSessionTests.Hashes(session.Folder);
        Assert.Throws<InvalidOperationException>(() =>
            TranscriptCorrectionService.Save(session, plan, result.Texts, CancellationToken.None, result.AudioVersion));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task LongSelectedSegmentsUseBoundedOverlappingChunks()
    {
        using var folder = new TestFolder();
        var session = AppStorage.CreateSession(new() { SaveFolder = folder.Root }, "Synthetic long selection");
        using (var output = new DurableWaveFile(session.WavePath))
        {
            var second = new byte[PcmFormat.BytesPerSecond];
            for (var frame = 0; frame < PcmFormat.SampleRate; frame++)
            {
                var sample = (short)(Math.Sin(2 * Math.PI * 220 * frame / PcmFormat.SampleRate) * 5000);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(second.AsSpan(frame * 4), sample);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(second.AsSpan(frame * 4 + 2), sample);
            }
            for (var index = 0; index < 65; index++)
                output.Write(second);
        }
        session.Status = "Saved";
        session.Frames = 65L * PcmFormat.SampleRate;
        AppStorage.SaveTranscript(session, AudioEditPlanTests.Document(65, new TranscriptSegment(0, 65, "Long source.")));
        var fake = new FakeTranscriber();
        fake.Worker.Response = (_, number, _) =>
            Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 10, $"Part {number}.")]);
        await Service(fake).GenerateAsync(session, Plan(session, 0), new(), new Updates(), CancellationToken.None);
        Assert.Equal(3, fake.Worker.Chunks.Count);
        var durations = fake.Worker.Chunks.Select(bytes =>
        {
            using var reader = new WaveFileReader(new MemoryStream(bytes));
            Assert.Equal(16000, reader.WaveFormat.SampleRate);
            Assert.Equal(1, reader.WaveFormat.Channels);
            return reader.TotalTime.TotalSeconds;
        }).ToArray();
        Assert.InRange(durations[0], 29.99, 30.01);
        Assert.InRange(durations[1], 31.99, 32.01);
        Assert.InRange(durations[2], 6.99, 7.01);
    }

    [Fact]
    public async Task ReviewedCorrectionsSurviveSilenceRecoveryAndCannotBeOverwrittenByFullRegeneration()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var edited = TranscriptCorrectionService.Save(session, Plan(session, 0), ["A reviewed correction."], CancellationToken.None);
        var muted = AudioEditPlan.Create(edited, session.Frames, [TranscriptPresentation.Rows(edited)[1]]).CreateTranscript();
        var restore = new AudioRestorePlan(muted, edited, session.Frames,
            [new(PcmFormat.SampleRate / 2, PcmFormat.SampleRate)], [new(PcmFormat.SampleRate / 2, PcmFormat.SampleRate)]);
        Assert.False(restore.RebuildsTranscript);
        Assert.True(restore.CreateTranscript().Segments[0].IsUserEdited);
        var before = EditedSessionTests.Hashes(session.Folder);
        var fake = new FakeTranscriber();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AiPipeline(localTranscriber: fake).TranscribeAsync(session,
                new() { SpeechModel = "base", SaveFolder = folder.Root },
                () => (session.Frames, false), new Progress<AiUpdate>(), CancellationToken.None));
        Assert.Equal(0, fake.OpenCount);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public void VocabularyIsOptionalSimplifiedDeduplicatedAndPrioritizesTheLatestSpelling()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("""{"SpeechModel":"small"}""")!;
        Assert.Empty(legacy.SpeechVocabularyTerms);
        Assert.Empty(SpeechVocabulary.CreatePrompt(legacy.SpeechVocabularyTerms).Text);
        var terms = SpeechVocabulary.Parse(" audiomemo\r\n\u5343\u554f\nAudioMemo\n");
        Assert.Equal(["\u5343\u95ee", "AudioMemo"], terms);
        var hint = SpeechVocabulary.CreatePrompt(terms);
        Assert.Equal("AudioMemo, \u5343\u95ee", hint.Text);
        Assert.Equal(2, hint.UsedTerms);
        Assert.Throws<InvalidDataException>(() => SpeechVocabulary.Normalize(["bad\nterm"]));
        Assert.Throws<InvalidDataException>(() => SpeechVocabulary.Normalize([new string('x', 161)]));
        Assert.Throws<InvalidDataException>(() => SpeechVocabulary.Normalize(Enumerable.Range(0, 201).Select(value => $"term{value}")));
    }

    [Fact]
    public void TheMaximumVocabularyFitsTheEditorIncludingWindowsLineBreaks()
    {
        var terms = Enumerable.Range(0, SpeechVocabulary.MaximumTerms)
            .Select(index => $"term{index:D3}".PadRight(SpeechVocabulary.MaximumTermBytes, 'x')).ToArray();
        var input = string.Join("\r\n", terms) + "\r\n";
        Assert.Equal(SpeechVocabulary.MaximumInputLength, input.Length);
        Assert.Equal(terms, SpeechVocabulary.Parse(input));
        Assert.Throws<InvalidDataException>(() => SpeechVocabulary.Parse(input + "x"));
    }

    [Theory]
    [InlineData(38, 2, 200)]
    [InlineData(39, 1, 39)]
    public void VocabularyPromptEnforcesTheExactConservativeByteBudget(int lastLength, int expectedTerms, int expectedBytes)
    {
        var hint = SpeechVocabulary.CreatePrompt([new string('a', 160), new string('b', lastLength)]);
        Assert.Equal(2, hint.TotalTerms);
        Assert.Equal(expectedTerms, hint.UsedTerms);
        Assert.Equal(expectedBytes, Encoding.UTF8.GetByteCount(hint.Text));
        Assert.InRange(Encoding.UTF8.GetByteCount(hint.Text), 0, SpeechVocabulary.MaximumPromptBytes);
        var chinese = SpeechVocabulary.CreatePrompt(Enumerable.Range(0, 12).Select(index => new string('\u8bcd', 20) + index));
        Assert.True(chinese.UsedTerms < chinese.TotalTerms);
        Assert.InRange(Encoding.UTF8.GetByteCount(chinese.Text), 1, SpeechVocabulary.MaximumPromptBytes);
    }

    internal static TranscriptCorrectionPlan Plan(RecordingSession session, params int[] rows)
    {
        var document = AppStorage.LoadTranscript(session);
        var available = TranscriptPresentation.Rows(document);
        return TranscriptCorrectionPlan.Create(document, session.Frames, rows.Select(index => available[index]).ToArray());
    }

    internal static SelectedTranscriptionService Service(FakeTranscriber fake) =>
        new(fake, (_, _) => Task.CompletedTask);

    internal sealed class FakeTranscriber : ILocalTranscriber
    {
        public string WorkerExecutable => "unused-fake-worker";
        public FakeWorker Worker { get; } = new();
        public SpeechModel? Model { get; private set; }
        public string? Language { get; private set; }
        public bool Draft { get; private set; }
        public IReadOnlyList<string>? Vocabulary { get; private set; }
        public int OpenCount { get; private set; }
        public Task<ISpeechWorker> OpenAsync(SpeechModel model, string language, string acceleration, bool draft,
            CancellationToken cancellationToken, IReadOnlyList<string>? vocabulary = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            Model = model;
            Language = language;
            Draft = draft;
            Vocabulary = vocabulary;
            return Task.FromResult<ISpeechWorker>(Worker);
        }
    }

    internal sealed class FakeWorker : ISpeechWorker
    {
        public string Runtime => "Cpu (synthetic test)";
        public string? StartupWarning => null;
        public bool Disposed { get; private set; }
        public List<string> Paths { get; } = [];
        public List<byte[]> Chunks { get; } = [];
        public FileStream? HeldAudio { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<string, int, CancellationToken, Task<IReadOnlyList<TranscriptSegment>>> Response { get; set; } =
            (_, number, _) => Task.FromResult<IReadOnlyList<TranscriptSegment>>([new(0, 0.5, $"Recognized selection {number}.")]);
        public Task<IReadOnlyList<TranscriptSegment>> TranscribeAsync(string wavePath, CancellationToken cancellationToken)
        {
            Paths.Add(wavePath);
            Chunks.Add(File.ReadAllBytes(wavePath));
            return Response(wavePath, Paths.Count, cancellationToken);
        }
        public ValueTask DisposeAsync()
        {
            HeldAudio?.Dispose();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Updates : IProgress<SelectedTranscriptionProgress>
    {
        public void Report(SelectedTranscriptionProgress value) { }
    }

    private sealed class SummaryCapture : ISummaryService
    {
        public string Text { get; private set; } = "";
        public string Previous { get; private set; } = "";
        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous,
            bool live, Action<string>? warning, CancellationToken cancellationToken)
        {
            Text = transcript;
            Previous = previous;
            return Task.FromResult("Fresh synthetic summary.");
        }

        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints,
            Action<string>? warning, CancellationToken cancellationToken) =>
            Task.FromResult("Fresh synthetic overall summary.");
    }
}
