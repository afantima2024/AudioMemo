using System.Text;
using AudioMemo.Core.Localization;

namespace AudioMemo.Core.AI;

public sealed record SpeechHintPrompt(string Text, int UsedTerms, int TotalTerms);

public static class SpeechVocabulary
{
    public const int MaximumTerms = 200;
    public const int MaximumTermBytes = 160;
    public const int MaximumInputLength = MaximumTerms * (MaximumTermBytes + 2);
    // A UTF-8 byte budget is a conservative bound without adding a second tokenizer.
    public const int MaximumPromptBytes = 200;

    public static List<string> Parse(string text)
    {
        if (text.Length > MaximumInputLength)
            throw new InvalidDataException(UiText.Get("InvalidSpeechVocabulary"));
        return Normalize(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    public static List<string> Normalize(IEnumerable<string> terms)
    {
        var result = new List<string>();
        foreach (var value in terms)
        {
            if (value is null)
                throw new InvalidDataException(UiText.Get("InvalidSpeechVocabulary"));
            var term = ChineseText.ToSimplified(value.Trim());
            if (term.Length == 0)
                continue;
            if (term.Any(char.IsControl) || Encoding.UTF8.GetByteCount(term) > MaximumTermBytes)
                throw new InvalidDataException(UiText.Get("InvalidSpeechVocabulary"));
            result.RemoveAll(existing => existing.Equals(term, StringComparison.OrdinalIgnoreCase));
            result.Add(term);
            if (result.Count > MaximumTerms)
                throw new InvalidDataException(UiText.Get("InvalidSpeechVocabulary"));
        }
        return result;
    }

    public static SpeechHintPrompt CreatePrompt(IEnumerable<string> terms)
    {
        var normalized = Normalize(terms);
        var chosen = new List<string>();
        var bytes = 0;
        foreach (var term in normalized.AsEnumerable().Reverse())
        {
            var length = Encoding.UTF8.GetByteCount(term) + (chosen.Count == 0 ? 0 : 2);
            if (bytes + length > MaximumPromptBytes)
                continue;
            chosen.Add(term);
            bytes += length;
        }
        return new(string.Join(", ", chosen), chosen.Count, normalized.Count);
    }
}
