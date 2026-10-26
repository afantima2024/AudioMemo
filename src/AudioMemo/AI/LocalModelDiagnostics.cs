using AudioMemo.Core.Localization;
using AudioMemo.Services;

namespace AudioMemo.AI;

public static class LocalModelDiagnostics
{
    public const double SlowTokensPerSecond = 5;
    public static TimeSpan SlowRequestThreshold { get; } = TimeSpan.FromSeconds(30);

    public static string? MemoryWarning(long modelBytes, MemorySnapshot memory, UiText text)
    {
        if (modelBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(modelBytes));
        if (modelBytes + 4d * 1024 * 1024 * 1024 <= memory.AvailableBytes)
            return null;
        return text.FormatValue("LocalMemoryWarning", modelBytes / (1024d * 1024 * 1024),
            memory.AvailableBytes / (1024d * 1024 * 1024));
    }

    public static string? SpeedWarning(TimeSpan elapsed, double? tokensPerSecond, UiText text)
    {
        if (tokensPerSecond is >= 0 and < SlowTokensPerSecond)
            return text.FormatValue("LocalSpeedWarning", tokensPerSecond);
        return elapsed >= SlowRequestThreshold ? text["LocalSlowRequest"] : null;
    }
}
