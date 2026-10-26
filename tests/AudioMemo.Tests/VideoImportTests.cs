using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class VideoImportTests
{
    [Theory]
    [InlineData("mp4")]
    [InlineData("mov")]
    [InlineData("mkv")]
    [InlineData("webm")]
    [InlineData("avi")]
    [InlineData("wmv")]
    [InlineData("mpeg")]
    [InlineData("ts")]
    [InlineData("ogv")]
    public async Task VideoAudioIsImportedLocallyWithoutChangingTheSourceOrStartingAi(string extension)
    {
        using var folder = new TestFolder();
        var video = await MakeVideoAsync(folder, extension);
        var before = SHA256.HashData(File.ReadAllBytes(video));
        var progress = new Updates();
        var result = await new AudioImportService().ImportAsync(video, folder.File("library"), progress, CancellationToken.None);
        Assert.Equal(Path.GetFileNameWithoutExtension(video), result.Title);
        Assert.Equal(Path.GetFileName(video), result.ImportedFileName);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(video)));
        Assert.InRange((double)result.Frames / PcmFormat.SampleRate, 1.9, 2.15);
        Assert.Equal(DurableWaveFile.HeaderSize + result.Frames * PcmFormat.BlockAlign, new FileInfo(result.WavePath).Length);
        using (var wave = File.OpenRead(result.WavePath))
            Assert.Equal(result.Frames, DurableWaveFile.ValidateAndGetFrames(wave));
        Assert.InRange(Frequency(result.WavePath), 435, 445);
        Assert.False(File.Exists(result.TranscriptPath));
        Assert.False(File.Exists(result.SummaryPath));
        Assert.Empty(File.ReadAllText(result.NotesPath));
        Assert.Equal(result.Id, Assert.Single(AppStorage.LoadSessions(folder.File("library")).Sessions).Id);
        Assert.Empty(Directory.GetDirectories(folder.File("library"), ".import-*"));
        Assert.Equal(0, progress.Values[0]);
        Assert.Equal(100, progress.Values[^1]);
        Assert.Equal(progress.Values.Order(), progress.Values);
    }

    [Fact]
    public async Task MultipleTracksRequireAChoiceAndOnlyTheSelectedTrackIsDecoded()
    {
        using var folder = new TestFolder();
        var video = await MakeVideoAsync(folder, "mkv", twoTracks: true);
        var service = new VideoAudioService();
        var information = await service.ProbeAsync(video, CancellationToken.None);
        Assert.Equal(2, information.AudioTracks.Count);
        Assert.Equal(["eng", "zho"], information.AudioTracks.Select(track => track.Language));
        Assert.False(information.AudioTracks[0].IsDefault);
        Assert.True(information.AudioTracks[1].IsDefault);
        var library = folder.File("library");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AudioImportService().ImportAsync(video, library, new Updates(), CancellationToken.None));
        Assert.Equal(UiText.Get("VideoChooseTrack"), error.Message);
        Assert.False(Directory.Exists(library));
        foreach (var (track, expected) in information.AudioTracks.Zip(new[] { 440, 880 }))
        {
            var imported = await new AudioImportService().ImportAsync(video, library, new Updates(), CancellationToken.None,
                new(information, track.StreamIndex));
            Assert.InRange(Frequency(imported.WavePath), expected - 3, expected + 3);
        }
    }

    [Fact]
    public async Task SurroundAudioIsDownmixedAndInitialAudioDelayIsRetained()
    {
        using var folder = new TestFolder();
        var video = await MakeVideoAsync(folder, "mkv", surround: true, delayed: true);
        var information = await new VideoAudioService().ProbeAsync(video, CancellationToken.None);
        Assert.Equal(6, Assert.Single(information.AudioTracks).Channels);
        var result = await new AudioImportService().ImportAsync(video, folder.File("library"), new Updates(), CancellationToken.None);
        var bytes = File.ReadAllBytes(result.WavePath);
        var initial = bytes.AsSpan(DurableWaveFile.HeaderSize, (int)(0.45 * PcmFormat.SampleRate) * PcmFormat.BlockAlign);
        Assert.True(initial.IndexOfAnyExcept((byte)0) < 0);
        var samples = bytes.AsSpan(DurableWaveFile.HeaderSize + PcmFormat.SampleRate * PcmFormat.BlockAlign);
        Assert.True(samples.IndexOfAnyExcept((byte)0) >= 0);
        Assert.InRange((double)result.Frames / PcmFormat.SampleRate, 1.95, 2.05);
    }

    [Theory]
    [InlineData("silent-video")]
    [InlineData("corrupt")]
    [InlineData("truncated")]
    [InlineData("playlist")]
    public async Task UnusableMediaNeverPublishesPartialAudio(string kind)
    {
        using var folder = new TestFolder();
        string video;
        if (kind == "silent-video")
            video = await MakeVideoAsync(folder, "mkv", noAudio: true);
        else if (kind == "truncated")
        {
            video = await MakeVideoAsync(folder, "mp4");
            using var file = File.OpenWrite(video);
            file.SetLength(file.Length / 2);
        }
        else
        {
            video = folder.File("not-video.mp4");
            File.WriteAllText(video, kind == "playlist"
                ? "#EXTM3U\n#EXTINF:2,\nhttp://127.0.0.1:1/must-not-be-contacted.ts\n" : "Not a video");
        }
        var original = File.ReadAllBytes(video);
        var library = folder.File("library");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new AudioImportService().ImportAsync(video, library, new Updates(), CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(video));
        Assert.True(!Directory.Exists(library) || Directory.GetFileSystemEntries(library).Length == 0);
    }

    [Fact]
    public async Task ChangedSourcesAndInvalidSelectionsAreRejectedBeforeImport()
    {
        using var folder = new TestFolder();
        var video = await MakeVideoAsync(folder, "mkv", twoTracks: true);
        var information = await new VideoAudioService().ProbeAsync(video, CancellationToken.None);
        var library = folder.File("library");
        await Assert.ThrowsAsync<InvalidDataException>(() => new AudioImportService().ImportAsync(video, library,
            new Updates(), CancellationToken.None, new(information, 999)));
        File.SetLastWriteTimeUtc(video, File.GetLastWriteTimeUtc(video).AddMinutes(1));
        await Assert.ThrowsAsync<IOException>(() => new AudioImportService().ImportAsync(video, library,
            new Updates(), CancellationToken.None, new(information, information.AudioTracks[0].StreamIndex)));
        Assert.False(Directory.Exists(library));
    }

    [Fact]
    public async Task CancellingVideoImportCleansStagingAndReleasesTheSource()
    {
        using var folder = new TestFolder();
        var video = await MakeVideoAsync(folder, "mkv");
        var original = File.ReadAllBytes(video);
        var library = folder.File("library");
        using var cancellation = new CancellationTokenSource();
        var updates = new Updates(value =>
        {
            Assert.Throws<IOException>(() => File.Open(video, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AudioImportService().ImportAsync(video, library, updates, cancellation.Token));
        Assert.Empty(Directory.GetFileSystemEntries(library));
        Assert.Equal(original, File.ReadAllBytes(video));
        using var unlocked = File.Open(video, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task AFailedPcmConsumerStopsTheChildWithoutWaitingForTheImportTimeout()
    {
        var start = new ProcessStartInfo(Tool());
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "anullsrc=r=48000:cl=stereo", "-f", "s16le", "pipe:1" })
            start.ArgumentList.Add(argument);
        var elapsed = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<IOException>(() => ProcessRunner.RunStreamingAsync(start,
            TimeSpan.FromSeconds(30), async (output, token) =>
            {
                Assert.True(await output.ReadAsync(new byte[16], token) > 0);
                throw new IOException("Synthetic PCM write failure");
            }, CancellationToken.None));
        Assert.Equal("Synthetic PCM write failure", error.Message);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancellingAStreamingConsumerStopsAnActiveMediaProcess()
    {
        using var cancellation = new CancellationTokenSource();
        var start = new ProcessStartInfo(Tool());
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "anullsrc=r=48000:cl=stereo", "-f", "s16le", "pipe:1" })
            start.ArgumentList.Add(argument);
        var elapsed = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunStreamingAsync(start,
            TimeSpan.FromSeconds(30), async (output, token) =>
            {
                Assert.True(await output.ReadAsync(new byte[16], token) > 0);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }, cancellation.Token));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MissingSharedLibrariesAreReportedBeforeStartingTheMediaTool()
    {
        using var folder = new TestFolder();
        var video = folder.File("video.mp4");
        File.WriteAllText(video, "synthetic input");
        var tools = folder.File("incomplete-tools");
        Directory.CreateDirectory(tools);
        foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe", "manifest.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "FFmpeg", name), Path.Combine(tools, name));
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new VideoAudioService(tools).ProbeAsync(video, CancellationToken.None));
        Assert.Equal(UiText.Get("VideoToolsMissing"), error.Message);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-1")]
    public void InvalidDurationMetadataIsRejected(string duration)
    {
        var json = """{"streams":[{"index":1,"codec_type":"audio","codec_name":"pcm_s16le","channels":2}],"format":{"duration":"VALUE"}}"""
            .Replace("VALUE", duration, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => VideoAudioService.ReadProbe(@"C:\synthetic.mp4", 10, 0, json));
    }

    [Theory]
    [InlineData("""{"codec_type":"audio","index":"1"}""")]
    [InlineData("""{"codec_type":"audio","index":1,"channels":"2"}""")]
    [InlineData("""{"codec_type":"audio","index":1,"tags":true}""")]
    [InlineData("""{"codec_type":"audio","index":1,"disposition":false}""")]
    [InlineData("""{"codec_type":"audio","index":1,"disposition":{"default":"1"}}""")]
    [InlineData("""{"codec_type":"audio","index":1,"disposition":{"default":2}}""")]
    public void MalformedTrackMetadataIsReportedAsInvalidMedia(string stream)
    {
        var json = $$$"""{"streams":[{{{stream}}}],"format":{}}""";
        var error = Assert.Throws<InvalidDataException>(() =>
            VideoAudioService.ReadProbe(@"C:\synthetic.mp4", 10, 0, json));
        Assert.Equal(UiText.Get("VideoProbeInvalid"), error.Message);
    }

    internal static async Task<string> MakeVideoAsync(TestFolder folder, string extension,
        bool twoTracks = false, bool noAudio = false, bool surround = false, bool delayed = false, string? ffmpeg = null)
    {
        var video = folder.File($"Video & \u4f1a\u8bae {Guid.NewGuid():N}.{extension}");
        var start = new ProcessStartInfo(ffmpeg ?? Tool()) { WorkingDirectory = folder.Root };
        var (videoCodec, audioCodec, rate) = extension switch
        {
            "mkv" => ("ffv1", "pcm_s16le", 2),
            "webm" => ("libvpx-vp9", "libopus", 2),
            "wmv" => ("wmv2", "wmav2", 2),
            "mpeg" or "ts" => ("mpeg2video", "mp2", 25),
            "ogv" => ("libtheora", "libvorbis", 2),
            _ => ("mpeg4", "aac", 2)
        };
        var arguments = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", $"color=c=black:s=64x48:r={rate}:d=2" };
        if (!noAudio)
        {
            if (delayed)
                arguments.AddRange(["-itsoffset", "0.5"]);
            arguments.AddRange(["-f", "lavfi", "-i", surround
                ? "aevalsrc=0.05*sin(2*PI*440*t)|0.05*sin(2*PI*660*t)|0.05*sin(2*PI*880*t)|0|0.05*sin(2*PI*440*t)|0.05*sin(2*PI*660*t):s=48000:d=2:c=5.1"
                : "sine=frequency=440:sample_rate=48000:duration=2"]);
            if (twoTracks)
                arguments.AddRange(["-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=2"]);
        }
        arguments.AddRange(["-map", "0:v"]);
        if (!noAudio)
        {
            arguments.AddRange(["-map", "1:a", "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "title=English"]);
            if (twoTracks)
                arguments.AddRange(["-map", "2:a", "-metadata:s:a:1", "language=zho", "-metadata:s:a:1",
                    "title=Chinese", "-disposition:a:0", "0", "-disposition:a:1", "default"]);
        }
        arguments.AddRange(["-t", "2", "-c:v", videoCodec,
            "-pix_fmt", "yuv420p", "-threads", "2"]);
        if (!noAudio)
            arguments.AddRange(["-c:a", audioCodec]);
        if (extension is "mp4" or "mov")
            arguments.AddRange(["-movflags", "+faststart"]);
        arguments.Add(video);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Error);
        return video;
    }

    private static string Tool() => Path.Combine(AppContext.BaseDirectory, "FFmpeg", "ffmpeg.exe");

    private static double Frequency(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var crossings = 0;
        // Measure an interior second, excluding codec delay and tail padding.
        var first = DurableWaveFile.HeaderSize + PcmFormat.SampleRate / 4 * PcmFormat.BlockAlign;
        var last = first + PcmFormat.SampleRate * PcmFormat.BlockAlign;
        Assert.True(bytes.Length >= last);
        var previous = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(first - PcmFormat.BlockAlign));
        for (var offset = first; offset < last; offset += PcmFormat.BlockAlign)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset));
            if (sample > 0 && previous <= 0)
                crossings++;
            previous = sample;
        }
        return crossings;
    }

    private sealed class Updates(Action<double>? update = null) : IProgress<double>
    {
        public List<double> Values { get; } = [];
        public void Report(double value) { Values.Add(value); update?.Invoke(value); }
    }
}
