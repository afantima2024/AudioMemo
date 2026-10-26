using System.Globalization;
using System.Text.Json;
using AudioMemo.Core.AI;
using AudioMemo.Core.Models;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class SetupAndStatusTests
{
    [Fact]
    public void NewProfilesRequireSetupWhileExistingPreferencesMigrateWithoutBeingRewritten()
    {
        using var folder = new TestFolder();
        var storage = new AppStorage(folder.Root);
        Assert.False(storage.LoadSettings().SetupCompleted);
        var path = Path.Combine(folder.Root, "settings.json");
        var legacy = JsonSerializer.Serialize(new { SaveFolder = folder.Root, Theme = "dark", LiveTranscription = true });
        File.WriteAllText(path, legacy);
        var settings = storage.LoadSettings();
        Assert.True(settings.SetupCompleted);
        Assert.True(settings.LiveTranscription);
        Assert.Equal("dark", settings.Theme);
        Assert.Equal(legacy, File.ReadAllText(path));
        Assert.True(settings.StatusBar.Llm);
        Assert.True(settings.StatusBar.Messages);
        Assert.False(settings.StatusBar.LibrarySize);
        Assert.False(settings.SummarySetupDeferred);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitSetupStatePersists(bool complete)
    {
        using var folder = new TestFolder();
        var storage = new AppStorage(folder.Root);
        storage.SaveSettings(new() { SaveFolder = folder.Root, SetupCompleted = complete, SummarySetupDeferred = !complete });
        Assert.Equal(complete, storage.LoadSettings().SetupCompleted);
        Assert.Equal(!complete, storage.LoadSettings().SummarySetupDeferred);
    }

    [Fact]
    public void BothResetChoicesAreStagedAndNeverDeleteRecordingsOrModelFiles()
    {
        using var folder = new TestFolder();
        var session = EditedSessionTests.MakeSource(folder);
        File.WriteAllText(Path.Combine(folder.Root, "synthetic-model.bin"), "SYNTHETIC MODEL");
        var settings = new AppSettings
        {
            SaveFolder = folder.Root, SetupCompleted = true, LiveTranscription = true, Theme = "dark",
            SummaryProvider = CloudLlmCatalog.Qwen, CloudConsent = true
        };
        var endpoint = settings.ActiveSummaryEndpoint;
        settings.GetCloudProfile().ProtectedApiKeys[endpoint] = "SYNTHETIC PROTECTED KEY";
        settings.GetCloudProfile().ConsentEndpoint = endpoint;
        var original = JsonSerializer.Serialize(settings);
        var files = EditedSessionTests.Hashes(folder.Root);
        var keep = SetupValidation.Prepare(settings, SetupResetMode.KeepSettings);
        Assert.Equal(original, JsonSerializer.Serialize(keep));
        keep.GetCloudProfile().ProtectedApiKeys.Clear();
        Assert.Equal(original, JsonSerializer.Serialize(settings));
        var reset = SetupValidation.Prepare(settings, SetupResetMode.ResetPreferences);
        Assert.False(reset.SetupCompleted);
        Assert.Empty(reset.CloudModels);
        Assert.False(reset.CloudConsent);
        Assert.Equal(new AppSettings().Theme, reset.Theme);
        Assert.Equal(files, EditedSessionTests.Hashes(folder.Root));
        Assert.True(File.Exists(session.WavePath));
    }

    [Fact]
    public void FolderCheckRequiresAnAbsoluteWritableDirectoryAndLeavesNoProbeFiles()
    {
        using var folder = new TestFolder();
        var target = Path.Combine(folder.Root, "recordings");
        SetupValidation.CheckFolder(target, CancellationToken.None);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        var unrelated = Path.Combine(target, "preserve.txt");
        File.WriteAllText(unrelated, "PRESERVE");
        SetupValidation.CheckFolder(target, CancellationToken.None);
        Assert.Equal([unrelated], Directory.GetFiles(target));
        Assert.Equal("PRESERVE", File.ReadAllText(unrelated));
        Assert.Throws<InvalidDataException>(() => SetupValidation.CheckFolder("relative-folder", CancellationToken.None));
        Assert.Throws<IOException>(() => SetupValidation.CheckFolder(unrelated, CancellationToken.None));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var absent = Path.Combine(folder.Root, "not-created");
        Assert.ThrowsAny<OperationCanceledException>(() => SetupValidation.CheckFolder(absent, cancelled.Token));
        Assert.False(Directory.Exists(absent));
    }

    [Fact]
    public void StatusBarSelectionsPersistIndependentlyAndRejectUnknownOptions()
    {
        using var folder = new TestFolder();
        var storage = new AppStorage(folder.Root);
        var settings = new AppSettings { SaveFolder = folder.Root };
        var names = new[] { "Messages", "Llm", "SpeechModel", "LibrarySize", "RecordingCount", "FreeDiskSpace" };
        foreach (var name in names)
            settings.StatusBar.Set(name, true);
        settings.StatusBar.Llm = false;
        settings.StatusBar.Messages = false;
        storage.SaveSettings(settings);
        var loaded = storage.LoadSettings();
        Assert.All(names, name => Assert.Equal(settings.StatusBar.Get(name), loaded.StatusBar.Get(name)));
        Assert.Throws<ArgumentException>(() => loaded.StatusBar.Set("unknown", true));
        Assert.Throws<ArgumentException>(() => loaded.StatusBar.Get("unknown"));
        loaded.StatusBar = null!;
        Assert.Throws<InvalidDataException>(loaded.Validate);
    }

    [Fact]
    public void StatisticsCountOnlyKnownRecordingFoldersAndDoNotReadFileContents()
    {
        using var folder = new TestFolder();
        var first = Directory.CreateDirectory(Path.Combine(folder.Root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(folder.Root, "second")).FullName;
        Directory.CreateDirectory(Path.Combine(first, "nested"));
        File.WriteAllBytes(Path.Combine(first, "audio.wav"), new byte[123]);
        File.WriteAllBytes(Path.Combine(first, "nested", "notes.md"), new byte[45]);
        File.WriteAllBytes(Path.Combine(second, "summary.md"), new byte[67]);
        File.WriteAllBytes(Path.Combine(folder.Root, "unrelated.bin"), new byte[999]);
        using var locked = new FileStream(Path.Combine(first, "audio.wav"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = LibraryStatistics.Read(folder.Root, [first, second, first], true, false, CancellationToken.None);
        Assert.Equal(235, result.OccupiedBytes);
        Assert.Null(result.FreeBytes);
    }

    [Fact]
    public void StatisticsFailuresAreNotReportedAsZeroOrSuccessfulResults()
    {
        using var folder = new TestFolder();
        Assert.ThrowsAny<IOException>(() => LibraryStatistics.Read(folder.Root,
            [Path.Combine(folder.Root, "missing")], true, false, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => LibraryStatistics.Read(folder.Root, [], true, true, cancellation.Token));
        Assert.Equal(0, LibraryStatistics.Read(folder.Root, [], true, false, CancellationToken.None).OccupiedBytes);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1,023 B")]
    [InlineData(1024, "1.0 KiB")]
    [InlineData(1073741824, "1.0 GiB")]
    public void StorageUnitsUseBinaryBytesAndRespectTheChosenCulture(long bytes, string display)
    {
        Assert.Equal(display, LibraryStatistics.FormatBytes(bytes, CultureInfo.GetCultureInfo("en")));
        Assert.Throws<ArgumentOutOfRangeException>(() => LibraryStatistics.FormatBytes(-1, CultureInfo.InvariantCulture));
    }
}
