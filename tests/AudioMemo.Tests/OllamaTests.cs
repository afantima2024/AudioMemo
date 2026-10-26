using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class OllamaTests
{
    [Theory]
    [InlineData("http://localhost:11434", "127.0.0.1")]
    [InlineData("http://127.0.0.1:11434/", "127.0.0.1")]
    [InlineData("http://127.0.0.2:12000", "127.0.0.2")]
    [InlineData("http://[::1]:11434", "::1")]
    public void OnlyLoopbackServersAreAccepted(string endpoint, string host)
    {
        var uri = SummaryProviders.LocalEndpoint(endpoint);
        Assert.True(uri.IsLoopback);
        Assert.Equal(host, uri.IdnHost);
    }

    [Theory]
    [InlineData("https://ollama.com")]
    [InlineData("http://192.168.1.10:11434")]
    [InlineData("http://0.0.0.0:11434")]
    [InlineData("http://localhost.example.com:11434")]
    [InlineData("http://user:secret@localhost:11434")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://127.0.0.1:11434?redirect=example.com")]
    [InlineData("http://127.0.0.1:11434#fragment")]
    [InlineData("file:///C:/models")]
    [InlineData("")]
    public void RemoteOrAmbiguousAddressesAreRejected(string endpoint) =>
        Assert.Throws<InvalidDataException>(() => SummaryProviders.LocalEndpoint(endpoint));

    [Fact]
    public void ExistingSettingsKeepCopilotAndLargeLocalModelsRemainAnOption()
    {
        var existing = JsonSerializer.Deserialize<AppSettings>("""{"SummaryModel":"old-model","CloudConsent":true}""")!;
        existing.Validate();
        Assert.Equal(SummaryProviders.Copilot, existing.SummaryProvider);
        Assert.Equal("old-model", existing.ActiveSummaryModel);
        Assert.True(existing.CloudConsent);
        var local = LocalSettings();
        local.OllamaModel = "deepseek-r1:671b";
        local.OllamaContextTokens = 262144;
        local.Validate();
        Assert.True(local.CanSummarize);
        Assert.False(local.CloudConsent);
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(local))!;
        Assert.Equal(local.OllamaModel, restored.ActiveSummaryModel);
        Assert.Equal(local.OllamaContextTokens, restored.OllamaContextTokens);
    }

    [Fact]
    public async Task LocalRequestsUseOnlyDownloadedModelsAndDoNotNeedCloudConsent()
    {
        using var handler = new OllamaHandler();
        using var client = new HttpClient(handler);
        var result = await new SummaryService(new OllamaService(client)).SummarizeBatchAsync(
            LocalSettings(), "A synthetic meeting on Friday.", "", false, null, CancellationToken.None);

        Assert.Equal("## Main points\nMeet on Friday.", result);
        Assert.Equal(["/api/tags", "/api/show", "/api/chat"], handler.Requests.Select(request => request.Path));
        using var chat = JsonDocument.Parse(handler.Requests[^1].Body);
        var body = chat.RootElement;
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.False(body.GetProperty("think").GetBoolean());
        Assert.False(body.TryGetProperty("tools", out _));
        Assert.Equal("unit-model:latest", body.GetProperty("model").GetString());
        Assert.Equal(32768, body.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Contains("untrusted meeting content", body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.DoesNotContain("private reasoning", result);
    }

    [Fact]
    public async Task OverallRequestsStayLocalAndUseTheSameInstalledModel()
    {
        using var handler = new OllamaHandler();
        using var client = new HttpClient(handler);
        var settings = LocalSettings();
        Assert.False(settings.CloudConsent);
        const string mainPoints = "## Decisions\n  - Friday was approved.";
        await new SummaryService(new OllamaService(client))
            .SummarizeOverallAsync(settings, mainPoints, null, CancellationToken.None);
        Assert.Equal(["/api/tags", "/api/show", "/api/chat"], handler.Requests.Select(request => request.Path));
        using var chat = JsonDocument.Parse(handler.Requests[^1].Body);
        Assert.Equal("unit-model:latest", chat.RootElement.GetProperty("model").GetString());
        var prompt = chat.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("Do not include recording timestamps", prompt);
        using var data = JsonDocument.Parse(prompt[prompt.IndexOf("{\"mainPoints\"", StringComparison.Ordinal)..]);
        Assert.Equal(mainPoints, data.RootElement.GetProperty("mainPoints").GetString());
        Assert.Single(data.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task CopilotOverallRequestsStillRequireSharingConsent() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SummaryService().SummarizeOverallAsync(new AppSettings { CloudConsent = false },
                "Synthetic main points.", null, CancellationToken.None));

    [Fact]
    public async Task StructuredReviewPreservesExactQuotesAndNormalizesOnlyParsedReasons()
    {
        const string quote = "\u4e7e";
        const string reason = "\u8acb\u78ba\u8a8d\u540d\u7a31";
        var response = JsonSerializer.Serialize(new { items = new[] { new { segment = 0, quote, reason } } },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using var handler = new OllamaHandler
        {
            Chat = JsonSerializer.Serialize(new { message = new { content = response }, done = true, done_reason = "stop" })
        };
        using var client = new HttpClient(handler);
        var output = await new SummaryService(new OllamaService(client)).GenerateStructuredPromptAsync(
            LocalSettings(), "Review synthetic text only.", null, CancellationToken.None);
        Assert.Equal(response, output);
        var item = Assert.Single(TranscriptReview.Parse(output, new([new(0, 0, "\u4e7e\u5764")])));
        Assert.Equal(quote, item.Quote);
        Assert.Equal(ChineseText.ToSimplified(reason), item.Reason);
        using var request = JsonDocument.Parse(handler.Requests[^1].Body);
        Assert.Equal("unit-model:latest", request.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task StructuredCopilotRequestsStillRequireSharingConsent() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SummaryService().GenerateStructuredPromptAsync(
            new AppSettings { CloudConsent = false }, "Synthetic review.", null, CancellationToken.None));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalSummariesAndModelTestsNormalizeTraditionalChinese(bool modelTest)
    {
        const string traditional = "## \u6703\u8b70\u8a18\u9304\nAlex \u5728\u9031\u4e94\u6e96\u5099 slides\u3002";
        const string simplified = "## \u4f1a\u8bae\u8bb0\u5f55\nAlex \u5728\u5468\u4e94\u51c6\u5907 slides\u3002";
        using var handler = new OllamaHandler
        {
            Chat = JsonSerializer.Serialize(new { message = new { content = traditional }, done = true, done_reason = "stop" })
        };
        using var client = new HttpClient(handler);
        var service = new OllamaService(client);
        var result = modelTest
            ? await service.TestAsync(LocalSettings(), null, CancellationToken.None)
            : await service.SummarizeAsync(LocalSettings(), traditional, "", false, null, CancellationToken.None);
        Assert.Equal(simplified, result.Text);
        using var chat = JsonDocument.Parse(handler.Requests[^1].Body);
        Assert.Contains("must use Simplified Chinese (zh-CN), never Traditional Chinese.",
            chat.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChineseConversionCannotExceedTheSavedSummarySizeLimit()
    {
        using var handler = new OllamaHandler
        {
            Chat = JsonSerializer.Serialize(new
            {
                message = new { content = new string('\u346e', 4001) },
                done = true,
                done_reason = "stop"
            })
        };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OllamaService(client).TestAsync(LocalSettings(), null, CancellationToken.None));
    }

    [Theory]
    [InlineData("remote_model", "a-model")]
    [InlineData("remote_host", "https://ollama.com")]
    public async Task CloudAliasesAreBlockedBeforeAnyTranscriptIsSent(string field, string value)
    {
        using var handler = new OllamaHandler
        {
            Tags = $$"""{"models":[{"name":"unit-model:latest","size":100,"{{field}}":"{{value}}"}]}"""
        };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OllamaService(client).TestAsync(LocalSettings(), null, CancellationToken.None));
        Assert.Equal("/api/tags", Assert.Single(handler.Requests).Path);
    }

    [Theory]
    [InlineData("qwen3:cloud")]
    [InlineData("deepseek-r1:8b-cloud")]
    [InlineData("organization/model-cloud:latest")]
    public async Task ExplicitCloudNamesAreRejectedWithoutRequests(string model)
    {
        using var handler = new OllamaHandler();
        using var client = new HttpClient(handler);
        var settings = LocalSettings();
        settings.OllamaModel = model;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OllamaService(client).TestAsync(settings, null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ARemoteShowResponseIsAlsoRejectedBeforeGeneration()
    {
        using var handler = new OllamaHandler { Details = """{"remote_host":"https://ollama.com","capabilities":["completion"]}""" };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OllamaService(client).TestAsync(LocalSettings(), null, CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/api/chat");
    }

    [Fact]
    public async Task MissingLocalModelDoesNotDownloadOrFallBackToCopilot()
    {
        using var handler = new OllamaHandler { Tags = """{"models":[]}""" };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SummaryService(new OllamaService(client)).SummarizeBatchAsync(
                LocalSettings(), "Synthetic text", "", false, null, CancellationToken.None));
        Assert.Equal("/api/tags", Assert.Single(handler.Requests).Path);
    }

    [Theory]
    [InlineData("""{"message":{"content":""},"done":true}""")]
    [InlineData("""{"message":{"content":"unfinished"},"done":false}""")]
    [InlineData("""{"message":{"content":"truncated"},"done":true,"done_reason":"length"}""")]
    [InlineData("""{"message":{"content":"tools","tool_calls":[{"function":{"name":"shell"}}]},"done":true}""")]
    [InlineData("""{"done":true}""")]
    [InlineData("<html>not an API response</html>")]
    public async Task InvalidOrIncompleteNotesAreNotAccepted(string response)
    {
        using var handler = new OllamaHandler { Chat = response };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OllamaService(client).TestAsync(LocalSettings(), null, CancellationToken.None));
    }

    [Fact]
    public async Task LegacyThinkingPrefixesAreNotSavedAsMeetingNotes()
    {
        using var handler = new OllamaHandler
        {
            Chat = """{"message":{"content":"<think>Internal reasoning</think>\nFinal notes."},"done":true}"""
        };
        using var client = new HttpClient(handler);
        var result = await new OllamaService(client).TestAsync(LocalSettings(), null, CancellationToken.None);
        Assert.Equal("Final notes.", result.Text);
    }

    [OllamaFact]
    [Trait("Category", "LocalLlm")]
    public async Task AnInstalledOllamaModelSummarizesOnlyTheBuiltInSyntheticMeeting()
    {
        var settings = LocalSettings();
        settings.OllamaModel = Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_OLLAMA_MODEL")!;
        settings.OllamaEndpoint = Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_OLLAMA_ENDPOINT")
            ?? SummaryProviders.DefaultOllamaEndpoint;
        var result = await new OllamaService().TestAsync(settings, null, CancellationToken.None);
        Assert.Contains("Friday", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Alex", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(result.Text.Length, 1, SummaryPrompt.MaximumPreviousSummaryCharacters);
    }

    [Fact]
    public async Task OversizedResponsesAreBounded()
    {
        using var handler = new OllamaHandler { Chat = new string('x', 4 * 1024 * 1024 + 1) };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OllamaService(client).TestAsync(LocalSettings(), null, CancellationToken.None));
    }

    public sealed class OllamaFactAttribute : FactAttribute
    {
        public OllamaFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_OLLAMA_MODEL")))
                Skip = "Install Ollama and a local model, then set AUDIOMEMO_TEST_OLLAMA_MODEL for the synthetic end-to-end check.";
        }
    }

    [Fact]
    public async Task ATooSmallContextDoesNotSilentlyTruncateTheInput()
    {
        using var handler = new OllamaHandler();
        using var client = new HttpClient(handler);
        var settings = LocalSettings();
        settings.OllamaContextTokens = 8192;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OllamaService(client).SummarizeAsync(
            settings, new string('\u4e2d', 4000), "", false, null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancellationReachesTheInFlightLocalRequest()
    {
        using var handler = new OllamaHandler { WaitForCancellation = true };
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var operation = new OllamaService(client).TestAsync(LocalSettings(), null, cancellation.Token);
        await handler.ChatStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(handler.RequestCancelled);
    }

    [Fact]
    public async Task ThirtySecondWarningAppearsBeforeARequestFinishes()
    {
        using var handler = new OllamaHandler { WaitForCancellation = true };
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var warnedWhileRunning = false;
        var message = new UiText("en")["LocalSlowRequest"];
        var operation = new OllamaService(client).TestAsync(LocalSettings(), warning =>
        {
            if (warning != message)
                return;
            warnedWhileRunning = !handler.RequestCancelled;
            cancellation.Cancel();
        }, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(warnedWhileRunning, "The 30-second warning must appear while local generation is still pending.");
        Assert.True(handler.RequestCancelled);
    }

    [Fact]
    public async Task SlowGenerationWarnsButStillReturnsTheSummary()
    {
        using var handler = new OllamaHandler
        {
            Chat = """{"message":{"content":"Useful notes."},"done":true,"eval_count":40,"eval_duration":20000000000}"""
        };
        using var client = new HttpClient(handler);
        var warnings = new List<string>();
        var result = await new OllamaService(client).TestAsync(LocalSettings(), warnings.Add, CancellationToken.None);
        Assert.Equal("Useful notes.", result.Text);
        Assert.Equal(2, result.TokensPerSecond);
        Assert.Contains(warnings, warning => warning.Contains("2.0", StringComparison.Ordinal));
    }

    [Fact]
    public void PerformanceWarningsUseExactThresholdsWithoutRestrictingModelChoices()
    {
        var text = new UiText("en");
        Assert.Null(LocalModelDiagnostics.SpeedWarning(TimeSpan.Zero, 5, text));
        Assert.NotNull(LocalModelDiagnostics.SpeedWarning(TimeSpan.Zero, 4.99, text));
        Assert.Null(LocalModelDiagnostics.SpeedWarning(TimeSpan.FromSeconds(29.99), null, text));
        Assert.NotNull(LocalModelDiagnostics.SpeedWarning(TimeSpan.FromSeconds(30), null, text));
        const ulong gib = 1024UL * 1024 * 1024;
        Assert.Null(LocalModelDiagnostics.MemoryWarning((long)gib, new(16 * gib, 5 * gib), text));
        Assert.NotNull(LocalModelDiagnostics.MemoryWarning((long)gib, new(16 * gib, 5 * gib - 1), text));
        Assert.NotNull(LocalModelDiagnostics.MemoryWarning((long)(377 * gib), new(128 * gib, 100 * gib), text));
    }

    [Fact]
    public void ModelGuideIncludesChineseModelsAndServerOptions()
    {
        var models = OllamaModelCatalog.Recommendations;
        Assert.Contains(models, model => model.Model.StartsWith("qwen", StringComparison.Ordinal));
        Assert.Contains(models, model => model.Model.StartsWith("deepseek", StringComparison.Ordinal));
        Assert.Contains(models, model => model.Model.StartsWith("glm", StringComparison.Ordinal));
        Assert.Contains(models, model => model.Model.EndsWith(":671b", StringComparison.Ordinal));
        Assert.All(models, model =>
        {
            Assert.Equal("https", new Uri(model.PageUrl).Scheme);
            Assert.Equal("ollama.com", new Uri(model.PageUrl).Host);
            Assert.Equal("ollama pull " + model.Model, model.Command);
            Assert.False(SummaryProviders.HasCloudTag(model.Model));
            Assert.NotEmpty(model.Label);
            Assert.Equal(model.Label, model.ToString());
        });
        Assert.Equal("unit-model", new OllamaModel("unit-model", 100, false).ToString());
    }

    internal static AppSettings LocalSettings() => new()
    {
        SummaryProvider = SummaryProviders.Ollama,
        OllamaModel = "unit-model",
        CloudConsent = false
    };

    private sealed class OllamaHandler : HttpMessageHandler
    {
        public string Tags { get; init; } = """{"models":[{"name":"unit-model:latest","size":1048576}]}""";
        public string Details { get; init; } = """{"capabilities":["completion"],"thinking":{"values":[false,true],"default":true}}""";
        public string Chat { get; init; } = """{"message":{"content":"## Main points\nMeet on Friday.","thinking":"private reasoning"},"done":true,"done_reason":"stop","eval_count":50,"eval_duration":1000000000}""";
        public bool WaitForCancellation { get; init; }
        public bool RequestCancelled { get; private set; }
        public TaskCompletionSource ChatStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotNull(request.RequestUri);
            Assert.True(request.RequestUri.IsLoopback);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri.AbsolutePath;
            Requests.Add((path, body));
            if (path == "/api/chat" && WaitForCancellation)
            {
                ChatStarted.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { RequestCancelled = true; throw; }
            }
            var json = path switch
            {
                "/api/tags" => Tags,
                "/api/show" => Details,
                "/api/chat" => Chat,
                _ => throw new InvalidOperationException("Unexpected local API route: " + path)
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
