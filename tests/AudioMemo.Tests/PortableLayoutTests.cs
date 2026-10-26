using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class PortableLayoutTests
{
    private static string PublishedDirectory =>
        Path.GetDirectoryName(Path.GetFullPath(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_PORTABLE")!))!;

    [PortableFact]
    [Trait("Category", "Portable")]
    public void PublishedFolderContainsOnlyTheExecutableAndSupportDirectory()
    {
        Assert.Equal("AudioMemo.exe", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(PublishedDirectory))));
        Assert.Equal("support", Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(PublishedDirectory))));

        var support = Path.Combine(PublishedDirectory, "support");
        Assert.Empty(Directory.EnumerateFiles(support));
        Assert.Equal(["Docs", "Licenses", "Runtime"], Directory.EnumerateDirectories(support)
            .Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var runtimeDirectory = Path.Combine(support, "Runtime");
        foreach (var file in new[]
        {
            "AudioMemo.exe", "AudioMemo.dll", "AudioMemo.Core.dll",
            "AudioMemo.deps.json", "AudioMemo.runtimeconfig.json",
            "hostfxr.dll", "hostpolicy.dll", "coreclr.dll",
            "PresentationFramework.dll", "PresentationFramework.Fluent.dll", "Whisper.net.dll",
            "sherpa-onnx.dll", "sherpa-onnx-c-api.dll", "onnxruntime.dll",
            @"runtimes\vulkan\win-x64\ggml-vulkan-whisper.dll",
            @"runtimes\noavx\win-x64\whisper.dll",
            "OpenccNetLib.dll", "ZstdSharp.dll", "System.Formats.Cbor.dll",
            @"zh-CN\AudioMemo.Core.resources.dll"
        })
            Assert.True(File.Exists(Path.Combine(runtimeDirectory, file)), $"Missing runtime file: {file}");
        Assert.Equal(["PRIVACY.md", "README.md", "THIRD-PARTY-NOTICES.txt"],
            Directory.EnumerateFiles(Path.Combine(support, "Docs")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var file in new[] { "Chinese-text-conversion.txt", "sherpa-onnx.txt",
            "ONNX-Runtime-ThirdPartyNotices.txt", "Pyannote-segmentation.txt", "3D-Speaker.txt" })
            Assert.True(File.Exists(Path.Combine(support, "Licenses", file)), $"Missing license: {file}");
        Assert.True(Directory.Exists(Path.Combine(support, "Licenses", "Microsoft.NETCore.App")));
        Assert.True(Directory.Exists(Path.Combine(support, "Licenses", "Microsoft.WindowsDesktop.App")));
        var mediaTools = Path.Combine(runtimeDirectory, "FFmpeg");
        var mediaLicenses = Path.Combine(support, "Licenses", "FFmpeg");
        using var dependency = JsonDocument.Parse(File.ReadAllText(Path.Combine(mediaLicenses, "dependency.json")));
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(mediaTools, "manifest.json"))))
        {
            Assert.Equal(dependency.RootElement.GetProperty("version").GetString(), manifest.RootElement.GetProperty("Version").GetString());
            Assert.Equal(dependency.RootElement.GetProperty("sha256").GetString(), manifest.RootElement.GetProperty("ArchiveSHA256").GetString());
            var files = manifest.RootElement.GetProperty("Files").EnumerateArray().ToArray();
            Assert.Equal(9, files.Length);
            foreach (var file in files)
            {
                var name = file.GetProperty("Name").GetString()!;
                Assert.Equal(name, Path.GetFileName(name));
                using var stream = File.OpenRead(Path.Combine(mediaTools, name));
                Assert.Equal(file.GetProperty("SHA256").GetString(), Convert.ToHexString(SHA256.HashData(stream)));
            }
            Assert.False(File.Exists(Path.Combine(mediaTools, "ffplay.exe")));
        }
        foreach (var file in new[] { "AudioMemo-FFmpeg-notice.txt", "COPYING.LGPLv3.txt", "COPYING.GPLv3.txt",
            "Upstream-LICENSE.md", "dependency.json", "build-configuration.txt", @"Sources\ffmpeg-source.zip",
            @"Sources\ffmpeg-build-source.zip" })
            Assert.True(File.Exists(Path.Combine(mediaLicenses, file)), $"Missing FFmpeg notice/source: {file}");
        foreach (var (file, hash) in new[] { ("ffmpeg-source.zip", "sourceSha256"), ("ffmpeg-build-source.zip", "buildSourceSha256") })
        {
            using var stream = File.OpenRead(Path.Combine(mediaLicenses, "Sources", file));
            Assert.Equal(dependency.RootElement.GetProperty(hash).GetString(), Convert.ToHexString(SHA256.HashData(stream)));
        }
        var configuration = File.ReadAllText(Path.Combine(mediaLicenses, "build-configuration.txt"));
        Assert.Contains("--enable-shared", configuration);
        Assert.DoesNotContain("--enable-gpl", configuration);
        Assert.DoesNotContain("--enable-nonfree", configuration);
        Assert.False(Directory.Exists(Path.Combine(runtimeDirectory, "Licenses")));
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(runtimeDirectory, "AudioMemo.runtimeconfig.json")));
        Assert.False(runtime.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties")
            .GetProperty("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering").GetBoolean());
        var sourceNative = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\src\AudioMemo\Native\win-x64\sherpa-onnx-c-api.dll"));
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(sourceNative)),
            SHA256.HashData(File.ReadAllBytes(Path.Combine(runtimeDirectory, "sherpa-onnx-c-api.dll"))));
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.Combine(runtimeDirectory, "runtimes"), "*", SearchOption.AllDirectories),
            directory => Path.GetFileName(directory).StartsWith("linux", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(directory) is "win-x86" or "win-arm64");
    }

    [PortableFact]
    [Trait("Category", "Portable")]
    public void BothLaunchersExposeTheCorrectWindowsIconAtEverySize()
    {
        var frames = IconAssetTests.ReadIconFrames();
        foreach (var executable in new[]
        {
            Path.Combine(PublishedDirectory, "AudioMemo.exe"),
            Path.Combine(PublishedDirectory, "support", "Runtime", "AudioMemo.exe")
        })
        {
            Assert.Equal("Copyright \u00a9 2026 Afanti Ma", FileVersionInfo.GetVersionInfo(executable).LegalCopyright);
            var bytes = File.ReadAllBytes(executable);
            foreach (var (size, png) in frames)
            {
                Assert.True(bytes.AsSpan().IndexOf(png) >= 0, $"{executable} is missing the {size}-pixel icon frame.");
                Assert.Equal(IconAssetTests.ExtractSourceWindowsIcon(size), IconAssetTests.ExtractWindowsIcon(executable, size));
            }
        }
    }

    [PortableFact]
    [Trait("Category", "Portable")]
    public void GroupedSpeechAndSpeakerLibrariesRemainLoadable()
    {
        foreach (var (path, symbol) in new[]
        {
            (@"runtimes\noavx\win-x64\whisper.dll", "whisper_init_from_file_with_params"),
            ("onnxruntime.dll", "OrtGetApiBase"),
            ("sherpa-onnx-c-api.dll", "SherpaOnnxCreateOfflineSpeakerDiarization"),
            ("sherpa-onnx-c-api.dll", "SherpaOnnxOfflineSpeakerDiarizationProcessWithCallback"),
            ("sherpa-onnx-c-api.dll", "SherpaOnnxCreateSpeakerEmbeddingExtractor"),
            ("sherpa-onnx-c-api.dll", "SherpaOnnxSpeakerEmbeddingExtractorComputeEmbedding")
        })
        {
            var library = NativeLibrary.Load(Path.Combine(PublishedDirectory, "support", "Runtime", path));
            try { Assert.NotEqual(nint.Zero, NativeLibrary.GetExport(library, symbol)); }
            finally { NativeLibrary.Free(library); }
        }
    }

    [PortableFact]
    [Trait("Category", "Portable")]
    public async Task BothAppHostsUseTheBundledRuntimeAfterRelocation()
    {
        using var folder = new TestFolder();
        var relocated = Path.Combine(folder.Root, "AudioMemo \u4fbf\u643a\u7248");
        Directory.CreateDirectory(relocated);
        foreach (var directory in Directory.EnumerateDirectories(PublishedDirectory, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(relocated, Path.GetRelativePath(PublishedDirectory, directory)));
        foreach (var file in Directory.EnumerateFiles(PublishedDirectory, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(relocated, Path.GetRelativePath(PublishedDirectory, file)));

        var runtimeDirectory = Path.Combine(relocated, "support", "Runtime");
        foreach (var executable in new[] { Path.Combine(relocated, "AudioMemo.exe"), Path.Combine(runtimeDirectory, "AudioMemo.exe") })
        {
            var start = new ProcessStartInfo(executable) { WorkingDirectory = folder.Root };
            start.ArgumentList.Add("--speech-worker");
            start.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
            start.Environment["DOTNET_HOST_TRACE"] = "1";
            start.Environment.Remove("DOTNET_HOST_TRACEFILE");
            start.Environment.Remove("COREHOST_TRACEFILE");
            start.Environment["DOTNET_ROOT"] = Path.Combine(folder.Root, "no-global-runtime");
            start.Environment["DOTNET_ROOT_X64"] = start.Environment["DOTNET_ROOT"];

            var result = await ProcessRunner.RunAsync(start, TimeSpan.FromSeconds(30), CancellationToken.None);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Expected --speech-worker <model> <16kHz-WAV> <language>.", result.Error);
            Assert.Contains($"Resolved fxr [{Path.Combine(runtimeDirectory, "hostfxr.dll")}]", result.Error);
            Assert.Contains($"CoreCLR path = '{Path.Combine(runtimeDirectory, "coreclr.dll")}'", result.Error);
        }
        var mediaDirectory = Path.Combine(runtimeDirectory, "FFmpeg");
        var video = await VideoImportTests.MakeVideoAsync(folder, "mp4", ffmpeg: Path.Combine(mediaDirectory, "ffmpeg.exe"));
        var videos = new VideoAudioService(mediaDirectory);
        var information = await videos.ProbeAsync(video, CancellationToken.None);
        Assert.Single(information.AudioTracks);
        var imported = await new AudioImportService(videos).ImportAsync(video, folder.File("video-library"),
            new Progress<double>(), CancellationToken.None);
        Assert.InRange((double)imported.Frames / AudioMemo.Core.Audio.PcmFormat.SampleRate, 1.9, 2.15);
        if (Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEECH") is { Length: > 0 } speech)
        {
            Directory.Move(Path.Combine(runtimeDirectory, "runtimes", "vulkan"), Path.Combine(folder.Root, "disabled-vulkan"));
            await using var fallback = await new LocalTranscriber(Path.Combine(relocated, "AudioMemo.exe"))
                .OpenAsync(SpeechModels.Get("base"), "en", "auto", true, CancellationToken.None);
            Assert.StartsWith("Cpu", fallback.Runtime);
            Assert.False(string.IsNullOrWhiteSpace(fallback.StartupWarning));
            Assert.NotEmpty(await fallback.TranscribeAsync(speech, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new LocalTranscriber(Path.Combine(relocated, "AudioMemo.exe"))
                    .OpenAsync(SpeechModels.Get("base"), "en", "vulkan", true, CancellationToken.None));
        }
    }
}

public sealed class PortableFactAttribute : FactAttribute
{
    public PortableFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_PORTABLE")))
            Skip = "Set AUDIOMEMO_TEST_PORTABLE to the published top-level AudioMemo.exe to check layout and relocation.";
    }
}
