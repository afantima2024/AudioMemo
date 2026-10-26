using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace AudioMemo.Services;

public static class ModelFiles
{
    public static async Task VerifyAsync(string path, long size, string sha256, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != size ||
            !Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Core.Localization.UiText.Get("ModelChecksumFailed"));
    }

    public static async Task DownloadAsync(string url, string path, long size, string sha256,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioMemo/0.2");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[128 * 1024];
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += count;
                    if (received > size)
                        throw new InvalidDataException("The model download exceeds its expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    progress.Report(100d * received / size);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            await VerifyAsync(temporary, size, sha256, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
