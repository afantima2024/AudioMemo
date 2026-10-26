using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

public sealed record OnlineComparisonRow(int Index, long StartFrame, long EndFrame, string LocalText, string OnlineText,
    bool LocalUnrecognized = false);
public sealed record OnlineComparison(string SessionId, long Frames, AudioSourceVersion AudioVersion, string Provider,
    string Model, string Endpoint, DateTimeOffset CreatedAt, List<OnlineComparisonRow> Rows);

public static class OnlineComparisonStore
{
    private static string FilePath(RecordingSession session, TranscriptCorrectionPlan plan)
    {
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan.Indices)));
        return Path.Combine(session.Folder, "online-comparisons", key + ".json");
    }

    public static OnlineComparison? Load(RecordingSession session, TranscriptCorrectionPlan plan)
    {
        var path = FilePath(session, plan);
        if (!File.Exists(path))
            return null;
        var comparison = JsonSerializer.Deserialize<OnlineComparison>(AtomicFile.ReadText(path))
            ?? throw new InvalidDataException(UiText.Get("OnlineComparisonInvalid"));
        Validate(session, plan, comparison);
        return comparison;
    }

    public static void Save(RecordingSession session, TranscriptCorrectionPlan plan, OnlineComparison comparison,
        CancellationToken cancellationToken)
    {
        using var audio = TranscriptCorrectionService.OpenAudio(session, plan);
        Validate(session, plan, comparison);
        var path = FilePath(session, plan);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteJson(path, comparison, cancellationToken);
    }

    private static void Validate(RecordingSession session, TranscriptCorrectionPlan plan, OnlineComparison comparison)
    {
        if (comparison.SessionId != session.Id || comparison.Frames != plan.SourceFrames ||
            comparison.AudioVersion != AudioSourceVersion.Read(session.WavePath))
            throw new InvalidOperationException(UiText.Get("OnlineComparisonStale"));
        OnlineSpeechCatalog.Get(comparison.Provider, comparison.Model);
        OnlineSpeechCatalog.Endpoint(comparison.Provider, comparison.Endpoint);
        if (comparison.Rows is null || comparison.Rows.Count != plan.Segments.Count)
            throw new InvalidDataException(UiText.Get("OnlineComparisonInvalid"));
        for (var index = 0; index < comparison.Rows.Count; index++)
        {
            var row = comparison.Rows[index];
            var selected = plan.Segments[index];
            if (row is null || row.Index != plan.Indices[index] ||
                row.StartFrame != AudioEditPlan.FrameAt(selected.StartSeconds, plan.SourceFrames) ||
                row.EndFrame != AudioEditPlan.FrameAt(selected.EndSeconds, plan.SourceFrames))
                throw new InvalidOperationException(UiText.Get("OnlineComparisonStale"));
            if (string.IsNullOrWhiteSpace(row.LocalText) || row.LocalText.Length > TranscriptCorrectionPlan.MaximumTextLength ||
                row.OnlineText is null || row.OnlineText.Length > TranscriptCorrectionPlan.MaximumTextLength ||
                row.OnlineText.Any(character => char.IsControl(character) && character is not ('\t' or '\n' or '\r')))
                throw new InvalidDataException(UiText.Get("OnlineComparisonInvalid"));
        }
    }
}
