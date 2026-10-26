using System.Diagnostics;
using System.IO;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo.AI;

public static class SpeakerModels
{
    private const string Release = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";
    public static string Folder => Path.Combine(AppStorage.ModelsFolder, "speakers");
    public static string Segmentation => Path.Combine(Folder, "segmentation.onnx");
    public static string Embedding => Path.Combine(Folder, "embedding.onnx");
    private const long SegmentationSize = 5_992_913;
    private const long EmbeddingSize = 39_593_761;
    private const string SegmentationHash = "220AD67CA923BEF2FA91F2390C786097BF305BCEB5E261D4AF67B38E938E1079";
    private const string EmbeddingHash = "1A331345F04805BADBB495C775A6DDFFCDD1A732567D5EC8B3D5749E3C7A5E4B";

    public static bool IsDownloaded => File.Exists(Segmentation) && new FileInfo(Segmentation).Length == SegmentationSize &&
        File.Exists(Embedding) && new FileInfo(Embedding).Length == EmbeddingSize;

    public static async Task VerifyAsync(CancellationToken cancellationToken)
    {
        if (!IsDownloaded)
            throw new FileNotFoundException(UiText.Get("SpeakerModelsMissing"));
        await ModelFiles.VerifyAsync(Segmentation, SegmentationSize, SegmentationHash, cancellationToken);
        await ModelFiles.VerifyAsync(Embedding, EmbeddingSize, EmbeddingHash, cancellationToken);
    }

    public static async Task DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        var temporary = Path.Combine(Folder, "download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var archive = Path.Combine(temporary, "segmentation.tar.bz2");
            await ModelFiles.DownloadAsync(Release + "speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2",
                archive, 6_958_444, "24615EE884C897D9D2BA09BB4D30DA6BB1B15E685065962DB5B02E76E4996488",
                new Progress<double>(value => progress.Report(value * 0.15)), cancellationToken);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe"));
            foreach (var argument in new[] { "-xjf", archive, "-C", temporary,
                "sherpa-onnx-pyannote-segmentation-3-0/model.onnx", "sherpa-onnx-pyannote-segmentation-3-0/LICENSE" })
                start.ArgumentList.Add(argument);
            var result = await ProcessRunner.RunAsync(start, TimeSpan.FromMinutes(1), cancellationToken);
            if (result.ExitCode != 0)
                throw new IOException("Speaker model extraction failed: " + result.Error);
            var extracted = Path.Combine(temporary, "sherpa-onnx-pyannote-segmentation-3-0", "model.onnx");
            await ModelFiles.VerifyAsync(extracted, SegmentationSize, SegmentationHash, cancellationToken);
            await ModelFiles.DownloadAsync(Release + "speaker-recongition-models/3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
                Path.Combine(temporary, "embedding.onnx"), EmbeddingSize, EmbeddingHash,
                new Progress<double>(value => progress.Report(15 + value * 0.85)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(extracted, Segmentation, true);
            File.Move(Path.Combine(temporary, "embedding.onnx"), Embedding, true);
            File.Copy(Path.Combine(temporary, "sherpa-onnx-pyannote-segmentation-3-0", "LICENSE"),
                Path.Combine(Folder, "segmentation-LICENSE.txt"), true);
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }
}
