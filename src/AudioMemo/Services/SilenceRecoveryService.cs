using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

internal sealed record RecoveryAudioSource(RecordingSession Session, AudioFrameRange Range);

public sealed class SilenceRecoveryContext
{
    private readonly TranscriptDocument currentTranscript;
    private readonly TranscriptDocument originalTranscript;
    private readonly IReadOnlyList<(RecordingSession Session, string Fingerprint, string TranscriptFingerprint)> history;
    internal IReadOnlyList<RecoveryAudioSource> AudioSources { get; }
    public RecordingSession Current { get; }
    public RecordingSession Original { get; }
    public IReadOnlyList<AudioFrameRange> MutedRanges { get; }

    internal SilenceRecoveryContext(RecordingSession current, RecordingSession original,
        IReadOnlyList<AudioFrameRange> ranges, IReadOnlyList<RecordingSession> chain, IReadOnlyList<RecoveryAudioSource> audioSources)
    {
        Current = current;
        Original = original;
        MutedRanges = ranges;
        AudioSources = audioSources;
        currentTranscript = AppStorage.LoadTranscript(current);
        originalTranscript = ComposeOriginalTranscript(audioSources, current.Frames);
        if (currentTranscript.IsDraft || originalTranscript.IsDraft ||
            currentTranscript.ProcessedFrames != current.Frames || originalTranscript.ProcessedFrames != current.Frames)
            throw new InvalidOperationException(UiText.Get("RestoreNeedsTranscript"));
        history = chain.Select(session => (session, Fingerprint(session),
            AudioEditPlan.Fingerprint(AppStorage.LoadTranscript(session)))).ToArray();
    }

    public AudioRestorePlan Plan(IReadOnlyList<AudioFrameRange> selection) =>
        new(currentTranscript, originalTranscript, Current.Frames, MutedRanges, selection);

    internal AudioRestorePlan CompletePlan() =>
        new(currentTranscript, originalTranscript, Current.Frames, MutedRanges, MutedRanges, allowEmptySelection: true);

    public string DescribeRange(AudioFrameRange range) => string.Join(" ", originalTranscript.Segments
        .Where(segment => segment.StartSeconds < (double)range.EndFrame / PcmFormat.SampleRate &&
            segment.EndSeconds > (double)range.StartFrame / PcmFormat.SampleRate)
        .Select(segment => (segment.SpeakerId is null ? "" : originalTranscript.SpeakerName(segment.SpeakerId) + ": ") + segment.Text));

    public IReadOnlyList<AudioFrameRange> MutedRows()
    {
        var result = new List<AudioFrameRange>();
        var rows = TranscriptPresentation.Rows(originalTranscript);
        foreach (var muted in MutedRanges)
        {
            var intervals = rows.Select(row => new AudioFrameRange(
                Math.Max(muted.StartFrame, AudioEditPlan.FrameAt(row.StartSeconds, Current.Frames)),
                Math.Min(muted.EndFrame, AudioEditPlan.FrameAt(row.EndSeconds, Current.Frames))))
                .Where(range => range.Length > 0).OrderBy(range => range.StartFrame).ToArray();
            var merged = new List<AudioFrameRange>();
            foreach (var range in intervals)
            {
                if (merged.Count > 0 && range.StartFrame < merged[^1].EndFrame)
                    merged[^1] = merged[^1] with { EndFrame = Math.Max(merged[^1].EndFrame, range.EndFrame) };
                else
                    merged.Add(range);
            }
            var cursor = muted.StartFrame;
            foreach (var range in merged)
            {
                if (range.StartFrame > cursor)
                    result.Add(new(cursor, range.StartFrame));
                result.Add(range);
                cursor = range.EndFrame;
            }
            if (cursor < muted.EndFrame)
                result.Add(new(cursor, muted.EndFrame));
        }
        return result;
    }

    internal void Verify(AudioRestorePlan plan)
    {
        foreach (var item in history)
        {
            var session = JsonSerializer.Deserialize<RecordingSession>(AtomicFile.ReadText(item.Session.MetadataPath))
                ?? throw new InvalidDataException(UiText.Get("RestoreHistoryChanged"));
            if (Fingerprint(session) != item.Fingerprint ||
                AudioEditPlan.Fingerprint(AppStorage.LoadTranscript(item.Session)) != item.TranscriptFingerprint)
                throw new InvalidOperationException(UiText.Get("RestoreHistoryChanged"));
        }
        if (!plan.MatchesCurrent(AppStorage.LoadTranscript(Current)) ||
            !plan.MatchesOriginal(originalTranscript))
            throw new InvalidOperationException(UiText.Get("RestoreHistoryChanged"));
    }

    private static string Fingerprint(RecordingSession session) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(session)));

    private static TranscriptDocument ComposeOriginalTranscript(IReadOnlyList<RecoveryAudioSource> sources, long frames)
    {
        var documents = sources.Select(source => (source, document: AppStorage.LoadTranscript(source.Session))).ToArray();
        var result = documents[0].document.Copy();
        result.Segments = [];
        result.SpeakerNames.Clear();
        result.SpeakerColors.Clear();
        result.Me = null;
        result.ProcessedFrames = frames;
        result.IsDraft = false;
        var usedIds = documents.SelectMany(item => item.document.Segments)
            .Select(segment => segment.SpeakerId).OfType<string>().ToHashSet();
        foreach (var (source, document) in documents)
        {
            if (document.ProcessedFrames < source.Range.EndFrame ||
                document.IsDraft && source.Session.PreservedTranscriptFrames < source.Range.EndFrame)
                throw new InvalidOperationException(UiText.Get("RestoreNeedsTranscript"));
            var start = (double)source.Range.StartFrame / AudioMemo.Core.Audio.PcmFormat.SampleRate;
            var end = (double)source.Range.EndFrame / AudioMemo.Core.Audio.PcmFormat.SampleRate;
            var segments = document.Segments.Where(segment => segment.StartSeconds < end && segment.EndSeconds > start).ToArray();
            if (segments.Any(segment => segment.StartSeconds < start || segment.EndSeconds > end))
                throw new InvalidOperationException(UiText.Get("RestoreOverlapSelection"));
            foreach (var id in segments.Select(segment => segment.SpeakerId).OfType<string>().Distinct())
            {
                if (result.Segments.Any(segment => segment.SpeakerId == id) &&
                    !document.Segments.Any(segment => segment.EndSeconds <= start && segment.SpeakerId == id &&
                        result.Segments.Any(previous => previous.StartSeconds == segment.StartSeconds &&
                            previous.EndSeconds == segment.EndSeconds && previous.SpeakerId == id)))
                {
                    // A newly recorded voice can reuse an ID that existed only in previously muted text.
                    var number = 1;
                    while (usedIds.Contains("spkr" + number))
                        number++;
                    var renamed = "spkr" + number;
                    usedIds.Add(renamed);
                    result.Segments = result.Segments.Select(segment =>
                        segment.SpeakerId == id ? segment with { SpeakerId = renamed } : segment).ToList();
                    if (result.SpeakerNames.Remove(id, out var name))
                        result.SpeakerNames[renamed] = name;
                    if (result.SpeakerColors.Remove(id, out var color))
                        result.SpeakerColors[renamed] = color;
                }
                if (document.SpeakerNames.TryGetValue(id, out var speakerName))
                    result.SpeakerNames[id] = speakerName;
                if (document.SpeakerColors.TryGetValue(id, out var speakerColor))
                    result.SpeakerColors[id] = speakerColor;
            }
            result.Segments.AddRange(segments);
            result.SpeakersIdentified &= document.SpeakersIdentified;
        }
        return result;
    }
}

public sealed class SilenceRecoveryService
{
    public Task<SilenceRecoveryContext> PrepareAsync(string library, RecordingSession selected, CancellationToken token) =>
        Task.Run(() => Prepare(library, selected, token), token);

    internal static SilenceRecoveryContext Prepare(string library, RecordingSession selected, CancellationToken token, bool allowUnmuted = false)
    {
        var roots = new[] { Path.GetFullPath(library), Directory.GetParent(Path.GetFullPath(selected.Folder))!.FullName }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var sessions = new Dictionary<string, RecordingSession>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            var loaded = AppStorage.LoadSessions(root);
            if (loaded.Errors.Count > 0)
                throw new InvalidDataException(UiText.Format("HistoryReadFailed", string.Join("; ", loaded.Errors)));
            foreach (var item in loaded.Sessions)
            {
                if (sessions.TryGetValue(item.Id, out var duplicate) &&
                    !Path.GetFullPath(item.Folder).Equals(Path.GetFullPath(duplicate.Folder), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
                sessions[item.Id] = item;
            }
        }
        if (!sessions.TryGetValue(selected.Id, out var current) ||
            !Path.GetFullPath(current.Folder).Equals(Path.GetFullPath(selected.Folder), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(UiText.Get("RestoreHistoryChanged"));
        if (current.Status is "Recording" or "Paused" or "Interrupted" || current.Frames <= 0)
            throw new InvalidOperationException(UiText.Get("ReplayAfterStop"));
        var chain = new List<RecordingSession>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var original = current;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(original.Id) || original.Frames <= 0 ||
                original.Status is "Recording" or "Paused" or "Interrupted")
                throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
            chain.Add(original);
            if (original.Edit is null || original.Edit.Mode == "Cut")
                break;
            if (original.Edit.Mode is not ("Silence" or "Restore" or AudioWorkspaceService.Mode) || !original.HasAudioPrefix(original.Edit.SourceFrames))
                throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
            var parent = original.Edit.Mode == AudioWorkspaceService.Mode ? AudioWorkspaceService.LoadSource(original) :
                sessions.GetValueOrDefault(original.Edit.SourceSessionId);
            if (parent is null)
                throw new FileNotFoundException(UiText.Format("RestoreSourceMissing", original.Edit.SourceTitle));
            if (!parent.HasAudioPrefix(original.Edit.SourceFrames))
                throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
            original = parent;
        }
        var audioSources = new List<RecoveryAudioSource> { new(original, new(0, original.Frames)) };
        IReadOnlyList<AudioFrameRange> muted = [];
        foreach (var session in chain.AsEnumerable().Reverse().Skip(1))
        {
            var edit = session.Edit!;
            var inheritedFrames = edit.SourceFrames;
            audioSources = audioSources.Where(source => source.Range.StartFrame < inheritedFrames)
                .Select(source => source with { Range = source.Range with { EndFrame = Math.Min(source.Range.EndFrame, inheritedFrames) } })
                .ToList();
            if (session.Frames > inheritedFrames)
                audioSources.Add(new(session, new(inheritedFrames, session.Frames)));
            muted = muted.Where(range => range.StartFrame < inheritedFrames)
                .Select(range => range with { EndFrame = Math.Min(range.EndFrame, inheritedFrames) }).ToArray();
            if (edit.Mode == AudioWorkspaceService.Mode)
            {
                if (edit.RestoredRanges.Count != 0)
                    throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
                muted = AudioRangeSet.Normalize(edit.RemovedRanges, inheritedFrames);
            }
            else if (edit.Mode == "Silence")
            {
                if (edit.RemovedRanges.Count == 0 || edit.RestoredRanges.Count != 0)
                    throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
                muted = AudioRangeSet.Normalize(muted.Concat(edit.RemovedRanges), inheritedFrames);
            }
            else
            {
                if (edit.RemovedRanges.Count != 0 || edit.RestoredRanges.Count == 0 ||
                    AudioRangeSet.Subtract(edit.RestoredRanges, muted, inheritedFrames).Count != 0)
                    throw new InvalidDataException(UiText.Get("RestoreHistoryInvalid"));
                muted = AudioRangeSet.Subtract(muted, edit.RestoredRanges, inheritedFrames);
            }
        }
        if (muted.Count == 0 && !allowUnmuted)
            throw new InvalidOperationException(UiText.Get("NoMutedSections"));
        foreach (var source in audioSources)
            if (!File.Exists(source.Session.WavePath))
                throw new FileNotFoundException(UiText.Format("RestoreSourceMissing", source.Session.Title), source.Session.WavePath);
        return new(current, original, muted, chain, audioSources);
    }

    public Task<RecordingSession> CreateAsync(string library, SilenceRecoveryContext recovery, AudioRestorePlan plan,
        string title, string format, IProgress<SessionEditProgress> progress, CancellationToken token) =>
        Task.Run(() =>
        {
            recovery.Verify(plan);
            using var current = new FileStream(recovery.Current.WavePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            var edit = new RecordingEdit(recovery.Current.Id, recovery.Current.Title, recovery.Current.CreatedAt,
                recovery.Current.Frames, [])
            {
                Mode = "Restore",
                RestoredRanges = plan.RestoredRanges.ToList()
            };
            var originals = new List<AudioRestoreSource>();
            try
            {
                foreach (var source in recovery.AudioSources)
                    originals.Add(new(new FileStream(source.Session.WavePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.SequentialScan), source.Session.Frames, source.Range));
                return EditedSessionService.PublishCopy(library, recovery.Current, title, format, plan.CreateTranscript(), edit,
                    (path, copyProgress) => AudioSilenceWriter.Restore(current, originals, path, plan, copyProgress, token),
                    () => recovery.Verify(plan), progress, token);
            }
            finally
            {
                foreach (var original in originals)
                    original.Audio.Dispose();
            }
        }, token);

    public Task<RecordingSession> SaveAsync(string library, SilenceRecoveryContext recovery, AudioRestorePlan plan,
        IProgress<SessionEditProgress> progress, CancellationToken token) =>
        Task.Run(() => AudioWorkspaceService.Restore(library, recovery, plan, progress, token), token);
}
