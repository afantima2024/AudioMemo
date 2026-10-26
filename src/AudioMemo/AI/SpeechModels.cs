using System.IO;
using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo.AI;

public sealed record SpeechModel(string Id, string LabelResource, long Size, string Sha256)
{
    public string Label => UiText.Get(LabelResource);
    public string FileName => $"ggml-{Id}.bin";
    public string Path => System.IO.Path.Combine(AppStorage.ModelsFolder, FileName);
    public string DownloadUrl => $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}";
    public override string ToString() => Label;
}

public static class SpeechModels
{
    public static IReadOnlyList<SpeechModel> Available { get; } =
    [
        new("base", "ModelBase", 147_951_465,
            "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe"),
        new("small", "ModelSmall", 487_601_967,
            "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"),
        new("large-v3-turbo-q5_0", "ModelTurbo", 574_041_195,
            "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2")
    ];

    public static SpeechModel Get(string id) =>
        Available.FirstOrDefault(model => model.Id == id)
            ?? throw new ArgumentException("Unsupported speech model.", nameof(id));

    public static bool IsDownloaded(string id)
    {
        var model = Get(id);
        return File.Exists(model.Path) && new FileInfo(model.Path).Length == model.Size;
    }

    public static async Task VerifyAsync(SpeechModel model, CancellationToken cancellationToken)
    {
        if (!IsDownloaded(model.Id))
            throw new FileNotFoundException(UiText.Get("DownloadModelFirst"));
        await ModelFiles.VerifyAsync(model.Path, model.Size, model.Sha256, cancellationToken).ConfigureAwait(false);
    }

    public static Task DownloadAsync(string id, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var model = Get(id);
        return ModelFiles.DownloadAsync(model.DownloadUrl, model.Path, model.Size, model.Sha256, progress, cancellationToken);
    }
}
