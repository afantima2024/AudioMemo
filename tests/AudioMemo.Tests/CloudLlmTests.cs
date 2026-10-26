using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class CloudLlmTests
{
    internal const string TestKey = "synthetic-unit-api-key-not-a-real-credential";

    [Theory]
    [InlineData(CloudLlmCatalog.Doubao, "https://ark.cn-beijing.volces.com/api/v3", "/api/v3/")]
    [InlineData(CloudLlmCatalog.Qwen, "https://dashscope.aliyuncs.com/compatible-mode/v1", "/compatible-mode/v1/")]
    [InlineData(CloudLlmCatalog.Qwen, "https://dashscope-intl.aliyuncs.com/compatible-mode/v1/", "/compatible-mode/v1/")]
    [InlineData(CloudLlmCatalog.Qwen, "https://workspace-123.cn-beijing.maas.aliyuncs.com/compatible-mode/v1", "/compatible-mode/v1/")]
    [InlineData(CloudLlmCatalog.Qwen, "https://workspace-123.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/", "/compatible-mode/v1/")]
    [InlineData(CloudLlmCatalog.DeepSeek, "https://api.deepseek.com", "/")]
    [InlineData(CloudLlmCatalog.DeepSeek, "https://api.deepseek.com/v1", "/v1/")]
    public void OfficialEndpointsAreCanonicalAndRegionAware(string provider, string endpoint, string path)
    {
        var uri = CloudLlmCatalog.Endpoint(provider, endpoint);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal(443, uri.Port);
        Assert.Equal(path, uri.AbsolutePath);
    }

    [Theory]
    [InlineData(CloudLlmCatalog.DeepSeek, "http://api.deepseek.com")]
    [InlineData(CloudLlmCatalog.DeepSeek, "https://api.deepseek.com.example.com")]
    [InlineData(CloudLlmCatalog.DeepSeek, "https://example.com")]
    [InlineData(CloudLlmCatalog.DeepSeek, "https://api.deepseek.com:8443")]
    [InlineData(CloudLlmCatalog.DeepSeek, "https://api.deepseek.com?key=not-a-key")]
    [InlineData(CloudLlmCatalog.DeepSeek, "https://api.deepseek.com/#fragment")]
    [InlineData(CloudLlmCatalog.DeepSeek, "https://api.deepseek.com/chat/completions")]
    [InlineData(CloudLlmCatalog.Doubao, "https://api.deepseek.com/v1")]
    [InlineData(CloudLlmCatalog.Qwen, "https://workspace.cn-beijing.maas.aliyuncs.com.example.com/compatible-mode/v1")]
    [InlineData(CloudLlmCatalog.Qwen, "https://workspace.cn-beijing.maas.aliyuncs.com/other")]
    [InlineData(CloudLlmCatalog.Qwen, "https://workspace.unknown.maas.aliyuncs.com/compatible-mode/v1")]
    [InlineData(CloudLlmCatalog.Qwen, "https://a.b.cn-beijing.maas.aliyuncs.com/compatible-mode/v1")]
    [InlineData(CloudLlmCatalog.Qwen, "https://127.0.0.1/compatible-mode/v1")]
    public void ArbitraryOrAmbiguousDestinationsAreRejected(string provider, string endpoint) =>
        Assert.Throws<InvalidDataException>(() => CloudLlmCatalog.Endpoint(provider, endpoint));

    [Fact]
    public void LegacyCopilotConsentDoesNotAuthorizeAnyNewCloudProvider()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"SummaryModel":"existing-model","CloudConsent":true}""")!;
        settings.Validate();
        Assert.True(settings.CanSummarize);
        foreach (var provider in CloudLlmCatalog.Providers)
        {
            settings.SummaryProvider = provider.Id;
            Assert.False(settings.CanSummarize);
            Assert.Empty(settings.GetCloudProfile().ProtectedApiKeys);
        }
        settings.SummaryProvider = SummaryProviders.Copilot;
        Assert.Equal("existing-model", settings.ActiveSummaryModel);
        Assert.True(settings.CanSummarize);
        settings.SummaryProvider = SummaryProviders.Ollama;
        Assert.True(settings.CanSummarize);
    }

    [Fact]
    public void ApiKeysAreProtectedAndBoundToTheProviderAndCanonicalEndpoint()
    {
        var settings = CloudSettings(CloudLlmCatalog.Qwen);
        var profile = settings.GetCloudProfile();
        var originalEndpoint = settings.ActiveSummaryEndpoint;
        var serialized = JsonSerializer.Serialize(settings);
        Assert.DoesNotContain(TestKey, serialized);
        var restored = JsonSerializer.Deserialize<AppSettings>(serialized)!;
        Assert.Equal(TestKey, CloudApiKey.Read(restored.SummaryProvider, restored.GetCloudProfile()));
        profile.Endpoint = CloudLlmCatalog.Get(CloudLlmCatalog.Qwen).Endpoints[1];
        Assert.False(CloudApiKey.IsConfigured(settings.SummaryProvider, profile));
        Assert.False(settings.CanSummarize);
        profile.ProtectedApiKeys[settings.ActiveSummaryEndpoint] = profile.ProtectedApiKeys[originalEndpoint];
        Assert.Throws<InvalidOperationException>(() => CloudApiKey.Read(settings.SummaryProvider, profile));
        CloudApiKey.Set(settings.SummaryProvider, profile, "synthetic-second-region-key");
        Assert.Equal("synthetic-second-region-key", CloudApiKey.Read(settings.SummaryProvider, profile));
        CloudApiKey.Forget(settings.SummaryProvider, profile);
        Assert.False(CloudApiKey.IsConfigured(settings.SummaryProvider, profile));
        profile.Endpoint = originalEndpoint;
        Assert.Equal(TestKey, CloudApiKey.Read(settings.SummaryProvider, profile));
        var other = settings.GetCloudProfile(CloudLlmCatalog.DeepSeek);
        other.ProtectedApiKeys[other.Endpoint] = profile.ProtectedApiKeys[originalEndpoint];
        Assert.Throws<InvalidOperationException>(() => CloudApiKey.Read(CloudLlmCatalog.DeepSeek, other));
    }

    [Theory]
    [InlineData(CloudLlmCatalog.Doubao, "/api/v3/chat/completions")]
    [InlineData(CloudLlmCatalog.Qwen, "/compatible-mode/v1/chat/completions")]
    [InlineData(CloudLlmCatalog.DeepSeek, "/chat/completions")]
    public async Task EveryProviderUsesItsOwnOfficialApiAndOnlyFinalSimplifiedNotes(string provider, string path)
    {
        var settings = CloudSettings(provider);
        using var handler = new ApiHandler
        {
            ResponseBody = """{"choices":[{"finish_reason":"stop","message":{"content":"## \u6703\u8b70\u8a18\u9304\nFriday.","reasoning_content":"Reasoning must not be saved."}}]}"""
        };
        using var client = new HttpClient(handler);
        var result = await new SummaryService(cloud: new CloudLlmService(client)).SummarizeBatchAsync(
            settings, "Synthetic meeting text.", "", false, null, CancellationToken.None);
        Assert.Equal("## \u4f1a\u8bae\u8bb0\u5f55\nFriday.", result);
        Assert.DoesNotContain("Reasoning", result);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(path, request.Uri.AbsolutePath);
        Assert.Equal(new Uri(settings.ActiveSummaryEndpoint).Host, request.Uri.Host);
        Assert.Equal("Bearer", request.Scheme);
        Assert.Equal(TestKey, request.Key);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal("unit-model", json.RootElement.GetProperty("model").GetString());
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(4096, json.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("tools", out _));
        Assert.False(json.RootElement.TryGetProperty("web_search", out _));
        Assert.Contains("untrusted meeting content", Prompt(json));
        Assert.Contains("Simplified Chinese", Prompt(json));
        if (provider == CloudLlmCatalog.Qwen)
            Assert.False(json.RootElement.GetProperty("enable_thinking").GetBoolean());
        else
            Assert.Equal("disabled", json.RootElement.GetProperty("thinking").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(CloudLlmCatalog.Doubao)]
    [InlineData(CloudLlmCatalog.Qwen)]
    [InlineData(CloudLlmCatalog.DeepSeek)]
    public async Task StructuredReviewPreservesExactQuotesWithTheSameProviderAndConsent(string provider)
    {
        const string quote = "\u4e7e";
        const string reason = "\u8acb\u78ba\u8a8d\u540d\u7a31";
        var response = JsonSerializer.Serialize(new { items = new[] { new { segment = 0, quote, reason } } },
            new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var settings = CloudSettings(provider);
        using var handler = new ApiHandler
        {
            ResponseBody = JsonSerializer.Serialize(new { choices = new[]
                { new { finish_reason = "stop", message = new { content = response } } } })
        };
        using var client = new HttpClient(handler);
        var service = new SummaryService(cloud: new CloudLlmService(client));
        var output = await service.GenerateStructuredPromptAsync(settings, "Review synthetic text only.", null,
            CancellationToken.None);
        Assert.Equal(response, output);
        var item = Assert.Single(TranscriptReview.Parse(output, new([new(0, 0, "\u4e7e\u5764")])));
        Assert.Equal(quote, item.Quote);
        Assert.Equal(ChineseText.ToSimplified(reason), item.Reason);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(settings.ActiveSummaryEndpoint).Host, request.Uri.Host);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal(settings.ActiveSummaryModel, body.RootElement.GetProperty("model").GetString());
        settings.GetCloudProfile().ConsentEndpoint = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateStructuredPromptAsync(
            settings, "PRIVATE TRANSCRIPT", null, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(CloudLlmCatalog.Doubao)]
    [InlineData(CloudLlmCatalog.Qwen)]
    [InlineData(CloudLlmCatalog.DeepSeek)]
    public async Task MissingConsentOrCredentialsCannotSendTranscriptText(string provider)
    {
        var settings = CloudSettings(provider);
        using var handler = new ApiHandler();
        using var client = new HttpClient(handler);
        var service = new CloudLlmService(client);
        settings.GetCloudProfile().ConsentEndpoint = "";
        settings.CloudConsent = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SummarizeAsync(settings, "PRIVATE TRANSCRIPT", "", false, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SummaryService(cloud: service).SummarizeOverallAsync(settings, "PRIVATE MAIN POINTS", null, CancellationToken.None));
        settings.GetCloudProfile().ConsentEndpoint = settings.ActiveSummaryEndpoint;
        CloudApiKey.Forget(provider, settings.GetCloudProfile());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SummarizeAsync(settings, "PRIVATE TRANSCRIPT", "", false, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SummaryService(cloud: service).SummarizeOverallAsync(settings, "PRIVATE MAIN POINTS", null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(CloudLlmCatalog.Doubao)]
    [InlineData(CloudLlmCatalog.Qwen)]
    [InlineData(CloudLlmCatalog.DeepSeek)]
    public async Task OverallRequestsKeepTheSelectedProviderModelAndConsent(string provider)
    {
        var settings = CloudSettings(provider);
        using var handler = new ApiHandler();
        using var client = new HttpClient(handler);
        const string mainPoints = "## Decisions\n  - Meet on Friday.";
        await new SummaryService(cloud: new CloudLlmService(client))
            .SummarizeOverallAsync(settings, mainPoints, null, CancellationToken.None);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(settings.ActiveSummaryEndpoint).Host, request.Uri.Host);
        Assert.Equal(TestKey, request.Key);
        using var json = JsonDocument.Parse(request.Body);
        Assert.Equal(settings.ActiveSummaryModel, json.RootElement.GetProperty("model").GetString());
        var prompt = Prompt(json);
        Assert.Contains("Do not include recording timestamps", prompt);
        using var data = JsonDocument.Parse(prompt[prompt.IndexOf("{\"mainPoints\"", StringComparison.Ordinal)..]);
        Assert.Equal(mainPoints, data.RootElement.GetProperty("mainPoints").GetString());
        Assert.Single(data.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task TheExplicitSampleTestDoesNotRequirePermissionToShareRecordings()
    {
        var settings = CloudSettings(CloudLlmCatalog.Qwen);
        settings.GetCloudProfile().ConsentEndpoint = "";
        settings.GetCloudProfile().DisableThinking = false;
        using var handler = new ApiHandler();
        using var client = new HttpClient(handler);
        await new CloudLlmService(client).TestAsync(settings, CancellationToken.None);
        using var request = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Contains("This is a synthetic test.", Prompt(request));
        Assert.False(request.RootElement.TryGetProperty("enable_thinking", out _));
        Assert.False(request.RootElement.TryGetProperty("thinking", out _));
        Assert.Equal(512, request.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(settings.CanSummarize);
    }

    [Fact]
    public async Task SupportedDiscoveryDoesNotSendASummaryOrChangeTheSelectedModel()
    {
        var settings = CloudSettings(CloudLlmCatalog.DeepSeek);
        settings.GetCloudProfile().ConsentEndpoint = "";
        using var handler = new ApiHandler { ResponseBody = """{"data":[{"id":"unit-model-b"},{"id":"unit-model-a"},{"id":"unit-model-b"}]}""" };
        using var client = new HttpClient(handler);
        var service = new CloudLlmService(client);
        Assert.Equal(["unit-model-a", "unit-model-b"], await service.DiscoverModelsAsync(settings, CancellationToken.None));
        Assert.Equal("unit-model", settings.ActiveSummaryModel);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/models", request.Uri.AbsolutePath);
        Assert.Empty(request.Body);
        settings.SummaryProvider = CloudLlmCatalog.Doubao;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DiscoverModelsAsync(settings, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(302)]
    public async Task ApiFailuresNeverEchoSecretsRetryOrFallBack(int status)
    {
        using var handler = new ApiHandler
        {
            Status = (HttpStatusCode)status,
            ResponseBody = "PRIVATE TRANSCRIPT " + TestKey
        };
        using var client = new HttpClient(handler);
        var settings = CloudSettings(CloudLlmCatalog.DeepSeek);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SummaryService(cloud: new CloudLlmService(client)).SummarizeBatchAsync(
                settings, "PRIVATE TRANSCRIPT", "", false, null, CancellationToken.None));
        Assert.Contains(status.ToString(), error.Message);
        if (status == 413)
            Assert.Contains(new UiText(settings.UiLanguage)["CloudRequestTooLarge"], error.Message);
        Assert.DoesNotContain(TestKey, error.ToString());
        Assert.DoesNotContain("PRIVATE TRANSCRIPT", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":"Truncated."}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":""}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"content_filter","message":{"content":"Refused."}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"Notes","tool_calls":[{"name":"shell"}]}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"Notes","function_call":{"name":"shell"}}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"Notes","refusal":"Refused"}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"<think>Unfinished reasoning"}}]}""")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"error":{"message":"Failure"}}""")]
    [InlineData("<html>Invalid response</html>")]
    public async Task IncompleteOrNonTextOutputsCannotBecomeSavedNotes(string body)
    {
        using var handler = new ApiHandler { ResponseBody = body };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CloudLlmService(client).TestAsync(CloudSettings(CloudLlmCatalog.DeepSeek), CancellationToken.None));
    }

    [Fact]
    public async Task ResponseAndConvertedSummarySizesRemainBounded()
    {
        using var handler = new ApiHandler { ResponseBody = new string('x', 4 * 1024 * 1024 + 1) };
        using var client = new HttpClient(handler);
        var service = new CloudLlmService(client);
        var settings = CloudSettings(CloudLlmCatalog.DeepSeek);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.TestAsync(settings, CancellationToken.None));
        handler.ResponseBody = JsonSerializer.Serialize(new
        {
            choices = new[] { new { finish_reason = "stop", message = new { content = new string('\u346e', 4001) } } }
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => service.TestAsync(settings, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationReachesTheApiAndTimeoutDoesNotRetry()
    {
        using var handler = new ApiHandler { WaitForCancellation = true };
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var settings = CloudSettings(CloudLlmCatalog.DeepSeek);
        var request = new CloudLlmService(client).TestAsync(settings, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True(handler.WasCancelled);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new CloudLlmService(client, TimeSpan.FromMilliseconds(50)).TestAsync(settings, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CloudSummariesPersistProviderEndpointAndCleanInputsWithoutLeakingSettings()
    {
        using var folder = new TestFolder();
        var settings = CloudSettings(CloudLlmCatalog.Qwen);
        settings.SaveFolder = folder.Root;
        var session = AppStorage.CreateSession(settings, "Synthetic cloud summary");
        AppStorage.SaveTranscript(session, new() { Revision = "cloud-test", Segments = [new(0, 1, "Meeting on Friday.")] });
        AtomicFile.WriteText(session.NotesPath, "PRIVATE NOTE MUST STAY LOCAL");
        using var handler = new ApiHandler();
        using var client = new HttpClient(handler);
        var pipeline = new AiPipeline(summaryService: new SummaryService(cloud: new CloudLlmService(client)));
        await pipeline.SummarizeAsync(session, settings, new Progress<AiUpdate>(), false, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        var first = handler.Requests[0];
        Assert.Contains("Meeting on Friday.", first.Body);
        Assert.DoesNotContain("PRIVATE NOTE", first.Body);
        Assert.DoesNotContain(TestKey, first.Body);
        Assert.All(handler.Requests, request =>
        {
            Assert.DoesNotContain("PRIVATE NOTE", request.Body);
            Assert.DoesNotContain(TestKey, request.Body);
        });
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(session.Folder, "summary.json")));
        Assert.Equal(settings.SummaryProvider, saved.RootElement.GetProperty("Provider").GetString());
        Assert.Equal(settings.ActiveSummaryEndpoint, saved.RootElement.GetProperty("Endpoint").GetString());
        Assert.DoesNotContain("ProtectedApiKeys", saved.RootElement.GetRawText());
        Assert.False(string.IsNullOrWhiteSpace(saved.RootElement.GetProperty("OverallText").GetString()));

        settings.GetCloudProfile().Endpoint = CloudLlmCatalog.Get(CloudLlmCatalog.Qwen).Endpoints[1];
        settings.GetCloudProfile().ConsentEndpoint = settings.ActiveSummaryEndpoint;
        CloudApiKey.Set(settings.SummaryProvider, settings.GetCloudProfile(), "synthetic-second-region-key");
        await pipeline.SummarizeAsync(session, settings, new Progress<AiUpdate>(), false, CancellationToken.None);
        Assert.Equal(4, handler.Requests.Count);
        using var secondRequest = JsonDocument.Parse(handler.Requests[2].Body);
        var prompt = Prompt(secondRequest);
        using var payload = JsonDocument.Parse(prompt[prompt.IndexOf("{\"previousSummary\"", StringComparison.Ordinal)..]);
        Assert.Empty(payload.RootElement.GetProperty("previousSummary").GetString()!);
        Assert.All(handler.Requests.Skip(2), request =>
        {
            Assert.Equal("synthetic-second-region-key", request.Key);
            Assert.Equal("dashscope-intl.aliyuncs.com", request.Uri.Host);
        });

        var summaryBefore = File.ReadAllText(session.SummaryPath);
        settings.SummaryProvider = CloudLlmCatalog.DeepSeek;
        var profile = settings.GetCloudProfile();
        profile.Model = "unit-model";
        profile.ConsentEndpoint = settings.ActiveSummaryEndpoint;
        CloudApiKey.Set(settings.SummaryProvider, profile, TestKey);
        handler.Status = HttpStatusCode.PaymentRequired;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.SummarizeAsync(session, settings, new Progress<AiUpdate>(), false, CancellationToken.None));
        Assert.Equal(summaryBefore, File.ReadAllText(session.SummaryPath));
        Assert.Equal("PRIVATE NOTE MUST STAY LOCAL", File.ReadAllText(session.NotesPath));
    }

    internal static AppSettings CloudSettings(string provider)
    {
        var settings = new AppSettings { SummaryProvider = provider };
        var profile = settings.GetCloudProfile();
        profile.Model = "unit-model";
        profile.ConsentEndpoint = settings.ActiveSummaryEndpoint;
        CloudApiKey.Set(provider, profile, TestKey);
        return settings;
    }

    private static string Prompt(JsonDocument request) =>
        request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;

    private sealed record ApiRequest(Uri Uri, HttpMethod Method, string? Scheme, string? Key, string Body);

    private sealed class ApiHandler : HttpMessageHandler
    {
        public List<ApiRequest> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = """{"choices":[{"finish_reason":"stop","message":{"content":"## Main points\nMeet on Friday."}}]}""";
        public bool WaitForCancellation { get; set; }
        public bool WasCancelled { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests.Add(new(request.RequestUri!, request.Method, request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            Started.TrySetResult();
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { WasCancelled = true; throw; }
            }
            return new(Status)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
                Headers = { Location = new Uri("https://example.com/never-follow-this-test-redirect") }
            };
        }
    }
}
