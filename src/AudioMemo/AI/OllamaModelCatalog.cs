using AudioMemo.Core.Localization;

namespace AudioMemo.AI;

public sealed record LocalModelRecommendation(string Model, string DescriptionKey, string PageUrl)
{
    public string Label => Model + " - " + UiText.Get(DescriptionKey);
    public string Command => "ollama pull " + Model;
    public override string ToString() => Label;
}

public static class OllamaModelCatalog
{
    public const string DownloadUrl = "https://ollama.com/download/windows";
    public static IReadOnlyList<LocalModelRecommendation> Recommendations { get; } =
    [
        new("qwen2.5:7b", "ModelGuideQwenSmall", "https://ollama.com/library/qwen2.5:7b"),
        new("qwen3:8b", "ModelGuideQwenSmall", "https://ollama.com/library/qwen3:8b"),
        new("qwen3.5:9b", "ModelGuideQwenModern", "https://ollama.com/library/qwen3.5:9b"),
        new("deepseek-r1:8b", "ModelGuideDeepSeekSmall", "https://ollama.com/library/deepseek-r1:8b"),
        new("deepseek-r1:14b", "ModelGuideMedium", "https://ollama.com/library/deepseek-r1:14b"),
        new("qwen3:30b", "ModelGuideLarge", "https://ollama.com/library/qwen3:30b"),
        new("deepseek-r1:32b", "ModelGuideLarge", "https://ollama.com/library/deepseek-r1:32b"),
        new("glm4:9b", "ModelGuideGlm", "https://ollama.com/library/glm4:9b"),
        new("glm-4.7-flash:latest", "ModelGuideLarge", "https://ollama.com/library/glm-4.7-flash"),
        new("deepseek-r1:70b", "ModelGuideWorkstation", "https://ollama.com/library/deepseek-r1:70b"),
        new("deepseek-r1:671b", "ModelGuideServer", "https://ollama.com/library/deepseek-r1:671b")
    ];
}
