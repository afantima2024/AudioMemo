using System.IO;
using AudioMemo.Audio;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.AI;

public sealed class SelectedOnlineTranscriptionService(OnlineSpeechService? service = null, string? workDirectory = null)
{
    private readonly OnlineSpeechService service = service ?? new();
    private readonly string workDirectory = workDirectory ?? AppStorage.WorkFolder;

    public async Task<SelectedTranscriptionResult> GenerateAsync(RecordingSession session, TranscriptCorrectionPlan selection,
        AppSettings settings, bool allowSplitting, string authorizedEndpoint, IProgress<SelectedTranscriptionProgress> progress,
        CancellationToken cancellationToken)
    {
        settings.Validate();
        var profile = settings.OnlineSpeech.GetProfile();
        var model = OnlineSpeechCatalog.Get(settings.OnlineSpeech.Provider, profile.Model);
        var endpoint = OnlineSpeechCatalog.Endpoint(settings.OnlineSpeech.Provider, profile.Endpoint).AbsoluteUri;
        if (authorizedEndpoint != endpoint)
            throw new InvalidOperationException(UiText.Get("OnlineAudioConsentRequired"));
        cancellationToken.ThrowIfCancellationRequested();
        using var audio = TranscriptCorrectionService.OpenAudio(session, selection);
        var version = AudioSourceVersion.Read(session.WavePath);
        var plan = new OnlineTranscriptionPlan(selection, model, allowSplitting);
        plan.EnsureAllowed();
        Directory.CreateDirectory(workDirectory);
        var pieces = selection.Segments.Select(_ => new List<string>()).ToArray();
        var unrecognized = new HashSet<int>();
        for (var index = 0; index < plan.Chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranscriptCorrectionService.VerifySource(session, selection);
            var part = plan.Chunks[index];
            progress.Report(new(index, plan.Chunks.Count, model.Id));
            var temporary = Path.Combine(workDirectory, $"online-selection-{Guid.NewGuid():N}.wav");
            try
            {
                await Task.Run(() => AudioExport.WriteSpeechChunk(session.WavePath, temporary, part.StartFrame,
                    part.Frames, cancellationToken), cancellationToken).ConfigureAwait(false);
                var text = await service.TranscribeAsync(settings, temporary, part.Frames, authorizedEndpoint, cancellationToken)
                    .ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                    unrecognized.Add(part.Row);
                else
                    pieces[part.Row].Add(text);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        TranscriptCorrectionService.VerifySource(session, selection);
        if (version != AudioSourceVersion.Read(session.WavePath))
            throw new InvalidOperationException(UiText.Get("EditSourceChanged"));
        var texts = pieces.Select((parts, index) => parts.Count == 0 || unrecognized.Contains(index)
            ? "" : string.Join(Environment.NewLine, parts)).ToArray();
        if (texts.Any(text => text.Length > TranscriptCorrectionPlan.MaximumTextLength))
            throw new InvalidDataException(UiText.Get("InvalidCorrectionText"));
        if (texts.All(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(UiText.Get("SelectionNoSpeech"));
        progress.Report(new(plan.Chunks.Count, plan.Chunks.Count, model.Id));
        return new(texts, unrecognized.Order().ToArray(), version);
    }
}
