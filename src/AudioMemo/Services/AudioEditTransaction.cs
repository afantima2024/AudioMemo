using System.IO;
using System.Text.Json;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

internal sealed class AudioEditTransaction : IDisposable
{
    private const string JournalName = ".audio-edit.json";
    private const string DirectoryPrefix = ".audio-edit-";
    private static readonly string[] names =
    [
        "audio.wav", "audio.mp3", "audio-output.wav", "transcript.json", "transcript.txt",
        "summary.json", "summary.md", "transcript.refining.json", "pending-processing.json",
        ".restore-source\\audio.wav", ".restore-source\\transcript.json", ".restore-source\\transcript.txt",
        ".restore-source\\session.json", "session.json"
    ];
    private readonly string folder;
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly string working;
    private bool publishing;
    public string Staging { get; }
    internal sealed record Entry(string Name, bool HadOriginal, bool HasReplacement);
    internal sealed record Journal(string Id, bool Committed, Entry[] Entries);

    public AudioEditTransaction(string folder)
    {
        this.folder = Path.GetFullPath(folder);
        VerifyPaths(this.folder);
        if (File.Exists(Path.Combine(folder, JournalName)))
            throw new IOException(UiText.Get("AudioEditRecoveryRequired"));
        working = Path.Combine(this.folder, DirectoryPrefix + id);
        if (Directory.Exists(working) || File.Exists(working))
            throw new IOException(UiText.Get("EditDestinationExists"));
        Staging = Path.Combine(working, "new");
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Path.Combine(working, "old"));
        // Track staging too, so a crash cannot strand an unpublished full-size audio copy.
        try { AtomicFile.WriteJson(Path.Combine(this.folder, JournalName), new Journal(id, false, [])); }
        catch
        {
            VerifyOwnedDirectory(working);
            Directory.Delete(working, true);
            throw;
        }
    }

    public void Commit(Action verify, CancellationToken token, Action<int>? afterReplacement = null)
    {
        VerifyPaths(folder);
        var entries = names.Select(name => new Entry(name, File.Exists(Path.Combine(folder, name)),
            File.Exists(Path.Combine(Staging, name)))).Where(entry => entry.HadOriginal || entry.HasReplacement).ToArray();
        // An unchanged recovery WAV stays in place; only newly staged recovery data is replaced.
        entries = entries.Where(entry => entry.Name != ".restore-source\\audio.wav" || entry.HasReplacement).ToArray();
        foreach (var entry in entries)
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(working, "old", entry.Name))!);
        verify();
        token.ThrowIfCancellationRequested();
        var journalPath = Path.Combine(folder, JournalName);
        var journal = new Journal(id, false, entries);
        publishing = true;
        try
        {
            AtomicFile.WriteJson(journalPath, journal, token);
            var count = 0;
            foreach (var entry in entries)
            {
                var target = Path.Combine(folder, entry.Name);
                var previous = Path.Combine(working, "old", entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (entry.HadOriginal && entry.HasReplacement)
                    File.Replace(Path.Combine(Staging, entry.Name), target, previous);
                else if (entry.HadOriginal)
                    File.Move(target, previous);
                else
                    File.Move(Path.Combine(Staging, entry.Name), target);
                afterReplacement?.Invoke(++count);
            }
            // Cancellation is honored before publication, never halfway through this short commit.
            AtomicFile.WriteJson(journalPath, journal with { Committed = true });
        }
        catch (Exception original)
        {
            try { Recover(folder); }
            catch (Exception rollback)
            {
                throw new AggregateException(UiText.Get("AudioEditRecoveryRequired"), original, rollback);
            }
            throw;
        }
        Recover(folder);
    }

    public static void Recover(string folder)
    {
        var path = Path.Combine(folder, JournalName);
        if (!File.Exists(path))
            return;
        VerifyPaths(folder);
        var journal = JsonSerializer.Deserialize<Journal>(AtomicFile.ReadText(path))
            ?? throw new InvalidDataException(UiText.Get("AudioEditRecoveryRequired"));
        if (!Guid.TryParseExact(journal.Id, "N", out _) || journal.Entries is null ||
            journal.Entries.Any(entry => entry is null || !entry.HadOriginal && !entry.HasReplacement) ||
            journal.Entries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() != journal.Entries.Length ||
            journal.Entries.Any(entry => !names.Contains(entry.Name, StringComparer.Ordinal)))
            throw new InvalidDataException(UiText.Get("AudioEditRecoveryRequired"));
        var working = Path.Combine(folder, DirectoryPrefix + journal.Id);
        if (!Directory.Exists(working))
        {
            if (!journal.Committed)
                throw new IOException(UiText.Get("AudioEditRecoveryRequired"));
            File.Delete(path);
            return;
        }
        VerifyOwnedDirectory(working);
        if (!journal.Committed)
        {
            foreach (var entry in journal.Entries.Reverse())
            {
                var target = Path.Combine(folder, entry.Name);
                var previous = Path.Combine(working, "old", entry.Name);
                if (File.Exists(previous))
                    AtomicFile.PublishTemporary(previous, target);
                else if (entry.HadOriginal && !File.Exists(target))
                    throw new IOException(UiText.Get("AudioEditRecoveryRequired"));
                else if (!entry.HadOriginal && File.Exists(target))
                    File.Delete(target);
            }
            AtomicFile.WriteJson(path, journal with { Committed = true });
        }
        Directory.Delete(working, true);
        File.Delete(path);
        var recovery = Path.Combine(folder, ".restore-source");
        if (Directory.Exists(recovery) && !Directory.EnumerateFileSystemEntries(recovery).Any())
            Directory.Delete(recovery);
    }

    private static void VerifyPaths(string folder)
    {
        foreach (var path in new[] { folder, Path.Combine(folder, ".restore-source"), Path.Combine(folder, JournalName) }
            .Concat(names.Select(name => Path.Combine(folder, name))))
            if ((File.Exists(path) || Directory.Exists(path)) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(UiText.Get("AudioEditLinkedPath"));
    }

    private static void VerifyOwnedDirectory(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException(UiText.Get("AudioEditLinkedPath"));
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(UiText.Get("AudioEditLinkedPath"));
                if (Directory.Exists(path))
                    pending.Push(path);
            }
        }
    }

    public void Dispose()
    {
        if (!publishing)
            Recover(folder);
        if (Directory.Exists(working) && !File.Exists(Path.Combine(folder, JournalName)))
        {
            VerifyOwnedDirectory(working);
            Directory.Delete(working, true);
        }
    }
}
