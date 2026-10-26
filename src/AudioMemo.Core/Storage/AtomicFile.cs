using System.Text;
using System.Text.Json;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Storage;

public static class AtomicFile
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly int[] publishRetryDelays = [25, 50, 100, 200, 400];

    public static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static void WriteText(string path, string content, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                stream.Write(bytes);
                stream.Flush(true);
            }
            PublishTemporary(temporary, path, cancellationToken);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void WriteJson<T>(string path, T value, CancellationToken cancellationToken = default) =>
        WriteText(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

    public static void PublishTemporary(string temporary, string path, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporary, path, true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Windows can report ERROR_ACCESS_DENIED when a reader disallows replacing the file.
                var mayBeBusy = OperatingSystem.IsWindows() && (error.HResult & 0xFFFF) is 5 or 32 or 33;
                if (mayBeBusy && attempt < publishRetryDelays.Length)
                {
                    if (cancellationToken.WaitHandle.WaitOne(publishRetryDelays[attempt]))
                        cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }
                throw new IOException(UiText.Format("FilePublishFailed", Path.GetFileName(path), Path.GetFullPath(path), error.Message), error);
            }
        }
    }
}
