using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AudioMemo.Core.Localization;
using AudioMemo.Core.Storage;

namespace AudioMemo.Services;

[JsonConverter(typeof(JsonStringEnumConverter<MessageSeverity>))]
internal enum MessageSeverity { Information, Warning, Error, Question }

internal sealed record MessageEntry(Guid Id, DateTimeOffset When, MessageSeverity Severity, string Reason, string What)
{
    [JsonIgnore] public string TimeLabel => When.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    [JsonIgnore] public string LevelLabel => UiText.Get("Message" + Severity);
}

internal sealed record MessageHistoryPage(IReadOnlyList<MessageEntry> Entries, long Before, int UnreadableEntries,
    string? ReadError = null);

internal sealed class MessageHistoryStore(string folder, TimeProvider? timeProvider = null)
{
    private static readonly Regex Credentials = new(
        """(?i)(\b(?:authorization|api[_-]?key|access[_-]?token|token|password|secret)\s*["']?\s*[:=]\s*["']?\s*(?:bearer\s+)?|\bbearer\s+)[^\s"',;&}]+""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ApiKeys = new(@"\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly object gate = new();
    private readonly List<MessageEntry> pending = [];
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private string? reportedFailure;
    public string Path { get; } = System.IO.Path.Combine(folder, "message-history.jsonl");
    public string? WriteError { get; private set; }
    public event Action<MessageEntry>? Added;
    public event Action? Cleared;

    internal static string Redact(string text) =>
        ApiKeys.Replace(Credentials.Replace(text, "$1[redacted]"), "[redacted]");

    public MessageEntry Add(string text, string reason, MessageSeverity severity = MessageSeverity.Information)
    {
        var entry = new MessageEntry(Guid.NewGuid(), time.GetLocalNow(), severity, Redact(reason), Redact(text));
        lock (gate)
        {
            pending.Add(entry);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                using var output = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (output.Length > 0)
                {
                    output.Position = output.Length - 1;
                    if (output.ReadByte() != '\n')
                    {
                        // Preserve a partial last entry after a crash; keep subsequent entries readable.
                        output.Position = output.Length;
                        output.WriteByte((byte)'\n');
                    }
                }
                output.Position = output.Length;
                while (pending.Count > 0)
                {
                    output.Write(JsonSerializer.SerializeToUtf8Bytes(pending[0]));
                    output.WriteByte((byte)'\n');
                    output.Flush(true);
                    pending.RemoveAt(0);
                }
                WriteError = reportedFailure = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteError = Redact(ex.Message);
            }
        }
        Added?.Invoke(entry);
        return entry;
    }

    public string? TakeWriteWarning()
    {
        lock (gate)
        {
            if (WriteError is null || WriteError == reportedFailure)
                return null;
            reportedFailure = WriteError;
            return UiText.Format("MessageHistoryWriteFailed", WriteError);
        }
    }

    public Task<MessageHistoryPage> ReadAsync(long? before, int count, CancellationToken token) =>
        Task.Run(() => Read(before, count, token), token);

    internal MessageHistoryPage Read(long? before, int count, CancellationToken token)
    {
        if (count is < 1 or > 1000 || before < 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        token.ThrowIfCancellationRequested();
        var entries = new List<MessageEntry>();
        var seen = new HashSet<Guid>();
        FileStream input;
        long cursor;
        lock (gate)
        {
            if (before is null)
            {
                entries.AddRange(pending.AsEnumerable().Reverse());
                seen.UnionWith(entries.Select(entry => entry.Id));
            }
            try { input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new(entries, 0, 0);
            }
            catch (Exception ex) when (entries.Count > 0 && ex is IOException or UnauthorizedAccessException)
            {
                return new(entries, before ?? 0, 0, Redact(ex.Message));
            }
            cursor = Math.Min(before ?? input.Length, input.Length);
        }
        using (input)
        {
            var buffer = new byte[8192];
            var reversed = new List<byte>();
            var unreadable = 0;
            var scanned = 0;
            while (cursor > 0)
            {
                token.ThrowIfCancellationRequested();
                var start = Math.Max(0, cursor - buffer.Length);
                var length = checked((int)(cursor - start));
                input.Position = start;
                input.ReadExactly(buffer.AsSpan(0, length));
                for (var index = length - 1; index >= 0; index--)
                {
                    if (buffer[index] != '\n')
                    {
                        reversed.Add(buffer[index]);
                        continue;
                    }
                    if (reversed.Count == 0)
                        continue;
                    AddLine();
                    if (++scanned >= count)
                        return new(entries, start + index + 1, unreadable);
                }
                cursor = start;
            }
            if (reversed.Count > 0)
                AddLine();
            return new(entries, 0, unreadable);

            void AddLine()
            {
                token.ThrowIfCancellationRequested();
                reversed.Reverse();
                try
                {
                    var item = JsonSerializer.Deserialize<MessageEntry>(reversed.ToArray());
                    if (item is null || item.Id == Guid.Empty || item.When == default || !Enum.IsDefined(item.Severity) ||
                        string.IsNullOrWhiteSpace(item.Reason) || string.IsNullOrWhiteSpace(item.What))
                        unreadable++;
                    else if (seen.Add(item.Id))
                        entries.Add(item with { Reason = Redact(item.Reason), What = Redact(item.What) });
                }
                catch (JsonException) { unreadable++; }
                reversed.Clear();
            }
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            AtomicFile.WriteText(Path, "");
            pending.Clear();
            WriteError = reportedFailure = null;
        }
        Cleared?.Invoke();
    }
}
