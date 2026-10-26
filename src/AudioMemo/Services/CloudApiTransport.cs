using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AudioMemo.Core.Localization;

namespace AudioMemo.Services;

internal sealed class CloudApiTransport(HttpClient? httpClient = null)
{
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client = httpClient ?? SharedClient;

    public async Task<JsonDocument> SendAsync(HttpRequestMessage request, string provider, UiText text,
        TimeSpan timeout, CancellationToken cancellationToken, string requestRejectedHint = "CloudRequestRejected")
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "CloudAuthFailed",
                    HttpStatusCode.Forbidden => "CloudAccessFailed",
                    HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests => "CloudQuotaFailed",
                    HttpStatusCode.RequestEntityTooLarge => "CloudRequestTooLarge",
                    HttpStatusCode.NotFound or HttpStatusCode.BadRequest => requestRejectedHint,
                    >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => "CloudRedirectBlocked",
                    _ => "CloudServerFailed"
                };
                // Provider error bodies may echo credentials, audio, or private text.
                throw new InvalidOperationException(text.FormatValue("CloudHttpFailed", provider, (int)response.StatusCode, text[hint]));
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidDataException(text["CloudResponseTooLarge"]);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + count > MaximumResponseBytes)
                    throw new InvalidDataException(text["CloudResponseTooLarge"]);
                buffer.Write(chunk, 0, count);
            }
            var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.TryGetProperty("error", out _))
            {
                document.Dispose();
                throw new InvalidDataException(text["CloudInvalidResponse"]);
            }
            return document;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(text.FormatValue("CloudConnectionFailed", provider, request.RequestUri!.Host), ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(text["CloudRequestTimeout"], ex);
        }
        catch (JsonException ex) { throw new InvalidDataException(text["CloudInvalidResponse"], ex); }
    }
}
