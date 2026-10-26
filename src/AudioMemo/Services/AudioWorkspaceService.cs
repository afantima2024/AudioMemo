using System.IO;
using System.Text.Json;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

internal static class AudioWorkspaceService
{
    public const string Mode = "Working";
    private const string SourceFolder = ".restore-source";

    public static bool ReusesSession(RecordingSession session) => session.Edit?.Mode is "Silence" or "Restore" or Mode;

    internal static RecordingSession LoadSource(RecordingSession session)
    {
        var edit = session.Edit ?? throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
        var folder = Path.Combine(session.Folder, SourceFolder);
        var source = JsonSerializer.Deserialize<RecordingSession>(AtomicFile.ReadText(Path.Combine(folder, "session.json")))
            ?? throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
        source.Folder = folder;
        if (source.Edit is not null || source.Id != edit.SourceSessionId ||
            source.Frames != edit.SourceFrames || source.Status != "Saved")
            throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
        return source;
    }

    public static RecordingSession Create(string library, RecordingSession source, AudioEditPlan plan, string title,
        string format, IProgress<SessionEditProgress> progress, CancellationToken token)
    {
        var original = EditedSessionService.VerifySource(source, plan);
        var recovery = SilenceRecoveryService.Prepare(library, original, token, allowUnmuted: true);
        var complete = recovery.CompletePlan();
        using var input = Open(original);
        var baseline = NewSource(original, original.Frames);
        return EditedSessionService.PublishCopy(library, original, title, format, plan.CreateTranscript(),
            new(baseline.Id, original.Title, original.CreatedAt, original.Frames,
                AudioRangeSet.Normalize(recovery.MutedRanges.Concat(plan.MutedRanges), original.Frames).ToList()) { Mode = Mode },
            (path, copyProgress) => AudioSilenceWriter.Write(input, path, plan, copyProgress, token),
            () => { EditedSessionService.VerifySource(source, plan); recovery.Verify(complete); }, progress, token,
            copy =>
            {
                baseline.Folder = Path.Combine(copy.Folder, SourceFolder);
                Directory.CreateDirectory(baseline.Folder);
                WriteRestored(recovery, complete, baseline.WavePath, new CopyProgress(progress), token);
                AppStorage.SaveTranscript(baseline, complete.CreateTranscript());
            });
    }

    public static RecordingSession Mute(string library, RecordingSession selected, AudioEditPlan plan,
        IProgress<SessionEditProgress> progress, CancellationToken token)
    {
        var current = EditedSessionService.VerifySource(selected, plan);
        var recovery = SilenceRecoveryService.Prepare(library, current, token, allowUnmuted: true);
        return Update(library, recovery, plan.CreateTranscript(),
            AudioRangeSet.Normalize(recovery.MutedRanges.Concat(plan.MutedRanges), current.Frames),
            (path, updates) =>
            {
                using var audio = Open(current);
                AudioSilenceWriter.Write(audio, path, plan, updates, token);
            }, () => EditedSessionService.VerifySource(selected, plan), progress, token);
    }

    public static RecordingSession Restore(string library, SilenceRecoveryContext recovery, AudioRestorePlan plan,
        IProgress<SessionEditProgress> progress, CancellationToken token)
    {
        recovery.Verify(plan);
        return Update(library, recovery, plan.CreateTranscript(), plan.RemainingMutedRanges,
            (path, updates) => WriteRestored(recovery, plan, path, updates, token),
            () => recovery.Verify(plan), progress, token);
    }

    private static RecordingSession Update(string library, SilenceRecoveryContext recovery, TranscriptDocument transcript,
        IReadOnlyList<AudioFrameRange> muted, Action<string, IProgress<double>> writeAudio, Action verify,
        IProgress<SessionEditProgress> progress, CancellationToken token)
    {
        var current = recovery.Current;
        var loaded = AppStorage.LoadSessions(Directory.GetParent(Path.GetFullPath(current.Folder))!.FullName);
        if (loaded.Errors.Count > 0)
            throw new IOException(UiText.Format("HistoryReadFailed", string.Join("; ", loaded.Errors)));
        if (loaded.Sessions.Any(session => session.Id != current.Id && session.Edit?.SourceSessionId == current.Id))
            throw new InvalidOperationException(UiText.Get("AudioEditHasDependents"));
        if (current.Edit?.Mode != Mode && Directory.Exists(Path.Combine(current.Folder, SourceFolder)))
            throw new IOException(UiText.Get("EditDestinationExists"));
        var complete = recovery.CompletePlan();
        recovery.Verify(complete);
        var source = current.Edit?.Mode == Mode ? LoadSource(current) : NewSource(recovery.Original, current.Frames);
        var extendSource = current.Edit?.Mode != Mode || source.Frames != current.Frames;
        var updated = JsonSerializer.Deserialize<RecordingSession>(JsonSerializer.Serialize(current))!;
        using var transaction = new AudioEditTransaction(current.Folder);
        updated.Folder = transaction.Staging;
        progress.Report(new(SessionEditStage.CopyingAudio, 0));
        writeAudio(updated.WavePath, new CopyProgress(progress));
        source.Folder = Path.Combine(transaction.Staging, SourceFolder);
        Directory.CreateDirectory(source.Folder);
        source.Frames = current.Frames;
        if (extendSource)
            WriteRestored(recovery, complete, source.WavePath, new CopyProgress(progress), token);
        AppStorage.SaveTranscript(source, complete.CreateTranscript());
        updated.Edit = new(source.Id, current.Edit?.SourceTitle ?? current.Title,
            source.CreatedAt, current.Frames, muted.ToList()) { Mode = Mode };
        if (updated.RequestedFormat == "MP3" || !updated.AudioOutput.UsesSafetyFormat)
        {
            progress.Report(new(updated.RequestedFormat == "MP3" ? SessionEditStage.EncodingMp3 : SessionEditStage.EncodingWave));
            AudioExport.SaveConfigured(updated, token);
        }
        AppStorage.SaveTranscript(updated, transcript);
        token.ThrowIfCancellationRequested();
        progress.Report(new(SessionEditStage.SavingCopy));
        transaction.Commit(() => { verify(); recovery.Verify(complete); }, token);
        updated.Folder = current.Folder;
        return updated;
    }

    private static RecordingSession NewSource(RecordingSession source, long frames) => new()
    {
        Title = source.Title, CreatedAt = source.CreatedAt, Status = "Saved", Frames = frames,
        RequestedFormat = "WAV", Devices = [.. source.Devices]
    };

    private static FileStream Open(RecordingSession session)
    {
        var input = new FileStream(session.WavePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        try
        {
            if (DurableWaveFile.ValidateAndGetFrames(input) != session.Frames ||
                input.Length != DurableWaveFile.HeaderSize + session.Frames * PcmFormat.BlockAlign)
                throw new InvalidDataException(UiText.Get("EditSourceChanged"));
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    private static void WriteRestored(SilenceRecoveryContext recovery, AudioRestorePlan plan, string path,
        IProgress<double> progress, CancellationToken token)
    {
        using var current = Open(recovery.Current);
        var sources = new List<AudioRestoreSource>();
        try
        {
            foreach (var source in recovery.AudioSources)
                sources.Add(new(Open(source.Session), source.Session.Frames, source.Range));
            AudioSilenceWriter.Restore(current, sources, path, plan, progress, token);
        }
        finally
        {
            foreach (var source in sources)
                source.Audio.Dispose();
        }
    }

    private sealed class CopyProgress(IProgress<SessionEditProgress> progress) : IProgress<double>
    {
        public void Report(double value) => progress.Report(new(SessionEditStage.CopyingAudio, value));
    }
}
