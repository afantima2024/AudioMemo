using System.IO;
using System.Text.Json;
using AudioMemo.Audio;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using NAudio.Wave;

namespace AudioMemo.Services;

public enum HistoryExportKind { Transcript, Mp3 }
public sealed record HistoryExportProgress(int Completed, int Total, string Title);

public sealed class HistoryExportException(int completed, int total, string title, Exception inner)
    : IOException(UiText.Format("HistoryExportFailed", completed, total, title, inner.Message), inner);

public static class HistoryExportService
{
    public static string FileNameStem(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var stem = new string(title.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray()).Trim().TrimEnd('.');
        if (stem.Length > 150)
            stem = stem[..(char.IsHighSurrogate(stem[149]) ? 149 : 150)];
        if (stem.Length == 0)
            stem = "recording";
        var device = stem.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) &&
            "123456789\u00b9\u00b2\u00b3".Contains(device[3]))
            stem = "_" + stem;
        return stem;
    }

    public static Task<int> ExportAsync(IReadOnlyList<RecordingSession> sessions, string destination, HistoryExportKind kind,
        IProgress<HistoryExportProgress> progress, CancellationToken token) =>
        Task.Run(async () =>
        {
            if (sessions.Count == 0)
                throw new InvalidOperationException(UiText.Get("SelectSession"));
            destination = Path.GetFullPath(destination);
            if (!Directory.Exists(destination))
                throw new DirectoryNotFoundException(destination);
            var transcripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var session in sessions)
            {
                token.ThrowIfCancellationRequested();
                var current = JsonSerializer.Deserialize<RecordingSession>(AtomicFile.ReadText(session.MetadataPath));
                if (current is null || current.Id != session.Id || current.Frames != session.Frames ||
                    current.Title != session.Title || current.Status != session.Status)
                    throw new HistoryExportException(0, sessions.Count, session.Title,
                        new InvalidOperationException(UiText.Get("EditSourceChanged")));
                if (kind == HistoryExportKind.Transcript)
                {
                    var transcript = AppStorage.LoadTranscript(session);
                    if (transcript.Segments.Count == 0)
                        throw new HistoryExportException(0, sessions.Count, session.Title,
                            new InvalidOperationException(UiText.Get("NoTranscript")));
                    transcripts.Add(session.Folder, TranscriptPresentation.CopyText(TranscriptPresentation.Rows(transcript), true));
                }
                else if (kind != HistoryExportKind.Mp3)
                    throw new ArgumentOutOfRangeException(nameof(kind));
                else if (session.Status is "Recording" or "Paused" or "Interrupted" || session.Frames <= 0 || !File.Exists(session.WavePath))
                    throw new HistoryExportException(0, sessions.Count, session.Title,
                        new InvalidOperationException(UiText.Get("RecoverFirst")));
            }
            var completed = 0;
            foreach (var session in sessions)
            {
                token.ThrowIfCancellationRequested();
                progress.Report(new(completed, sessions.Count, session.Title));
                var extension = kind == HistoryExportKind.Mp3 ? ".mp3" : ".txt";
                var temporary = Path.Combine(destination, ".export-" + Guid.NewGuid().ToString("N") + extension);
                try
                {
                    using var audio = kind == HistoryExportKind.Mp3
                        ? new FileStream(session.WavePath, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
                    if (audio is not null && (DurableWaveFile.ValidateAndGetFrames(audio) != session.Frames ||
                        audio.Length != DurableWaveFile.HeaderSize + session.Frames * PcmFormat.BlockAlign))
                        throw new InvalidDataException(UiText.Get("EditSourceChanged"));
                    if (kind == HistoryExportKind.Transcript)
                        AtomicFile.WriteText(temporary, transcripts[session.Folder], token);
                    else if (File.Exists(session.Mp3Path) &&
                        File.GetLastWriteTimeUtc(session.Mp3Path) >= File.GetLastWriteTimeUtc(session.WavePath))
                    {
                        using var source = new FileStream(session.Mp3Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
                        using (var reader = new MediaFoundationReader(session.Mp3Path))
                        {
                            var tolerance = Math.Max(0.1, 2304d / reader.WaveFormat.SampleRate);
                            if (reader.WaveFormat.SampleRate != session.AudioOutput.SampleRate ||
                                reader.WaveFormat.Channels != session.AudioOutput.Channels ||
                                Math.Abs(reader.TotalTime.TotalSeconds - session.Duration.TotalSeconds) > tolerance ||
                                reader.Read(new byte[4096], 0, 4096) == 0)
                                throw new InvalidDataException(UiText.Get("SavedMp3Invalid"));
                        }
                        using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
                        await source.CopyToAsync(output, token).ConfigureAwait(false);
                        output.Flush(true);
                    }
                    else
                        AudioExport.ToMp3(session.WavePath, temporary, session.Frames, session.AudioOutput, token);
                    token.ThrowIfCancellationRequested();
                    var stem = FileNameStem(session.Title);
                    for (var number = 1; ; number++)
                    {
                        token.ThrowIfCancellationRequested();
                        var target = Path.Combine(destination, stem + (number == 1 ? "" : $" ({number})") + extension);
                        if (File.Exists(target) || Directory.Exists(target))
                            continue;
                        try { File.Move(temporary, target); break; }
                        catch (IOException) when (File.Exists(target) || Directory.Exists(target)) { }
                    }
                    completed++;
                    progress.Report(new(completed, sessions.Count, session.Title));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new HistoryExportException(completed, sessions.Count, session.Title, ex);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
            return completed;
        }, token);
}
