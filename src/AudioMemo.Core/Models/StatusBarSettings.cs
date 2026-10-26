namespace AudioMemo.Core.Models;

public sealed class StatusBarSettings
{
    public bool Messages { get; set; } = true;
    public bool Llm { get; set; } = true;
    public bool SpeechModel { get; set; } = true;
    public bool LibrarySize { get; set; }
    public bool RecordingCount { get; set; }
    public bool FreeDiskSpace { get; set; }

    public bool Get(string option) => option switch
    {
        nameof(Messages) => Messages,
        nameof(Llm) => Llm,
        nameof(SpeechModel) => SpeechModel,
        nameof(LibrarySize) => LibrarySize,
        nameof(RecordingCount) => RecordingCount,
        nameof(FreeDiskSpace) => FreeDiskSpace,
        _ => throw new ArgumentException("Unknown status bar option.", nameof(option))
    };

    public void Set(string option, bool value)
    {
        switch (option)
        {
            case nameof(Messages): Messages = value; break;
            case nameof(Llm): Llm = value; break;
            case nameof(SpeechModel): SpeechModel = value; break;
            case nameof(LibrarySize): LibrarySize = value; break;
            case nameof(RecordingCount): RecordingCount = value; break;
            case nameof(FreeDiskSpace): FreeDiskSpace = value; break;
            default: throw new ArgumentException("Unknown status bar option.", nameof(option));
        }
    }
}
