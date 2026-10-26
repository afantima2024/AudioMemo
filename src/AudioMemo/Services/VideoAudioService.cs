using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;

namespace AudioMemo.Services;

public sealed record VideoAudioTrack(int StreamIndex, string Codec, int Channels, string Language,
    string Title, bool IsDefault, double? StartSeconds, double? DurationSeconds);

public sealed record VideoMediaInfo(string Path, long Length, long LastWriteTicks, double? StartSeconds,
    double? DurationSeconds, IReadOnlyList<VideoAudioTrack> AudioTracks)
{
    public double? ExpectedSeconds(VideoAudioTrack track) => track.DurationSeconds is { } duration
        ? duration + Math.Max(0, (track.StartSeconds ?? StartSeconds ?? 0) - (StartSeconds ?? 0))
        : DurationSeconds;
}

public sealed record VideoImportSelection(VideoMediaInfo Media, int StreamIndex);

public sealed class VideoAudioService(string? toolsDirectory = null)
{
    private const string Containers = "mov,matroska,webm,avi,asf,mpeg,mpegts,ogg";
    private static readonly HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".asf",
        ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".vob", ".3gp", ".3g2", ".ogv"
    };

    public static bool IsVideo(string path) => extensions.Contains(System.IO.Path.GetExtension(path));

    public async Task<VideoMediaInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException(UiText.Get("ImportFileMissing"));
        cancellationToken.ThrowIfCancellationRequested();
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var start = Tool("ffprobe.exe");
        Add(start, "-v", "error", "-protocol_whitelist", "file", "-format_whitelist", Containers,
            "-probesize", "10000000", "-analyzeduration", "10000000", "-show_entries",
            "format=start_time,duration:stream=index,codec_type,codec_name,channels,start_time,duration:stream_tags=language,title:stream_disposition=default",
            "-of", "json", path);
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        CheckResult(result);
        try
        {
            return ReadProbe(path, source.Length, File.GetLastWriteTimeUtc(path).Ticks, result.Output);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(UiText.Get("VideoProbeInvalid"), ex);
        }
    }

    public static VideoAudioTrack ValidateSelection(string path, VideoImportSelection selection)
    {
        var source = new FileInfo(path);
        if (!string.Equals(System.IO.Path.GetFullPath(path), selection.Media.Path, StringComparison.OrdinalIgnoreCase) ||
            !source.Exists || source.Length != selection.Media.Length || source.LastWriteTimeUtc.Ticks != selection.Media.LastWriteTicks)
            throw new IOException(UiText.Get("VideoSourceChanged"));
        return selection.Media.AudioTracks.SingleOrDefault(track => track.StreamIndex == selection.StreamIndex)
            ?? throw new InvalidDataException(UiText.Get("VideoChooseTrack"));
    }

    public async Task ExtractAsync(string path, VideoImportSelection selection, DurableWaveFile writer,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        var track = ValidateSelection(path, selection);
        var expected = selection.Media.ExpectedSeconds(track);
        var start = Tool("ffmpeg.exe");
        Add(start, "-nostdin", "-hide_banner", "-nostats", "-loglevel", "error", "-xerror",
            "-err_detect", "explode", "-protocol_whitelist", "file,pipe", "-format_whitelist", Containers,
            "-copyts", "-start_at_zero", "-i", path, "-map", $"0:{track.StreamIndex}",
            "-vn", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
            "-af", "aresample=48000:async=1:first_pts=0", "-ac", "2", "-ar", "48000",
            "-c:a", "pcm_s16le", "-threads", "2", "-f", "s16le", "pipe:1");
        var result = await ProcessRunner.RunStreamingAsync(start, TimeSpan.FromHours(24), async (output, token) =>
        {
            var buffer = new byte[64 * 1024 + PcmFormat.BlockAlign];
            var pending = 0;
            var lastProgress = Stopwatch.StartNew();
            while (true)
            {
                var count = await output.ReadAsync(buffer.AsMemory(pending, 64 * 1024), token).ConfigureAwait(false);
                if (count == 0)
                    break;
                token.ThrowIfCancellationRequested();
                var available = pending + count;
                var aligned = available - available % PcmFormat.BlockAlign;
                writer.Write(buffer.AsSpan(0, aligned));
                pending = available - aligned;
                buffer.AsSpan(aligned, pending).CopyTo(buffer);
                if (expected is > 0 && lastProgress.ElapsedMilliseconds >= 100)
                {
                    progress.Report(Math.Clamp(100d * writer.FramesWritten / PcmFormat.SampleRate / expected.Value, 0, 99));
                    lastProgress.Restart();
                }
            }
            if (pending != 0)
                throw new InvalidDataException(UiText.Get("VideoAudioIncomplete"));
        }, cancellationToken).ConfigureAwait(false);
        CheckResult(result);
        if (track.DurationSeconds is > 0 && expected is > 0 &&
            (double)writer.FramesWritten / PcmFormat.SampleRate < expected.Value - Math.Max(0.25, expected.Value * 0.001))
            throw new InvalidDataException(UiText.Get("VideoAudioIncomplete"));
    }

    internal static VideoMediaInfo ReadProbe(string path, long length, long lastWriteTicks, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("streams", out var streams) ||
            streams.ValueKind != JsonValueKind.Array || !root.TryGetProperty("format", out var format) ||
            format.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(UiText.Get("VideoProbeInvalid"));
        var tracks = new List<VideoAudioTrack>();
        var indices = new HashSet<int>();
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(UiText.Get("VideoProbeInvalid"));
            if (Text(stream, "codec_type") != "audio")
                continue;
            if (!stream.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number ||
                !index.TryGetInt32(out var number) ||
                number < 0 || !indices.Add(number))
                throw new InvalidDataException(UiText.Get("VideoProbeInvalid"));
            var channels = 0;
            if (stream.TryGetProperty("channels", out var count) &&
                (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out channels) || channels < 0))
                throw new InvalidDataException(UiText.Get("VideoProbeInvalid"));
            var language = "";
            var title = "";
            if (stream.TryGetProperty("tags", out var tags))
            {
                if (tags.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException(UiText.Get("VideoProbeInvalid"));
                language = Text(tags, "language");
                title = Text(tags, "title");
            }
            var isDefault = false;
            if (stream.TryGetProperty("disposition", out var disposition))
            {
                if (disposition.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException(UiText.Get("VideoProbeInvalid"));
                if (disposition.TryGetProperty("default", out var flag))
                {
                    if (flag.ValueKind != JsonValueKind.Number || !flag.TryGetInt32(out var value) || value is not (0 or 1))
                        throw new InvalidDataException(UiText.Get("VideoProbeInvalid"));
                    isDefault = value == 1;
                }
            }
            tracks.Add(new(number, Text(stream, "codec_name"), channels, language, title, isDefault,
                Number(stream, "start_time"), Number(stream, "duration", duration: true)));
        }
        if (tracks.Count == 0)
            throw new InvalidDataException(UiText.Get("VideoHasNoAudio"));
        return new(System.IO.Path.GetFullPath(path), length, lastWriteTicks,
            Number(format, "start_time"), Number(format, "duration", duration: true), tracks.AsReadOnly());
    }

    private static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return "";
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(UiText.Get("VideoProbeInvalid"));
        return new string((value.GetString() ?? "").Where(character => !char.IsControl(character)).Take(160).ToArray()).Trim();
    }

    private static double? Number(JsonElement element, string name, bool duration = false)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (text == "N/A")
            return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number) || (duration && number < 0))
            throw new InvalidDataException(UiText.Get("VideoProbeInvalid"));
        return duration && number == 0 ? null : number;
    }

    private ProcessStartInfo Tool(string name)
    {
        var directory = toolsDirectory ?? System.IO.Path.Combine(AppContext.BaseDirectory, "FFmpeg");
        var path = System.IO.Path.Combine(directory, name);
        var manifest = System.IO.Path.Combine(directory, "manifest.json");
        if (!File.Exists(path) || !File.Exists(manifest))
            throw new FileNotFoundException(UiText.Get("VideoToolsMissing"), path);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Files", out var files) ||
                files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
                throw new InvalidDataException(UiText.Get("VideoToolsMissing"));
            foreach (var item in files.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("Name", out var field) ||
                    field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: > 0 } file ||
                    System.IO.Path.GetFileName(file) != file || !File.Exists(System.IO.Path.Combine(directory, file)))
                    throw new FileNotFoundException(UiText.Get("VideoToolsMissing"), path);
            }
        }
        catch (JsonException ex) { throw new InvalidDataException(UiText.Get("VideoToolsMissing"), ex); }
        return new(path) { WorkingDirectory = System.IO.Path.GetDirectoryName(path)! };
    }

    private static void Add(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
    }

    private static void CheckResult(ProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            var error = result.Error.Trim();
            if (error.Length > 4000)
                error = "..." + error[^4000..];
            throw new InvalidDataException(UiText.Format("VideoImportFailed", result.ExitCode, error));
        }
    }
}
