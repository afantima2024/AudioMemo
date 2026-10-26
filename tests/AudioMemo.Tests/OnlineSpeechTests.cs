using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioMemo.AI;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class OnlineSpeechTests
{
    [Theory]
    [InlineData(OnlineSpeechCatalog.OpenAi, "https://api.openai.com/v1", "https://api.openai.com/v1/")]
    [InlineData(OnlineSpeechCatalog.Qwen, "https://dashscope.aliyuncs.com/compatible-mode/v1", "https://dashscope.aliyuncs.com/compatible-mode/v1/")]
    [InlineData(OnlineSpeechCatalog.Qwen, "https://w123.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", "https://w123.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/")]
    public void SupportedDestinationsAreCanonical(string provider, string endpoint, string expected) =>
        Assert.Equal(expected, OnlineSpeechCatalog.Endpoint(provider, endpoint).AbsoluteUri);

    [Theory]
    [InlineData(OnlineSpeechCatalog.OpenAi, "http://api.openai.com/v1")]
    [InlineData(OnlineSpeechCatalog.OpenAi, "https://api.openai.com.evil.example/v1")]
    [InlineData(OnlineSpeechCatalog.OpenAi, "https://user@api.openai.com/v1")]
    [InlineData(OnlineSpeechCatalog.OpenAi, "https://api.openai.com/v1?secret=invalid")]
    [InlineData(OnlineSpeechCatalog.OpenAi, "https://api.openai.com/v1#fragment")]
    [InlineData(OnlineSpeechCatalog.OpenAi, "https://api.openai.com:8443/v1")]
    [InlineData(OnlineSpeechCatalog.Qwen, "https://dashscope-us.aliyuncs.com/compatible-mode/v1")]
    [InlineData(OnlineSpeechCatalog.Qwen, "https://workspace.us-east-1.maas.aliyuncs.com/compatible-mode/v1")]
    [InlineData(OnlineSpeechCatalog.Qwen, "https://api.openai.com/v1")]
    [InlineData("deepseek", "https://api.deepseek.com/v1")]
    public void NonSpeechProvidersAndUnapprovedDestinationsAreRejected(string provider, string endpoint) =>
        Assert.Throws<InvalidDataException>(() => OnlineSpeechCatalog.Endpoint(provider, endpoint));

    [Fact]
    public void LegacyPreferencesDoNotEnableAudioUploadsAndKeysRemainEndpointScoped()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"CloudConsent":true}""")!;
        settings.Validate();
        Assert.Equal(210, settings.HistoryWidth);
        Assert.Empty(settings.OnlineSpeech.Profiles);
        Assert.Throws<InvalidOperationException>(() => CloudApiKey.ReadOnline(settings));
        var profile = settings.GetCloudProfile(CloudLlmCatalog.Qwen);
        CloudApiKey.Set(CloudLlmCatalog.Qwen, profile, CloudLlmTests.TestKey);
        Assert.Equal(CloudLlmTests.TestKey, CloudApiKey.ReadOnline(settings));
        var speech = settings.OnlineSpeech.GetProfile();
        speech.Endpoint = OnlineSpeechCatalog.Endpoints(OnlineSpeechCatalog.Qwen)[1];
        Assert.Throws<InvalidOperationException>(() => CloudApiKey.ReadOnline(settings));
        CloudApiKey.SetOnline(settings.OnlineSpeech, "synthetic-region-two-key");
        Assert.Equal("synthetic-region-two-key", CloudApiKey.ReadOnline(settings));
        var serialized = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain("synthetic-region-two-key", serialized);
        Assert.DoesNotContain(CloudLlmTests.TestKey, serialized);
        Assert.Equal("synthetic-region-two-key", CloudApiKey.ReadOnline(JsonSerializer.Deserialize<AppSettings>(serialized)!));
        settings.OnlineSpeech.Provider = OnlineSpeechCatalog.OpenAi;
        Assert.Throws<InvalidOperationException>(() => CloudApiKey.ReadOnline(settings));
        var openai = settings.OnlineSpeech.GetProfile();
        openai.ProtectedApiKeys[openai.Endpoint] = speech.ProtectedApiKeys[speech.Endpoint];
        Assert.Throws<InvalidOperationException>(() => CloudApiKey.ReadOnline(settings));
        openai.Model = "deepseek-chat";
        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void ExactPartLimitsAccountForEncodingOverheadAndRequireExplicitSplitting()
    {
        foreach (var model in OnlineSpeechCatalog.Models)
        {
            Assert.True(model.Fits(model.MaximumFrames));
            Assert.False(model.Fits(model.MaximumFrames + 1));
            Assert.InRange(model.EstimatedBytes(model.MaximumFrames), model.MaximumInputBytes - 16, model.MaximumInputBytes);
        }
        var qwen = OnlineSpeechCatalog.Models[0];
        Assert.Equal(300, qwen.MaximumSeconds);
        Assert.Equal(10_000_000, qwen.MaximumInputBytes);
        Assert.InRange(qwen.MaximumFrames / (double)PcmFormat.SampleRate, 234, 234.5);
        var source = AudioEditPlanTests.Document(700,
            new(0, 400, "Long selected segment."),
            new(400, 600, "DO NOT UPLOAD THIS GAP.") { SpeakerId = "spkr2" },
            new(600, 700, "Later selected segment."));
        var selection = TranscriptCorrectionPlan.Create(source, source.ProcessedFrames,
            [TranscriptPresentation.Rows(source)[0], TranscriptPresentation.Rows(source)[2]]);
        var blocked = new OnlineTranscriptionPlan(selection, qwen, false);
        Assert.True(blocked.RequiresSplitting);
        Assert.False(blocked.Allowed);
        Assert.Throws<InvalidOperationException>(blocked.EnsureAllowed);
        var allowed = new OnlineTranscriptionPlan(selection, qwen, true);
        Assert.True(allowed.Allowed);
        Assert.Equal(3, allowed.Chunks.Count);
        Assert.Equal(500L * PcmFormat.SampleRate, allowed.SelectedFrames);
        Assert.Equal(allowed.SelectedFrames, allowed.Chunks.Sum(chunk => chunk.Frames));
        Assert.All(allowed.Chunks, chunk =>
        {
            Assert.True(qwen.Fits(chunk.Frames));
            Assert.True(chunk.StartFrame + chunk.Frames <= 400L * PcmFormat.SampleRate || chunk.StartFrame >= 600L * PcmFormat.SampleRate);
        });
        var larger = new OnlineTranscriptionPlan(selection, OnlineSpeechCatalog.Models[1], false);
        Assert.True(larger.Allowed);
        Assert.Equal(2, larger.Chunks.Count);
    }

    [Theory]
    [InlineData(OnlineSpeechCatalog.Qwen, "zh")]
    [InlineData(OnlineSpeechCatalog.Qwen, "auto")]
    [InlineData(OnlineSpeechCatalog.OpenAi, "en")]
    [InlineData(OnlineSpeechCatalog.OpenAi, "auto")]
    public async Task AudioRequestsUseExactSelectedRangesWithoutTextNotesOrFileNames(string provider, string language)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0, 2);
        var settings = Settings(provider, folder.Root);
        settings.Language = language;
        settings.SpeechVocabularyTerms = ["PRIVATE VOCABULARY"];
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var work = folder.File("work");
        var result = await new SelectedOnlineTranscriptionService(new(client), work).GenerateAsync(session, plan,
            settings, false, settings.OnlineSpeech.GetProfile().Endpoint, new Updates(), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(["Online selection 1.", "Online selection 2."], result.Texts);
        Assert.Empty(result.Unrecognized);
        Assert.Empty(Directory.GetFiles(work));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        for (var index = 0; index < handler.Requests.Count; index++)
        {
            var request = handler.Requests[index];
            Assert.Equal(CloudLlmTests.TestKey, request.Key);
            Assert.Equal(settings.OnlineSpeech.GetProfile().Model, request.Fields["model"]);
            Assert.DoesNotContain(session.Title, request.Metadata);
            Assert.DoesNotContain("PRIVATE", request.Metadata);
            Assert.DoesNotContain("Keep the first", request.Metadata);
            Assert.DoesNotContain(CloudLlmTests.TestKey, request.Metadata);
            Assert.True(request.Length <= OnlineSpeechCatalog.Get(provider, settings.OnlineSpeech.GetProfile().Model).MaximumInputBytes);
            if (provider == OnlineSpeechCatalog.OpenAi)
            {
                Assert.Equal("/v1/audio/transcriptions", request.Uri.AbsolutePath);
                Assert.Equal("selection.wav", request.FileName);
                Assert.False(request.Fields.ContainsKey("language"));
                Assert.Equal(language != "auto", request.Fields.ContainsKey("languages[]"));
                if (language != "auto")
                    Assert.Equal(language, request.Fields["languages[]"]);
            }
            else
            {
                Assert.Equal("/compatible-mode/v1/chat/completions", request.Uri.AbsolutePath);
                Assert.Equal(language != "auto", request.Fields.ContainsKey("language"));
                Assert.Contains("\"enable_itn\":false", request.Metadata);
                Assert.Contains("\"stream\":false", request.Metadata);
            }
            var expected = folder.File($"expected-{index}.wav");
            var segment = plan.Segments[index];
            AudioExport.WriteSpeechChunk(session.WavePath, expected,
                AudioEditPlan.FrameAt(segment.StartSeconds, session.Frames),
                AudioEditPlan.FrameAt(segment.EndSeconds, session.Frames) - AudioEditPlan.FrameAt(segment.StartSeconds, session.Frames),
                CancellationToken.None);
            Assert.Equal(File.ReadAllBytes(expected), request.Audio);
        }
    }

    [Fact]
    public async Task ConsentAndActualFileLimitsBlockBeforeAnyHttpCall()
    {
        using var folder = new TestFolder();
        var settings = Settings(OnlineSpeechCatalog.Qwen, folder.Root);
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var service = new OnlineSpeechService(client);
        var endpoint = settings.OnlineSpeech.GetProfile().Endpoint;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranscribeAsync(settings, "missing.wav", 48000, "", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranscribeAsync(settings, "missing.wav", 48000,
            OnlineSpeechCatalog.Endpoints(OnlineSpeechCatalog.Qwen)[1], CancellationToken.None));
        var path = folder.File("oversized.wav");
        using (var stream = File.Create(path))
            stream.SetLength(7_500_000);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranscribeAsync(settings, path, 48000, endpoint, CancellationToken.None));
        settings.OnlineSpeech.Provider = OnlineSpeechCatalog.OpenAi;
        CloudApiKey.SetOnline(settings.OnlineSpeech, CloudLlmTests.TestKey);
        using (var stream = File.Create(path))
            stream.SetLength(25_000_001);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranscribeAsync(settings, path, 48000,
            settings.OnlineSpeech.GetProfile().Endpoint, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InvalidAudioMetadataIsRejectedBeforePlanningMillionsOfRequests()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = Settings(OnlineSpeechCatalog.Qwen, folder.Root);
        const double seconds = 2_000_000_000;
        session.Frames = (long)seconds * PcmFormat.SampleRate;
        AppStorage.SaveTranscript(session, AudioEditPlanTests.Document(seconds,
            new TranscriptSegment(0, seconds, "Invalid claimed duration.")));
        var plan = TranscriptCorrectionTests.Plan(session, 0);
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SelectedOnlineTranscriptionService(new(client), folder.File("work")).GenerateAsync(
                session, plan, settings, true, settings.OnlineSpeech.GetProfile().Endpoint, new Updates(), CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.False(Directory.Exists(folder.File("work")));
    }

    [Fact]
    public async Task LargeRowsSplitOnlyWithApprovalAndEveryActualRequestFits()
    {
        using var folder = new TestFolder();
        var settings = Settings(OnlineSpeechCatalog.Qwen, folder.Root);
        var session = AppStorage.CreateSession(settings, "Synthetic long split");
        using (var writer = new DurableWaveFile(session.WavePath))
        {
            var second = new byte[PcmFormat.BytesPerSecond];
            for (var index = 0; index < 250; index++)
                writer.Write(second);
            session.Frames = writer.FramesWritten;
        }
        session.Status = "Saved";
        AppStorage.SaveTranscript(session, AudioEditPlanTests.Document(250, new TranscriptSegment(0, 250, "Original long row.")));
        var plan = TranscriptCorrectionTests.Plan(session, 0);
        var before = EditedSessionTests.Hashes(session.Folder);
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var service = new SelectedOnlineTranscriptionService(new(client), folder.File("work"));
        var endpoint = settings.OnlineSpeech.GetProfile().Endpoint;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAsync(session, plan, settings, false, endpoint, new Updates(), CancellationToken.None));
        Assert.Empty(handler.Requests);
        var result = await service.GenerateAsync(session, plan, settings, true, endpoint, new Updates(), CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Online selection 1." + Environment.NewLine + "Online selection 2.", Assert.Single(result.Texts));
        Assert.All(handler.Requests, request => Assert.InRange(request.Length, 1, 10_000_000));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        Assert.Empty(Directory.GetFiles(folder.File("work")));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(402)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(413)]
    [InlineData(307)]
    [InlineData(500)]
    public async Task HttpFailuresDoNotExposeBodiesRetryOrPublishPartialText(int status)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var settings = Settings(OnlineSpeechCatalog.OpenAi, folder.Root);
        using var handler = new Handler
        {
            Response = (number, _) => Task.FromResult(number == 1 ? Reply("{\"text\":\"First preview only.\"}") :
                Reply("PRIVATE BODY " + CloudLlmTests.TestKey, (HttpStatusCode)status))
        };
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SelectedOnlineTranscriptionService(new(client), folder.File("work")).GenerateAsync(session,
                TranscriptCorrectionTests.Plan(session, 0, 2), settings, false, settings.OnlineSpeech.GetProfile().Endpoint,
                new Updates(), CancellationToken.None));
        Assert.DoesNotContain("PRIVATE BODY", error.Message);
        Assert.DoesNotContain(CloudLlmTests.TestKey, error.Message);
        if (status is 400 or 404)
            Assert.Contains(new UiText(settings.UiLanguage)["OnlineSpeechRequestRejected"], error.Message);
        if (status == 413)
            Assert.Contains(new UiText(settings.UiLanguage)["CloudRequestTooLarge"], error.Message);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        Assert.Empty(Directory.GetFiles(folder.File("work")));
    }

    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":"cut off"}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"text","tool_calls":[]}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"refusal":"No","content":"text"}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":42}}]}""")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"error":{"message":"PRIVATE ERROR"}}""")]
    [InlineData("not-json")]
    public async Task InvalidOrTruncatedResponsesCannotBeUsed(string response)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = Settings(OnlineSpeechCatalog.Qwen, folder.Root);
        using var handler = new Handler { Response = (_, _) => Task.FromResult(Reply(response)) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SelectedOnlineTranscriptionService(new(client), folder.File("work")).GenerateAsync(session,
                TranscriptCorrectionTests.Plan(session, 0), settings, false, settings.OnlineSpeech.GetProfile().Endpoint,
                new Updates(), CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Empty(Directory.GetFiles(folder.File("work")));
    }

    [Fact]
    public async Task CancellationStopsHttpAndCleansTemporaryAudio()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var settings = Settings(OnlineSpeechCatalog.OpenAi, folder.Root);
        using var handler = new Handler
        {
            Response = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Reply("{}"); }
        };
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var pending = new SelectedOnlineTranscriptionService(new(client), folder.File("work")).GenerateAsync(session,
            TranscriptCorrectionTests.Plan(session, 0, 2), settings, false, settings.OnlineSpeech.GetProfile().Endpoint,
            new Updates(), cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(handler.Requests);
        Assert.Empty(Directory.GetFiles(folder.File("work")));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutsAndExcessiveResponsesRemainBoundedAndDoNotRetry(bool timeout)
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = Settings(OnlineSpeechCatalog.OpenAi, folder.Root);
        using var handler = new Handler
        {
            Response = async (_, token) =>
            {
                if (timeout)
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Reply(new string('x', 4 * 1024 * 1024 + 1));
            }
        };
        using var client = new HttpClient(handler);
        var service = new SelectedOnlineTranscriptionService(new(client, TimeSpan.FromMilliseconds(150)), folder.File("work"));
        var error = await Record.ExceptionAsync(() => service.GenerateAsync(session, TranscriptCorrectionTests.Plan(session, 0),
            settings, false, settings.OnlineSpeech.GetProfile().Endpoint, new Updates(), CancellationToken.None));
        if (timeout)
            Assert.IsType<TimeoutException>(error);
        else
            Assert.IsType<InvalidDataException>(error);
        Assert.Single(handler.Requests);
        Assert.Empty(Directory.GetFiles(folder.File("work")));
    }

    [Fact]
    public async Task EmptyRecognitionIsExplicitAndSimplifiedResultsAreNotAppliedAutomatically()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var settings = Settings(OnlineSpeechCatalog.OpenAi, folder.Root);
        using var handler = new Handler
        {
            Response = (number, _) => Task.FromResult(Reply(number == 1 ? """{"text":""}""" : """{"text":"\u6703\u8b70\u8a18\u9304"}"""))
        };
        using var client = new HttpClient(handler);
        var result = await new SelectedOnlineTranscriptionService(new(client), folder.File("work")).GenerateAsync(session,
            TranscriptCorrectionTests.Plan(session, 0, 2), settings, false, settings.OnlineSpeech.GetProfile().Endpoint,
            new Updates(), CancellationToken.None);
        Assert.Equal([0], result.Unrecognized);
        Assert.Equal(["", "\u4f1a\u8bae\u8bb0\u5f55"], result.Texts);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task ConcurrentTranscriptChangesStopBeforeTheNextPaidRequest()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var settings = Settings(OnlineSpeechCatalog.OpenAi, folder.Root);
        using var handler = new Handler
        {
            Response = (_, _) =>
            {
                Assert.Throws<IOException>(() => File.Open(session.WavePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
                var changed = AppStorage.LoadTranscript(session);
                changed.Segments[0] = changed.Segments[0] with { Text = "External edit" };
                AppStorage.SaveTranscript(session, changed);
                return Task.FromResult(Reply("""{"text":"Unused online preview"}"""));
            }
        };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SelectedOnlineTranscriptionService(new(client), folder.File("work")).GenerateAsync(session,
                TranscriptCorrectionTests.Plan(session, 0, 2), settings, false, settings.OnlineSpeech.GetProfile().Endpoint,
                new Updates(), CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.Equal("External edit", AppStorage.LoadTranscript(session).Segments[0].Text);
    }

    [Fact]
    public void StoredComparisonsReopenSeparatelyAndRejectStaleAudio()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0, 2);
        var before = EditedSessionTests.Hashes(session.Folder);
        var comparison = Comparison(session, plan);
        OnlineComparisonStore.Save(session, plan, comparison, CancellationToken.None);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        var restored = OnlineComparisonStore.Load(session, plan)!;
        Assert.Equal(comparison.Rows, restored.Rows);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(session.Folder, "online-comparisons")));
        Assert.DoesNotContain("ProtectedApiKeys", File.ReadAllText(file));
        Assert.DoesNotContain("PRIVATE NOTE", File.ReadAllText(file));
        Assert.DoesNotContain("base64", File.ReadAllText(file));
        Assert.Null(OnlineComparisonStore.Load(session, TranscriptCorrectionTests.Plan(session, 1)));
        var saved = TranscriptCorrectionService.Save(session, plan, restored.Rows.Select(row => row.OnlineText).ToArray(),
            CancellationToken.None, restored.AudioVersion);
        Assert.Equal("Online 0", saved.Segments[0].Text);
        var updatedPlan = TranscriptCorrectionTests.Plan(session, 0, 2);
        Assert.NotNull(OnlineComparisonStore.Load(session, updatedPlan));
        File.SetLastWriteTimeUtc(session.WavePath, comparison.AudioVersion.LastWriteUtc.AddSeconds(5));
        Assert.Throws<InvalidOperationException>(() => OnlineComparisonStore.Load(session, updatedPlan));
    }

    internal static AppSettings Settings(string provider, string folder)
    {
        var settings = new AppSettings { SaveFolder = folder };
        settings.OnlineSpeech.Provider = provider;
        CloudApiKey.SetOnline(settings.OnlineSpeech, CloudLlmTests.TestKey);
        return settings;
    }

    [Fact]
    public void ComparisonCachePreservesMissingLocalResultsAndReadsOlderFilesWithoutTheFlag()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        var plan = TranscriptCorrectionTests.Plan(session, 0, 2);
        var comparison = Comparison(session, plan);
        comparison.Rows[0] = comparison.Rows[0] with { LocalUnrecognized = true };
        comparison.Rows[1] = comparison.Rows[1] with { OnlineText = "" };
        OnlineComparisonStore.Save(session, plan, comparison, CancellationToken.None);
        var restored = OnlineComparisonStore.Load(session, plan)!;
        Assert.True(restored.Rows[0].LocalUnrecognized);
        Assert.False(restored.Rows[1].LocalUnrecognized);
        Assert.Equal("", restored.Rows[1].OnlineText);

        var legacy = JsonSerializer.SerializeToNode(comparison)!;
        foreach (var row in legacy["Rows"]!.AsArray())
            Assert.True(row!.AsObject().Remove("LocalUnrecognized"));
        var path = Assert.Single(Directory.GetFiles(Path.Combine(session.Folder, "online-comparisons")));
        File.WriteAllText(path, legacy.ToJsonString());
        restored = OnlineComparisonStore.Load(session, plan)!;
        Assert.All(restored.Rows, row => Assert.False(row.LocalUnrecognized));
        Assert.Equal(comparison.Rows.Select(row => row.LocalText), restored.Rows.Select(row => row.LocalText));
    }

    internal static OnlineComparison Comparison(RecordingSession session, TranscriptCorrectionPlan plan) =>
        new(session.Id, session.Frames, AudioSourceVersion.Read(session.WavePath), OnlineSpeechCatalog.Qwen, "qwen3-asr-flash",
            OnlineSpeechCatalog.Endpoints(OnlineSpeechCatalog.Qwen)[0], DateTimeOffset.Now,
            plan.Segments.Select((segment, index) => new OnlineComparisonRow(plan.Indices[index],
                AudioEditPlan.FrameAt(segment.StartSeconds, session.Frames), AudioEditPlan.FrameAt(segment.EndSeconds, session.Frames),
                segment.Text, $"Online {index}")).ToList());

    internal static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    internal sealed record Request(Uri Uri, string? Key, Dictionary<string, string> Fields, byte[] Audio,
        string? FileName, long Length, string Metadata);

    internal sealed class Handler : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public Func<int, CancellationToken, Task<HttpResponseMessage>>? Response { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            var fields = new Dictionary<string, string>();
            byte[] audio = [];
            string? fileName = null;
            string metadata;
            if (message.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (name == "file")
                    {
                        audio = await part.ReadAsByteArrayAsync(token);
                        fileName = part.Headers.ContentDisposition.FileName!.Trim('"');
                    }
                    else
                        fields[name] = await part.ReadAsStringAsync(token);
                }
                metadata = JsonSerializer.Serialize(fields);
            }
            else
            {
                var json = JsonNode.Parse(await message.Content!.ReadAsStringAsync(token))!;
                var item = json["messages"]![0]!["content"]![0]!["input_audio"]!;
                var value = item["data"]!.GetValue<string>();
                Assert.StartsWith("data:audio/wav;base64,", value);
                audio = Convert.FromBase64String(value[(value.IndexOf(',') + 1)..]);
                item["data"] = "[audio omitted from test metadata]";
                fields["model"] = json["model"]!.GetValue<string>();
                if (json["asr_options"]!["language"] is { } language)
                    fields["language"] = language.GetValue<string>();
                metadata = json.ToJsonString();
            }
            Requests.Add(new(message.RequestUri!, message.Headers.Authorization?.Parameter, fields, audio, fileName,
                message.Content!.Headers.ContentLength!.Value, metadata));
            Started.TrySetResult();
            if (Response is not null)
                return await Response(Requests.Count, token);
            var text = $"Online selection {Requests.Count}.";
            return Reply(message.RequestUri!.AbsolutePath.EndsWith("chat/completions", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = text } } } })
                : JsonSerializer.Serialize(new { text }));
        }
    }

    private sealed class Updates : IProgress<SelectedTranscriptionProgress>
    {
        public void Report(SelectedTranscriptionProgress value) { }
    }
}
