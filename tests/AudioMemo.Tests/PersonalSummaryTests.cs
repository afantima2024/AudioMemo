using System.Text.Json;
using System.Text.Json.Nodes;
using AudioMemo.AI;
using AudioMemo.Core.AI;
using AudioMemo.Core.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class PersonalSummaryTests
{
    [Fact]
    public async Task FullTranscriptAndIdentityAreUsedWithoutReadingPersonalNotesIntoThePrompt()
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        var before = EditedSessionTests.Hashes(session.Folder);
        var capture = new Capture();
        var settings = OllamaTests.LocalSettings();
        var generation = Guid.NewGuid().ToString("N");
        var updates = new Updates();
        var result = await Run(session, settings, capture, generation, updates);
        var request = Assert.Single(capture.Requests);
        Assert.Contains("My first contribution", request.Data.GetProperty("transcript").GetString());
        Assert.Contains("Alex agreed to review", request.Data.GetProperty("transcript").GetString());
        Assert.Contains("UNRELATED OTHER TOPIC", request.Data.GetProperty("transcript").GetString());
        Assert.Equal("spkr1", request.Data.GetProperty("me").GetProperty("speakerId").GetString());
        Assert.Equal("Al", request.Data.GetProperty("me").GetProperty("aliases")[0].GetString());
        Assert.Contains("never invent", request.Prompt);
        Assert.Contains("English or Simplified Chinese only", request.Prompt);
        Assert.Contains("untrusted DATA", request.Prompt);
        Assert.DoesNotContain("PRIVATE NOTE", request.Prompt);
        Assert.DoesNotContain("OLD SUMMARY", request.Prompt);
        Assert.DoesNotContain("PRIVATE NOTE", File.ReadAllText(PersonalSummaryStore.PathFor(session)));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder).Where(pair => pair.Key != PersonalSummaryStore.FileName).ToArray());
        Assert.Equal(settings.OllamaModel, result.Model);
        Assert.Contains(settings.OllamaModel, result.NotesSection);
        Assert.Contains("Noise", result.NotesSection);
        Assert.True(result.Completed);
        Assert.Equal(generation, result.GenerationId);
        Assert.All(updates.Values, update => Assert.Null(update.Summary));
        Assert.Contains(updates.Values, update => update.WorkProgress?.Phase == "about-me");
        var completed = EditedSessionTests.Hashes(session.Folder);
        await Run(session, settings, capture, generation);
        Assert.Single(capture.Requests);
        Assert.Equal(completed, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task SplitUtterancesKeepTheirSpeakerAndSuccessfulBatchesResumeExactlyOnce()
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        var transcript = AppStorage.LoadTranscript(session);
        transcript.Segments[0] = transcript.Segments[0] with { Text = "MY LONG WORDS " + string.Concat(Enumerable.Repeat("\U0001F600", 5000)) };
        AppStorage.SaveTranscript(session, transcript);
        var settings = OllamaTests.LocalSettings();
        var generation = Guid.NewGuid().ToString("N");
        var failed = new Capture { FailOnCall = 2 };
        await Assert.ThrowsAsync<IOException>(() => Run(session, settings, failed, generation));
        var partial = PersonalSummaryStore.Load(session)!;
        Assert.False(partial.Completed);
        Assert.True(partial.SegmentOffset > 0);
        Assert.Equal("spkr1", failed.Requests[1].Data.GetProperty("continuationSpeaker").GetProperty("speakerId").GetString());
        Assert.Equal(partial.Text, failed.Requests[1].Data.GetProperty("previousSummary").GetString());
        var resumed = new Capture();
        var result = await Run(session, settings, resumed, generation);
        Assert.True(result.Completed);
        var expected = SummaryPrompt.ResumableBatches(transcript.Segments, maximumCharacters: 4000).Select(batch => batch.Text).ToArray();
        Assert.Equal(expected, new[] { failed.Requests[0].Data.GetProperty("transcript").GetString() }
            .Concat(resumed.Requests.Select(request => request.Data.GetProperty("transcript").GetString())).ToArray());
        Assert.Equal(partial.Text, resumed.Requests[0].Data.GetProperty("previousSummary").GetString());
        Assert.Equal("spkr1", resumed.Requests[0].Data.GetProperty("continuationSpeaker").GetProperty("speakerId").GetString());
        Assert.All(resumed.Requests, request => Assert.DoesNotContain("PRIVATE NOTE", request.Prompt));
    }

    [Fact]
    public async Task FailedOrCancelledNewGenerationKeepsPreviousResultsAndNotes()
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        var settings = OllamaTests.LocalSettings();
        await Run(session, settings, new Capture(), Guid.NewGuid().ToString("N"));
        var before = EditedSessionTests.Hashes(session.Folder);
        await Assert.ThrowsAsync<IOException>(() => Run(session, settings, new Capture { FailOnCall = 1 },
            Guid.NewGuid().ToString("N")));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
        using var cancellation = new CancellationTokenSource();
        var cancelled = new Capture { StartingRequest = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(session, settings, cancelled,
            Guid.NewGuid().ToString("N"), token: cancellation.Token));
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("name")]
    [InlineData("model")]
    [InlineData("policy")]
    public async Task ChangedInputsDiscardOldInferenceButKeepSectionOwnership(string change)
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        var settings = OllamaTests.LocalSettings();
        var generation = Guid.NewGuid().ToString("N");
        var first = await Run(session, settings, new Capture(), generation);
        AtomicFile.WriteText(session.NotesPath, "PRIVATE PREFIX\n\n" + first.NotesSection + "\n\nPRIVATE SUFFIX");
        first.AppliedSection = first.NotesSection;
        first.AppliedGenerationId = generation;
        if (change == "policy")
            first.PolicyVersion++;
        PersonalSummaryStore.Save(session, first);
        if (change == "model")
            settings.OllamaModel = "new-selected-model";
        else if (change != "policy")
        {
            var transcript = AppStorage.LoadTranscript(session);
            if (change == "name")
                transcript.SpeakerNames["spkr1"] = "Corrected name";
            else
                transcript.Segments[0] = transcript.Segments[0] with { Text = "Corrected words." };
            AppStorage.SaveTranscript(session, transcript);
        }
        var notes = File.ReadAllBytes(session.NotesPath);
        var capture = new Capture();
        var updated = await Run(session, settings, capture, generation);
        Assert.Empty(Assert.Single(capture.Requests).Data.GetProperty("previousSummary").GetString()!);
        Assert.Equal(first.NotesSection, updated.AppliedSection);
        Assert.Equal(first.Provider, updated.AppliedProvider);
        Assert.Equal(first.Model, updated.AppliedModel);
        Assert.Equal(notes, File.ReadAllBytes(session.NotesPath));
    }

    [Fact]
    public async Task LegacyAppliedModelSurvivesAnIncompleteReplacementAndCopy()
    {
        using var folder = new TestFolder();
        using var copyFolder = new TestFolder();
        var session = Source(folder);
        var settings = OllamaTests.LocalSettings();
        var first = await Run(session, settings, new Capture(), Guid.NewGuid().ToString("N"));
        first.AppliedSection = first.NotesSection;
        first.AppliedGenerationId = first.GenerationId;
        var legacy = JsonSerializer.SerializeToNode(first)!.AsObject();
        legacy.Remove(nameof(PersonalSummaryDocument.AppliedProvider));
        legacy.Remove(nameof(PersonalSummaryDocument.AppliedModel));
        AtomicFile.WriteText(PersonalSummaryStore.PathFor(session), legacy.ToJsonString());
        var notes = "PRIVATE PREFIX\n\n" + first.NotesSection.Replace("Synthetic contribution 1", "PRIVATE MANUAL EDIT");
        AtomicFile.WriteText(session.NotesPath, notes);
        var previous = PersonalSummaryStore.Load(session)!;
        Assert.Equal((first.Provider, first.Model), PersonalSummaryStore.AppliedAttribution(previous, notes));

        var transcript = AppStorage.LoadTranscript(session);
        transcript.Segments[0] = transcript.Segments[0] with { Text = new string('x', 9500) };
        AppStorage.SaveTranscript(session, transcript);
        settings.OllamaModel = "new-incomplete-model";
        var capture = new Capture { FailOnCall = 2 };
        await Assert.ThrowsAsync<IOException>(() => Run(session, settings, capture, Guid.NewGuid().ToString("N")));
        var pending = PersonalSummaryStore.Load(session)!;
        Assert.False(pending.Completed);
        Assert.Equal(settings.OllamaModel, pending.Model);
        Assert.Equal(first.Provider, pending.AppliedProvider);
        Assert.Equal(first.Model, pending.AppliedModel);
        Assert.Equal((first.Provider, first.Model), PersonalSummaryStore.AppliedAttribution(pending, notes));
        Assert.Equal(notes, AtomicFile.ReadText(session.NotesPath));
        Assert.DoesNotContain("PRIVATE", AtomicFile.ReadText(PersonalSummaryStore.PathFor(session)));
        Assert.All(capture.Requests, request => Assert.DoesNotContain("PRIVATE", request.Prompt));

        var copy = Source(copyFolder);
        AtomicFile.WriteText(copy.NotesPath, notes);
        PersonalSummaryStore.CopyNotesTracking(session, copy);
        var copied = PersonalSummaryStore.Load(copy)!;
        Assert.Empty(copied.GenerationId);
        Assert.Empty(copied.Text);
        Assert.Empty(copied.Model);
        Assert.Equal((first.Provider, first.Model), PersonalSummaryStore.AppliedAttribution(copied, notes));
    }

    [Fact]
    public async Task ReadyButUnappliedResultsAreNotReportedAsTheNotesModel()
    {
        using var folder = new TestFolder();
        using var copyFolder = new TestFolder();
        var session = Source(folder);
        var result = await Run(session, OllamaTests.LocalSettings(), new Capture(), Guid.NewGuid().ToString("N"));
        var notes = AtomicFile.ReadText(session.NotesPath);
        Assert.Equal(("", ""), PersonalSummaryStore.AppliedAttribution(result, notes));
        var copy = Source(copyFolder);
        PersonalSummaryStore.CopyNotesTracking(session, copy);
        Assert.Equal(("", ""), PersonalSummaryStore.AppliedAttribution(PersonalSummaryStore.Load(copy), notes));
        Assert.Equal((result.Provider, result.Model),
            PersonalSummaryStore.AppliedAttribution(result, notes + "\n\n" + result.NotesSection));
        Assert.Equal(("", ""), PersonalSummaryStore.AppliedAttribution(result,
            notes + "\n\n" + result.NotesSection + "\n\n" + result.NotesSection));
    }

    [Theory]
    [InlineData("AppliedProvider")]
    [InlineData("AppliedModel")]
    public void NullAppliedModelMetadataIsRejected(string field)
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        AtomicFile.WriteText(PersonalSummaryStore.PathFor(session), new JsonObject { [field] = null }.ToJsonString());
        Assert.Throws<InvalidDataException>(() => PersonalSummaryStore.Load(session));
    }

    [Theory]
    [InlineData("missing-tag")]
    [InlineData("draft")]
    [InlineData("unfinished")]
    [InlineData("no-consent")]
    public async Task IneligibleInputsCannotStartAModelRequest(string invalid)
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        var transcript = AppStorage.LoadTranscript(session);
        var tag = transcript.Me!.TagId;
        var settings = OllamaTests.LocalSettings();
        if (invalid == "missing-tag")
            transcript.Me = null;
        else if (invalid == "draft")
            transcript.IsDraft = true;
        else if (invalid == "unfinished")
            transcript.ProcessedFrames--;
        else
        {
            settings.SummaryProvider = SummaryProviders.Copilot;
            settings.CloudConsent = false;
        }
        AppStorage.SaveTranscript(session, transcript);
        var before = EditedSessionTests.Hashes(session.Folder);
        var capture = new Capture();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AiPipeline(summaryService: capture)
            .SummarizeAboutMeAsync(session, settings, new Updates(), CancellationToken.None, Guid.NewGuid().ToString("N"), tag));
        Assert.Empty(capture.Requests);
        Assert.Equal(before, EditedSessionTests.Hashes(session.Folder));
    }

    [Fact]
    public async Task ChangedTagRejectsOldPendingIntentAndChangedSourceCannotPublishAnInFlightResult()
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        var transcript = AppStorage.LoadTranscript(session);
        var tag = transcript.Me!.TagId;
        transcript.Me = SelfSpeaker.Create("spkr2", "Another person");
        AppStorage.SaveTranscript(session, transcript);
        var capture = new Capture();
        var settings = OllamaTests.LocalSettings();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AiPipeline(summaryService: capture)
            .SummarizeAboutMeAsync(session, settings, new Updates(), CancellationToken.None, Guid.NewGuid().ToString("N"), tag));
        Assert.Empty(capture.Requests);
        capture.StartingRequest = () =>
        {
            var changed = AppStorage.LoadTranscript(session);
            changed.Segments[0] = changed.Segments[0] with { Text = "EXTERNAL CORRECTION" };
            AppStorage.SaveTranscript(session, changed);
        };
        var notes = File.ReadAllBytes(session.NotesPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(session, settings, capture, Guid.NewGuid().ToString("N")));
        Assert.Equal(notes, File.ReadAllBytes(session.NotesPath));
        Assert.Contains("EXTERNAL CORRECTION", AppStorage.LoadTranscript(session).ToText());
        Assert.Null(PersonalSummaryStore.Load(session));
    }

    [Fact]
    public async Task EditingCopiesOnlySectionTrackingAndInPlaceChangesCannotReuseStaleInference()
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        var settings = OllamaTests.LocalSettings();
        var first = await Run(session, settings, new Capture(), Guid.NewGuid().ToString("N"));
        AtomicFile.WriteText(session.NotesPath, "PRIVATE NOTE\n\n" + first.NotesSection);
        var transcript = AppStorage.LoadTranscript(session);
        var plan = AudioEditPlan.Create(transcript, session.Frames,
            TranscriptPresentation.Rows(transcript).Where(row => row.SpeakerId == "spkr2").ToArray());
        var copy = AudioWorkspaceService.Create(folder.Root, session, plan, "Synthetic Me edit", "WAV", new EditUpdates(), CancellationToken.None);
        Assert.Equal(File.ReadAllBytes(session.NotesPath), File.ReadAllBytes(copy.NotesPath));
        Assert.True(transcript.Me!.Matches(AppStorage.LoadTranscript(copy).Me));
        var tracked = PersonalSummaryStore.Load(copy)!;
        Assert.Empty(tracked.GenerationId);
        Assert.Empty(tracked.Text);
        Assert.False(tracked.Completed);
        Assert.Equal(first.NotesSection, tracked.AppliedSection);
        Assert.Equal(first.Provider, tracked.AppliedProvider);
        Assert.Equal(first.Model, tracked.AppliedModel);
        var result = await Run(copy, settings, new Capture(), Guid.NewGuid().ToString("N"));
        var beforeNotes = File.ReadAllBytes(copy.NotesPath);
        var document = AppStorage.LoadTranscript(copy);
        var nextPlan = AudioEditPlan.Create(document, copy.Frames, [TranscriptPresentation.Rows(document)[0]]);
        var edited = AudioWorkspaceService.Mute(folder.Root, copy, nextPlan, new EditUpdates(), CancellationToken.None);
        Assert.Equal(copy.Id, edited.Id);
        Assert.Equal(beforeNotes, File.ReadAllBytes(edited.NotesPath));
        Assert.False(PersonalSummaryStore.Load(edited)!.Matches(AppStorage.LoadTranscript(edited), settings));
        Assert.Equal(result.NotesSection, PersonalSummaryStore.Load(edited)!.NotesSection);
    }

    [Fact]
    public async Task InheritedCliModelIsPinnedWithoutChangingUserPreferences()
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        var settings = new AppSettings { SaveFolder = folder.Root, SummaryProvider = SummaryProviders.Copilot, CloudConsent = true };
        var capture = new Capture { ResolvedModel = "synthetic-cli-model" };
        var result = await Run(session, settings, capture, Guid.NewGuid().ToString("N"));
        Assert.Equal("synthetic-cli-model", result.Model);
        Assert.All(capture.Requests, request => Assert.Equal("synthetic-cli-model", request.Model));
        Assert.Empty(settings.SummaryModel);
    }

    [Fact]
    public void PendingPersonalWorkStoresIdentityAndGenerationButNotAliasesOrNotes()
    {
        using var folder = new TestFolder();
        var session = Source(folder);
        var tag = AppStorage.LoadTranscript(session).Me!.TagId;
        var generation = Guid.NewGuid().ToString("N");
        var work = ProcessingWork.ForAi("SummarizeAboutMe", OllamaTests.LocalSettings(), generation, tag);
        PendingProcessingStore.Defer(session, [work]);
        var plan = PendingProcessingStore.Load(session)!;
        var restored = Assert.Single(plan.Work);
        Assert.Equal(ProcessingWorkKind.AboutMe, restored.Kind);
        Assert.Equal(generation, restored.SummaryGenerationId);
        Assert.Equal(tag, restored.MeTagId);
        Assert.Equal("SummarizeAboutMe", restored.LabelKey);
        var json = File.ReadAllText(PendingProcessingStore.PathFor(session));
        Assert.DoesNotContain("PRIVATE NOTE", json);
        Assert.DoesNotContain("\"Names\"", json);
        foreach (var invalid in new[]
        {
            work with { MeTagId = null }, work with { MeTagId = "invalid" },
            work with { SummaryGenerationId = null }, work with { Kind = ProcessingWorkKind.MainPoints },
            work with { Kind = ProcessingWorkKind.Speakers }
        })
            Assert.Throws<InvalidDataException>(() => PendingProcessingStore.Save(session, plan with { Work = [invalid] }));
    }

    internal static RecordingSession Source(TestFolder folder)
    {
        var session = EditedSessionTests.MakeSource(folder);
        var transcript = AppStorage.LoadTranscript(session);
        transcript.Me = SelfSpeaker.Create("spkr1", "Al\nAlex");
        transcript.Segments[0] = transcript.Segments[0] with { Text = "My first contribution: I will review the draft." };
        transcript.Segments[1] = transcript.Segments[1] with { Text = "Alex agreed to review the draft; please ask Al about it." };
        transcript.Segments[3] = transcript.Segments[3] with { Text = "UNRELATED OTHER TOPIC: lunch." };
        AppStorage.SaveTranscript(session, transcript);
        return session;
    }

    private static Task<PersonalSummaryDocument> Run(RecordingSession session, AppSettings settings, Capture capture,
        string generation, Updates? updates = null, CancellationToken token = default) =>
        new AiPipeline(summaryService: capture).SummarizeAboutMeAsync(session, settings, updates ?? new(), token,
            generation, AppStorage.LoadTranscript(session).Me!.TagId);

    internal sealed record Request(string Prompt, string Model)
    {
        public JsonElement Data
        {
            get
            {
                using var json = JsonDocument.Parse(Prompt[Prompt.IndexOf("{\"me\"", StringComparison.Ordinal)..]);
                return json.RootElement.Clone();
            }
        }
    }

    internal sealed class Capture : ISummaryService
    {
        public List<Request> Requests { get; } = [];
        public int FailOnCall { get; init; }
        public int HoldOnCall { get; init; }
        public string? ResolvedModel { get; init; }
        public Action? StartingRequest { get; set; }
        public bool Cancelled { get; private set; }
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ResolveModel(AppSettings settings) => ResolvedModel ?? settings.ActiveSummaryModel;

        public async Task<string> SummarizePromptAsync(AppSettings settings, string prompt, Action<string>? warning, CancellationToken token)
        {
            Requests.Add(new(prompt, settings.ActiveSummaryModel));
            StartingRequest?.Invoke();
            token.ThrowIfCancellationRequested();
            if (Requests.Count == FailOnCall)
                throw new IOException("Synthetic personal-summary failure.");
            if (Requests.Count == HoldOnCall)
            {
                Held.TrySetResult();
                try { await Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return $"### My contributions\n- Synthetic contribution {Requests.Count} at [00:00:00].\n" +
                "### Mentions by others\n- [spkr2] asked me to review the draft at [00:00:01].";
        }

        public Task<string> SummarizeBatchAsync(AppSettings settings, string transcript, string previous, bool live,
            Action<string>? warning, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("General main points must not be changed by this feature.");
        public Task<string> SummarizeOverallAsync(AppSettings settings, string mainPoints, Action<string>? warning, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An overall summary must not be requested by this feature.");
    }

    private sealed class Updates : IProgress<AiUpdate>
    {
        public List<AiUpdate> Values { get; } = [];
        public void Report(AiUpdate value) => Values.Add(value);
    }

    private sealed class EditUpdates : IProgress<SessionEditProgress>
    {
        public void Report(SessionEditProgress value) { }
    }
}
