using System.Security.Cryptography;
using AudioMemo.Audio;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class RecordingStartupTests
{
    private static RecordingSources Sources => new(new("mic", "Test microphone", 1), new("playback", "Test playback", 1));

    [Theory]
    [InlineData("constructor")]
    [InlineData("start")]
    [InlineData("not-running")]
    [InlineData("disconnected")]
    [InlineData("stop-error")]
    [InlineData("lost-while-starting")]
    public async Task NoUsableInputsCreateNoRecordingFilesOrHistory(string failure)
    {
        using var folder = new TestFolder();
        var factory = new RecordingEngineTests.FakeFactory();
        await using var recorder = new RecordingEngine((selection, loopback, packet, error) =>
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
            if (failure == "constructor")
                throw new IOException("Synthetic device could not be opened.");
            var input = Assert.IsType<RecordingEngineTests.FakeInput>(factory.Create(selection, loopback, packet, error));
            input.OnStart = () =>
            {
                Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
                switch (failure)
                {
                    case "start": throw new IOException("Synthetic device could not be started.");
                    case "not-running": input.RequestStop(); break;
                    case "disconnected": input.Fail(); break;
                    case "stop-error": input.RequestStop(); input.ThrowOnStop = true; break;
                    case "lost-while-starting" when loopback:
                        factory.Inputs[0].Fail();
                        input.Fail();
                        break;
                }
            };
            return input;
        });
        var warnings = new List<string>();
        recorder.Warning += warnings.Add;
        var exception = await Assert.ThrowsAsync<RecordingDeviceException>(() =>
            recorder.StartAsync(new() { SaveFolder = folder.File("recordings") }, "Failed start", Sources));
        Assert.Contains(UiText.Format("NoWorkingDevice", ""), exception.Message);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Null(recorder.Session);
        Assert.Equal(new RecordingSources(null, null), recorder.Sources);
        Assert.Equal(0, recorder.Progress.Frames);
        Assert.All(factory.Inputs, input => Assert.False(input.IsRunning));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
        Assert.Empty(AppStorage.LoadSessions(folder.File("recordings")).Sessions);
        if (failure == "stop-error")
            Assert.Contains("Test driver failed during stop.", exception.Message);
        if (failure is "disconnected" or "lost-while-starting")
            Assert.NotEmpty(warnings);
    }

    [Theory]
    [InlineData("constructor", false)]
    [InlineData("constructor", true)]
    [InlineData("start", false)]
    [InlineData("start", true)]
    [InlineData("not-running", false)]
    [InlineData("not-running", true)]
    [InlineData("disconnected", false)]
    [InlineData("disconnected", true)]
    public async Task OneHealthySourceStillStartsAndSilentAudioIsNotRejected(string failure, bool brokenPlayback)
    {
        using var folder = new TestFolder();
        var factory = new RecordingEngineTests.FakeFactory();
        await using var recorder = new RecordingEngine((selection, loopback, packet, error) =>
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
            if (loopback == brokenPlayback && failure == "constructor")
                throw new IOException("Synthetic device could not be opened.");
            var input = Assert.IsType<RecordingEngineTests.FakeInput>(factory.Create(selection, loopback, packet, error));
            input.OnStart = () =>
            {
                Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
                if (loopback != brokenPlayback)
                    return;
                switch (failure)
                {
                    case "start": throw new IOException("Synthetic device could not be started.");
                    case "not-running": input.RequestStop(); break;
                    case "disconnected": input.Fail(); break;
                }
            };
            return input;
        });
        var warnings = new List<string>();
        recorder.Warning += warnings.Add;
        var session = await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Quiet but available", Sources);
        Assert.Equal(RecorderState.Recording, recorder.State);
        Assert.NotEmpty(warnings);
        Assert.Equal(brokenPlayback ? Sources.Microphone : null, recorder.Sources.Microphone);
        Assert.Equal(brokenPlayback ? null : Sources.Playback, recorder.Sources.Playback);
        Assert.Single(factory.Inputs, input => input.IsRunning);
        Assert.Equal(session.Id, Assert.Single(AppStorage.LoadSessions(folder.Root).Sessions).Id);
        await Task.Delay(60);
        await recorder.StopAsync();
        Assert.Equal("Saved", session.Status);
        Assert.True(session.Frames > 0);
        Assert.Equal(0, recorder.Progress.Peak);
        Assert.All(factory.Inputs, input => Assert.False(input.IsRunning));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, " ")]
    [InlineData(true, "")]
    [InlineData(true, " ")]
    public async Task MissingDeviceIdsAreRejectedBeforeCaptureOrFileCreation(bool playback, string id)
    {
        using var folder = new TestFolder();
        var factory = new RecordingEngineTests.FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var missing = new SourceSelection(id, "Missing device", 1);
        var exception = await Assert.ThrowsAsync<RecordingDeviceException>(() =>
            recorder.StartAsync(new() { SaveFolder = folder.Root }, "No device", playback ? new(null, missing) : new(missing, null)));
        Assert.Equal(UiText.Format("ChooseWorkingDevice", UiText.Get(playback ? "Playback" : "Microphone")), exception.Message);
        Assert.Empty(factory.Inputs);
        Assert.Null(recorder.Session);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
    }

    [Fact]
    public async Task AFailedNewStartPreservesThePreviouslySavedSessionAndCanBeRetried()
    {
        using var folder = new TestFolder();
        var factory = new RecordingEngineTests.FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var settings = new AppSettings { SaveFolder = folder.Root };
        var previous = await recorder.StartAsync(settings, "Previously saved", Sources);
        await recorder.StopAsync();
        var before = EditedSessionTests.Hashes(previous.Folder);
        var previousSources = recorder.Sources;
        await Assert.ThrowsAsync<RecordingDeviceException>(() =>
            recorder.StartAsync(settings, "Must not appear", new(new("broken", "Unavailable microphone", 1), null)));
        Assert.Same(previous, recorder.Session);
        Assert.Equal(previousSources, recorder.Sources);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Equal(before, EditedSessionTests.Hashes(previous.Folder));
        Assert.Single(Directory.EnumerateDirectories(folder.Root));
        Assert.Equal(previous.Id, Assert.Single(AppStorage.LoadSessions(folder.Root).Sessions).Id);
        var retry = await recorder.StartAsync(settings, "Successful retry", Sources);
        Assert.Equal("Successful retry", retry.Title);
        Assert.NotEqual(previous.Id, retry.Id);
        await recorder.StopAsync();
        Assert.Equal(2, AppStorage.LoadSessions(folder.Root).Sessions.Count);
    }

    [Fact]
    public async Task AFailedResumePreservesPausedAudioAndDeviceSelectionsForRetry()
    {
        using var folder = new TestFolder();
        var factory = new RecordingEngineTests.FakeFactory();
        var fail = false;
        await using var recorder = new RecordingEngine((selection, loopback, packet, error) =>
        {
            var input = Assert.IsType<RecordingEngineTests.FakeInput>(factory.Create(selection, loopback, packet, error));
            if (fail)
                input.OnStart = input.Fail;
            return input;
        });
        var session = await recorder.StartAsync(new() { SaveFolder = folder.Root }, "Paused", Sources);
        await recorder.PauseAsync();
        var frames = recorder.Progress.Frames;
        var changes = session.Devices.ToArray();
        var before = PausedFileHashes(session);
        fail = true;
        await Assert.ThrowsAsync<RecordingDeviceException>(recorder.ResumeAsync);
        Assert.Equal(RecorderState.Paused, recorder.State);
        Assert.Same(session, recorder.Session);
        Assert.Equal(Sources, recorder.Sources);
        Assert.Equal(frames, recorder.Progress.Frames);
        Assert.Equal(changes, session.Devices);
        Assert.Equal(before, PausedFileHashes(session));
        Assert.All(factory.Inputs, input => Assert.False(input.IsRunning));
        fail = false;
        await recorder.ResumeAsync();
        Assert.Equal(RecorderState.Recording, recorder.State);
        await recorder.StopAsync();
        Assert.Single(AppStorage.LoadSessions(folder.Root).Sessions);
    }

    [Fact]
    public async Task DeviceLossAfterOpeningButBeforePublicationStillLeavesNoRecording()
    {
        using var folder = new TestFolder();
        var factory = new RecordingEngineTests.FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var disconnected = false;
        recorder.Warning += _ =>
        {
            if (disconnected)
                return;
            disconnected = true;
            factory.Inputs.Single(input => input.IsRunning).Fail();
        };
        await Assert.ThrowsAsync<RecordingDeviceException>(() =>
            recorder.StartAsync(new() { SaveFolder = folder.Root }, "Lost just before starting",
                new(new("broken", "Unavailable microphone", 1), Sources.Playback)));
        Assert.True(disconnected);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Null(recorder.Session);
        Assert.All(factory.Inputs, input => Assert.False(input.IsRunning));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Root));
    }

    [Fact]
    public async Task FileCreationFailureStopsCaptureWithoutMislabelingItAsADeviceFailure()
    {
        using var folder = new TestFolder();
        var occupied = folder.File("not-a-folder");
        File.WriteAllText(occupied, "Existing file");
        var factory = new RecordingEngineTests.FakeFactory();
        await using var recorder = new RecordingEngine(factory.Create);
        var exception = await Record.ExceptionAsync(() =>
            recorder.StartAsync(new() { SaveFolder = occupied }, "Cannot save", Sources));
        Assert.IsAssignableFrom<IOException>(exception);
        Assert.IsNotType<RecordingDeviceException>(exception);
        Assert.Equal(RecorderState.Idle, recorder.State);
        Assert.Null(recorder.Session);
        Assert.All(factory.Inputs, input => Assert.False(input.IsRunning));
        Assert.Equal("Existing file", File.ReadAllText(occupied));
        Assert.Single(Directory.EnumerateFileSystemEntries(folder.Root));
    }

    internal static KeyValuePair<string, string>[] PausedFileHashes(RecordingSession session) =>
        Directory.GetFiles(session.Folder).Order().Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return KeyValuePair.Create(Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(stream)));
        }).ToArray();
}
