using System.Diagnostics;
using System.Text.Json;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class SpeakerIntegrationTests
{
    [SpeakerFact]
    [Trait("Category", "Speakers")]
    public async Task PublicFourSpeakerSampleProducesPersistentRenameableLabels()
    {
        await SpeakerModels.VerifyAsync(CancellationToken.None);
        var executable = Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_WORKER")!;
        var start = new ProcessStartInfo(executable);
        foreach (var argument in new[] { "--speaker-worker", SpeakerModels.Segmentation, SpeakerModels.Embedding,
            Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEAKERS")!, "4" })
            start.ArgumentList.Add(argument);
        var result = await ProcessRunner.RunAsync(start, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.Error);
        var turns = JsonSerializer.Deserialize<SpeakerTurn[]>(result.Output)!;
        Assert.Equal(4, turns.Select(turn => turn.Speaker).Distinct().Count());
        var transcript = new TranscriptDocument
        {
            Segments = turns.OrderBy(turn => turn.Start).Select(turn =>
                new TranscriptSegment(turn.Start, turn.End, "Synthetic alignment check.")).ToList()
        };
        SpeakerAssignment.Apply(transcript, turns);
        Assert.Equal(new[] { "spkr1", "spkr2", "spkr3", "spkr4" },
            transcript.Segments.Select(segment => segment.SpeakerId).Distinct().Order());
        using var folder = new TestFolder();
        var session = AppStorage.CreateSession(new() { SaveFolder = folder.Root }, "Public speaker labels");
        AppStorage.SaveTranscript(session, transcript);
        AppStorage.RenameSpeaker(session, "spkr1", "Presenter");
        var saved = AppStorage.LoadTranscript(session);
        Assert.True(saved.SpeakersIdentified);
        Assert.Contains("Presenter:", saved.ToText());
        Assert.Equal(saved.ToText(), ChineseText.ToSimplified(saved.ToText()));
    }
}

public sealed class SpeakerFactAttribute : FactAttribute
{
    public SpeakerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_SPEAKERS")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUDIOMEMO_TEST_WORKER")))
            Skip = "Set AUDIOMEMO_TEST_SPEAKERS to the official public four-speaker WAV and AUDIOMEMO_TEST_WORKER to AudioMemo.exe.";
    }
}
