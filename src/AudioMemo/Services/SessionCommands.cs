using System.IO;
using System.Text.Json;
using AudioMemo.Core.Models;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

public static class SessionCommands
{
    public static void Rename(RecordingSession session, string title)
    {
        title = title.Trim();
        if (title.Length is < 1 or > 160 || title.Any(char.IsControl))
            throw new ArgumentException(Core.Localization.UiText.Get("InvalidSessionTitle"));
        var current = JsonSerializer.Deserialize<RecordingSession>(AtomicFile.ReadText(session.MetadataPath))
            ?? throw new InvalidDataException(Core.Localization.UiText.Get("EditSourceChanged"));
        if (current.Id != session.Id)
            throw new InvalidOperationException(Core.Localization.UiText.Get("EditSourceChanged"));
        current.Folder = session.Folder;
        current.Title = title;
        current.Save();
        session.Title = title;
    }

    public static string ValidateDeletion(string library, RecordingSession session)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(library));
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(session.Folder));
        if (!string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(session.Id, "N", out _) || !File.Exists(session.MetadataPath))
            throw new IOException("Only a saved session directly inside the recording library can be deleted.");
        using var metadata = JsonDocument.Parse(File.ReadAllText(session.MetadataPath));
        if (metadata.RootElement.GetProperty("Id").GetString() != session.Id)
            throw new IOException("The session folder no longer matches this recording.");
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Session deletion is blocked for linked folders.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Session deletion is blocked for linked files.");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
        return folder;
    }

    public static void Recycle(string library, RecordingSession session) =>
        RecycleBin.Move(() => ValidateDeletion(library, session));
}
