using System.IO;
using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

public sealed record AudioSourceVersion(long Length, DateTime LastWriteUtc)
{
    public static AudioSourceVersion Read(string path)
    {
        var file = new FileInfo(path);
        return new(file.Length, file.LastWriteTimeUtc);
    }
}

public static class TranscriptCorrectionService
{
    public static void VerifySource(RecordingSession session, TranscriptCorrectionPlan plan)
    {
        var current = JsonSerializer.Deserialize<RecordingSession>(AtomicFile.ReadText(session.MetadataPath))
            ?? throw new InvalidDataException(UiText.Get("EditSourceChanged"));
        if (current.Id != session.Id || current.Frames != plan.SourceFrames ||
            current.Status is "Recording" or "Paused" or "Interrupted" || !plan.Matches(AppStorage.LoadTranscript(session)))
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
    }

    public static FileStream OpenAudio(RecordingSession session, TranscriptCorrectionPlan plan)
    {
        VerifySource(session, plan);
        var stream = new FileStream(session.WavePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (DurableWaveFile.ValidateAndGetFrames(stream) != plan.SourceFrames ||
                stream.Length != DurableWaveFile.HeaderSize + plan.SourceFrames * PcmFormat.BlockAlign)
                throw new InvalidDataException(UiText.Get("EditSourceChanged"));
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public static TranscriptDocument Save(RecordingSession session, TranscriptCorrectionPlan plan,
        IReadOnlyList<string> texts, CancellationToken cancellationToken, AudioSourceVersion? expectedAudio = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifySource(session, plan);
        using var audio = expectedAudio is null ? null : OpenAudio(session, plan);
        if (expectedAudio is not null && expectedAudio != AudioSourceVersion.Read(session.WavePath))
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        var updated = plan.Apply(texts);
        if (plan.Matches(updated))
        {
            lock (AppStorage.TranscriptSync)
            {
                VerifySource(session, plan);
                updated.CopySpeakerColorsFrom(AppStorage.LoadTranscript(session));
                return updated;
            }
        }
        return Commit(session, plan, updated, cancellationToken);
    }

    public static TranscriptDocument SaveSpeakerAssignment(RecordingSession session, TranscriptCorrectionPlan plan,
        string targetSpeakerId, bool mergeWholeSpeaker, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifySource(session, plan);
        var updated = TranscriptSpeakerCorrection.Apply(AppStorage.LoadTranscript(session), plan, targetSpeakerId, mergeWholeSpeaker);
        return Commit(session, plan, updated, cancellationToken);
    }

    internal static TranscriptDocument Commit(RecordingSession session, TranscriptCorrectionPlan plan,
        TranscriptDocument updated, CancellationToken cancellationToken)
    {
        lock (AppStorage.TranscriptSync)
        {
            VerifySource(session, plan);
            updated.CopySpeakerColorsFrom(AppStorage.LoadTranscript(session));
            var textPath = Path.Combine(session.Folder, "transcript.txt");
            var oldText = File.Exists(textPath) ? AtomicFile.ReadText(textPath) : null;
            try
            {
                AtomicFile.WriteText(textPath, updated.ToText());
                VerifySource(session, plan);
                cancellationToken.ThrowIfCancellationRequested();
                // JSON is authoritative. Commit it last; restore the text export if this step fails.
                AtomicFile.WriteJson(session.TranscriptPath, updated);
            }
            catch (Exception original)
            {
                try
                {
                    if (oldText is null)
                        File.Delete(textPath);
                    else
                        AtomicFile.WriteText(textPath, oldText);
                }
                catch (Exception rollback)
                {
                    throw new AggregateException(UiText.Get("CorrectionSaveFailed"), original, rollback);
                }
                throw;
            }
            return updated;
        }
    }
}
