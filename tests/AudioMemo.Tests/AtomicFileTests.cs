using AudioMemo.Core.Storage;

namespace AudioMemo.Tests;

public sealed class AtomicFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporarySourceOrDestinationLocksDoNotDestroyTheExistingFile(bool lockSource)
    {
        using var folder = new TestFolder();
        var target = folder.File("summary.json");
        var temporary = folder.File("summary.pending");
        File.WriteAllText(target, "Original");
        File.WriteAllText(temporary, "Replacement");
        using var reader = new FileStream(lockSource ? temporary : target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publish = Task.Run(() =>
        {
            started.SetResult();
            AtomicFile.PublishTemporary(temporary, target);
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.False(publish.IsCompleted);
            Assert.Equal("Original", AtomicFile.ReadText(target));
        }
        finally
        {
            reader.Dispose();
            await publish.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal("Replacement", AtomicFile.ReadText(target));
        Assert.False(File.Exists(temporary));
    }

    [Fact]
    public async Task CancellingDuringALockWaitPreservesTheOldFileAndRemovesTheTemporaryCopy()
    {
        using var folder = new TestFolder();
        var target = folder.File("notes.md");
        File.WriteAllText(target, "Original notes");
        using var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = Task.Run(() =>
        {
            started.SetResult();
            AtomicFile.WriteText(target, "Uncommitted notes", cancellation.Token);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Original notes", AtomicFile.ReadText(target));
        Assert.Equal([target], Directory.GetFiles(folder.Root));
    }

    [Fact]
    public void ALongHeldLockReportsTheDestinationAndPreservesTheOriginal()
    {
        using var folder = new TestFolder();
        var target = folder.File("summary.json");
        File.WriteAllText(target, "Original");
        using var reader = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var error = Assert.Throws<IOException>(() => AtomicFile.WriteText(target, "Replacement"));
        Assert.Contains("summary.json", error.Message);
        Assert.Contains(target, error.Message);
        Assert.True(error.InnerException is IOException or UnauthorizedAccessException);
        Assert.Equal("Original", AtomicFile.ReadText(target));
        Assert.Equal([target], Directory.GetFiles(folder.Root));
    }

    [Fact]
    public void ReadOnlyFilesAreNotForceUnlockedOrOverwritten()
    {
        using var folder = new TestFolder();
        var target = folder.File("notes.md");
        File.WriteAllText(target, "Original");
        var attributes = File.GetAttributes(target);
        File.SetAttributes(target, attributes | FileAttributes.ReadOnly);
        try
        {
            var error = Assert.Throws<IOException>(() => AtomicFile.WriteText(target, "Replacement"));
            Assert.Contains(target, error.Message);
            Assert.True(File.GetAttributes(target).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal("Original", AtomicFile.ReadText(target));
            Assert.Equal([target], Directory.GetFiles(folder.Root));
        }
        finally { File.SetAttributes(target, attributes); }
    }

    [Fact]
    public void AnAlreadyCancelledWriteDoesNotCreateAFile()
    {
        using var folder = new TestFolder();
        Assert.Throws<OperationCanceledException>(() =>
            AtomicFile.WriteJson(folder.File("summary.json"), new { Text = "Synthetic" }, new CancellationToken(true)));
        Assert.Empty(Directory.GetFiles(folder.Root));
    }
}
