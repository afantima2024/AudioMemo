using System.Globalization;
using System.IO;

namespace AudioMemo.AI;

internal sealed class SpeakerProgress(TextWriter writer)
{
    internal const string Prefix = "[AudioMemo speaker progress] ";
    private int percentage = -1;
    public Exception? Error { get; private set; }

    public int Report(int completed, int total, IntPtr state)
    {
        if (Error is not null)
            return 0;
        try
        {
            Validate(completed, total);
            var nextPercentage = (int)(100L * completed / total);
            if (nextPercentage != percentage)
            {
                writer.WriteLine(FormattableString.Invariant($"{Prefix}{completed}/{total}"));
                writer.Flush();
                percentage = nextPercentage;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            // Managed exceptions must not cross the native progress callback boundary.
            Error = ex;
        }
        return 0;
    }

    public static AiWorkProgress? Parse(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            return null;
        var fields = line[Prefix.Length..].Split('/');
        if (fields.Length != 2 ||
            !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var completed) ||
            !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var total))
            throw new InvalidDataException("Invalid speaker progress message.");
        Validate(completed, total);
        // The native callback counts embeddings, not the later clustering work.
        return completed == total ? new("speaker-finalizing") : new("speaker-features", (double)completed / total);
    }

    private static void Validate(int completed, int total)
    {
        if (total <= 0 || completed < 0 || completed > total)
            throw new InvalidDataException("Invalid speaker progress counts.");
    }
}
