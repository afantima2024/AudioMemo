using AudioMemo.Core.Localization;

namespace AudioMemo.Core.Models;

public sealed record ShortcutCommand(string Id, string LabelKey, string DefaultGesture, bool TranscriptOnly = false);

public sealed class ShortcutSettings
{
    public static IReadOnlyList<ShortcutCommand> Commands { get; } = Array.AsReadOnly<ShortcutCommand>(
    [
        new("new", "NewConversationButton", "Ctrl+N"),
        new("record", "StartPauseRecording", "Ctrl+R"),
        new("stop", "Stop", "Ctrl+Shift+R"),
        new("settings", "Settings", "Ctrl+OemComma"),
        new("help", "HelpAndAbout", "F1"),
        new("history", "ToggleHistory", "Ctrl+B"),
        new("library", "OpenSavingFolder", "Ctrl+Shift+O"),
        new("files", "OpenFiles", "Ctrl+O"),
        new("import", "ImportAudio", ""),
        new("recover", "RecoverWave", "Ctrl+Alt+R"),
        new("devices", "ApplyDevices", "Ctrl+D"),
        new("refreshDevices", "RefreshAudioDevices", "Ctrl+Shift+D"),
        new("model", "ApplyModel", "Ctrl+Enter"),
        new("refreshModels", "RefreshSummaryModels", "Ctrl+Shift+M"),
        new("live", "LiveText", "Ctrl+Shift+L"),
        new("transcribe", "TranscribeResume", "Ctrl+T"),
        new("speakers", "IdentifySpeakers", "Ctrl+Shift+I"),
        new("restore", "RestoreMutedSections", "Ctrl+Shift+U"),
        new("cancelAi", "CancelAi", "Ctrl+Shift+X"),
        new("summarize", "UpdateMainPoints", "Ctrl+M"),
        new("export", "ExportConfiguredAudio", "Ctrl+E"),
        new("copy", "CopyAllText", "Ctrl+Shift+C"),
        new("copyAnnotated", "CopyAnnotated", "Ctrl+Alt+C"),
        new("latest", "JumpToLatest", "Ctrl+End", true),
        new("first", "TranscriptBeginning", "Ctrl+Home", true),
        new("pageUp", "TranscriptPageUp", "PageUp", true),
        new("pageDown", "TranscriptPageDown", "PageDown", true),
        new("speakerFilter", "SpeakerFilterLabel", "Ctrl+Shift+F"),
        new("play", "PlaySelectedRange", "Ctrl+P"),
        new("replayPause", "ReplayPauseResume", "Ctrl+Shift+P"),
        new("replayStop", "ReplayStop", "Ctrl+Alt+P"),
        new("silence", "MuteSelectedRows", "Ctrl+Alt+X"),
        new("edit", "EditTranscriptText", "F2"),
        new("reviewUnclear", "ReviewUnclearTranscript", ""),
        new("retranscribe", "RetranscribeSelection", "Ctrl+Shift+T"),
        new("online", "OnlineTranscription", ""),
        new("search", "SearchTranscript", "Ctrl+F"),
        new("searchNext", "SearchNext", "F3"),
        new("searchPrevious", "SearchPrevious", "Shift+F3"),
        new("searchClear", "SearchClear", "Escape", true),
        new("speakerColor", "SetSpeakerColor", "Ctrl+Alt+K"),
        new("speakerCorrection", "CorrectSpeaker", "Ctrl+Shift+K"),
        new("transcriptPane", "Transcript", "Alt+D1"),
        new("summaryPane", "MainPoints", "Alt+D2"),
        new("notesPane", "MyNotes", "Alt+D3"),
        new("dismissError", "Dismiss", "Ctrl+Alt+W")
    ]);

    private static readonly Dictionary<string, string> keys = (
        Enumerable.Range('A', 26).Select(value => ((char)value).ToString())
        .Concat(Enumerable.Range(0, 10).Select(value => "D" + value))
        .Concat(Enumerable.Range(1, 24).Select(value => "F" + value))
        .Concat(["OemComma", "OemPeriod", "OemPlus", "OemMinus", "OemSemicolon",
            "Space", "Enter", "Escape", "Home", "End", "PageUp", "PageDown", "Insert", "Delete",
            "Up", "Down", "Left", "Right"]))
        .ToDictionary(value => value, value => value, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y",
        "Ctrl+Shift+Z", "Ctrl+Insert", "Shift+Insert", "Shift+Delete",
        "Ctrl+Left", "Ctrl+Right", "Ctrl+Shift+Left", "Ctrl+Shift+Right",
        "Ctrl+Shift+Home", "Ctrl+Shift+End", "Ctrl+Delete",
        "Ctrl+Up", "Ctrl+Down", "Ctrl+Shift+Up", "Ctrl+Shift+Down", "Shift+PageUp", "Shift+PageDown",
        "F10", "Shift+F10", "Alt+Space", "Alt+F4", "Ctrl+Alt+Delete", "Ctrl+Shift+Escape", "Ctrl+Escape"
    };
    public Dictionary<string, string> Overrides { get; set; } = [];

    public string Gesture(string id) => Overrides.TryGetValue(id, out var value)
        ? Normalize(value) : Commands.Single(command => command.Id == id).DefaultGesture;

    public ShortcutCommand? Find(string gesture, bool transcriptFocused, bool searchFocused) =>
        gesture.Length == 0 ? null : Commands.FirstOrDefault(command =>
            (!command.TranscriptOnly || transcriptFocused || (searchFocused && command.Id == "searchClear")) &&
            Gesture(command.Id).Equals(gesture, StringComparison.Ordinal));

    public void Validate()
    {
        if (Overrides is null || Overrides.Keys.Any(id => Commands.All(command => command.Id != id)))
            throw new InvalidDataException(UiText.Get("InvalidShortcuts"));
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in Commands)
        {
            var gesture = Gesture(command.Id);
            if (gesture.Length == 0)
                continue;
            if (reserved.Contains(gesture) || (gesture == "Escape" && command.Id != "searchClear") ||
                (gesture is "PageUp" or "PageDown" or "Ctrl+Home" or "Ctrl+End" && !command.TranscriptOnly))
                throw new InvalidDataException(UiText.Format("ShortcutReserved", gesture));
            if (used.TryGetValue(gesture, out var previous))
                throw new InvalidDataException(UiText.Format("ShortcutConflict", gesture, UiText.Get(previous), UiText.Get(command.LabelKey)));
            used.Add(gesture, command.LabelKey);
        }
    }

    public static string Normalize(string? gesture)
    {
        if (gesture is null || gesture.Length > 80)
            throw new InvalidDataException(UiText.Get("InvalidShortcuts"));
        if (string.IsNullOrWhiteSpace(gesture))
            return "";
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries);
        if (!keys.TryGetValue(parts[^1], out var key))
            throw new InvalidDataException(UiText.Get("InvalidShortcutGesture"));
        var modifiers = parts[..^1];
        if (modifiers.Distinct(StringComparer.OrdinalIgnoreCase).Count() != modifiers.Length ||
            modifiers.Any(value => !new[] { "Ctrl", "Alt", "Shift" }.Contains(value, StringComparer.OrdinalIgnoreCase)) ||
            (!modifiers.Contains("Ctrl", StringComparer.OrdinalIgnoreCase) && !modifiers.Contains("Alt", StringComparer.OrdinalIgnoreCase) &&
                !(key.Length > 1 && key[0] == 'F') && key is not ("Escape" or "PageUp" or "PageDown")))
            throw new InvalidDataException(UiText.Get("InvalidShortcutGesture"));
        return string.Join("+", new[] { "Ctrl", "Alt", "Shift" }.Where(value => modifiers.Contains(value, StringComparer.OrdinalIgnoreCase)).Append(key));
    }
}
