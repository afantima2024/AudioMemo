using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AudioMemo.Services;

namespace AudioMemo.Tests;

public sealed class MessageHistoryTests
{
    [Fact]
    public async Task ReopenedHistoryKeepsWhenWhyAndWhatAndPagesNewestFirst()
    {
        using var folder = new TestFolder();
        var time = new FixedTime();
        var store = new MessageHistoryStore(folder.Root, time);
        var saved = Enumerable.Range(0, 41).Select(index => store.Add($"Notice {index}.", $"Operation {index}",
            index % 2 == 0 ? MessageSeverity.Information : MessageSeverity.Warning)).ToArray();
        store = new MessageHistoryStore(folder.Root, time);
        var results = new List<MessageEntry>();
        long? cursor = null;
        do
        {
            var page = await store.ReadAsync(cursor, 7, CancellationToken.None);
            Assert.InRange(page.Entries.Count, 1, 7);
            Assert.Equal(0, page.UnreadableEntries);
            results.AddRange(page.Entries);
            Assert.True(cursor is null || page.Before < cursor);
            cursor = page.Before;
        } while (cursor != 0);
        Assert.Equal(saved.Reverse(), results);
        Assert.All(results, entry => Assert.Equal(time.GetUtcNow(), entry.When));
        Assert.Equal(results.Count, results.Select(entry => entry.Id).Distinct().Count());
    }

    [Fact]
    public void LargeUtf8EntriesAcrossReadBlocksAndCorruptLinesDoNotHideOlderMessages()
    {
        using var folder = new TestFolder();
        var store = new MessageHistoryStore(folder.Root);
        var first = store.Add("First notice.", "Recording");
        var large = new MessageEntry(Guid.NewGuid(), DateTimeOffset.Now, MessageSeverity.Error,
            "Synthetic multilingual operation", string.Concat(Enumerable.Repeat("\u4e2d\u6587\U0001F3A4\n", 6000)));
        var options = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.AppendAllText(store.Path, JsonSerializer.Serialize(large, options) + "\r\nbroken entry\n", new UTF8Encoding(false));
        var last = store.Add("Last notice.", "Settings");
        var all = new List<MessageEntry>();
        var broken = 0;
        long? cursor = null;
        do
        {
            var page = store.Read(cursor, 1, CancellationToken.None);
            all.AddRange(page.Entries);
            broken += page.UnreadableEntries;
            cursor = page.Before;
        } while (cursor != 0);
        Assert.Equal([last, large, first], all);
        Assert.Equal(1, broken);
        Assert.Contains("broken entry", File.ReadAllText(store.Path));
    }

    [Fact]
    public void InterruptedLastWriteIsPreservedAndDoesNotCorruptLaterNotices()
    {
        using var folder = new TestFolder();
        var store = new MessageHistoryStore(folder.Root);
        var first = store.Add("Saved before interruption.", "Import");
        File.AppendAllText(store.Path, """{"Id":"incomplete""");
        var last = store.Add("Saved after interruption.", "Import");
        var page = store.Read(null, 10, CancellationToken.None);
        Assert.Equal([last, first], page.Entries);
        Assert.Equal(1, page.UnreadableEntries);
        Assert.Contains("""{"Id":"incomplete""", File.ReadAllText(store.Path));
    }

    [Theory]
    [InlineData("Authorization: Bearer synthetic-access", "synthetic-access")]
    [InlineData("\"api_key\":\"synthetic-api-value\"", "synthetic-api-value")]
    [InlineData("password = synthetic-password", "synthetic-password")]
    [InlineData("Token: synthetic-token&next=1", "synthetic-token")]
    [InlineData("Failure using sk-syntheticlongkeyhere", "sk-syntheticlongkeyhere")]
    public void CredentialsAreRedactedBeforePersistenceAndDisplay(string text, string secret)
    {
        using var folder = new TestFolder();
        var store = new MessageHistoryStore(folder.Root);
        var entry = store.Add(text, text, MessageSeverity.Error);
        Assert.DoesNotContain(secret, entry.What);
        Assert.DoesNotContain(secret, entry.Reason);
        Assert.DoesNotContain(secret, File.ReadAllText(store.Path));
        Assert.Contains("[redacted]", Assert.Single(store.Read(null, 10, CancellationToken.None).Entries).What);
    }

    [Fact]
    public void FailedWritesRemainVisibleAndAreRetriedWithoutLosingMessages()
    {
        using var folder = new TestFolder();
        var store = new MessageHistoryStore(folder.Root);
        using (var locked = new FileStream(store.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            var first = store.Add("Not lost while locked.", "Synthetic failure");
            Assert.NotNull(store.WriteError);
            Assert.NotNull(store.TakeWriteWarning());
            Assert.Null(store.TakeWriteWarning());
            var unsaved = store.Read(null, 10, CancellationToken.None);
            Assert.NotNull(unsaved.ReadError);
            Assert.Equal(first, Assert.Single(unsaved.Entries));
            Assert.NotEqual(Guid.Empty, first.Id);
        }
        var last = store.Add("Saving is available again.", "Recovery");
        Assert.Null(store.WriteError);
        var page = new MessageHistoryStore(folder.Root).Read(null, 10, CancellationToken.None);
        Assert.Equal(2, page.Entries.Count);
        Assert.Equal(last, page.Entries[0]);
        Assert.Equal("Not lost while locked.", page.Entries[1].What);
    }

    [Fact]
    public void ClearOnlyRemovesTheJournalAndCancellationStopsReads()
    {
        using var folder = new TestFolder();
        var privateFile = folder.File("notes.md");
        File.WriteAllText(privateFile, "PRIVATE SYNTHETIC NOTE");
        var store = new MessageHistoryStore(folder.Root);
        store.Add("A notice.", "Recording");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Read(null, 10, cancellation.Token));
        var notifications = 0;
        store.Cleared += () => notifications++;
        store.Clear();
        Assert.Equal(1, notifications);
        Assert.Empty(store.Read(null, 10, CancellationToken.None).Entries);
        Assert.Equal("PRIVATE SYNTHETIC NOTE", File.ReadAllText(privateFile));
        store.Add("After clearing.", "Settings");
        Assert.Single(new MessageHistoryStore(folder.Root).Read(null, 10, CancellationToken.None).Entries);
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 23, 9, 14, 15, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
