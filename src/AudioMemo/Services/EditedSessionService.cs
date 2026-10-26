using System.IO;
using System.Text.Json;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

public enum SessionEditStage { CopyingAudio, EncodingMp3, SavingCopy, EncodingWave }
public sealed record SessionEditProgress(SessionEditStage Stage, double? Percent = null);

public sealed class EditedSessionService
{
    public Task<RecordingSession> SaveAsync(string library, RecordingSession source, AudioEditPlan plan,
        string title, string format, IProgress<SessionEditProgress> progress, CancellationToken cancellationToken,
        bool? createCopy = null) =>
        Task.Run(() => !(createCopy ?? !AudioWorkspaceService.ReusesSession(source))
            ? AudioWorkspaceService.Mute(library, source, plan, progress, cancellationToken)
            : AudioWorkspaceService.Create(library, source, plan, title, format, progress, cancellationToken), cancellationToken);

    public Task<RecordingSession> CreateAsync(string library, RecordingSession source, AudioEditPlan plan,
        string title, string format, IProgress<SessionEditProgress> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Create(library, source, plan, title, format, progress, cancellationToken), cancellationToken);

    private static RecordingSession Create(string library, RecordingSession source, AudioEditPlan plan,
        string title, string format, IProgress<SessionEditProgress> progress, CancellationToken cancellationToken)
    {
        var original = VerifySource(source, plan);
        using var input = new FileStream(original.WavePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        return PublishCopy(library, original, title, format, plan.CreateTranscript(),
            new(original.Id, original.Title, original.CreatedAt, original.Frames, plan.MutedRanges.ToList()) { Mode = "Silence" },
            (path, copyProgress) => AudioSilenceWriter.Write(input, path, plan, copyProgress, cancellationToken),
            () => VerifySource(source, plan), progress, cancellationToken);
    }

    internal static RecordingSession PublishCopy(string library, RecordingSession original, string title, string format,
        TranscriptDocument transcript, RecordingEdit edit, Action<string, IProgress<double>> writeWave,
        Action verifySource, IProgress<SessionEditProgress> progress, CancellationToken cancellationToken,
        Action<RecordingSession>? prepareCopy = null)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 160 || title.Any(char.IsControl))
            throw new ArgumentException(UiText.Get("InvalidSessionTitle"));
        if (format is not ("WAV" or "MP3"))
            throw new ArgumentException(UiText.Get("EditInvalidFormat"));
        library = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library));
        var sourceFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(original.Folder));
        if (library.Equals(sourceFolder, StringComparison.OrdinalIgnoreCase) ||
            library.StartsWith(sourceFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("EditLibraryInsideSource"));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(library);
        var session = new RecordingSession
        {
            Title = title,
            RequestedFormat = format,
            AudioOutput = original.AudioOutput.Copy(),
            Status = "Saved",
            Frames = transcript.ProcessedFrames,
            TranscriptionModel = transcript.Model,
            TranscriptionLanguage = transcript.Language,
            Devices = [.. original.Devices],
            Edit = edit
        };
        var destination = Path.Combine(library, $"{session.CreatedAt:yyyy-MM-dd_HH-mm-ss}_{session.Id[..8]}");
        var staging = Path.Combine(library, AppStorage.EditStagingPrefix + session.Id);
        if (Directory.Exists(destination) || Directory.Exists(staging))
            throw new IOException(UiText.Get("EditDestinationExists"));
        session.Folder = staging;
        Directory.CreateDirectory(staging);
        var committed = false;
        try
        {
            progress.Report(new(SessionEditStage.CopyingAudio, 0));
            writeWave(session.WavePath, new CopyProgress(progress));
            if (format == "MP3" || !session.AudioOutput.UsesSafetyFormat)
            {
                progress.Report(new(format == "MP3" ? SessionEditStage.EncodingMp3 : SessionEditStage.EncodingWave));
                AudioExport.SaveConfigured(session, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new(SessionEditStage.SavingCopy));
            AppStorage.SaveTranscript(session, transcript);
            AtomicFile.WriteText(session.NotesPath, File.Exists(original.NotesPath) ? AtomicFile.ReadText(original.NotesPath) : "");
            PersonalSummaryStore.CopyNotesTracking(original, session);
            prepareCopy?.Invoke(session);
            verifySource();
            cancellationToken.ThrowIfCancellationRequested();
            // Publish the complete folder in one operation; old summaries are deliberately not copied.
            Directory.Move(staging, destination);
            committed = true;
            session.Folder = destination;
            return session;
        }
        finally
        {
            if (!committed && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    internal static RecordingSession VerifySource(RecordingSession source, AudioEditPlan plan)
    {
        var current = JsonSerializer.Deserialize<RecordingSession>(AtomicFile.ReadText(source.MetadataPath))
            ?? throw new InvalidDataException(UiText.Get("EditSourceChanged"));
        current.Folder = source.Folder;
        if (current.Id != source.Id || current.Frames != plan.SourceFrames ||
            current.Status is "Recording" or "Paused" or "Interrupted" ||
            !plan.Matches(AppStorage.LoadTranscript(current)))
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        return current;
    }

    private sealed class CopyProgress(IProgress<SessionEditProgress> progress) : IProgress<double>
    {
        public void Report(double value) => progress.Report(new(SessionEditStage.CopyingAudio, value));
    }
}
