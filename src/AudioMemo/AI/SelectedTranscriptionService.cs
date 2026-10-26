using System.IO;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.AI;

public sealed record SelectedTranscriptionProgress(int Completed, int Total, string Model, string? Runtime = null,
    string? Warning = null, SelectedTranscriptionRow? Row = null, string? PartialText = null, double? Fraction = null);
public sealed record SelectedTranscriptionRow(int Index, string Text, bool Unrecognized);
public sealed record SelectedTranscriptionResult(IReadOnlyList<string> Texts, IReadOnlyList<int> Unrecognized,
    AudioSourceVersion AudioVersion);

public sealed class SelectedTranscriptionCheckpoint
{
    private readonly TranscriptCorrectionPlan plan;
    private readonly (string Model, string Language, string Acceleration, string Vocabulary) options;
    private readonly List<SelectedTranscriptionRow> completed = [];
    private readonly double totalFrames;
    private double processedFrames;
    private int running;
    public IReadOnlyList<SelectedTranscriptionRow> CompletedRows { get; }
    public AudioSourceVersion? AudioVersion { get; private set; }
    public string PartialText { get; private set; } = "";
    public bool IsComplete => completed.Count == plan.Segments.Count;
    public double Fraction => totalFrames == 0 ? (double)completed.Count / plan.Segments.Count :
        Math.Clamp(processedFrames / totalFrames, 0, 1);
    internal TranscriptDocument Pending { get; private set; } = new();
    internal long? NextFrame { get; private set; }

    public SelectedTranscriptionCheckpoint(TranscriptCorrectionPlan plan, AppSettings settings)
    {
        this.plan = plan;
        options = Options(settings);
        CompletedRows = completed.AsReadOnly();
        totalFrames = plan.Segments.Sum(segment => (double)(AudioEditPlan.FrameAt(segment.EndSeconds, plan.SourceFrames) -
            AudioEditPlan.FrameAt(segment.StartSeconds, plan.SourceFrames)));
    }

    internal void BeginRun()
    {
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException(UiText.Get("SessionBusy"));
    }

    internal void EndRun() => Volatile.Write(ref running, 0);

    internal void Verify(TranscriptCorrectionPlan currentPlan, AppSettings settings, AudioSourceVersion version)
    {
        if (!ReferenceEquals(plan, currentPlan) || options != Options(settings))
            throw new InvalidOperationException(UiText.Get("SelectionResumeChanged"));
        if (AudioVersion is not null && AudioVersion != version)
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        AudioVersion = version;
    }

    internal void CompleteChunk(TranscriptDocument recognized, string text, long position, long end)
    {
        Pending = recognized;
        PartialText = text;
        NextFrame = end;
        processedFrames += end - position;
    }

    internal SelectedTranscriptionRow CompleteRow(string original)
    {
        var missing = string.IsNullOrWhiteSpace(PartialText);
        var row = new SelectedTranscriptionRow(completed.Count, missing ? original : PartialText, missing);
        completed.Add(row);
        Pending = new();
        NextFrame = null;
        PartialText = "";
        return row;
    }

    private static (string, string, string, string) Options(AppSettings settings) =>
        (settings.SpeechModel, settings.Language, settings.SpeechAcceleration,
            settings.SpeechVocabularyEnabled ? SpeechVocabulary.CreatePrompt(settings.SpeechVocabularyTerms).Text : "");
}

public sealed class SelectedTranscriptionService(ILocalTranscriber? localTranscriber = null,
    Func<SpeechModel, CancellationToken, Task>? verifyModel = null)
{
    private readonly ILocalTranscriber transcriber = localTranscriber ?? new LocalTranscriber();
    private readonly Func<SpeechModel, CancellationToken, Task> verify = verifyModel ?? SpeechModels.VerifyAsync;

    public async Task<SelectedTranscriptionResult> GenerateAsync(RecordingSession session, TranscriptCorrectionPlan plan,
        AppSettings settings, IProgress<SelectedTranscriptionProgress> progress, CancellationToken cancellationToken,
        SelectedTranscriptionCheckpoint? checkpoint = null)
    {
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint ??= new(plan, settings);
        checkpoint.BeginRun();
        try
        {
            return await GenerateCoreAsync(session, plan, settings, progress, checkpoint, cancellationToken).ConfigureAwait(false);
        }
        finally { checkpoint.EndRun(); }
    }

    private async Task<SelectedTranscriptionResult> GenerateCoreAsync(RecordingSession session, TranscriptCorrectionPlan plan,
        AppSettings settings, IProgress<SelectedTranscriptionProgress> progress, SelectedTranscriptionCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        using var audio = TranscriptCorrectionService.OpenAudio(session, plan);
        var version = AudioSourceVersion.Read(session.WavePath);
        checkpoint.Verify(plan, settings, version);
        var model = SpeechModels.Get(settings.SpeechModel);
        progress.Report(new(checkpoint.CompletedRows.Count, plan.Segments.Count, model.Label,
            PartialText: checkpoint.PartialText, Fraction: checkpoint.Fraction));
        await verify(model, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(AppStorage.WorkFolder);
        var vocabulary = settings.SpeechVocabularyEnabled ? settings.SpeechVocabularyTerms : [];
        ISpeechWorker? worker = null;
        try
        {
            for (var index = checkpoint.CompletedRows.Count; index < plan.Segments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var original = plan.Segments[index];
                var first = AudioEditPlan.FrameAt(original.StartSeconds, plan.SourceFrames);
                var last = AudioEditPlan.FrameAt(original.EndSeconds, plan.SourceFrames);
                for (var position = checkpoint.NextFrame ?? first; position < last;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var start = Math.Max(first, position - AiPipeline.OverlapFrames);
                    var end = Math.Min(last, position + AiPipeline.ChunkFrames);
                    var temporary = Path.Combine(AppStorage.WorkFolder, $"speech-selection-{Guid.NewGuid():N}.wav");
                    var recognized = checkpoint.Pending.Copy();
                    try
                    {
                        var rms = await Task.Run(() => AudioExport.WriteSpeechChunk(session.WavePath, temporary,
                            start, end - start, cancellationToken), cancellationToken).ConfigureAwait(false);
                        if (rms >= 0.0001)
                        {
                            if (worker is null)
                            {
                                worker = await transcriber.OpenAsync(model, settings.Language, settings.SpeechAcceleration,
                                    false, cancellationToken, vocabulary).ConfigureAwait(false);
                                progress.Report(new(index, plan.Segments.Count, model.Label, worker.Runtime, worker.StartupWarning,
                                    PartialText: checkpoint.PartialText, Fraction: checkpoint.Fraction));
                            }
                            var segments = await worker.TranscribeAsync(temporary, cancellationToken).ConfigureAwait(false);
                            TranscriptMerger.Append(recognized, segments, (double)start / PcmFormat.SampleRate,
                                (double)position / PcmFormat.SampleRate, (double)end / PcmFormat.SampleRate);
                        }
                    }
                    catch
                    {
                        if (worker is not null)
                        {
                            var failedWorker = worker;
                            worker = null;
                            await failedWorker.DisposeAsync().ConfigureAwait(false);
                        }
                        throw;
                    }
                    finally
                    {
                        if (File.Exists(temporary))
                            File.Delete(temporary);
                    }
                    var text = TranscriptPresentation.CopyText(TranscriptPresentation.Rows(recognized));
                    if (text.Length > TranscriptCorrectionPlan.MaximumTextLength)
                        throw new InvalidDataException(UiText.Get("InvalidCorrectionText"));
                    checkpoint.CompleteChunk(recognized, text, position, end);
                    position = end;
                    if (position < last)
                        progress.Report(new(index, plan.Segments.Count, model.Label, worker?.Runtime,
                            PartialText: text, Fraction: checkpoint.Fraction));
                }
                var row = checkpoint.CompleteRow(original.Text);
                progress.Report(new(index + 1, plan.Segments.Count, model.Label, worker?.Runtime,
                    Row: row, PartialText: "", Fraction: checkpoint.Fraction));
            }
            cancellationToken.ThrowIfCancellationRequested();
            TranscriptCorrectionService.VerifySource(session, plan);
            var texts = checkpoint.CompletedRows.Select(row => row.Text).ToArray();
            var unrecognized = checkpoint.CompletedRows.Where(row => row.Unrecognized).Select(row => row.Index).ToArray();
            if (unrecognized.Length == texts.Length)
                throw new InvalidOperationException(UiText.Get("SelectionNoSpeech"));
            return new(texts, unrecognized, version);
        }
        finally
        {
            if (worker is not null)
                await worker.DisposeAsync().ConfigureAwait(false);
        }
    }
}
